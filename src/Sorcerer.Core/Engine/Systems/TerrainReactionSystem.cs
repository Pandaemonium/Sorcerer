using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class TerrainReactionSystem
{
    private readonly GameEngine _engine;
    private readonly GameState _state;

    public TerrainReactionSystem(GameEngine engine)
    {
        _engine = engine;
        _state = engine.State;
    }

    public void ApplyTurnReactions()
    {
        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .OrderBy(entity => entity.Id.Value))
        {
            var point = entity.Get<PositionComponent>().Position;
            if (!_state.Terrain.TryGetValue(point, out var terrain))
            {
                continue;
            }

            ApplyEntityTerrainReaction(entity, point, terrain);
        }
    }

    private void ApplyEntityTerrainReaction(Entity entity, GridPoint point, string terrain)
    {
        var normalized = terrain.Trim().ToLowerInvariant();
        if (IsWater(normalized) && HasActiveStatus(entity, "burning"))
        {
            _engine.RemoveStatus(entity, "burning");
            _engine.SetTerrain(point, "steam_mist", duration: 3);
            _state.AddMessage($"{Subject(entity)} {Verb(entity, "hiss", "hisses")} in the sudden mist.");
            return;
        }

        if (IsFire(normalized) && !HasActiveStatus(entity, "burning"))
        {
            _engine.ApplyStatus(entity, "burning", duration: 3);
            return;
        }

        if (IsVines(normalized) && !HasActiveStatus(entity, "rooted"))
        {
            _engine.ApplyStatus(entity, "snared", duration: 2, displayName: "vine-snared");
        }
    }

    private bool HasActiveStatus(Entity entity, string status)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return false;
        }

        return container.Statuses.Any(instance =>
            instance.Id.Equals(status, StringComparison.OrdinalIgnoreCase)
            && (instance.ExpiresTurn is null || instance.ExpiresTurn > _state.Turn));
    }

    private static bool IsWater(string terrain) =>
        terrain.Contains("water", StringComparison.OrdinalIgnoreCase)
        || terrain.Contains("river", StringComparison.OrdinalIgnoreCase);

    private static bool IsFire(string terrain) =>
        terrain.Contains("fire", StringComparison.OrdinalIgnoreCase)
        || terrain.Contains("flame", StringComparison.OrdinalIgnoreCase);

    private static bool IsVines(string terrain) =>
        terrain.Contains("vine", StringComparison.OrdinalIgnoreCase)
        || terrain.Contains("bramble", StringComparison.OrdinalIgnoreCase);

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;
}
