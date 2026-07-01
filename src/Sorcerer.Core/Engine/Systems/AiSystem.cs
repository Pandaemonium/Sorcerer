using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Status;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class AiSystem
{
    private readonly GameEngine _engine;
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;

    public AiSystem(GameEngine engine, StatusRegistry statusRegistry)
    {
        _engine = engine;
        _state = engine.State;
        _statusRegistry = statusRegistry;
    }

    public IReadOnlyList<StateDelta> RunActorTurns()
    {
        var player = _state.ControlledEntity;
        if (!player.TryGet<ActorComponent>(out var playerActor) || !playerActor.Alive)
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var actor in _state.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ControllerComponent>(out var controller)
                && controller.Kind == ControllerKind.Ai)
            .Where(entity => entity.TryGet<ActorComponent>(out var stats)
                && stats.Alive
                && IsHostile(entity, player)
                && CanNoticeTarget(entity, player))
            .OrderBy(entity => entity.Id.Value))
        {
            if (!player.Get<ActorComponent>().Alive)
            {
                break;
            }

            if (IsUnableToAct(actor))
            {
                continue;
            }

            if (HasActiveBehavior(actor, "dance") || HasActiveBehavior(actor, "freeze_dread"))
            {
                continue;
            }

            if (!actor.TryGet<PositionComponent>(out var actorPosition)
                || !player.TryGet<PositionComponent>(out var playerPosition))
            {
                continue;
            }

            if (HasActiveBehavior(actor, "coward"))
            {
                var fleeDestination = StepAway(actorPosition.Position, playerPosition.Position);
                if (CanEnter(fleeDestination))
                {
                    deltas.Add(_engine.MoveEntity(actor, fleeDestination, "aiMove"));
                }

                continue;
            }

            if (HasActiveBehavior(actor, "mimic"))
            {
                if (_state.LastControlledMoveDelta is { } mimicDelta)
                {
                    var mimicDestination = actorPosition.Position.Translate(mimicDelta.X, mimicDelta.Y);
                    if (CanEnter(mimicDestination))
                    {
                        deltas.Add(_engine.MoveEntity(actor, mimicDestination, "aiMove"));
                    }
                }

                continue;
            }

            var distance = GameEngine.Distance(actorPosition.Position, playerPosition.Position);
            if (distance <= 1)
            {
                deltas.AddRange(_engine.AttackEntity(actor, player));
                continue;
            }

            if (distance <= 8)
            {
                var destination = StepToward(actorPosition.Position, playerPosition.Position);
                if (CanEnter(destination))
                {
                    deltas.Add(_engine.MoveEntity(actor, destination, "aiMove"));
                }
            }
        }

        return deltas;
    }

    private bool HasActiveBehavior(Entity entity, string tag) =>
        entity.TryGet<BehaviorTagsComponent>(out var behaviors)
        && behaviors.Tags.TryGetValue(tag, out var expiry)
        && (expiry is null || expiry > _state.Turn);

    public Entity? FindNearestHostile()
    {
        var actor = _state.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        return _state.Entities.Values
            .Where(entity => entity.Id != actor.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var targetActor)
                && targetActor.Alive
                && IsHostile(actor, entity)
                && CanNoticeTarget(actor, entity))
            .OrderBy(entity => GameEngine.Distance(origin, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value)
            .FirstOrDefault();
    }

    public bool IsHostile(Entity actor, Entity target)
    {
        if (!actor.TryGet<ActorComponent>(out var actorStats)
            || !target.TryGet<ActorComponent>(out var targetStats))
        {
            return false;
        }

        if (target.Id == _state.ControlledEntityId
            && _state.Bonds.TryGet(SoulIdFor(actor), SoulIdFor(target), out var bond))
        {
            if (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase)
                || bond.Loyalty >= 5)
            {
                return false;
            }

            if (bond.Posture.Equals("betrayer", StringComparison.OrdinalIgnoreCase)
                || bond.Resentment >= 7)
            {
                return true;
            }
        }

        return _state.Factions.IsHostile(actorStats.Faction, targetStats.Faction);
    }

    private bool CanNoticeTarget(Entity actor, Entity target)
    {
        if (!target.TryGet<StatusContainerComponent>(out var statuses)
            || !statuses.Statuses.Any(status => IsStatusActive(status) && _statusRegistry.ConcealsBearer(status.Id)))
        {
            return true;
        }

        if (!actor.TryGet<PositionComponent>(out var actorPosition)
            || !target.TryGet<PositionComponent>(out var targetPosition))
        {
            return true;
        }

        return GameEngine.Distance(actorPosition.Position, targetPosition.Position) <= 2;
    }

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private bool CanEnter(GridPoint point) =>
        _engine.InBounds(point)
        && !_state.BlockingTerrain.Contains(point)
        && _engine.BlockingEntityAt(point) is null;

    private bool IsUnableToAct(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return false;
        }

        return container.Statuses.Any(status => IsStatusActive(status) && _statusRegistry.BlocksAction(status.Id));
    }

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    private static GridPoint StepToward(GridPoint from, GridPoint to) =>
        from.Translate(Math.Sign(to.X - from.X), Math.Sign(to.Y - from.Y));

    private static GridPoint StepAway(GridPoint from, GridPoint to) =>
        from.Translate(-Math.Sign(to.X - from.X), -Math.Sign(to.Y - from.Y));
}
