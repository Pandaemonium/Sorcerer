using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

/// <summary>
/// <see cref="PromiseRealizationSystem"/> promise selection support: salience scoring, eligibility/trigger matching, and the naming, geometry, and region/realm resolution helpers the realizers draw on.
/// Split from the promise-realization system (Phase 0.4); selection, plan building, the
/// apply/rollback lifecycle, and the injected consequence sink stay in the base file.
/// </summary>
public sealed partial class PromiseRealizationSystem
{
    private IReadOnlyList<string> SelectionReasons(
        WorldPromise promise,
        PromiseRealizationContext context,
        string handler,
        int score,
        string target)
    {
        var reasons = new List<string>
        {
            "status:bound",
            $"handler:{handler}",
            $"salience:{Math.Clamp(promise.Salience, 1, 5)}",
            $"score:{score}",
        };

        if (TriggerHintHasExactMatch(promise.TriggerHint, context.Trigger))
        {
            reasons.Add("trigger:exact");
        }
        else if (string.IsNullOrWhiteSpace(promise.TriggerHint))
        {
            reasons.Add("trigger:broad");
        }
        else if (PromiseTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            reasons.Add("trigger:compatible");
        }

        if (!string.IsNullOrWhiteSpace(promise.BoundTargetId)
            && promise.BoundTargetId.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("bound_target:matched");
        }

        if (PromiseTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection)
        {
            reasons.Add(promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase)
                ? "direction:matched"
                : "direction:soft_mismatch");
        }

        if (PlaceMatchesContext(promise.ClaimedPlace, context)
            || PlaceMatchesContext(promise.BoundPlace, context))
        {
            reasons.Add("place:matched");
        }

        if (promise.Stacks > 1)
        {
            reasons.Add($"stacks:{promise.Stacks}");
        }

        if (context.PlacementOrigin is not null)
        {
            reasons.Add("placement:available");
        }

        return reasons
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int TravelPromiseScore(WorldPromise promise, PromiseRealizationContext context)
    {
        var score = Math.Clamp(promise.Salience, 1, 5) * 20;
        if (TriggerHintHasExactMatch(promise.TriggerHint, "travel"))
        {
            score += 18;
        }
        else if (string.IsNullOrWhiteSpace(promise.TriggerHint))
        {
            score += 6;
        }

        if (PromiseTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection
            && promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }

        if (PlaceMatchesContext(promise.ClaimedPlace, context)
            || PlaceMatchesContext(promise.BoundPlace, context))
        {
            score += 18;
        }

        score += NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "person" => 12,
            "merchant_stock" or "stock" or "trade" => 11,
            "service" => 11,
            "escape_route" or "route" or "door_rule" => 10,
            "site" or "town" or "landmark" => 10,
            "item" => 8,
            "threat" => 7,
            "quest" => 6,
            _ => 3,
        };

        if (_state.CurrentZoneId.Equals("0,0", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        score += Math.Min(10, Math.Max(0, promise.Stacks - 1) * 3);
        score += _state.Rng.NextInt(0, 12);
        return score;
    }

    private int AmbientPromiseScore(WorldPromise promise, PromiseRealizationContext context)
    {
        var score = Math.Clamp(promise.Salience, 1, 5) * 20;
        if (TriggerHintHasExactMatch(promise.TriggerHint, context.Trigger))
        {
            score += 18;
        }

        score += NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "threat" or "debt" => 14,
            "prophecy" or "omen" or "event" => 12,
            "escape_route" or "route" or "door_rule" => 10,
            "item" or "person" or "service" or "merchant_stock" or "stock" or "trade" => 8,
            "memory" or "quest" => 6,
            _ => 3,
        };

        score += Math.Min(10, Math.Max(0, promise.Stacks - 1) * 3);
        score += _state.Rng.NextInt(0, 8);
        return score;
    }

