using Sorcerer.Core.Characters;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Status;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class BodySwapSystem
{
    private readonly GameEngine _engine;
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;

    public BodySwapSystem(GameEngine engine, StatusRegistry statusRegistry)
    {
        _engine = engine;
        _state = engine.State;
        _statusRegistry = statusRegistry;
    }

    public ActionResult Possess(string? target)
    {
        var turnBefore = _state.Turn;
        var newBody = ResolveNearbyEntity(
            target,
            entity => entity.Id != _state.ControlledEntityId
                && entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive,
            range: 1);
        if (newBody is null)
        {
            return ActionResult.Simple(
                "possess",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                "No nearby living body is close enough to possess.");
        }

        if (!CanPossess(newBody, out var reason))
        {
            var resisted = reason ?? $"{newBody.Name} braces against your soul and refuses the door.";
            _state.AddMessage(resisted);
            var resistedTurnDeltas = _engine.AdvanceTurn();
            return new ActionResult
            {
                Action = "possess",
                Success = false,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { resisted }.Concat(resistedTurnDeltas.Select(delta => delta.Summary)).ToArray(),
                Deltas = resistedTurnDeltas,
            };
        }

        var deltas = PossessEntity(newBody);
        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "possess",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = deltas.Select(delta => delta.Summary).Concat(turnDeltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public bool CanPossess(Entity newBody, out string? reason)
    {
        reason = null;
        if (newBody.Id == _state.ControlledEntityId)
        {
            reason = "You are already in that body.";
            return false;
        }

        if (!newBody.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            reason = $"{newBody.Name} is not a living body.";
            return false;
        }

        if (_engine.IsHostile(_state.ControlledEntity, newBody) && !IsUnableToAct(newBody))
        {
            reason = $"{newBody.Name} braces against your soul and refuses the door.";
            return false;
        }

        return true;
    }

    public IReadOnlyList<StateDelta> PossessEntity(Entity newBody)
    {
        if (!CanPossess(newBody, out var reason))
        {
            throw new InvalidOperationException(reason ?? "Possession target rejected.");
        }

        var oldBody = _state.ControlledEntity;
        var newActor = newBody.Get<ActorComponent>();
        var oldActor = oldBody.Get<ActorComponent>();
        var targetIsHostile = _engine.IsHostile(oldBody, newBody);
        var oldSoul = oldBody.TryGet<SoulComponent>(out var oldSoulComponent)
            ? oldSoulComponent
            : new SoulComponent($"{oldBody.Id.Value}_soul");
        var newSoul = newBody.TryGet<SoulComponent>(out var newSoulComponent)
            ? newSoulComponent
            : new SoulComponent($"{newBody.Id.Value}_soul");
        var oldSoulRecord = CharacterMath.EnsureSoulRecord(_state, oldBody);
        var newSoulRecord = CharacterMath.EnsureSoulRecord(_state, newBody);

        oldBody.Set(newSoul);
        newBody.Set(oldSoul);
        oldBody.Set(new ControllerComponent(ControllerKind.Ai));
        newBody.Set(new ControllerComponent(ControllerKind.Player));
        oldBody.Set(new AiComponent(targetIsHostile ? "displaced_hostile_soul" : "displaced_soul"));
        newBody.Set(new AiComponent("player_controlled"));
        oldBody.Set(ActorWithSoulMana(oldActor with { Faction = newActor.Faction }, newSoulRecord));
        newBody.Set(ActorWithSoulMana(newActor with { Faction = oldActor.Faction }, oldSoulRecord));
        CharacterMath.SyncActorFromBodyAndSoul(oldBody, newSoulRecord);
        CharacterMath.SyncActorFromBodyAndSoul(newBody, oldSoulRecord);
        oldBody.Set(new FactionComponent(newActor.Faction, new[] { newActor.Faction, "displaced" }));
        newBody.Set(new FactionComponent(oldActor.Faction, new[] { oldActor.Faction, "possessed_body" }));

        var statusDeltas = new[]
        {
            _engine.ApplyStatus(oldBody, "disoriented", duration: 2, displayName: "disoriented soul"),
            _engine.ApplyStatus(newBody, "soul_swapped", duration: 8, displayName: "borrowed body"),
        };
        _state.ControlledEntityId = newBody.Id;
        _state.SelectedTarget = null;
        _engine.RecordDeed(
            newBody,
            "body_swap",
            targetIsHostile ? 5 : 3,
            oldBody.Get<PositionComponent>().Position,
            newBody.Get<PositionComponent>().Position,
            new[] { "wild_magic", "body_swap", targetIsHostile ? "violation" : "consent" });

        var message = $"Your soul crosses into {newBody.Name}; {oldBody.Name} staggers with someone else behind the eyes.";
        _state.AddMessage(message);
        return statusDeltas.Concat(new[]
            {
                new StateDelta(
                    "possess",
                    newBody.Id.Value,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["oldBody"] = oldBody.Id.Value,
                        ["newBody"] = newBody.Id.Value,
                        ["playerSoul"] = oldSoul.SoulId,
                        ["displacedSoul"] = newSoul.SoulId,
                    }),
            }).ToArray();
    }

    private Entity? ResolveNearbyEntity(
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var origin = _state.ControlledEntity.Get<PositionComponent>().Position;
        var candidates = _state.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.Distance(origin, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            return candidates.FirstOrDefault(entity =>
                entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || (entity.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))));
        }

        if (_state.SelectedTarget is { } selected)
        {
            var selectedEntity = candidates.FirstOrDefault(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        return candidates.FirstOrDefault();
    }

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

    private static ActorComponent ActorWithSoulMana(ActorComponent actor, SoulRecord soul) =>
        actor with
        {
            Mana = Math.Min(soul.Mana, soul.MaxMana),
            MaxMana = soul.MaxMana,
        };
}
