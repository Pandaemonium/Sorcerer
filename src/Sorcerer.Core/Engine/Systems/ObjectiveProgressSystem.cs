using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class ObjectiveProgressSystem
{
    private readonly GameEngine _engine;

    public ObjectiveProgressSystem(GameEngine engine)
    {
        _engine = engine;
    }

    private GameState State => _engine.State;

    public IReadOnlyList<StateDelta> Evaluate(ActionResult action)
    {
        if (!action.Success)
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var promise in State.PromiseLedger.Promises
            .Where(promise => promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase))
            .ToArray())
        {
            var contract = PromiseObjectiveContracts.For(State, promise);
            if (contract is null || contract.Kind == "meet")
            {
                continue;
            }

            var target = ObjectiveTarget(promise);
            if (!ObjectiveSatisfied(contract, promise, target, action, out var completionReason))
            {
                continue;
            }

            var transaction = GameTransaction.Begin(State);
            var local = new List<StateDelta>();
            var status = contract.ReturnToGiver ? "ready_to_return" : "cleared";
            var updated = _engine.ApplyConsequence(WorldConsequence.UpdatePromise(
                "objective_progress",
                promise.Id,
                status: status,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: completionReason,
                reason: "The objective contract was evaluated against authoritative world state.",
                operation: "objectiveProgress",
                details: new Dictionary<string, object?>
                {
                    ["objectiveKind"] = contract.Kind,
                    ["giverEntityId"] = contract.GiverEntityId,
                    ["giverName"] = contract.GiverName,
                    ["completionReason"] = completionReason,
                    ["targetEntityId"] = target?.Id.Value,
                    ["requiredItem"] = contract.RequiredItem,
                    ["playerVisible"] = false,
                }));
            local.AddRange(updated.Deltas);
            if (!updated.Applied)
            {
                transaction.Rollback();
                deltas.AddRange(FailureDiagnostics(local));
                deltas.Add(Skipped(promise, contract, updated.Error ?? "objective_progress_rejected"));
                continue;
            }

            var text = contract.ReturnToGiver
                ? $"Objective updated: return to {contract.GiverName}. {completionReason}"
                : $"Objective complete: {completionReason}";
            var message = _engine.ApplyConsequence(WorldConsequence.Message(
                "objective_progress",
                text,
                targetEntityId: target?.Id.Value ?? promise.Id,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: promise.Text,
                reason: "A completed objective condition produced a legible next step.",
                operation: "objectiveProgressMessage",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["objectiveKind"] = contract.Kind,
                    ["giverEntityId"] = contract.GiverEntityId,
                    ["targetEntityId"] = target?.Id.Value,
                }));
            local.AddRange(message.Deltas);
            if (!message.Applied)
            {
                transaction.Rollback();
                deltas.AddRange(FailureDiagnostics(local));
                deltas.Add(Skipped(promise, contract, message.Error ?? "objective_progress_message_rejected"));
                continue;
            }

            transaction.Commit();
            deltas.AddRange(local);
        }

        return deltas;
    }

    private bool ObjectiveSatisfied(
        PromiseObjectiveContract contract,
        WorldPromise promise,
        Entity? target,
        ActionResult action,
        out string reason)
    {
        reason = "";
        switch (contract.Kind)
        {
            case "fetch":
                if (Carries(State.ControlledEntity, contract.RequiredItem ?? promise.Subject))
                {
                    reason = $"You recovered {contract.RequiredItem ?? promise.Subject}.";
                    return true;
                }

                return false;
            case "delivery":
                if (target is not null && Carries(target, contract.RequiredItem))
                {
                    reason = $"{target.Name} received {contract.RequiredItem}.";
                    return true;
                }

                return false;
            case "escort":
                if (target is not null && IsFollowing(target))
                {
                    reason = $"{target.Name} agreed to travel with you.";
                    return true;
                }

                return false;
            case "threat":
                if (target is not null && ThreatResolved(target, action, out var resolution))
                {
                    reason = $"{target.Name} was resolved by {resolution}.";
                    return true;
                }

                return false;
            case "folk_service":
                if (target?.TryGet<WantComponent>(out var serviceWant) == true
                    && !serviceWant.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"{target.Name} performed the promised service.";
                    return true;
                }

                return false;
            case "rumor_verification":
                if (target is not null && TalkedTo(action, target))
                {
                    reason = $"You heard {target.Name}'s version of the rumor.";
                    return true;
                }

                return false;
            case "social_leverage":
                if (target is not null && HasSocialLeverage(target, out var leverage))
                {
                    reason = $"You gained leverage over {target.Name} through {leverage}.";
                    return true;
                }

                return false;
            default:
                return false;
        }
    }

    private Entity? ObjectiveTarget(WorldPromise promise) =>
        State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id, StringComparer.OrdinalIgnoreCase));

    private bool IsFollowing(Entity entity)
    {
        var playerSoul = State.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : State.ControlledEntityId.Value;
        var entitySoul = entity.TryGet<SoulComponent>(out var targetSoul) ? targetSoul.SoulId : entity.Id.Value;
        return (State.Bonds.TryGet(entitySoul, playerSoul, out var bond)
                && bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase))
            || entity.TryGet<AiComponent>(out var ai)
                && ai.PolicyId.Equals("follower", StringComparison.OrdinalIgnoreCase);
    }

    private bool HasSocialLeverage(Entity entity, out string leverage)
    {
        if (IsFollowing(entity))
        {
            leverage = "recruitment";
            return true;
        }

        var playerSoul = State.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : State.ControlledEntityId.Value;
        var entitySoul = entity.TryGet<SoulComponent>(out var targetSoul) ? targetSoul.SoulId : entity.Id.Value;
        if (State.Bonds.TryGet(entitySoul, playerSoul, out var bond)
            && bond.Loyalty + bond.Admiration >= 4)
        {
            leverage = "earned trust";
            return true;
        }

        if (entity.TryGet<WantComponent>(out var want)
            && !want.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            leverage = "a changed desire";
            return true;
        }

        if (entity.TryGet<ActorComponent>(out var actor)
            && actor.Faction.Equals("player", StringComparison.OrdinalIgnoreCase))
        {
            leverage = "changed allegiance";
            return true;
        }

        leverage = "";
        return false;
    }

    private static bool ThreatResolved(Entity entity, ActionResult action, out string resolution)
    {
        if (entity.TryGet<ActorComponent>(out var actor) && !actor.Alive)
        {
            resolution = "death";
            return true;
        }

        if (entity.TryGet<ActorComponent>(out actor)
            && actor.Faction.Equals("player", StringComparison.OrdinalIgnoreCase))
        {
            resolution = "alliance";
            return true;
        }

        var tags = entity.TryGet<TagsComponent>(out var tagged) ? tagged.Tags : Array.Empty<string>();
        foreach (var (tag, label) in new[]
        {
            ("displaced", "displacement"),
            ("trapped", "restraint"),
            ("transformed", "transformation"),
            ("satisfied", "satisfaction"),
            ("pacified", "pacification"),
            ("recruited", "recruitment"),
        })
        {
            if (tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                resolution = label;
                return true;
            }
        }

        if (action.Deltas.Any(delta =>
            delta.Target.Equals(entity.Id.Value, StringComparison.OrdinalIgnoreCase)
            && Equals(delta.Details.GetValueOrDefault("consequenceType"), WorldConsequenceTypes.TransformEntity)))
        {
            resolution = "transformation";
            return true;
        }

        if (entity.TryGet<StatusContainerComponent>(out var statuses)
            && statuses.Statuses.Any(status => status.Id is "frozen" or "rooted"))
        {
            resolution = "restraint";
            return true;
        }

        if (entity.TryGet<WantComponent>(out var want)
            && !want.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            resolution = "satisfaction";
            return true;
        }

        if (entity.TryGet<AiComponent>(out var ai)
            && !ai.PolicyId.Equals("hostile", StringComparison.OrdinalIgnoreCase))
        {
            resolution = "pacification";
            return true;
        }

        resolution = "";
        return false;
    }

    private static bool TalkedTo(ActionResult action, Entity target) =>
        action.Action.Equals("talk", StringComparison.OrdinalIgnoreCase)
        && action.Deltas.Any(delta =>
            delta.Operation.Equals("dialogue", StringComparison.OrdinalIgnoreCase)
            && delta.Target.Equals(target.Id.Value, StringComparison.OrdinalIgnoreCase));

    private static bool Carries(Entity entity, string? item)
    {
        if (string.IsNullOrWhiteSpace(item) || !entity.TryGet<InventoryComponent>(out var inventory))
        {
            return false;
        }

        var expected = NormalizeToken(item);
        return inventory.Items.Any(pair => pair.Value > 0
            && (NormalizeToken(pair.Key).Equals(expected, StringComparison.OrdinalIgnoreCase)
                || NormalizeToken(pair.Key).Contains(expected, StringComparison.OrdinalIgnoreCase)
                || expected.Contains(NormalizeToken(pair.Key), StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IEnumerable<StateDelta> deltas) =>
        deltas.Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase)).ToArray();

    private static StateDelta Skipped(WorldPromise promise, PromiseObjectiveContract contract, string failure) =>
        new(
            "objectiveProgressSkipped",
            promise.Id,
            $"Objective progress rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["objectiveKind"] = contract.Kind,
                ["failure"] = failure,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
}
