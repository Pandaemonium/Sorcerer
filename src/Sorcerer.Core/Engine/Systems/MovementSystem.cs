using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Status;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class MovementSystem
{
    private readonly GameEngine _engine;
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;

    public MovementSystem(GameEngine engine, StatusRegistry statusRegistry)
    {
        _engine = engine;
        _state = engine.State;
        _statusRegistry = statusRegistry;
    }

    public ActionResult MoveControlled(Direction direction)
    {
        var turnBefore = _state.Turn;
        var actor = _state.ControlledEntity;
        if (IsUnableToMove(actor))
        {
            var blockedByStatus = $"{Subject(actor)} {Verb(actor, "struggle", "struggles")} against binding magic.";
            _state.AddMessage(blockedByStatus);
            var turnDeltas = _engine.AdvanceTurn();
            return ActionResult.Simple(
                "move",
                success: false,
                consumedTurn: true,
                turnBefore,
                _state.Turn,
                new[] { blockedByStatus }.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray());
        }

        var position = actor.Get<PositionComponent>();
        var offset = direction.Offset();
        var destination = position.Position.Translate(offset.X, offset.Y);

        if (!_engine.InBounds(destination) || _state.BlockingTerrain.Contains(destination))
        {
            return ActionResult.Simple(
                "move",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                "Something solid refuses you.");
        }

        var blocker = _engine.BlockingEntityAt(destination);
        if (blocker is not null)
        {
            if (_engine.IsHostile(actor, blocker))
            {
                var origin = position.Position;
                var targetPoint = blocker.Get<PositionComponent>().Position;
                var delta = _engine.AttackEntity(actor, blocker);
                var kind = blocker.TryGet<ActorComponent>(out var targetActor) && !targetActor.Alive ? "kill" : "attack";
                _engine.RecordDeed(
                    actor,
                    kind,
                    kind == "kill" ? 4 : 2,
                    origin,
                    targetPoint,
                    new[] { "combat", "violence", blocker.Get<ActorComponent>().Faction });
                var turnDeltas = _engine.AdvanceTurn();
                return new ActionResult
                {
                    Action = "attack",
                    Success = true,
                    ConsumedTurn = true,
                    TurnBefore = turnBefore,
                    TurnAfter = _state.Turn,
                    Messages = new[] { delta.Summary }.Concat(turnDeltas.Select(item => item.Summary)).ToArray(),
                    Deltas = new[] { delta }.Concat(turnDeltas).ToArray(),
                };
            }

            return ActionResult.Simple(
                "move",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                $"{blocker.Name} blocks the way.");
        }

        actor.Set(new PositionComponent(destination));
        var movementTurnDeltas = _engine.AdvanceTurn();
        _state.AddMessage("You move.");
        return ActionResult.Simple(
            "move",
            success: true,
            consumedTurn: true,
            turnBefore,
            _state.Turn,
            new[] { "You move." }.Concat(movementTurnDeltas.Select(delta => delta.Summary)).ToArray());
    }

    public ActionResult Wait()
    {
        var turnBefore = _state.Turn;
        var turnDeltas = _engine.AdvanceTurn();
        _state.AddMessage("You wait.");
        return new ActionResult
        {
            Action = "wait",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = new[] { "You wait." }.Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = turnDeltas,
        };
    }

    public StateDelta MoveEntity(Entity entity, GridPoint destination, string operation)
    {
        var before = entity.Get<PositionComponent>().Position;
        if (!_engine.InBounds(destination)
            || _state.BlockingTerrain.Contains(destination)
            || _engine.BlockingEntityAt(destination) is not null)
        {
            var blocked = $"{entity.Name} cannot move to {destination.X},{destination.Y}.";
            _state.AddMessage(blocked);
            return new StateDelta(
                operation,
                entity.Id.Value,
                blocked,
                new Dictionary<string, object?>
                {
                    ["fromX"] = before.X,
                    ["fromY"] = before.Y,
                    ["blocked"] = true,
                });
        }

        entity.Set(new PositionComponent(destination));
        var message = $"{Subject(entity)} {Verb(entity, "move", "moves")} to {destination.X},{destination.Y}.";
        _state.AddMessage(message);
        return new StateDelta(
            operation,
            entity.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["fromX"] = before.X,
                ["fromY"] = before.Y,
                ["toX"] = destination.X,
                ["toY"] = destination.Y,
            });
    }

    public bool CanEnter(GridPoint point) =>
        _engine.InBounds(point)
        && !_state.BlockingTerrain.Contains(point)
        && _engine.BlockingEntityAt(point) is null;

    private bool IsUnableToMove(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return false;
        }

        return container.Statuses.Any(status => IsStatusActive(status) && _statusRegistry.BlocksMovement(status.Id));
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;
}
