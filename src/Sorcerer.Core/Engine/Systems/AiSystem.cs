using Sorcerer.Core.Consequences;
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
            .Where(entity => entity.TryGet<ActorComponent>(out var stats) && stats.Alive)
            .OrderBy(entity => entity.Id.Value))
        {
            if (!player.Get<ActorComponent>().Alive)
            {
                break;
            }

            // Enemies hunt the player; player-allies (e.g. summoned creatures) hunt the player's
            // foes; guards drift back to their post. Everyone else stays idle exactly as before.
            var huntsPlayer = IsHostile(actor, player) && CanNoticeTarget(actor, player);
            var isAlly = !huntsPlayer && IsPlayerAlly(actor);
            var isFollower = !huntsPlayer && !isAlly && IsPlayerFollower(actor);
            var isGuard = IsGuard(actor);
            if (!huntsPlayer && !isAlly && !isFollower && !isGuard)
            {
                continue;
            }

            if (IsUnableToAct(actor))
            {
                continue;
            }

            if (HasActiveBehavior(actor, "dance") || HasActiveBehavior(actor, "freeze_dread"))
            {
                continue;
            }

            if (!actor.TryGet<PositionComponent>(out var actorPosition))
            {
                continue;
            }

            if (isFollower)
            {
                // Followers keep pace with the leader but are not driven into combat by the shared
                // AI -- a low-maintenance company (Q29), directed by the player, not auto-swinging.
                MaybeFollowLeader(deltas, actor, actorPosition.Position, player);
                continue;
            }

            if (isGuard)
            {
                // Guards hold near their anchor: off-duty they drift home instead of wandering or
                // joining fights; provoked, they fight like any hostile but break pursuit beyond
                // the leash and walk back. Provocation, persuasion, and concealment all stay the
                // shared faction/bond/perception rules -- nothing here decides hostility.
                var anchor = GuardAnchor(actor);
                if (!huntsPlayer)
                {
                    if (anchor is { } post && GameEngine.Distance(actorPosition.Position, post) > 1)
                    {
                        var home = StepToward(actorPosition.Position, post);
                        if (CanEnter(home))
                        {
                            AddMoveDeltas(deltas, actor, home, "guard_return");
                        }
                    }

                    continue;
                }

                if (anchor is { } leashPost
                    && player.TryGet<PositionComponent>(out var quarry)
                    && GameEngine.Distance(actorPosition.Position, leashPost) > GuardLeash
                    && GameEngine.Distance(actorPosition.Position, quarry.Position) > 1)
                {
                    var back = StepToward(actorPosition.Position, leashPost);
                    if (CanEnter(back))
                    {
                        AddMoveDeltas(deltas, actor, back, "guard_return");
                    }

                    continue;
                }
            }

            // Coward/mimic are player-relative compulsions applied to enemies by behavior_control
            // spells, so they only steer actors that are hunting the player.
            if (huntsPlayer && HasActiveBehavior(actor, "coward"))
            {
                if (player.TryGet<PositionComponent>(out var cowardFrom))
                {
                    var fleeDestination = StepAway(actorPosition.Position, cowardFrom.Position);
                    if (CanEnter(fleeDestination))
                    {
                        AddMoveDeltas(deltas, actor, fleeDestination, "coward");
                    }
                }

                continue;
            }

            if (huntsPlayer && HasActiveBehavior(actor, "mimic"))
            {
                if (_state.LastControlledMoveDelta is { } mimicDelta)
                {
                    var mimicDestination = actorPosition.Position.Translate(mimicDelta.X, mimicDelta.Y);
                    if (CanEnter(mimicDestination))
                    {
                        AddMoveDeltas(deltas, actor, mimicDestination, "mimic");
                    }
                }

                continue;
            }

            var target = huntsPlayer ? player : NearestHostileTarget(actor);
            if (target is null || !target.TryGet<PositionComponent>(out var targetPosition))
            {
                continue;
            }

            var distance = GameEngine.Distance(actorPosition.Position, targetPosition.Position);
            if (distance <= 1)
            {
                deltas.AddRange(_engine.AttackEntity(actor, target));
                continue;
            }

            if (distance <= 8)
            {
                var destination = StepToward(actorPosition.Position, targetPosition.Position);
                if (CanEnter(destination))
                {
                    AddMoveDeltas(deltas, actor, destination, huntsPlayer ? "hostile_pursuit" : "ally_pursuit");
                }
            }
        }

        return deltas;
    }

    /// <summary>
    /// A player ally is an AI actor given the "ally" policy, which summons and conjurations receive
    /// when spawned into the player faction. Such allies hunt the player's enemies rather than the
    /// player. Deliberately narrow: recruited followers ("follower") keep their own follow/swap
    /// movement and are not driven into combat here.
    /// </summary>
    private static bool IsPlayerAlly(Entity actor) =>
        actor.TryGet<AiComponent>(out var ai)
        && ai.PolicyId.Equals("ally", StringComparison.OrdinalIgnoreCase);

    // A recruited follower (by AI policy or faction role). Followers fight the leader's foes when
    // there are any; this identifies them so, when there are none, they keep up instead of idling.
    private static bool IsPlayerFollower(Entity actor) =>
        (actor.TryGet<AiComponent>(out var ai) && ai.PolicyId.Equals("follower", StringComparison.OrdinalIgnoreCase))
        || (actor.TryGet<FactionComponent>(out var faction)
            && faction.Roles.Contains("follower", StringComparer.OrdinalIgnoreCase));

    // How far a guard chases before breaking off and walking back to its post. Short by design:
    // a guarded objective stays guarded, and luring the guard away is a legitimate tactic that
    // costs the player distance, not a free win.
    private const int GuardLeash = 4;

    private static bool IsGuard(Entity actor) =>
        actor.TryGet<AiComponent>(out var ai)
        && ai.PolicyId.Equals("guard", StringComparison.OrdinalIgnoreCase);

    private static GridPoint? GuardAnchor(Entity actor)
    {
        if (!actor.TryGet<AiComponent>(out var ai) || ai.Parameters is null)
        {
            return null;
        }

        return ReadAnchorCoordinate(ai.Parameters, "anchorX") is { } x
            && ReadAnchorCoordinate(ai.Parameters, "anchorY") is { } y
                ? new GridPoint(x, y)
                : null;
    }

    // Anchor coordinates arrive as ints from spawn consequences but come back from saves as
    // JsonElement numbers (or strings from hand-authored content); missing or malformed values
    // degrade to "no anchor", i.e. the pre-guard idle behavior.
    private static int? ReadAnchorCoordinate(IReadOnlyDictionary<string, object?> parameters, string key)
    {
        if (!parameters.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int number => number,
            long number => (int)number,
            double number => (int)number,
            string text when int.TryParse(text, out var parsed) => parsed,
            System.Text.Json.JsonElement json
                when json.ValueKind == System.Text.Json.JsonValueKind.Number
                    && json.TryGetInt32(out var parsed) => parsed,
            _ => null,
        };
    }

    // Keep a follower a step off the leader: close the gap when it has fallen behind (up to a
    // generous leash so a straggler still catches up), hold position when already alongside, and
    // never move onto or strike the leader. This is what makes a following low-maintenance (Q29).
    private const int FollowLeash = 12;

    private void MaybeFollowLeader(List<StateDelta> deltas, Entity actor, GridPoint from, Entity leader)
    {
        if (!IsPlayerFollower(actor) || !leader.TryGet<PositionComponent>(out var leaderPosition))
        {
            return;
        }

        var gap = GameEngine.Distance(from, leaderPosition.Position);
        if (gap <= 1 || gap > FollowLeash)
        {
            return;
        }

        var step = StepToward(from, leaderPosition.Position);
        if (CanEnter(step))
        {
            AddMoveDeltas(deltas, actor, step, "follow_leader");
        }
    }

    /// <summary>Nearest living entity this actor is hostile to and can perceive, or null.</summary>
    private Entity? NearestHostileTarget(Entity actor)
    {
        if (!actor.TryGet<PositionComponent>(out var position))
        {
            return null;
        }

        return _state.Entities.Values
            .Where(entity => entity.Id != actor.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var stats)
                && stats.Alive
                && entity.TryGet<PositionComponent>(out _)
                && IsHostile(actor, entity)
                && CanNoticeTarget(actor, entity))
            .OrderBy(entity => GameEngine.Distance(position.Position, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value)
            .FirstOrDefault();
    }

    private void AddMoveDeltas(List<StateDelta> deltas, Entity actor, GridPoint destination, string behavior)
    {
        var applied = _engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "ai",
            actor.Id.Value,
            destination.X,
            destination.Y,
            operation: "aiMove",
            sourceEntityId: actor.Id.Value,
            reason: $"AI behavior: {behavior}",
            details: new Dictionary<string, object?>
            {
                ["aiBehavior"] = behavior,
            }));
        deltas.AddRange(applied.Deltas);
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

    // Delegates to PerceptionSystem's shared concealment rule (via GameEngine) so AI targeting
    // and deed/witness capture use exactly one definition of "concealed."
    private bool CanNoticeTarget(Entity actor, Entity target) => _engine.CanPerceiveSubject(actor, target);

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
