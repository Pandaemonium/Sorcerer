using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;

namespace Sorcerer.Core.References;

public static class ReferenceBinder
{
    // Shared rejection wording so a player/agent can tell "nothing was selected" apart from
    // "the named/id target does not exist or is not visible" regardless of which reference
    // shape or binder (ReferenceBinder or EngineReferenceResolver) produced the failure.
    public const string NoSelectedTargetMessage =
        "No target is selected. Choose one with 'target <x> <y>' or name something you can see.";

    // Deliberately identical whether the name/id never existed or names a real entity the
    // player cannot currently perceive - the wording must not leak which case it is.
    public static string NoVisibleTargetMessage(string name) => $"Nothing you can see answers to '{name}'.";

    private static readonly HashSet<string> Selectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "self",
        "caster",
        "here",
        "selected_target",
        "target",
        "nearest_enemy",
        "nearest_ally",
        "all_enemies",
        "all_allies",
        "all_in_radius",
        "random_enemy",
    };

    public static EntityRef NormalizeEntityRef(object? value, int? radius = null, string? filter = null)
    {
        if (value is null)
        {
            return EntityRef.Self;
        }

        if (value is IReadOnlyDictionary<string, object?> fields)
        {
            foreach (var key in new[] { "id", "entityId", "entity_id", "selector", "name", "target", "value" })
            {
                if (fields.TryGetValue(key, out var nested) && nested is not null)
                {
                    return NormalizeEntityRef(nested, radius, filter);
                }
            }

            if (TryReadPoint(fields, out var x, out var y))
            {
                return new EntityRef("point", $"{x},{y}", radius, filter);
            }

            return new EntityRef(
                "malformed",
                "Malformed target object; expected id, name, selector, target, value, or x/y.",
                radius,
                filter);
        }

        var text = Convert.ToString(value)?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return EntityRef.Self;
        }

        var selector = text.Trim().ToLowerInvariant() switch
        {
            "player" or "self" or "caster" => "self",
            "here" => "here",
            "nearest" or "enemy" or "nearest enemy" or "nearest_enemy" or "nearest foe" => "nearest_enemy",
            "ally" or "nearest ally" or "nearest_ally" => "nearest_ally",
            "target" or "selected target" or "selected_target" or "there" or "that" => "selected_target",
            "all enemies" or "all_enemies" or "enemies" => "all_enemies",
            "all allies" or "all_allies" or "allies" => "all_allies",
            "all in radius" or "all_in_radius" => "all_in_radius",
            "random enemy" or "random_enemy" => "random_enemy",
            _ => "",
        };

        if (!string.IsNullOrWhiteSpace(selector))
        {
            return new EntityRef("selector", selector, radius, filter);
        }

        return text.Contains('_') || text.Any(char.IsDigit)
            ? new EntityRef("id", text, radius, filter)
            : new EntityRef("name", text, radius, filter);
    }

    private static bool TryReadPoint(IReadOnlyDictionary<string, object?> fields, out int x, out int y)
    {
        x = 0;
        y = 0;
        return TryReadInt(fields, "x", out x)
            && TryReadInt(fields, "y", out y);
    }

    private static bool TryReadInt(IReadOnlyDictionary<string, object?> fields, string key, out int value)
    {
        value = 0;
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            raw = enumerable.Cast<object?>().FirstOrDefault();
        }

        return int.TryParse(Convert.ToString(raw), out value);
    }

    public static GameReference Normalize(object? value)
    {
        if (value is null)
        {
            return new SelectorReference("self");
        }

        var text = Convert.ToString(value)?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return new SelectorReference("self");
        }

        return text switch
        {
            "player" or "self" or "caster" => new SelectorReference("self"),
            "nearest" or "enemy" or "nearest_enemy" or "nearest foe" => new SelectorReference("nearest_enemy"),
            "target" or "selected_target" or "there" => new SelectorReference("selected_target"),
            "all_enemies" or "enemies" => new SelectorReference("all_enemies"),
            _ => new EntityReference(text),
        };
    }

    public static BoundReference Bind(GameEngine engine, GameReference reference) =>
        reference switch
        {
            EntityReference entity => BindEntity(engine, reference, entity.EntityId),
            TileReference tile when engine.InBounds(tile.Position) => new BoundReference(
                reference,
                Entity: null,
                Position: tile.Position,
                Group: Array.Empty<Entity>(),
                Error: null),
            TileReference => BoundReference.Failure(reference, "Tile is outside the current map.", FailureCode.OutOfRange),
            SelectorReference selector => BindSelector(engine, reference, selector.Selector),
            FactionReference faction => BindFaction(engine, reference, faction.FactionId),
            _ => BoundReference.Failure(reference, "Unknown reference shape.", FailureCode.Unsupported),
        };

    private static BoundReference BindEntity(GameEngine engine, GameReference reference, string id)
    {
        var entity = engine.EntityById(id);
        if (entity is null)
        {
            return BoundReference.Failure(reference, NoVisibleTargetMessage(id), FailureCode.MissingTarget);
        }

        var position = entity.TryGet<PositionComponent>(out var pos) ? pos.Position : (GridPoint?)null;
        return new BoundReference(reference, entity, position, new[] { entity }, null);
    }

    private static BoundReference BindSelector(GameEngine engine, GameReference reference, string selector)
    {
        switch (selector.Trim().ToLowerInvariant())
        {
            case "self":
                return new BoundReference(
                    reference,
                    engine.State.ControlledEntity,
                    engine.State.ControlledEntity.Get<PositionComponent>().Position,
                    new[] { engine.State.ControlledEntity },
                    null);
            case "nearest_enemy":
                {
                    var target = engine.FindNearestHostile();
                    return target is null
                        ? BoundReference.Failure(reference, "No hostile target is visible.", FailureCode.MissingTarget)
                        : new BoundReference(
                            reference,
                            target,
                            target.Get<PositionComponent>().Position,
                            new[] { target },
                            null);
                }
            case "selected_target":
                return engine.State.SelectedTarget is { } selected
                    ? new BoundReference(reference, null, selected, Array.Empty<Entity>(), null)
                    : BoundReference.Failure(reference, NoSelectedTargetMessage, FailureCode.NoSelection);
            case "all_enemies":
                {
                    var actor = engine.State.ControlledEntity;
                    var faction = actor.TryGet<ActorComponent>(out var actorStats) ? actorStats.Faction : "";
                    var group = engine.State.Entities.Values
                        .Where(entity => entity.TryGet<ActorComponent>(out var targetStats)
                            && targetStats.Alive
                            && targetStats.Faction != faction
                            && targetStats.Faction != "neutral")
                        .ToArray();
                    return group.Length == 0
                        ? BoundReference.Failure(reference, "No hostile group is visible.", FailureCode.MissingTarget)
                        : new BoundReference(reference, null, null, group, null);
                }
            default:
                return BoundReference.Failure(reference, $"Unsupported selector {selector}.", FailureCode.Unsupported);
        }
    }

    private static BoundReference BindFaction(GameEngine engine, GameReference reference, string factionId)
    {
        var group = engine.State.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Faction == factionId)
            .ToArray();
        return group.Length == 0
            ? BoundReference.Failure(reference, $"No entities currently represent faction {factionId}.", FailureCode.MissingTarget)
            : new BoundReference(reference, null, null, group, null);
    }
}

