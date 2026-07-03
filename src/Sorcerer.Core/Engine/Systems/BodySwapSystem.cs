using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Status;
using Sorcerer.Core.Transactions;
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
            var resistedMessage = ApplyRequired(WorldConsequence.Message(
                "body_swap",
                resisted,
                targetEntityId: newBody.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: _state.ControlledEntityId.Value,
                evidence: $"{newBody.Name} resisted possession.",
                operation: "possessResisted",
                details: new Dictionary<string, object?>
                {
                    ["oldBody"] = _state.ControlledEntityId.Value,
                    ["newBody"] = newBody.Id.Value,
                }));
            var resistedTurnDeltas = _engine.AdvanceTurn();
            return new ActionResult
            {
                Action = "possess",
                Success = false,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = resistedMessage.Messages.Concat(resistedTurnDeltas.PlayerMessages()).ToArray(),
                Deltas = resistedMessage.Deltas.Concat(resistedTurnDeltas).ToArray(),
            };
        }

        var deltas = PossessEntity(newBody);
        var skipped = deltas.FirstOrDefault(delta =>
            delta.Operation.Equals("possessionSkipped", StringComparison.OrdinalIgnoreCase));
        if (skipped is not null)
        {
            var failure = skipped.Details.TryGetValue("failure", out var value) && value is not null
                ? value.ToString() ?? "Possession could not be completed."
                : "Possession could not be completed.";
            return new ActionResult
            {
                Action = "possess",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "possess",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = VisiblePossessionMessages(deltas).Concat(turnDeltas.PlayerMessages()).ToArray(),
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
        var transaction = GameTransaction.Begin(_state);
        var deltas = new List<StateDelta>();
        var deltaStart = deltas.Count;
        if (!TryApplyPossessionConsequence(
            WorldConsequence.SwapSouls(
            "body_swap",
            oldBody.Id.Value,
            newBody.Id.Value,
            sourceEntityId: oldBody.Id.Value,
            evidence: $"{oldBody.Name} and {newBody.Name} exchange souls.",
            operation: "possessSoulSwap",
            details: PossessionDetails(oldBody, newBody, oldSoul.SoulId, newSoul.SoulId)),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.UpdateControl(
            "body_swap",
            oldBody.Id.Value,
            "ai",
            aiPolicyId: targetIsHostile ? "displaced_hostile_soul" : "displaced_soul",
            sourceEntityId: newBody.Id.Value,
            evidence: $"{oldBody.Name} now carries the displaced soul.",
            operation: "possessOldBodyControl"),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.UpdateControl(
            "body_swap",
            newBody.Id.Value,
            "player",
            aiPolicyId: "player_controlled",
            sourceEntityId: oldBody.Id.Value,
            evidence: $"{newBody.Name} now carries the player soul.",
            operation: "possessNewBodyControl"),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.ChangeFaction(
            "body_swap",
            oldBody.Id.Value,
            newActor.Faction,
            roles: new[] { newActor.Faction, "displaced" },
            sourceEntityId: newBody.Id.Value,
            evidence: $"{oldBody.Name} inherits the displaced soul's allegiance.",
            operation: "possessOldBodyFaction"),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.ChangeFaction(
            "body_swap",
            newBody.Id.Value,
            oldActor.Faction,
            roles: new[] { oldActor.Faction, "possessed_body" },
            sourceEntityId: oldBody.Id.Value,
            evidence: $"{newBody.Name} inherits the player soul's allegiance.",
            operation: "possessNewBodyFaction"),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.ApplyStatus(
            "body_swap",
            oldBody.Id.Value,
            "disoriented",
            duration: 2,
            displayName: "disoriented soul",
            sourceEntityId: newBody.Id.Value,
            evidence: $"{oldBody.Name} carries the displaced soul after possession.",
            operation: "possessOldBodyStatus",
            details: PossessionDetails(oldBody, newBody, oldSoul.SoulId, newSoul.SoulId)),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.ApplyStatus(
            "body_swap",
            newBody.Id.Value,
            "soul_swapped",
            duration: 8,
            displayName: "borrowed body",
            sourceEntityId: oldBody.Id.Value,
            evidence: $"{newBody.Name} carries the player soul after possession.",
            operation: "possessNewBodyStatus",
            details: PossessionDetails(oldBody, newBody, oldSoul.SoulId, newSoul.SoulId)),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.SetControlledEntity(
            "body_swap",
            newBody.Id.Value,
            sourceEntityId: oldBody.Id.Value,
            evidence: $"{newBody.Name} carries the player soul after possession.",
            operation: "possessControlledEntity",
            details: PossessionDetails(oldBody, newBody, oldSoul.SoulId, newSoul.SoulId)),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.SetSelectedTarget(
            "body_swap",
            clear: true,
            sourceEntityId: newBody.Id.Value,
            evidence: "Possession changes the controlled body, so the old selected target is cleared.",
            operation: "possessClearTarget",
            details: PossessionDetails(oldBody, newBody, oldSoul.SoulId, newSoul.SoulId)),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        if (!TryApplyPossessionConsequence(
            WorldConsequence.RecordDeed(
                "engine",
                newBody.Id.Value,
                "body_swap",
                targetIsHostile ? 5 : 3,
                oldBody.Get<PositionComponent>().Position.X,
                oldBody.Get<PositionComponent>().Position.Y,
                newBody.Get<PositionComponent>().Position.X,
                newBody.Get<PositionComponent>().Position.Y,
                new[] { "wild_magic", "body_swap", targetIsHostile ? "violation" : "consent" },
                sourceEntityId: newBody.Id.Value),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        var message = $"Your soul crosses into {newBody.Name}; {oldBody.Name} staggers with someone else behind the eyes.";
        if (!TryApplyPossessionConsequence(
            WorldConsequence.Message(
            "body_swap",
            message,
            targetEntityId: newBody.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: oldBody.Id.Value,
            evidence: $"{newBody.Name} carries the player soul after possession.",
            operation: "possess",
            details: PossessionDetails(oldBody, newBody, oldSoul.SoulId, newSoul.SoulId)),
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            oldSoul.SoulId,
            newSoul.SoulId))
        {
            return deltas;
        }

        transaction.Commit();
        return deltas;
    }

    private bool TryApplyPossessionConsequence(
        WorldConsequence consequence,
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        Entity oldBody,
        Entity newBody,
        string playerSoul,
        string displacedSoul)
    {
        var applied = _engine.ApplyConsequence(consequence);
        if (applied.Applied)
        {
            deltas.AddRange(applied.Deltas);
            return true;
        }

        RollBackPossessionTransaction(
            transaction,
            deltas,
            deltaStart,
            oldBody,
            newBody,
            playerSoul,
            displacedSoul,
            consequence.Type,
            applied.Deltas,
            applied.Error ?? $"Failed to apply {consequence.Type} during possession.");
        return false;
    }

    private static void RollBackPossessionTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        Entity oldBody,
        Entity newBody,
        string playerSoul,
        string displacedSoul,
        string consequenceType,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        deltas.AddRange(FailureDiagnostics(failedDeltas));
        var rejectedCount = FailureDiagnostics(failedDeltas).Count;
        deltas.Add(new StateDelta(
            "possessionSkipped",
            newBody.Id.Value,
            $"Possession rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["oldBody"] = oldBody.Id.Value,
                ["newBody"] = newBody.Id.Value,
                ["playerSoul"] = playerSoul,
                ["displacedSoul"] = displacedSoul,
                ["consequenceType"] = consequenceType,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas
            .Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void RemoveRangeFrom<T>(List<T> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
    }

    private static IReadOnlyDictionary<string, object?> PossessionDetails(
        Entity oldBody,
        Entity newBody,
        string playerSoul,
        string displacedSoul) =>
        new Dictionary<string, object?>
        {
            ["oldBody"] = oldBody.Id.Value,
            ["newBody"] = newBody.Id.Value,
            ["playerSoul"] = playerSoul,
            ["displacedSoul"] = displacedSoul,
        };

    private WorldConsequenceApplyResult ApplyRequired(WorldConsequence consequence)
    {
        var applied = _engine.ApplyConsequence(consequence);
        if (!applied.Applied)
        {
            throw new InvalidOperationException(applied.Error ?? $"Failed to apply {consequence.Type} during possession.");
        }

        return applied;
    }

    private static IEnumerable<string> VisiblePossessionMessages(IEnumerable<StateDelta> deltas) =>
        deltas
            .Where(delta => !delta.Details.TryGetValue("consequenceType", out _)
                || !delta.Details.TryGetValue("visibility", out var visibility)
                || !string.Equals(visibility?.ToString(), WorldConsequenceVisibility.Hidden, StringComparison.OrdinalIgnoreCase))
            .PlayerMessages();

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
}
