using Sorcerer.Core.Characters;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

/// <summary>
/// Shared consequence-apply helpers (Phase 0.2 split): rejection/apply-result construction,
/// player-message visibility, subject/verb narration grammar, entity/geometry resolution, and
/// payload readers used across every handler family. This is the shared surface the family
/// partial files build on; it decides no gameplay policy of its own.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult Reject(WorldConsequence consequence, string error)
    {
        var delta = new StateDelta(
            "worldConsequenceRejected",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            error,
            Details(consequence, ("error", error)));
        return new WorldConsequenceApplyResult(false, consequence.TargetEntityId, error, Array.Empty<string>(), new[] { delta }, Details(consequence, ("error", error)));
    }

    private WorldConsequenceApplyResult RollBackFreeCaptive(
        WorldConsequence consequence,
        GameStateSnapshot snapshot,
        IReadOnlyList<StateDelta> failedDeltas,
        IReadOnlyList<string> failedMessages,
        string failure)
    {
        snapshot.Restore(_state);
        var diagnostics = failedDeltas
            .Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var skipped = new StateDelta(
            "freeCaptiveSkipped",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            $"Captive release rolled back: {failure}.",
            Details(
                consequence,
                ("failure", failure),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count),
                ("rejectedCount", diagnostics.Length),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            consequence.TargetEntityId,
            failure,
            Array.Empty<string>(),
            diagnostics.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("error", failure),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count)));
    }

    private WorldConsequenceApplyResult Applied(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyList<string> messages,
        StateDelta delta,
        params (string Key, object? Value)[] fields) =>
        new(true, targetId, null, messages, new[] { delta }, Details(consequence, fields));

    private IReadOnlyList<string> MaybeVisibleMessage(WorldConsequence consequence, string message)
    {
        if (!IsVisible(consequence.Visibility) || !PayloadAllowsPlayerMessage(consequence))
        {
            return Array.Empty<string>();
        }

        _state.AddMessage(message);
        return new[] { message };
    }

    private bool AddMessageIfAllowed(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        string message,
        bool defaultEmitMessage = true,
        bool includeVisible = true,
        bool playerVisible = true)
    {
        var shouldEmit = (includeVisible && IsVisible(consequence.Visibility))
            || (ReadBool(payload, "emitMessage") ?? defaultEmitMessage);
        if (shouldEmit && playerVisible && PayloadAllowsPlayerMessage(consequence))
        {
            _state.AddMessage(message);
            return true;
        }

        return false;
    }

    private static bool PayloadAllowsPlayerMessage(WorldConsequence consequence)
    {
        var payload = consequence.Payload;
        if (payload is null)
        {
            return true;
        }

        return ReadBool(payload, "playerVisible") != false
            && ReadBool(payload, "player_visible") != false
            && ReadBool(payload, "auditOnly") != true
            && ReadBool(payload, "audit_only") != true;
    }

    private static bool IsVisible(string visibility) =>
        NormalizeToken(visibility, WorldConsequenceVisibility.Hidden) is
            WorldConsequenceVisibility.Message or WorldConsequenceVisibility.Journal or WorldConsequenceVisibility.Lead or "visible";

    private IReadOnlyDictionary<string, object?> Details(WorldConsequence consequence, params (string Key, object? Value)[] fields)
    {
        var details = consequence.Payload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(consequence.Payload, StringComparer.OrdinalIgnoreCase);
        details["consequenceType"] = consequence.Type;
        details["source"] = consequence.Source;
        details["sourceEntityId"] = consequence.SourceEntityId;
        details["visibility"] = consequence.Visibility;
        details["timing"] = consequence.Timing;
        details["salience"] = consequence.Salience;
        details["confidence"] = consequence.Confidence;
        details["evidence"] = consequence.Evidence;
        details["reason"] = consequence.Reason;
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        return details;
    }

    private static void EnsureInteractableVerbs(Entity entity, params string[] verbs)
    {
        var existing = entity.TryGet<InteractableComponent>(out var interactable)
            ? interactable.Verbs
            : Array.Empty<string>();
        var merged = existing
            .Concat(verbs)
            .Where(verb => !string.IsNullOrWhiteSpace(verb))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(verb => verb, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        entity.Set(new InteractableComponent(merged));
    }

    private bool CanReach(Entity actor, Entity target, int range)
    {
        if (!actor.TryGet<PositionComponent>(out var actorPosition)
            || !target.TryGet<PositionComponent>(out var targetPosition))
        {
            return false;
        }

        return Distance(actorPosition.Position, targetPosition.Position) <= range;
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        var offsets = new[]
        {
            new GridPoint(0, 1),
            new GridPoint(1, 0),
            new GridPoint(0, -1),
            new GridPoint(-1, 0),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(1, -1),
            new GridPoint(-1, -1),
        };
        foreach (var point in offsets
            .Select(offset => new GridPoint(origin.X + offset.X, origin.Y + offset.Y))
            .Where(point => point.X > 0 && point.Y > 0 && point.X < _state.Width - 1 && point.Y < _state.Height - 1)
            .Where(point => !_state.BlockingTerrain.Contains(point))
            .Where(point => !_state.Entities.Values.Any(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == point
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement)))
        {
            return point;
        }

        return null;
    }

    private static int Distance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private bool IsBoundaryWall(GridPoint point) =>
        point.X == 0 || point.Y == 0 || point.X == _state.Width - 1 || point.Y == _state.Height - 1;

    private bool InBounds(GridPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X < _state.Width && point.Y < _state.Height;

    private Entity? BlockingEntityAt(GridPoint point) =>
        _state.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement
            && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive));

    private static bool TerrainBlocksMovement(string terrain) =>
        terrain is "wall" or "ice_wall" or "rubble" or "vines";

    private static string SentenceCase(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : $"{char.ToUpperInvariant(text[0])}{text[1..]}";

    private Entity? EntityById(string entityId) =>
        _state.Entities.TryGetValue(EntityId.Create(entityId), out var entity) ? entity : null;

    private string AttackSummary(Entity attacker, Entity defender, int amount, string damageType)
    {
        var alive = defender.TryGet<ActorComponent>(out var actor) && actor.Alive;
        return alive
            ? $"{Subject(attacker)} {Verb(attacker, "strike", "strikes")} {ObjectName(defender)} for {amount} {damageType} damage."
            : $"{Subject(attacker)} {Verb(attacker, "drop", "drops")} {ObjectName(defender)}.";
    }

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string ObjectName(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "you" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;

    private string Possessive(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "Your" : $"{entity.Name}'s";

    private static int ClampDelta(int value, int maxDelta) =>
        Math.Clamp(value, -maxDelta, maxDelta);

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static string BondSummary(BondRecord bond)
    {
        if (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase))
        {
            return "following";
        }

        if (bond.Loyalty + bond.Admiration >= 5)
        {
            return "warm enough to risk something";
        }

        if (bond.Fear > bond.Loyalty + bond.Admiration)
        {
            return "afraid";
        }

        if (bond.Resentment >= 5)
        {
            return "resentful";
        }

        return bond.Posture;
    }

    private static string NormalizeToken(string text, string fallback)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeClaimText(string text) =>
        string.Join(
            " ",
            text.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.', '!', '?');

    private static string CleanLedgerKey(string text, string fallback)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace(' ', '_');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FormatStackMessage(string? template, string text, string kind, int stacks)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return $"{text} deepens ({stacks} stacks).";
        }

        return template
            .Replace("{text}", text, StringComparison.OrdinalIgnoreCase)
            .Replace("{kind}", kind, StringComparison.OrdinalIgnoreCase)
            .Replace("{stacks}", stacks.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value switch
        {
            string text => text,
            _ => value?.ToString(),
        } : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int typed => typed,
            long typed => typed > int.MaxValue ? int.MaxValue : typed < int.MinValue ? int.MinValue : (int)typed,
            double typed => (int)Math.Round(typed),
            float typed => (int)Math.Round(typed),
            decimal typed => (int)Math.Round(typed),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static char ReadChar(IReadOnlyDictionary<string, object?> map, string key, char fallback)
    {
        var text = ReadString(map, key);
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim()[0];
    }

    private static bool TryReadPoint(IReadOnlyDictionary<string, object?> map, string? target, out GridPoint point)
    {
        if ((ReadInt(map, "x") ?? ReadInt(map, "X")) is { } x
            && (ReadInt(map, "y") ?? ReadInt(map, "Y")) is { } y)
        {
            point = new GridPoint(x, y);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(target)
            && target.StartsWith("tile:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = target["tile:".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var targetX)
                && int.TryParse(parts[1], out var targetY))
            {
                point = new GridPoint(targetX, targetY);
                return true;
            }
        }

        point = default;
        return false;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool typed => typed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryReadControllerKind(string? text, out ControllerKind kind)
    {
        kind = ControllerKind.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        kind = NormalizeToken(text, "") switch
        {
            "none" or "no_controller" or "uncontrolled" or "inert" => ControllerKind.None,
            "player" or "human" or "controlled" or "controlled_by_player" => ControllerKind.Player,
            "ai" or "npc" or "agent" or "computer" => ControllerKind.Ai,
            _ => kind,
        };
        return NormalizeToken(text, "") is
            "none" or "no_controller" or "uncontrolled" or "inert"
            or "player" or "human" or "controlled" or "controlled_by_player"
            or "ai" or "npc" or "agent" or "computer";
    }

    private static IReadOnlyDictionary<string, object?>? ReadDictionary(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            IReadOnlyDictionary<string, object?> readOnly => new Dictionary<string, object?>(readOnly, StringComparer.OrdinalIgnoreCase),
            IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase),
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, object?> PayloadWithoutSchedulerKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var copy = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "eventType", "event_type", "kind", "turns", "delay", "dueTurn", "due_turn", "operation" })
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, object?> PayloadWithoutPersistentKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var copy = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "target", "hook", "kind", "uses", "linkPartnerId", "linkPartner", "link_target", "effectType", "effect_type", "effectFields", "effect", "operation", "playerVisible" })
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, object?> PayloadWithoutWorldTurnKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var copy = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
        {
            "worldTurnReason",
            "world_turn_reason",
            "moveReason",
            "reason",
            "kind",
            "moveKind",
            "worldTurnSourceId",
            "world_turn_source_id",
            "sourceId",
            "source_id",
            "summary",
            "details",
            "recordDetails",
            "record_details",
            "operation",
        })
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static IReadOnlyList<string> ReadStringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object?> objects => objects.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<GridPoint> ReadPointList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<GridPoint>();
        }

        return value switch
        {
            string text => ParsePointListText(text),
            IEnumerable<GridPoint> points => points.ToArray(),
            IEnumerable<string> strings => strings.SelectMany(ParsePointListText).ToArray(),
            IEnumerable<object?> objects => objects.SelectMany(ParsePointObject).ToArray(),
            _ => Array.Empty<GridPoint>(),
        };
    }

    private static IReadOnlyList<GridPoint> ParsePointObject(object? value)
    {
        if (value is null)
        {
            return Array.Empty<GridPoint>();
        }

        if (value is GridPoint point)
        {
            return new[] { point };
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly
            && (ReadInt(readOnly, "x") ?? ReadInt(readOnly, "X")) is { } x
            && (ReadInt(readOnly, "y") ?? ReadInt(readOnly, "Y")) is { } y)
        {
            return new[] { new GridPoint(x, y) };
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            var map = new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
            if ((ReadInt(map, "x") ?? ReadInt(map, "X")) is { } mapX
                && (ReadInt(map, "y") ?? ReadInt(map, "Y")) is { } mapY)
            {
                return new[] { new GridPoint(mapX, mapY) };
            }
        }

        return ParsePointListText(value.ToString() ?? "");
    }

    private static IReadOnlyList<GridPoint> ParsePointListText(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParsePointText)
            .Where(point => point is not null)
            .Select(point => point!.Value)
            .ToArray();

    private static GridPoint? ParsePointText(string text)
    {
        var parts = text.Trim()
            .TrimStart('(')
            .TrimEnd(')')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out var x)
            && int.TryParse(parts[1], out var y)
                ? new GridPoint(x, y)
                : null;
    }

    private static bool HasAnyKey(IReadOnlyDictionary<string, object?> map, params string[] keys) =>
        keys.Any(key => map.Any(pair =>
            pair.Value is not null
            && pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags
            .Select(tag => NormalizeToken(tag, ""))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static char ReadGlyph(IReadOnlyDictionary<string, object?> map, char fallback)
    {
        var value = ReadString(map, "glyph");
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[0];
    }

    private static bool RecordMentionsMemory(EntityMemoryRecord record, string? subject, bool aboutCaster)
    {
        if (aboutCaster)
        {
            return record.Text.Contains("caster", StringComparison.OrdinalIgnoreCase)
                || record.Text.Contains("player", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.Contains("wild_magic", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(subject)
            && record.Text.Contains(subject, StringComparison.OrdinalIgnoreCase);
    }
}