    private static int AnchoredPromiseScore(
        WorldPromise promise,
        Entity anchor,
        PromiseRealizationContext context)
    {
        var trigger = NormalizeToken(context.Trigger);
        var kind = NormalizeToken(promise.RealizationKind ?? promise.Kind);
        var score = Math.Clamp(promise.Salience, 1, 5) * 20;
        if (TriggerHintHasExactMatch(promise.TriggerHint, context.Trigger))
        {
            score += 24;
        }
        else if (PromiseTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            score += string.IsNullOrWhiteSpace(promise.TriggerHint) ? 0 : 12;
        }

        if (!string.IsNullOrWhiteSpace(promise.BoundTargetId)
            && promise.BoundTargetId.Equals(anchor.Id.Value, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        score += kind switch
        {
            "door_rule" when anchor.Has<DoorComponent>() => 48,
            "merchant_stock" or "stock" or "trade" when IsTradeTrigger(trigger) => 44,
            "service" or "folk_magic" or "folk_magic_service" when IsServiceTrigger(trigger) => 44,
            "memory" when trigger == "talk" => 18,
            "escape_route" or "route" when trigger is "open" or "inspect" or "read" => 16,
            "item" when trigger is "inspect" or "read" or "talk" => 14,
            "quest" or "site" or "town" or "landmark" when trigger is "talk" or "read" or "inspect" => 12,
            "threat" when trigger is "open" or "talk" or "read" or "inspect" => 10,
            _ => 3,
        };

        if (anchor.Has<DoorComponent>() && trigger == "open")
        {
            score += kind == "door_rule" ? 24 : 8;
        }

        if (anchor.Has<MerchantComponent>() && IsTradeTrigger(trigger))
        {
            score += kind is "merchant_stock" or "stock" or "trade" ? 10 : 2;
        }

        if (anchor.Has<ServiceComponent>() && IsServiceTrigger(trigger))
        {
            score += kind is "service" or "folk_magic" or "folk_magic_service" ? 10 : 2;
        }

        score += Math.Min(10, Math.Max(0, promise.Stacks - 1) * 3);
        return score;
    }

    private static Dictionary<string, object?> ConsequenceDetails(
        WorldConsequence consequence,
        params (string Key, object? Value)[] fields)
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

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private bool IsTravelPromise(WorldPromise promise, PromiseRealizationContext context)
    {
        if (!promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            || !PromiseTriggerMatches(promise.TriggerHint, "travel"))
        {
            return false;
        }

        if (!PromiseTravelContextMatches(promise, context))
        {
            return false;
        }

        if (IsThreatKind(promise) && IsThreatRealizationOnCooldown())
        {
            return false;
        }

        return IsTravelBuildableKind(promise);
    }

    private static bool IsThreatKind(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind) == "threat";

    private bool IsThreatRealizationOnCooldown() =>
        _state.WorldTurns.HasRecent(
            ThreatRealizationCooldownKind,
            ThreatRealizationCooldownSourceId,
            _state.Turn,
            ThreatRealizationCooldownTurns);

    private void RecordThreatRealizationCooldown(WorldPromise promise, string trigger, List<StateDelta> deltas) =>
        deltas.AddRange(ApplyConsequence(WorldConsequence.RecordWorldTurn(
            $"promise:{promise.Id}:{trigger}",
            "threat_realized",
            ThreatRealizationCooldownKind,
            ThreatRealizationCooldownSourceId,
            $"A threat promise realized: {promise.Id}.",
            operation: "threatRealizationCooldown",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = trigger,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            })).Deltas);

    private bool IsAmbientPromise(WorldPromise promise, PromiseRealizationContext context)
    {
        if (!promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(promise.TriggerHint)
            || !AmbientTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            return false;
        }

        return IsAmbientBuildableKind(promise);
    }

    private static bool IsAnchoredPromise(WorldPromise promise, PromiseRealizationContext context)
    {
        if (promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
            || promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
            || !PromiseTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            return false;
        }

        return NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "memory" or "quest" or "prophecy" or "omen" or "event" or "item" or "person" or "threat" or "debt" or "merchant_stock" or "stock" or "trade" or "service" or "folk_magic" or "folk_magic_service" or "escape_route" or "route" or "door_rule" or "site" or "town" or "landmark";
    }

    private static bool IsTravelBuildableKind(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "site" or "quest" or "prophecy" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "service" or "folk_magic" or "folk_magic_service" or "escape_route" or "route" or "door_rule";

    private static bool IsAmbientBuildableKind(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "memory" or "quest" or "prophecy" or "omen" or "event" or "item" or "person" or "threat" or "debt" or "merchant_stock" or "stock" or "trade" or "service" or "folk_magic" or "folk_magic_service" or "escape_route" or "route" or "door_rule";

    private static string TravelHandlerFor(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "item" => "item",
            "person" => "person",
            "threat" => "threat",
            "merchant_stock" or "stock" or "trade" => "merchant_stock",
            "service" or "folk_magic" or "folk_magic_service" => "service",
            "escape_route" or "route" or "door_rule" => "escape_route",
            _ => "site",
        };

    private static string AnchoredHandlerFor(WorldPromise promise, Entity anchor, string trigger)
    {
        var kind = NormalizeToken(promise.RealizationKind ?? promise.Kind);
        return kind switch
        {
            "memory" => "memory",
            "threat" or "debt" => "threat",
            "item" => "item",
            "merchant_stock" or "stock" or "trade" => "merchant_stock",
            "service" or "folk_magic" or "folk_magic_service" => "service",
            "door_rule" when anchor.Has<DoorComponent>() && NormalizeToken(trigger) == "open" => "door_rule",
            "escape_route" or "route" or "door_rule" => "escape_route",
            "quest" => "quest",
            "site" or "town" or "landmark" => "site",
            _ => "omen",
        };
    }

    private string? TravelEligibilityFailure(WorldPromise promise, PromiseRealizationContext context)
    {
        if (!IsTravelBuildableKind(promise))
        {
            return "unsupported_realization_kind";
        }

        if (IsThreatKind(promise) && IsThreatRealizationOnCooldown())
        {
            return "threat_realization_cooldown";
        }

        if (PromiseHardTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection
            && !promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase))
        {
            return "direction_mismatch";
        }

        if (SpecificPlaceMismatch(promise.ClaimedPlace, context))
        {
            return "zone_mismatch";
        }

        return null;
    }

    private bool PromiseTravelContextMatches(WorldPromise promise, PromiseRealizationContext context)
    {
        if (PromiseHardTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection
            && !promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SpecificPlaceMismatch(promise.ClaimedPlace, context))
        {
            return false;
        }

        return true;
    }

    private static bool SpecificPlaceMismatch(string? place, PromiseRealizationContext context)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return false;
        }

        // Interior zone ids are as specific as grid zone ids: a promise deferred behind a
        // threshold must only realize inside that exact interior.
        if (LooksLikeZoneId(place) || place.TrimStart().StartsWith("interior:", StringComparison.OrdinalIgnoreCase))
        {
            return !string.Equals(place.Trim(), context.ZoneId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool PlaceMatchesContext(string? place, PromiseRealizationContext context)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return false;
        }

        return (LooksLikeZoneId(place)
                && string.Equals(place.Trim(), context.ZoneId, StringComparison.OrdinalIgnoreCase))
            || NormalizeToken(place).Equals(NormalizeToken(context.RegionId), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeZoneId(string place)
    {
        var parts = place.Trim().Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out _)
            && int.TryParse(parts[1], out _);
    }

    private static Direction? PromiseTravelDirection(WorldPromise promise)
    {
        var text = $"{promise.ClaimedPlace} {promise.Text} {promise.Subject}";
        var directions = new[]
        {
            (Direction.North, new[] { "north", "northern", "northward" }),
            (Direction.South, new[] { "south", "southern", "southward" }),
            (Direction.East, new[] { "east", "eastern", "eastward" }),
            (Direction.West, new[] { "west", "western", "westward" }),
        };
        var matches = directions
            .Where(pair => pair.Item2.Any(word => ContainsWord(text, word)))
            .Select(pair => pair.Item1)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Direction? PromiseHardTravelDirection(WorldPromise promise)
    {
        var text = $"{promise.ClaimedPlace} {promise.Text}";
        var directions = new[]
        {
            (Direction.North, new[] { "north of here", "north from here", "to the north", "toward the north", "northward" }),
            (Direction.South, new[] { "south of here", "south from here", "to the south", "toward the south", "southward" }),
            (Direction.East, new[] { "east of here", "east from here", "to the east", "toward the east", "eastward" }),
            (Direction.West, new[] { "west of here", "west from here", "to the west", "toward the west", "westward" }),
        };
        var matches = directions
            .Where(pair => pair.Item2.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Item1)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ContainsWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index = text.IndexOf(word, index + word.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Trigger words that should be treated as fully interchangeable, in both directions. A
    /// pairwise "if trigger is X, hint Y also matches" table silently drifts asymmetric (a
    /// promise hinted "sell" never fired on a "buy" trigger, because the "buy" row's allowed
    /// hints never listed "sell" as its own trigger's synonym, and vice versa); one symmetric
    /// group per concept fixes that structurally, since group membership is checked without
    /// regard to which side is the trigger and which is the hint.
    /// </summary>
    private static readonly string[] WaitSynonymGroup =
        { "wait", "rest", "linger", "delay", "time", "turn", "bellfall", "nightfall" };

    private static readonly string[][] TriggerSynonymGroups =
    {
        new[] { "open", "door", "opened", "unlock" },
        new[] { "talk", "speak", "name", "dialogue" },
        new[] { "read", "notice", "sign", "book" },
        new[] { "inspect", "examine", "look", "fixture" },
        new[] { "trade", "buy", "sell", "wares", "merchant", "market", "stock" },
        new[]
        {
            "services", "request", "service", "offer", "folk_magic",
            "door", "lock", "ward", "mend", "heal", "guide",
        },
        WaitSynonymGroup,
    };

    private static IEnumerable<string> SplitHints(string triggerHint) =>
        triggerHint.ToLowerInvariant()
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool PromiseTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        return SplitHints(triggerHint).Any(hint =>
            hint == normalizedTrigger
            || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase)
            || TriggerSynonymGroups.Any(group => group.Contains(normalizedTrigger) && group.Contains(hint)));
    }

    private static bool AmbientTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return false;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        return SplitHints(triggerHint).Any(hint =>
            hint == normalizedTrigger
            || (WaitSynonymGroup.Contains(normalizedTrigger) && WaitSynonymGroup.Contains(hint)));
    }

    private static bool TriggerHintHasExactMatch(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return false;
        }

        return SplitHints(triggerHint).Any(hint => hint.Equals(trigger, StringComparison.OrdinalIgnoreCase));
    }

    private static string PromiseContextLabel(PromiseRealizationContext context)
    {
        var parts = new List<string>
        {
            $"trigger={NormalizeToken(context.Trigger)}",
            $"region={NormalizeToken(context.RegionId)}",
        };
        if (!string.IsNullOrWhiteSpace(context.ZoneId))
        {
            parts.Add($"zone={context.ZoneId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(context.Direction))
        {
            parts.Add($"direction={NormalizeToken(context.Direction)}");
        }

        if (!string.IsNullOrWhiteSpace(context.AnchorEntityId))
        {
            parts.Add($"anchor={context.AnchorEntityId.Trim()}");
        }

        return string.Join(";", parts);
    }

    private static bool IsTradeTrigger(string trigger) =>
        trigger is "trade" or "buy" or "sell" or "wares";

    private static bool IsServiceTrigger(string trigger) =>
        trigger is "service" or "services" or "request";

    private static IReadOnlyList<string> PromiseTags(
        WorldPromise promise,
        string realization,
        RegionDefinition region) =>
        BasicPromiseTags(promise, realization)
            .Concat(region.TerrainTags)
            .Concat(region.VoiceTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    // The source claim's tags carry the promise's semantics ("waystation", "reaping",
    // "free_folk"), which realized content needs for affordances such as siteTag interior
    // bindings. The promise record itself stays thin; the claim ledger is the tag owner.
    private IReadOnlyList<string> PromiseTagsWithClaim(WorldPromise promise, string realization, RegionDefinition region)
    {
        var claimTags = string.IsNullOrWhiteSpace(promise.SourceClaimId)
            ? Array.Empty<string>()
            : _state.Claims.Records
                .FirstOrDefault(claim => claim.Id.Equals(promise.SourceClaimId, StringComparison.OrdinalIgnoreCase))
                ?.Tags?.ToArray() ?? Array.Empty<string>();
        return PromiseTags(promise, realization, region)
            .Concat(claimTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BasicPromiseTags(WorldPromise promise, string realization) =>
        new[] { "promise", realization, NormalizeToken(promise.Kind), NormalizeToken(promise.RealizationKind ?? realization) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string PromiseWantId(WorldPromise promise, string role) =>
        $"want_{NormalizeToken(promise.Id)}_{NormalizeToken(role)}";

    private static IReadOnlyList<string> PromiseWantTags(WorldPromise promise, string role) =>
        BasicPromiseTags(promise, role)
            .Concat(new[] { "want", "promise_source", "generated_npc" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private GridPoint FindGeneratedOpenPointNear(
        IReadOnlyDictionary<EntityId, Entity> entities,
        GridPoint origin,
        int dx,
        int dy)
    {
        var preferred = new GridPoint(
            Math.Clamp(origin.X + dx, 1, _state.Width - 2),
            Math.Clamp(origin.Y + dy, 1, _state.Height - 2));
        return FindGeneratedOpenPoint(entities, preferred);
    }

    private bool HasGeneratedOpenPointNear(
        IReadOnlyDictionary<EntityId, Entity> entities,
        GridPoint origin,
        int dx,
        int dy)
    {
        var preferred = new GridPoint(
            Math.Clamp(origin.X + dx, 1, _state.Width - 2),
            Math.Clamp(origin.Y + dy, 1, _state.Height - 2));
        return FindOpenNear(preferred, OccupiedPoints(entities.Values)) is not null;
    }

    private GridPoint FindGeneratedOpenPoint(IReadOnlyDictionary<EntityId, Entity> entities, GridPoint origin) =>
        FindOpenNear(origin, OccupiedPoints(entities.Values)) ?? origin;

    // A door-anchored promise whose payoff does not itself open the door (threat, item) should
    // only realize once the door has actually opened, not merely because someone attempted to
    // open it. Door-affecting handlers such as door_rule are exempt: their entire purpose is to
    // decide whether the door opens, so they must run before the lock check.
    private static bool AnchorDoorIsOpenOrNotADoor(Entity anchor) =>
        !anchor.TryGet<DoorComponent>(out var doorComponent) || doorComponent.IsOpen;

    private string? PreflightOpenAdjacentPayoffAnchor(Entity anchor)
    {
        if (!AnchorDoorIsOpenOrNotADoor(anchor))
        {
            return "anchor_door_not_open";
        }

        return HasOpenAdjacentPayoffTile(anchor) ? null : "no_open_adjacent_tile";
    }

    private bool HasOpenAdjacentPayoffTile(Entity anchor)
    {
        if (anchor.TryGet<PositionComponent>(out var anchorPosition)
            && FindOpenAdjacent(anchorPosition.Position) is not null)
        {
            return true;
        }

        return _state.ControlledEntity.TryGet<PositionComponent>(out var controlledPosition)
            && FindOpenAdjacent(controlledPosition.Position) is not null;
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        foreach (var offset in new[]
        {
            new GridPoint(0, -1),
            new GridPoint(1, 0),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, -1),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(-1, -1),
        })
        {
            var candidate = origin.Translate(offset.X, offset.Y);
            if (CanEnter(candidate, OccupiedPoints(_state.Entities.Values)))
            {
                return candidate;
            }
        }

        return null;
    }

    private GridPoint? FindOpenNear(GridPoint origin, HashSet<GridPoint> occupied)
    {
        foreach (var offset in new[]
        {
            new GridPoint(0, 0),
            new GridPoint(0, -1),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, 0),
            new GridPoint(-1, -1),
            new GridPoint(1, -1),
            new GridPoint(-1, 1),
            new GridPoint(1, 1),
        })
        {
            var point = origin.Translate(offset.X, offset.Y);
            if (CanEnter(point, occupied))
            {
                return point;
            }
        }

        for (var radius = 2; radius <= 5; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    var point = origin.Translate(dx, dy);
                    if (CanEnter(point, occupied))
                    {
                        return point;
                    }
                }
            }
        }

        return null;
    }

    private bool CanEnter(GridPoint point, HashSet<GridPoint> occupied) =>
        point.X > 0
        && point.Y > 0
        && point.X < _state.Width - 1
        && point.Y < _state.Height - 1
        && !_state.BlockingTerrain.Contains(point)
        && !occupied.Contains(point);

    private static HashSet<GridPoint> OccupiedPoints(IEnumerable<Entity> entities) =>
        entities
            .Where(entity => entity.TryGet<PositionComponent>(out _)
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement
                && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive))
            .Select(entity => entity.Get<PositionComponent>().Position)
            .ToHashSet();

    private static string PromiseSiteName(WorldPromise promise, RegionDefinition region)
    {
        // A site is named for what it is, not where it was said to be: "imperial relay
        // waystation", not "west along the measured road" (and never a raw zone id).
        if (!string.IsNullOrWhiteSpace(promise.Subject))
        {
            return promise.Subject;
        }

        if (!string.IsNullOrWhiteSpace(promise.ClaimedPlace)
            && !promise.ClaimedPlace.Equals(region.Id, StringComparison.OrdinalIgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(promise.ClaimedPlace, @"^-?\d+,-?\d+$"))
        {
            return promise.ClaimedPlace;
        }

        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("refuge"))
        {
            return lower.Contains("hollowmere") ? "Hollowmere refuge" : "promised refuge";
        }

        return region.Id switch
        {
            "hollowmere_margin" => "folded-road checkpoint",
            "wild_border" => "promise-touched border stone",
            _ => "promised waymark",
        };
    }

    private static string PromiseItemName(WorldPromise promise)
    {
        var lower = $"{promise.Subject} {promise.Text}".ToLowerInvariant();
        if (lower.Contains("blade") || lower.Contains("knife") || lower.Contains("sword"))
        {
            return "promised blade";
        }

        if (lower.Contains("key"))
        {
            return "promised key";
        }

        if (lower.Contains("pearl"))
        {
            return "promised pearl";
        }

        if (lower.Contains("red tincture"))
        {
            return "red tincture";
        }

        if (lower.Contains("tincture"))
        {
            return "promised tincture";
        }

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return "promise token";
    }

    private static string PromiseCommerceSubject(WorldPromise promise)
    {
        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        var itemName = PromiseItemName(promise);
        if (!itemName.Equals("promise token", StringComparison.OrdinalIgnoreCase))
        {
            return itemName;
        }

        if (ExtractAfterWord(promise.Text, "buy") is { } boughtThing)
        {
            return boughtThing;
        }

        return "promised goods";
    }

    private static string? ExtractAfterWord(string text, string word)
    {
        var marker = $"{word} ";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = text[(index + marker.Length)..];
        foreach (var stop in new[] { " if ", " when ", " from ", " for ", " to ", ".", ",", ";" })
        {
            var stopIndex = value.IndexOf(stop, StringComparison.OrdinalIgnoreCase);
            if (stopIndex >= 0)
            {
                value = value[..stopIndex];
            }
        }

        value = value.Trim().Trim('"', '\'', ':', '-', ' ');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string PromisePersonName(WorldPromise promise)
    {
        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return promise.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase)
            ? "Nannerl"
            : "promised stranger";
    }

    private static string PromiseMerchantName(WorldPromise promise)
    {
        var text = promise.Text.Trim();
        if (text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase))
        {
            return "Jimmer";
        }

        foreach (var phrase in new[] { " can sell", " sells", " trades", " offers" })
        {
            var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                var name = text[..index].Trim(' ', '.', ',', ';', ':', '"', '\'');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return "promised merchant";
    }

    // Threat naming/faction/stats now live in ThreatArchetypeGenerator (see
    // ResolveThreatArchetype below) rather than as separate helpers called independently at
    // the travel and anchored realization call sites -- that duplication was the actual bug
    // behind both the volume and blandness complaints this system was rebuilt to fix.

    private ThreatArchetype ResolveThreatArchetype(WorldPromise promise, RegionDefinition region) =>
        ThreatArchetypeGenerator.Generate(promise, region, ResolveRealm(region), _state.Rng);

    private RegionDefinition ResolveRegion(string regionId) =>
        _regions.Region(regionId) ?? _regions.Region("imperial_encounter")!;

    private RealmProfile ResolveRealm(RegionDefinition region) =>
        WorldRoll.Create(_state.Seed).RealmFor(region.RealmId);

    private static string PromiseServiceName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward"))
        {
            return "ward-breaking";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape"))
        {
            return "hidden-route finding";
        }

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return "quiet folk-magic service";
    }

    private static string PromiseServiceProviderName(WorldPromise promise)
    {
        var text = promise.Text.Trim();
        foreach (var phrase in new[] { " can ", " offers ", " knows ", " keeps " })
        {
            var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                var name = text[..index].Trim(' ', '.', ',', ';', ':', '"', '\'');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return "promised service keeper";
    }

    private static string PromiseServiceEffect(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward") || lower.Contains("key"))
        {
            return "open_or_unlock";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape") || lower.Contains("passage"))
        {
            return "create_route";
        }

        return "record_memory";
    }

    private static string? PromiseServiceItemCost(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("grave salt"))
        {
            return "grave salt";
        }

        if (lower.Contains("moon pearl"))
        {
            return "moon pearl";
        }

        return null;
    }

    private static string PromiseServiceTargetHint(WorldPromise promise, string serviceName)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("cell door"))
        {
            return "cell door";
        }

        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward"))
        {
            return "door";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape") || lower.Contains("passage"))
        {
            return serviceName;
        }

        return serviceName;
    }

    private static string PromiseRouteName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("drain"))
        {
            return "imperial drainage route";
        }

        if (lower.Contains("tunnel"))
        {
            return "hidden tunnel";
        }

        if (lower.Contains("grate"))
        {
            return "concealed grate";
        }

        if (lower.Contains("refuge"))
        {
            return lower.Contains("hollowmere") ? "path to Hollowmere refuge" : "refuge path";
        }

        if (lower.Contains("oak"))
        {
            return "burned oak road";
        }

        if (lower.Contains("road"))
        {
            return "hidden road";
        }

        if (lower.Contains("passage"))
        {
            return "secret passage";
        }

        if (lower.Contains("path"))
        {
            return "hidden path";
        }

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return lower.Contains("route") ? "concealed route" : "promised hidden route";
    }

    private static string? UsefulSubject(WorldPromise promise)
    {
        if (string.IsNullOrWhiteSpace(promise.Subject)
            || promise.Subject.Equals(promise.Kind, StringComparison.OrdinalIgnoreCase)
            || LooksTechnicalSubject(promise.Subject))
        {
            return null;
        }

        return promise.Subject;
    }

    private static bool LooksTechnicalSubject(string subject)
    {
        var normalized = subject.Trim().ToLowerInvariant();
        return normalized.Equals("player", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("_soul", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("promise_", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains('_', StringComparison.Ordinal);
    }

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }
}
