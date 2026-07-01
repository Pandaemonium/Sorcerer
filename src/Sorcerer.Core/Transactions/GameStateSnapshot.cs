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
    IReadOnlyDictionary<EntityId, Entity> Entities,
    IReadOnlyDictionary<string, ZoneSnapshot> Zones,
    IReadOnlySet<GridPoint> BlockingTerrain,
    IReadOnlyDictionary<GridPoint, string> Terrain,
    IReadOnlyDictionary<GridPoint, int> TerrainExpirations,
    IReadOnlyDictionary<string, IReadOnlySet<GridPoint>> ExploredBySoulId,
    IReadOnlyList<string> Messages,
    IReadOnlyList<SoulRecord> Souls,
    IReadOnlyList<DeedRecord> Deeds,
    IReadOnlyList<string> AppliedDeedIds,
    IReadOnlyList<FactionRecord> Factions,
    IReadOnlyList<LegendTag> LegendTags,
    IReadOnlyList<WorldMemoryRecord> Memories,
    IReadOnlyList<WorldPromise> Promises,
    IReadOnlyList<ScheduledEventRecord> ScheduledEvents,
    IReadOnlyList<TriggerRecord> Triggers,
    IReadOnlyList<SuspicionRecord> Suspicions,
    IReadOnlyList<CanonRecord> CanonRecords,
    IReadOnlyList<BondRecord> Bonds,
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
            state.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            state.Zones.ToDictionary(pair => pair.Key, pair => CloneZone(pair.Value), StringComparer.OrdinalIgnoreCase),
            new HashSet<GridPoint>(state.BlockingTerrain),
            new Dictionary<GridPoint, string>(state.Terrain),
            new Dictionary<GridPoint, int>(state.TerrainExpirations),
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
            state.PromiseLedger.Snapshot(),
            state.ScheduledEvents.Snapshot(),
            state.Triggers.Snapshot(),
            state.Suspicions.Snapshot(),
            state.Canon.Snapshot(),
            state.Bonds.Snapshot(),
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
        state.PromiseLedger.ReplaceAll(Promises);
        state.ScheduledEvents.ReplaceAll(ScheduledEvents);
        state.Triggers.ReplaceAll(Triggers);
        state.Suspicions.ReplaceAll(Suspicions);
        state.Canon.ReplaceAll(CanonRecords);
        state.Bonds.ReplaceAll(Bonds);
        state.BackgroundJobs.ReplaceAll(BackgroundJobs);
    }

    private static ZoneSnapshot CloneZone(ZoneSnapshot zone) =>
        zone with
        {
            Entities = zone.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            BlockingTerrain = new HashSet<GridPoint>(zone.BlockingTerrain),
            Terrain = new Dictionary<GridPoint, string>(zone.Terrain),
            TerrainExpirations = new Dictionary<GridPoint, int>(zone.TerrainExpirations),
            ExploredBySoulId = zone.ExploredBySoulId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<GridPoint>)new HashSet<GridPoint>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            RoomProfiles = zone.RoomProfiles.ToArray(),
            PromiseHooks = zone.PromiseHooks.ToArray(),
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
