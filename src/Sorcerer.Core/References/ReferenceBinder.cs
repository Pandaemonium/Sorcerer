using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.References;

public static class ReferenceBinder
{
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
            TileReference => BoundReference.Failure(reference, "Tile is outside the current map."),
            SelectorReference selector => BindSelector(engine, reference, selector.Selector),
            FactionReference faction => BindFaction(engine, reference, faction.FactionId),
            _ => BoundReference.Failure(reference, "Unknown reference shape."),
        };

    private static BoundReference BindEntity(GameEngine engine, GameReference reference, string id)
    {
        var entity = engine.EntityById(id);
        if (entity is null)
        {
            return BoundReference.Failure(reference, $"No entity with id {id}.");
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
                    ? BoundReference.Failure(reference, "No hostile target is visible.")
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
                    : BoundReference.Failure(reference, "No target is selected.");
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
                    ? BoundReference.Failure(reference, "No hostile group is visible.")
                    : new BoundReference(reference, null, null, group, null);
            }
            default:
                return BoundReference.Failure(reference, $"Unsupported selector {selector}.");
        }
    }

    private static BoundReference BindFaction(GameEngine engine, GameReference reference, string factionId)
    {
        var group = engine.State.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Faction == factionId)
            .ToArray();
        return group.Length == 0
            ? BoundReference.Failure(reference, $"No entities currently represent faction {factionId}.")
            : new BoundReference(reference, null, null, group, null);
    }
}