public sealed class EngineReferenceResolver : IReferenceResolver
{
    private static readonly HashSet<string> SupportedSelectors = new(StringComparer.OrdinalIgnoreCase)
    {
        "self",
        "caster",
        "here",
        "selected_target",
        "target",
        "nearest_enemy",
        "nearest_ally",
        "all_enemies",
        "all_allies",
        "all_in_radius",
        "random_enemy",
    };

    private readonly GameEngine _engine;
    private readonly Entity _caster;
    private readonly int _groupCap;
    private readonly IReadOnlyDictionary<string, Entity> _projectedEntities;

    public EngineReferenceResolver(
        GameEngine engine,
        Entity caster,
        int groupCap = 8,
        IReadOnlyDictionary<string, Entity>? projectedEntities = null)
    {
        _engine = engine;
        _caster = caster;
        _groupCap = groupCap;
        _projectedEntities = projectedEntities ?? new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
    }

    public ResolvedEntitySet Resolve(EntityRef reference) =>
        reference.Kind.Trim().ToLowerInvariant() switch
        {
            "id" => ResolveId(reference),
            "selector" => ResolveSelector(reference),
            "name" => ResolveName(reference),
            "point" => ResolvePoint(reference),
            "malformed" => ResolvedEntitySet.Failure(reference, reference.Value, FailureCode.Malformed),
            _ => ResolvedEntitySet.Failure(reference, $"Unsupported reference kind {reference.Kind}.", FailureCode.Unsupported),
        };

