using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Transactions;

public sealed record GameStateSnapshot(
    int Turn,
    EntityId ControlledEntityId,
    GridPoint? SelectedTarget,
    int Seed,
    ulong RngState,
    int NextEntitySerial,
    string RegionId,
    string CurrentZoneId,
    string RunStatus,
    string? RunConclusion,
    GridPoint? LastControlledMoveDelta,
    IReadOnlyDictionary<EntityId, Entity> Entities,
    IReadOnlyDictionary<string, ZoneSnapshot> Zones,
    IReadOnlySet<GridPoint> BlockingTerrain,
    IReadOnlyDictionary<GridPoint, string> Terrain,
    IReadOnlyDictionary<GridPoint, int> TerrainExpirations,
    IReadOnlyDictionary<GridPoint, TileFlow> TileFlows,
    IReadOnlyDictionary<string, IReadOnlySet<GridPoint>> ExploredBySoulId,
    IReadOnlyList<string> Messages,
    IReadOnlyList<SoulRecord> Souls,
    IReadOnlyList<DeedRecord> Deeds,
    IReadOnlyList<string> AppliedDeedIds,
    IReadOnlyList<FactionRecord> Factions,
    IReadOnlyList<LegendTag> LegendTags,
    IReadOnlyList<WorldMemoryRecord> Memories,
    IReadOnlyList<ClaimRecord> Claims,
    IReadOnlyList<RumorRecord> Rumors,
    IReadOnlyList<WorldTurnRecord> WorldTurns,
    IReadOnlyList<WorldPromise> Promises,
    IReadOnlyList<ScheduledEventRecord> ScheduledEvents,
    IReadOnlyList<TriggerRecord> Triggers,
    IReadOnlyList<SuspicionRecord> Suspicions,
    IReadOnlyList<CanonRecord> CanonRecords,
    IReadOnlyList<BondRecord> Bonds,
    IReadOnlyList<PersistentEffectRecord> PersistentEffects,
    BackgroundJobSettings BackgroundSettings,
    IReadOnlyDictionary<string, object?> WorldFlags,
    IReadOnlyList<BackgroundJob> BackgroundJobs)
{
    public static GameStateSnapshot Capture(GameState state) =>
        new(
            state.Turn,
            state.ControlledEntityId,
            state.SelectedTarget,
            state.Seed,
            state.Rng.State,
            state.NextEntitySerial,
            state.RegionId,
            state.CurrentZoneId,
            state.RunStatus,
            state.RunConclusion,
            state.LastControlledMoveDelta,
            state.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            state.Zones.ToDictionary(pair => pair.Key, pair => CloneZone(pair.Value), StringComparer.OrdinalIgnoreCase),
            new HashSet<GridPoint>(state.BlockingTerrain),
            new Dictionary<GridPoint, string>(state.Terrain),
            new Dictionary<GridPoint, int>(state.TerrainExpirations),
            new Dictionary<GridPoint, TileFlow>(state.TileFlows),
            state.ExploredBySoulId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<GridPoint>)new HashSet<GridPoint>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            state.Messages.ToArray(),
            state.Souls.Snapshot(),
            state.Deeds.Records.ToArray(),
            state.Deeds.AppliedSnapshot(),
            state.Factions.Snapshot(),
            state.Legend.Snapshot(),
            state.Memories.Snapshot(),
            state.Claims.Snapshot(),
            state.Rumors.Snapshot(),
            state.WorldTurns.Snapshot(),
            state.PromiseLedger.Snapshot(),
            state.ScheduledEvents.Snapshot(),
            state.Triggers.Snapshot(),
            state.Suspicions.Snapshot(),
            state.Canon.Snapshot(),
            state.Bonds.Snapshot(),
            state.PersistentEffects.Snapshot(),
            state.BackgroundSettings,
            CloneMap(state.WorldFlags),
            state.BackgroundJobs.Snapshot());

    public void Restore(GameState state)
    {
        state.Turn = Turn;
        state.ControlledEntityId = ControlledEntityId;
        state.SelectedTarget = SelectedTarget;
        state.Seed = Seed;
        state.Rng = new DeterministicRng(RngState);
        state.NextEntitySerial = NextEntitySerial;
        state.RegionId = RegionId;
        state.CurrentZoneId = CurrentZoneId;
        state.RunStatus = RunStatus;
        state.RunConclusion = RunConclusion;
        state.LastControlledMoveDelta = LastControlledMoveDelta;
        state.BackgroundSettings = BackgroundSettings;

        state.Entities.Clear();
        foreach (var pair in Entities)
        {
            state.Entities[pair.Key] = pair.Value.Clone();
        }

        state.Zones.Clear();
        foreach (var pair in Zones)
        {
            state.Zones[pair.Key] = CloneZone(pair.Value);
        }

        state.BlockingTerrain.Clear();
        foreach (var tile in BlockingTerrain)
        {
            state.BlockingTerrain.Add(tile);
        }

        state.Terrain.Clear();
        foreach (var pair in Terrain)
        {
            state.Terrain[pair.Key] = pair.Value;
        }

        state.TerrainExpirations.Clear();
        foreach (var pair in TerrainExpirations)
        {
            state.TerrainExpirations[pair.Key] = pair.Value;
        }

        state.TileFlows.Clear();
        foreach (var pair in TileFlows)
        {
            state.TileFlows[pair.Key] = pair.Value;
        }

        state.ExploredBySoulId.Clear();
        foreach (var pair in ExploredBySoulId)
        {
            state.ExploredBySoulId[pair.Key] = new HashSet<GridPoint>(pair.Value);
        }

        state.Messages.Clear();
        state.Messages.AddRange(Messages);
        state.Souls.ReplaceAll(Souls);
        state.Deeds.ReplaceAll(Deeds, AppliedDeedIds);
        state.Factions.ReplaceAll(Factions);
        state.Legend.ReplaceAll(LegendTags);
        state.Memories.ReplaceAll(Memories);
        state.Claims.ReplaceAll(Claims);
        state.Rumors.ReplaceAll(Rumors);
        state.WorldTurns.ReplaceAll(WorldTurns);
        state.PromiseLedger.ReplaceAll(Promises);
        state.ScheduledEvents.ReplaceAll(ScheduledEvents);
        state.Triggers.ReplaceAll(Triggers);
        state.Suspicions.ReplaceAll(Suspicions);
        state.Canon.ReplaceAll(CanonRecords);
        state.Bonds.ReplaceAll(Bonds);
        state.PersistentEffects.ReplaceAll(PersistentEffects);
        state.WorldFlags.Clear();
        foreach (var pair in WorldFlags)
        {
            state.WorldFlags[pair.Key] = CloneValue(pair.Value);
        }

        state.BackgroundJobs.ReplaceAll(BackgroundJobs);
    }

    private static ZoneSnapshot CloneZone(ZoneSnapshot zone) =>
        zone with
        {
            Entities = zone.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            BlockingTerrain = new HashSet<GridPoint>(zone.BlockingTerrain),
            Terrain = new Dictionary<GridPoint, string>(zone.Terrain),
            TerrainExpirations = new Dictionary<GridPoint, int>(zone.TerrainExpirations),
            TileFlows = new Dictionary<GridPoint, TileFlow>(zone.TileFlows),
            ExploredBySoulId = zone.ExploredBySoulId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<GridPoint>)new HashSet<GridPoint>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            RoomProfiles = zone.RoomProfiles.ToArray(),
            PromiseHooks = zone.PromiseHooks.ToArray(),
        };

    private static Dictionary<string, object?> CloneMap(IReadOnlyDictionary<string, object?> source) =>
        source
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                pair => pair.Key,
                pair => CloneValue(pair.Value),
                StringComparer.OrdinalIgnoreCase);

    private static object? CloneValue(object? value) =>
        value switch
        {
            IReadOnlyDictionary<string, object?> readOnlyMap => CloneMap(readOnlyMap),
            IDictionary<string, object?> map => CloneMap(new Dictionary<string, object?>(map, StringComparer.OrdinalIgnoreCase)),
            IEnumerable<object?> values when value is not string => values.Select(CloneValue).ToArray(),
            _ => value,
        };
}

public sealed class GameTransaction
{
    private readonly GameState _state;
    private readonly GameStateSnapshot _snapshot;
    private bool _committed;

    private GameTransaction(GameState state)
    {
        _state = state;
        _snapshot = GameStateSnapshot.Capture(state);
    }

    public static GameTransaction Begin(GameState state) => new(state);

    public void Commit() => _committed = true;

    public void Rollback()
    {
        if (!_committed)
        {
            _snapshot.Restore(_state);
            _committed = true;
        }
    }
}
