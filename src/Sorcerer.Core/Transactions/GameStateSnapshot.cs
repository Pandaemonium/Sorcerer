using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Transactions;

public sealed record GameStateSnapshot(
    int Turn,
    EntityId ControlledEntityId,
    GridPoint? SelectedTarget,
    ulong RngState,
    int NextEntitySerial,
    IReadOnlyDictionary<EntityId, Entity> Entities,
    IReadOnlySet<GridPoint> BlockingTerrain,
    IReadOnlyDictionary<GridPoint, string> Terrain,
    IReadOnlyDictionary<GridPoint, int> TerrainExpirations,
    IReadOnlyList<string> Messages,
    IReadOnlyList<WorldPromise> Promises,
    IReadOnlyList<ScheduledEventRecord> ScheduledEvents,
    IReadOnlyList<CanonRecord> CanonRecords,
    IReadOnlyList<BackgroundJob> BackgroundJobs)
{
    public static GameStateSnapshot Capture(GameState state) =>
        new(
            state.Turn,
            state.ControlledEntityId,
            state.SelectedTarget,
            state.Rng.State,
            state.NextEntitySerial,
            state.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            new HashSet<GridPoint>(state.BlockingTerrain),
            new Dictionary<GridPoint, string>(state.Terrain),
            new Dictionary<GridPoint, int>(state.TerrainExpirations),
            state.Messages.ToArray(),
            state.PromiseLedger.Snapshot(),
            state.ScheduledEvents.Snapshot(),
            state.Canon.Snapshot(),
            state.BackgroundJobs.Snapshot());

    public void Restore(GameState state)
    {
        state.Turn = Turn;
        state.ControlledEntityId = ControlledEntityId;
        state.SelectedTarget = SelectedTarget;
        state.Rng = new DeterministicRng(RngState);
        state.NextEntitySerial = NextEntitySerial;

        state.Entities.Clear();
        foreach (var pair in Entities)
        {
            state.Entities[pair.Key] = pair.Value.Clone();
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

        state.Messages.Clear();
        state.Messages.AddRange(Messages);
        state.PromiseLedger.ReplaceAll(Promises);
        state.ScheduledEvents.ReplaceAll(ScheduledEvents);
        state.Canon.ReplaceAll(CanonRecords);
        state.BackgroundJobs.ReplaceAll(BackgroundJobs);
    }
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
