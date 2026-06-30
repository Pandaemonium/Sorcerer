using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Transactions;

public sealed record GameStateSnapshot(
    int Turn,
    EntityId ControlledEntityId,
    GridPoint? SelectedTarget,
    IReadOnlyDictionary<EntityId, Entity> Entities,
    IReadOnlySet<GridPoint> BlockingTerrain,
    IReadOnlyList<string> Messages,
    IReadOnlyList<WorldPromise> Promises)
{
    public static GameStateSnapshot Capture(GameState state) =>
        new(
            state.Turn,
            state.ControlledEntityId,
            state.SelectedTarget,
            state.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            new HashSet<GridPoint>(state.BlockingTerrain),
            state.Messages.ToArray(),
            state.PromiseLedger.Snapshot());

    public void Restore(GameState state)
    {
        state.Turn = Turn;
        state.ControlledEntityId = ControlledEntityId;
        state.SelectedTarget = SelectedTarget;

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

        state.Messages.Clear();
        state.Messages.AddRange(Messages);
        state.PromiseLedger.ReplaceAll(Promises);
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
