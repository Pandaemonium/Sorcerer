using Sorcerer.Core.Consequences;
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
    private readonly PromiseRealizationSystem _promiseRealizationSystem;
    private readonly StatusRegistry _statusRegistry;

    public MovementSystem(GameEngine engine, StatusRegistry statusRegistry)
    {
        _engine = engine;
        _state = engine.State;
        _promiseRealizationSystem = new PromiseRealizationSystem(engine.State, engine);
        _statusRegistry = statusRegistry;
    }

    public ActionResult MoveControlled(Direction direction)
    {
        var turnBefore = _state.Turn;
        var actor = _state.ControlledEntity;
        if (IsUnableToMove(actor))
        {
            var blockedByStatus = $"{Subject(actor)} {Verb(actor, "struggle", "struggles")} against binding magic.";
            var blockedMessage = ApplyPlayerCommandMessage(
                blockedByStatus,
                "moveBlockedMessage",
                "The controlled body could not move because an active status blocked movement.",
                new Dictionary<string, object?>
                {
                    ["direction"] = direction.ToString(),
                    ["blockedReason"] = "status",
                    ["statuses"] = BlockingMovementStatuses(actor).ToArray(),
                });
            var turnDeltas = _engine.AdvanceTurn();
            return new ActionResult
            {
                Action = "move",
                Success = false,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = blockedMessage.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
                Deltas = blockedMessage.Deltas.Concat(turnDeltas).ToArray(),
            };
        }

        var position = actor.Get<PositionComponent>();
        var offset = direction.Offset();
        var destination = position.Position.Translate(offset.X, offset.Y);

        if (!_engine.InBounds(destination))
        {
            if (IsCardinal(direction) && IsPastMapEdge(destination))
            {
                return _engine.Travel(direction);
            }

            return BlockedMoveResult(
                direction,
                destination,
                turnBefore,
                "Something solid refuses you.",
                "edge",
                "The controlled body tried to move past a non-travel map edge.");
        }

        if (_state.BlockingTerrain.Contains(destination))
        {
            return BlockedMoveResult(
                direction,
                destination,
                turnBefore,
                "Something solid refuses you.",
                "terrain",
                "The controlled body tried to move into blocking terrain.");
        }

        var blocker = _engine.BlockingEntityAt(destination);
        if (blocker is not null)
        {
            if (blocker.Has<DoorComponent>())
            {
                return _engine.OpenDoor(
                    actor,
                    blocker,
                    new WorldActionContext(
                        "player_command",
                        ConsumeTurn: true,
                        ResultAction: "move",
                        DeltaOperation: "open"));
            }

            if (_engine.IsHostile(actor, blocker))
            {
                var origin = position.Position;
                var targetPoint = blocker.Get<PositionComponent>().Position;
                var attackDeltas = _engine.AttackEntity(actor, blocker);
                var kind = blocker.TryGet<ActorComponent>(out var targetActor) && !targetActor.Alive ? "kill" : "attack";
                var deed = _engine.ApplyConsequence(WorldConsequence.RecordDeed(
                    "engine",
                    actor.Id.Value,
                    kind,
                    kind == "kill" ? 4 : 2,
                    origin.X,
                    origin.Y,
                    targetPoint.X,
                    targetPoint.Y,
                    new[] { "combat", "violence", blocker.Get<ActorComponent>().Faction },
                    sourceEntityId: actor.Id.Value));
                var turnDeltas = _engine.AdvanceTurn();
                var actionDeltas = attackDeltas.Concat(deed.Deltas).ToArray();
                // A move that lands on a hostile becomes a strike with no beat of its own
                // (FEEL_LOG [01]); this line marks the player's own transition from walking to
                // fighting. Move-resolved-as-attack only - explicit attacks (dialogue, AI) don't
                // get it, since the player there already chose to fight.
                var framing = new[] { "Your step becomes a strike." };
                return new ActionResult
                {
                    Action = "attack",
                    Success = true,
                    ConsumedTurn = true,
                    TurnBefore = turnBefore,
                    TurnAfter = _state.Turn,
                    Messages = framing.Concat(actionDeltas.PlayerMessages()).Concat(turnDeltas.PlayerMessages()).ToArray(),
                    Deltas = actionDeltas.Concat(turnDeltas).ToArray(),
                };
            }

            if (IsFollowerOfControlled(blocker, actor))
            {
                return FollowerYieldMove(direction, turnBefore, actor, blocker, destination);
            }

            return BlockedMoveResult(
                direction,
                destination,
                turnBefore,
                $"{blocker.Name} blocks the way.",
                "entity",
                "The controlled body tried to move into a non-hostile blocking entity.",
                new Dictionary<string, object?>
                {
                    ["blockerId"] = blocker.Id.Value,
                    ["blockerName"] = blocker.Name,
                });
        }

        var move = _engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "player_command",
            actor.Id.Value,
            destination.X,
            destination.Y,
            operation: "move",
            sourceEntityId: actor.Id.Value,
            evidence: $"The controlled body moved {direction}.",
            emitMessage: false,
            message: "You move.",
            recordControlledMovement: true,
            details: new Dictionary<string, object?>
            {
                ["direction"] = direction.ToString(),
            }));
        if (!move.Applied || move.Deltas.Any(delta => delta.Details.TryGetValue("blocked", out var blocked) && blocked is true))
        {
            var failure = move.Error ?? move.Deltas.FirstOrDefault()?.Summary ?? "Something solid refuses you.";
            var blockedMessage = ApplyPlayerCommandMessage(
                failure,
                "moveBlockedMessage",
                "The shared move consequence reported that movement was blocked.",
                new Dictionary<string, object?>
                {
                    ["direction"] = direction.ToString(),
                    ["toX"] = destination.X,
                    ["toY"] = destination.Y,
                    ["blockedReason"] = "consequence",
                });
            return new ActionResult
            {
                Action = "move",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = blockedMessage.Messages,
                Deltas = move.Deltas.Concat(blockedMessage.Deltas).ToArray(),
            };
        }

        var movementTurnDeltas = _engine.AdvanceTurn();
        // A plain move produces no log line: the map already shows the player moving, and a "You
        // move." on every step buries the messages that matter (message-log immersion pass). The
        // move itself is still recorded as a (non-message) delta above for AI and state.
        return new ActionResult
        {
            Action = "move",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = movementTurnDeltas.PlayerMessages().ToArray(),
            Deltas = move.Deltas.Concat(movementTurnDeltas).ToArray(),
        };
    }

    private ActionResult FollowerYieldMove(
        Direction direction,
        int turnBefore,
        Entity actor,
        Entity follower,
        GridPoint destination)
    {
        var move = _engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "player_command",
            actor.Id.Value,
            destination.X,
            destination.Y,
            operation: "followerYieldSwap",
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: $"{follower.Name} yielded the way.",
            reason: "A follower blocking the controlled body swapped places instead of blocking movement.",
            message: $"You trade places with {follower.Name}.",
            recordControlledMovement: true,
            swapWithEntityId: follower.Id.Value,
            details: new Dictionary<string, object?>
            {
                ["direction"] = direction.ToString(),
                ["followerId"] = follower.Id.Value,
            }));
        if (!move.Applied || move.Deltas.Any(delta => delta.Details.TryGetValue("blocked", out var blocked) && blocked is true))
        {
            return BlockedMoveResult(
                direction,
                destination,
                turnBefore,
                $"{follower.Name} cannot make room.",
                "follower",
                "Follower-yield movement was requested but the shared move consequence could not swap positions.",
                new Dictionary<string, object?>
                {
                    ["blockerId"] = follower.Id.Value,
                    ["blockerName"] = follower.Name,
                });
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "move",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = move.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = move.Deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Wait()
    {
        var turnBefore = _state.Turn;
        var waitMessage = ApplyPlayerCommandMessage(
            "You wait.",
            "waitMessage",
            "The player intentionally spent a turn waiting.",
            null);
        var messages = waitMessage.Messages.ToList();
        var deltas = waitMessage.Deltas.ToList();
        deltas.AddRange(_promiseRealizationSystem.RealizeAmbientPromises(
            "wait",
            messages,
            alreadyPersistedMessages: messages.ToList()));
        if (_state.ControlledEntity.TryGet<ActorComponent>(out var resting) && resting.Mana < resting.MaxMana)
        {
            // Waiting a turn is a deliberate rest: recover 1 mana (never past the maximum, so a
            // full-mana wait produces no change and no message).
            var manaRegen = _engine.ApplyConsequence(WorldConsequence.RestoreMana(
                "wait_rest",
                _state.ControlledEntityId.Value,
                1,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: _state.ControlledEntityId.Value,
                reason: "Resting a turn recovers a little mana.",
                operation: "waitManaRegen"));
            messages.AddRange(manaRegen.Messages);
            deltas.AddRange(manaRegen.Deltas);
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "wait",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private WorldConsequenceApplyResult ApplyPlayerCommandMessage(
        string message,
        string operation,
        string reason,
        IReadOnlyDictionary<string, object?>? details)
    {
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payload["playerVisible"] = true;
        return _engine.ApplyConsequence(WorldConsequence.Message(
            "player_command",
            message,
            targetEntityId: _state.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: message,
            reason: reason,
            operation: operation,
            details: payload));
    }

    private ActionResult BlockedMoveResult(
        Direction direction,
        GridPoint destination,
        int turnBefore,
        string message,
        string blockedReason,
        string reason,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payload["direction"] = direction.ToString();
        payload["toX"] = destination.X;
        payload["toY"] = destination.Y;
        payload["blockedReason"] = blockedReason;
        var blockedMessage = ApplyPlayerCommandMessage(
            message,
            "moveBlockedMessage",
            reason,
            payload);
        return new ActionResult
        {
            Action = "move",
            Success = false,
            ConsumedTurn = false,
            FailureCode = Sorcerer.Core.Results.FailureCode.BlockedLine,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = blockedMessage.Messages,
            Deltas = blockedMessage.Deltas,
        };
    }

    public bool CanEnter(GridPoint point) =>
        _engine.InBounds(point)
        && !_state.BlockingTerrain.Contains(point)
        && _engine.BlockingEntityAt(point) is null;

    private static bool IsCardinal(Direction direction) =>
        direction is Direction.North or Direction.South or Direction.East or Direction.West;

    private bool IsPastMapEdge(GridPoint point) =>
        point.X < 0 || point.Y < 0 || point.X >= _state.Width || point.Y >= _state.Height;

    private bool IsUnableToMove(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return false;
        }

        return container.Statuses.Any(status => IsStatusActive(status) && _statusRegistry.BlocksMovement(status.Id));
    }

    private IEnumerable<string> BlockingMovementStatuses(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return Array.Empty<string>();
        }

        return container.Statuses
            .Where(status => IsStatusActive(status) && _statusRegistry.BlocksMovement(status.Id))
            .Select(status => status.Id);
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    private bool IsFollowerOfControlled(Entity entity, Entity controlled)
    {
        var followerSoulId = SoulIdFor(entity);
        var controlledSoulId = SoulIdFor(controlled);
        if (_state.Bonds.TryGet(followerSoulId, controlledSoulId, out var bond)
            && (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase) || bond.Loyalty >= 5))
        {
            return true;
        }

        return entity.TryGet<FactionComponent>(out var faction)
            && faction.Roles.Contains("follower", StringComparer.OrdinalIgnoreCase);
    }

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;
}