    private ResolvedEntitySet ResolveId(EntityRef reference)
    {
        var entity = _engine.EntityById(reference.Value);
        if (entity is null)
        {
            if (_projectedEntities.TryGetValue(reference.Value, out var projected))
            {
                return new ResolvedEntitySet(reference, new[] { projected }, PositionOf(projected), null);
            }

            return ResolvedEntitySet.Failure(reference, ReferenceBinder.NoVisibleTargetMessage(reference.Value), FailureCode.MissingTarget);
        }

        return new ResolvedEntitySet(reference, new[] { entity }, PositionOf(entity), null);
    }

    private ResolvedEntitySet ResolvePoint(EntityRef reference)
    {
        var parts = reference.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !int.TryParse(parts[0], out var x)
            || !int.TryParse(parts[1], out var y))
        {
            return ResolvedEntitySet.Failure(reference, "Malformed point target.", FailureCode.Malformed);
        }

        var point = new GridPoint(x, y);
        if (!_engine.InBounds(point))
        {
            return ResolvedEntitySet.Failure(reference, $"Target point {x},{y} is outside the encounter.", FailureCode.OutOfRange);
        }

        var occupant = _engine.State.Entities.Values
            .FirstOrDefault(entity => PositionOf(entity) == point);
        return occupant is null
            ? new ResolvedEntitySet(reference, Array.Empty<Entity>(), point, null)
            : new ResolvedEntitySet(reference, new[] { occupant }, point, null);
    }

    private ResolvedEntitySet ResolveSelector(EntityRef reference)
    {
        var selector = SupportedSelectors.Contains(reference.Value) ? reference.Value : reference.Value.ToLowerInvariant();
        return selector switch
        {
            "self" or "caster" => new ResolvedEntitySet(reference, new[] { _caster }, PositionOf(_caster), null),
            "here" => new ResolvedEntitySet(reference, new[] { _caster }, PositionOf(_caster), null),
            "selected_target" or "target" => ResolveSelectedTarget(reference),
            "nearest_enemy" => ResolveNearest(reference, hostile: true),
            "nearest_ally" => ResolveNearest(reference, hostile: false),
            "all_enemies" => ResolveGroup(reference, hostile: true),
            "all_allies" => ResolveGroup(reference, hostile: false),
            "all_in_radius" => ResolveAllInRadius(reference),
            "random_enemy" => ResolveRandomEnemy(reference),
            _ => ResolvedEntitySet.Failure(reference, $"Unsupported selector {reference.Value}.", FailureCode.Unsupported),
        };
    }

    private ResolvedEntitySet ResolveName(EntityRef reference)
    {
        var origin = PositionOf(_caster);
        var tokens = reference.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 2)
            .Select(token => token.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0)
        {
            return ResolvedEntitySet.Failure(reference, $"Name reference is too vague: {reference.Value}.", FailureCode.Malformed);
        }

        var matches = _engine.State.Entities.Values.Concat(_projectedEntities.Values)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Score = NameScore(entity, tokens),
                Distance = origin is null || PositionOf(entity) is not { } pos
                    ? 999
                    : Math.Abs(origin.Value.X - pos.X) + Math.Abs(origin.Value.Y - pos.Y),
            })
            .Where(match => match.Score > 0)
            .OrderByDescending(match => match.Score)
            .ThenBy(match => match.Distance)
            .ThenBy(match => match.Entity.Id.Value)
            .ToArray();

        if (matches.Length == 0)
        {
            return ResolvedEntitySet.Failure(reference, ReferenceBinder.NoVisibleTargetMessage(reference.Value), FailureCode.MissingTarget);
        }

        if (matches.Length > 1
            && matches[0].Score == matches[1].Score
            && matches[0].Distance == matches[1].Distance)
        {
            return ResolvedEntitySet.Failure(reference, $"Reference {reference.Value} is ambiguous.", FailureCode.AmbiguousTarget);
        }

        var entity = matches[0].Entity;
        return new ResolvedEntitySet(reference, new[] { entity }, PositionOf(entity), null);
    }

    private ResolvedEntitySet ResolveSelectedTarget(EntityRef reference)
    {
        if (_engine.State.SelectedTarget is not { } target)
        {
            return ResolvedEntitySet.Failure(reference, ReferenceBinder.NoSelectedTargetMessage, FailureCode.NoSelection);
        }

        var occupant = _engine.State.Entities.Values
            .FirstOrDefault(entity => PositionOf(entity) == target);
        return occupant is null
            ? new ResolvedEntitySet(reference, Array.Empty<Entity>(), target, null)
            : new ResolvedEntitySet(reference, new[] { occupant }, target, null);
    }

    private ResolvedEntitySet ResolveNearest(EntityRef reference, bool hostile)
    {
        var candidates = CandidateActors(hostile).ToArray();
        if (candidates.Length == 0)
        {
            return ResolvedEntitySet.Failure(reference, hostile ? "No hostile target is visible." : "No ally is visible.", FailureCode.MissingTarget);
        }

        var origin = PositionOf(_caster);
        var entity = candidates
            .OrderBy(entity => origin is null || PositionOf(entity) is not { } pos
                ? 999
                : Math.Abs(origin.Value.X - pos.X) + Math.Abs(origin.Value.Y - pos.Y))
            .ThenBy(entity => entity.Id.Value)
            .First();
        return new ResolvedEntitySet(reference, new[] { entity }, PositionOf(entity), null);
    }

    private ResolvedEntitySet ResolveGroup(EntityRef reference, bool hostile)
    {
        var group = CandidateActors(hostile).Take(_groupCap).ToArray();
        return group.Length == 0
            ? ResolvedEntitySet.Failure(reference, hostile ? "No hostile targets are visible." : "No allies are visible.", FailureCode.MissingTarget)
            : new ResolvedEntitySet(reference, group, null, null);
    }

    private ResolvedEntitySet ResolveAllInRadius(EntityRef reference)
    {
        var radius = reference.Radius ?? 2;
        var origin = _engine.State.SelectedTarget ?? PositionOf(_caster);
        if (origin is null)
        {
            return ResolvedEntitySet.Failure(reference, "No origin exists for radius targeting.", FailureCode.MissingTarget);
        }

        var group = _engine.State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out var pos)
                && Math.Abs(origin.Value.X - pos.Position.X) + Math.Abs(origin.Value.Y - pos.Position.Y) <= radius)
            .Take(_groupCap)
            .ToArray();
        return group.Length == 0
            ? ResolvedEntitySet.Failure(reference, "No entities are in the requested radius.", FailureCode.MissingTarget)
            : new ResolvedEntitySet(reference, group, origin, null);
    }

    private ResolvedEntitySet ResolveRandomEnemy(EntityRef reference)
    {
        var group = CandidateActors(hostile: true).ToArray();
        if (group.Length == 0)
        {
            return ResolvedEntitySet.Failure(reference, "No hostile target is visible.", FailureCode.MissingTarget);
        }

        var entity = group[_engine.State.Rng.NextInt(0, group.Length)];
        return new ResolvedEntitySet(reference, new[] { entity }, PositionOf(entity), null);
    }

    private IEnumerable<Entity> CandidateActors(bool hostile)
    {
        var casterFaction = _caster.TryGet<ActorComponent>(out var actor) ? actor.Faction : "";
        return _engine.State.Entities.Values
            .Where(entity => entity.Id != _caster.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var targetActor) && targetActor.Alive)
            .Where(entity => hostile
                ? _engine.IsHostile(_caster, entity)
                : entity.Get<ActorComponent>().Faction == casterFaction);
    }

    private static GridPoint? PositionOf(Entity entity) =>
        entity.TryGet<PositionComponent>(out var position) ? position.Position : null;

    private static int NameScore(Entity entity, IReadOnlyList<string> tokens)
    {
        var haystack = entity.Name.ToLowerInvariant();
        if (entity.TryGet<TagsComponent>(out var tags))
        {
            haystack += " " + string.Join(' ', tags.Tags).ToLowerInvariant();
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            haystack += " " + string.Join(' ', item.Tags).ToLowerInvariant() + " " + item.Material.ToLowerInvariant();
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            haystack += " " + string.Join(' ', fixture.Tags).ToLowerInvariant();
        }

        return tokens.Count(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
