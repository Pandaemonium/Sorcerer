using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
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

    public IReadOnlyList<StateDelta> ApplyTurnReactions()
    {
        var deltas = new List<StateDelta>();
        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .OrderBy(entity => entity.Id.Value))
        {
            var point = entity.Get<PositionComponent>().Position;
            var terrain = _state.Terrain.TryGetValue(point, out var ground) ? ground : "dry_floor";

            deltas.AddRange(ApplyEntityTerrainReaction(entity, point, terrain));
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyEntityTerrainReaction(Entity entity, GridPoint point, string terrain)
    {
        var deltas = new List<StateDelta>();
        var normalized = terrain.Trim().ToLowerInvariant();
        if (HasActiveCostProfile(entity, "curse_tide_debt_body"))
        {
            var tide = IsWater(normalized)
                ? _engine.ApplyConsequence(WorldConsequence.Heal(
                    "borrowed_tide",
                    entity.Id.Value,
                    1,
                    visibility: WorldConsequenceVisibility.Message,
                    sourceEntityId: entity.Id.Value,
                    evidence: terrain,
                    reason: "Borrowed Tide closes wounds on wet ground.",
                    operation: "borrowedTideWet"))
                : _state.Turn % 2 == 0
                    ? _engine.ApplyConsequence(WorldConsequence.Damage(
                        "borrowed_tide",
                        entity.Id.Value,
                        1,
                        "dryness",
                        visibility: WorldConsequenceVisibility.Message,
                        sourceEntityId: entity.Id.Value,
                        evidence: terrain,
                        reason: "Borrowed Tide reopens wounds on dry ground every other turn.",
                        operation: "borrowedTideDry"))
                    : null;
            if (tide is not null)
            {
                deltas.AddRange(tide.Deltas);
            }
        }
        if (IsWater(normalized) && HasActiveStatus(entity, "burning"))
        {
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.RemoveStatus(
                "terrain_reaction",
                entity.Id.Value,
                "burning",
                sourceEntityId: entity.Id.Value,
                reason: "Water terrain extinguished burning.",
                operation: "terrainRemoveStatus",
                details: TerrainDetails(point, terrain, "water_extinguish"))).Deltas);
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.SetTerrain(
                "terrain_reaction",
                point.X,
                point.Y,
                "steam_mist",
                duration: 3,
                sourceEntityId: entity.Id.Value,
                reason: "Water terrain became steam mist after extinguishing burning.",
                operation: "terrainSetTerrain",
                details: TerrainDetails(point, terrain, "water_extinguish"))).Deltas);
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.Message(
                "terrain_reaction",
                $"{Subject(entity)} {Verb(entity, "hiss", "hisses")} in the sudden mist.",
                targetEntityId: entity.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: entity.Id.Value,
                reason: "Terrain reaction narration.",
                operation: "terrainMessage",
                details: TerrainDetails(point, terrain, "water_extinguish"))).Deltas);
            return deltas;
        }

        if (IsFire(normalized) && !HasActiveStatus(entity, "burning"))
        {
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.ApplyStatus(
                "terrain_reaction",
                entity.Id.Value,
                "burning",
                duration: 3,
                sourceEntityId: entity.Id.Value,
                reason: "Fire terrain ignited an actor.",
                operation: "terrainApplyStatus",
                details: TerrainDetails(point, terrain, "fire_ignite"))).Deltas);
            return deltas;
        }

        if (IsVines(normalized) && !HasActiveStatus(entity, "rooted"))
        {
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.ApplyStatus(
                "terrain_reaction",
                entity.Id.Value,
                "snared",
                duration: 2,
                displayName: "vine-snared",
                sourceEntityId: entity.Id.Value,
                reason: "Vine terrain snared an actor.",
                operation: "terrainApplyStatus",
                details: TerrainDetails(point, terrain, "vine_snare"))).Deltas);
        }

        return deltas;
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

    private bool HasActiveCostProfile(Entity entity, string profileId) =>
        _state.PromiseLedger.Promises.Any(promise =>
            promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase)
            && promise.Status is not "cleared" and not "fulfilled"
            && promise.CostProfileId?.Equals(profileId, StringComparison.OrdinalIgnoreCase) == true
            && (string.IsNullOrWhiteSpace(promise.BoundTargetId)
                || promise.BoundTargetId.Equals(entity.Id.Value, StringComparison.OrdinalIgnoreCase)));

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

    private static IReadOnlyDictionary<string, object?> TerrainDetails(GridPoint point, string terrain, string reaction) =>
        new Dictionary<string, object?>
        {
            ["terrain"] = terrain,
            ["x"] = point.X,
            ["y"] = point.Y,
            ["reaction"] = reaction,
        };
}
