using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

/// <summary>
/// <see cref="InteractionSystem"/> generated-objective hand-off and completion (returned/contact objectives) and dialogue claim-seed application against the detached generation sandbox.
/// Split from the interaction system (Phase 0.4); the ctor, the Talk/dialogue interaction
/// core, and shared entity/bond/message helpers stay in the base file. All state changes
/// still go through _engine.ApplyConsequence.
/// </summary>
public sealed partial class InteractionSystem
{
    public WorldConsequenceApplyResult? ApplyGeneratedObjectiveHandoff(Entity source, string trigger)
    {
        var seed = _engine.CreateObjectiveHandoff(source, trigger);
        if (seed is null)
        {
            return null;
        }

        var deltas = ApplyClaimSeeds(source, trigger, new[] { seed });
        var skipped = deltas.FirstOrDefault(delta =>
            delta.Operation.Equals("claimSeedSkipped", StringComparison.OrdinalIgnoreCase));
        return skipped is null
            ? new WorldConsequenceApplyResult(
                true,
                source.Id.Value,
                null,
                ObjectiveMessages(deltas),
                deltas,
                new Dictionary<string, object?>
                {
                    ["sourceEntityId"] = source.Id.Value,
                    ["sourceTrigger"] = trigger,
                    ["generatedObjective"] = true,
                })
            : new WorldConsequenceApplyResult(
                false,
                source.Id.Value,
                Convert.ToString(skipped.Details.GetValueOrDefault("failure")) ?? "objective_handoff_rejected",
                Array.Empty<string>(),
                deltas,
                skipped.Details);
    }

    private static IReadOnlyList<string> ObjectiveMessages(IEnumerable<StateDelta> deltas) =>
        deltas
            .Where(delta => delta.Operation is "objectiveHandoffMessage" or "objectiveCompleteMessage" or "objectiveReturnMessage" or "objectiveReturnNeedsItem")
            .PlayerMessages()
            .ToArray();

    private IReadOnlyList<StateDelta> CompleteReturnedObjectives(Entity giver)
    {
        var deltas = new List<StateDelta>();
        foreach (var promise in State.PromiseLedger.Promises
            .Where(promise => promise.Status.Equals("ready_to_return", StringComparison.OrdinalIgnoreCase))
            .Where(promise => promise.SourceSpeakerId?.Equals(giver.Id.Value, StringComparison.OrdinalIgnoreCase) == true)
            .ToArray())
        {
            var contract = PromiseObjectiveContracts.For(State, promise);
            if (contract is null || !contract.ReturnToGiver)
            {
                continue;
            }

            string? returnedItem = null;
            if (contract.Kind == "fetch")
            {
                returnedItem = State.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
                    && !string.IsNullOrWhiteSpace(contract.RequiredItem)
                    ? FindInventoryKey(inventory, contract.RequiredItem!)
                    : null;
                if (returnedItem is null)
                {
                    var needed = _engine.ApplyConsequence(WorldConsequence.Message(
                        $"objective_return:{giver.Id.Value}",
                        $"{giver.Name} is still waiting for {contract.RequiredItem ?? promise.Subject}.",
                        targetEntityId: giver.Id.Value,
                        visibility: WorldConsequenceVisibility.Message,
                        sourceEntityId: giver.Id.Value,
                        evidence: promise.Text,
                        reason: "A fetch objective requires the recovered object at handoff, not only prior discovery.",
                        operation: "objectiveReturnNeedsItem",
                        details: new Dictionary<string, object?>
                        {
                            ["promiseId"] = promise.Id,
                            ["objectiveKind"] = contract.Kind,
                            ["giverEntityId"] = giver.Id.Value,
                            ["requiredItem"] = contract.RequiredItem,
                        }));
                    deltas.AddRange(needed.Deltas);
                    continue;
                }
            }

            var transaction = GameTransaction.Begin(State);
            var deltaStart = deltas.Count;
            bool ApplyOrRollback(WorldConsequence consequence, string fallback)
            {
                var applied = _engine.ApplyConsequence(consequence);
                deltas.AddRange(applied.Deltas);
                if (applied.Applied)
                {
                    return true;
                }

                transaction.Rollback();
                RemoveRangeFrom(deltas, deltaStart);
                deltas.AddRange(FailureDiagnostics(applied.Deltas));
                deltas.Add(new StateDelta(
                    "objectiveReturnSkipped",
                    promise.Id,
                    $"Objective return rolled back: {applied.Error ?? fallback}.",
                    new Dictionary<string, object?>
                    {
                        ["promiseId"] = promise.Id,
                        ["objectiveKind"] = contract.Kind,
                        ["giverEntityId"] = giver.Id.Value,
                        ["failure"] = applied.Error ?? fallback,
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                return false;
            }

            if (returnedItem is not null
                && !ApplyOrRollback(WorldConsequence.TransferItem(
                    $"objective_return:{giver.Id.Value}",
                    State.ControlledEntityId.Value,
                    "give",
                    returnedItem,
                    recipientEntityId: giver.Id.Value,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: State.ControlledEntityId.Value,
                    evidence: promise.Text,
                    reason: "A completed fetch objective transfers its concrete evidence to the giver.",
                    operation: "objectiveReturnItem",
                    details: new Dictionary<string, object?>
                    {
                        ["promiseId"] = promise.Id,
                        ["objectiveKind"] = contract.Kind,
                        ["giverEntityId"] = giver.Id.Value,
                        ["requiredItem"] = contract.RequiredItem,
                    }), "item_transfer_rejected"))
            {
                continue;
            }

            if (!ApplyOrRollback(WorldConsequence.UpdatePromise(
                $"objective_return:{giver.Id.Value}",
                promise.Id,
                status: "cleared",
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: giver.Id.Value,
                evidence: promise.Text,
                reason: "The player returned to the objective giver after satisfying its state contract.",
                operation: "objectiveReturned",
                details: new Dictionary<string, object?>
                {
                    ["objectiveKind"] = contract.Kind,
                    ["giverEntityId"] = giver.Id.Value,
                    ["playerVisible"] = false,
                }), "promise_update_rejected"))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(promise.SourceClaimId)
                && !ApplyOrRollback(WorldConsequence.UpdateClaim(
                    $"objective_return:{giver.Id.Value}",
                    promise.SourceClaimId!,
                    status: "applied",
                    boundPromiseId: promise.Id,
                    appliedTo: giver.Id.Value,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: giver.Id.Value,
                    evidence: promise.Text,
                    reason: "The returned objective applied its source claim.",
                    operation: "objectiveReturnClaimApplied",
                    details: new Dictionary<string, object?>
                    {
                        ["promiseId"] = promise.Id,
                        ["objectiveKind"] = contract.Kind,
                        ["giverEntityId"] = giver.Id.Value,
                    }), "claim_update_rejected"))
            {
                continue;
            }

            var memoryText = $"{giver.Name} learned that the sorcerer completed this objective: {promise.Text}";
            if (giver.Has<WantComponent>())
            {
                if (!ApplyOrRollback(WorldConsequence.UpdateWant(
                    $"objective_return:{giver.Id.Value}",
                    giver.Id.Value,
                    status: "satisfied",
                    stakes: "The requested work returned with an answer; later choices can create a new pressure.",
                    addTags: new[] { "objective_completed", contract.Kind, "satisfied_by_player" },
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: State.ControlledEntityId.Value,
                    evidence: promise.Text,
                    reason: "Returning a completed objective satisfied the giver's current pressure.",
                    operation: "objectiveReturnWant",
                    recordMemory: true,
                    memoryText: memoryText,
                    memoryProvenance: $"objective:{promise.Id}",
                    memoryShareable: true), "want_update_rejected"))
                {
                    continue;
                }
            }
            else if (!ApplyOrRollback(WorldConsequence.RecordMemory(
                $"objective_return:{giver.Id.Value}",
                giver.Id.Value,
                memoryText,
                $"objective:{promise.Id}",
                promise.Salience,
                shareable: true,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: promise.Text,
                reason: "The giver remembered why the objective completed.",
                operation: "objectiveReturnMemory"), "memory_rejected"))
            {
                continue;
            }

            if (giver.TryGet<FactionComponent>(out var faction)
                && State.Factions.Factions.Any(item => item.Id.Equals(faction.FactionId, StringComparison.OrdinalIgnoreCase))
                && !ApplyOrRollback(WorldConsequence.AdjustFactionStanding(
                    $"objective_return:{giver.Id.Value}",
                    faction.FactionId,
                    "gratitude",
                    1,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: giver.Id.Value,
                    evidence: promise.Text,
                    reason: "Concrete aid earned a small faction gratitude payoff.",
                    operation: "objectiveReturnStanding",
                    details: new Dictionary<string, object?>
                    {
                        ["promiseId"] = promise.Id,
                        ["giverEntityId"] = giver.Id.Value,
                    }), "standing_update_rejected"))
            {
                continue;
            }

            // Concrete reciprocity (Q46/Q47): help rendered is paid for, visibly. Weightier asks pay
            // more (salience-scaled), so quest income and component prices form one economy instead
            // of gratitude being the only -- and invisible -- wage.
            var payment = ObjectiveGoldPayment(promise.Salience);
            if (!ApplyOrRollback(WorldConsequence.ModifyInventory(
                $"objective_return:{giver.Id.Value}",
                State.ControlledEntityId.Value,
                "gold",
                op: "add",
                amount: payment,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: giver.Id.Value,
                evidence: promise.Text,
                reason: "A completed objective pays the promised consideration.",
                operation: "objectiveReturnPayment",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["objectiveKind"] = contract.Kind,
                    ["giverEntityId"] = giver.Id.Value,
                    ["gold"] = payment,
                }), "payment_rejected"))
            {
                continue;
            }

            if (!ApplyOrRollback(WorldConsequence.Message(
                $"objective_return:{giver.Id.Value}",
                $"Objective complete: {giver.Name} accepts what you learned and presses {payment} gold into your hand.",
                targetEntityId: giver.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: giver.Id.Value,
                evidence: promise.Text,
                reason: "Returning to the giver produced a cause-bearing completion receipt.",
                operation: "objectiveReturnMessage",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["objectiveKind"] = contract.Kind,
                    ["giverEntityId"] = giver.Id.Value,
                    ["gold"] = payment,
                }), "completion_message_rejected"))
            {
                continue;
            }

            transaction.Commit();
        }

        return deltas;
    }

    // Payment for a returned objective: 4 gold for a routine ask up to 12 for a weighty one
    // (salience 1..5) -- sized against component prices (grave salt 8, tincture 12) so a completed
    // quest funds roughly one meaningful purchase, keeping quest income and shop sinks in one economy.
    private static int ObjectiveGoldPayment(int salience) =>
        2 + (2 * Math.Clamp(salience, 1, 5));

    private IReadOnlyList<StateDelta> CompleteContactObjectives(Entity contact)
    {
        if (!contact.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var promise in State.PromiseLedger.Promises
            .Where(promise => anchor.PromiseIds.Contains(promise.Id, StringComparer.OrdinalIgnoreCase))
            .Where(promise => promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase))
            .Where(promise => NormalizeToken(promise.RealizationKind ?? promise.Kind, "") == "person")
            .Where(promise => PromiseObjectiveContracts.For(State, promise)?.Kind == "meet")
            .ToArray())
        {
            var transaction = GameTransaction.Begin(State);
            var deltaStart = deltas.Count;
            var updated = _engine.ApplyConsequence(WorldConsequence.UpdatePromise(
                $"talk:{contact.Id.Value}",
                promise.Id,
                status: "cleared",
                boundTargetId: contact.Id.Value,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: contact.Id.Value,
                evidence: promise.Text,
                reason: "The player reached and spoke with the promised contact.",
                operation: "objectiveContactMet",
                details: new Dictionary<string, object?>
                {
                    ["contactId"] = contact.Id.Value,
                    ["contactName"] = contact.Name,
                    ["sourceClaimId"] = promise.SourceClaimId,
                }));
            deltas.AddRange(updated.Deltas);
            if (!updated.Applied)
            {
                transaction.Rollback();
                RemoveRangeFrom(deltas, deltaStart);
                deltas.AddRange(FailureDiagnostics(updated.Deltas));
                continue;
            }

            var claimUpdated = _engine.ApplyConsequence(WorldConsequence.UpdateClaim(
                $"talk:{contact.Id.Value}",
                promise.SourceClaimId!,
                status: "applied",
                boundPromiseId: promise.Id,
                appliedTo: contact.Id.Value,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: contact.Id.Value,
                evidence: promise.Text,
                reason: "Meeting the promised contact applied the source claim.",
                operation: "objectiveClaimApplied",
                details: new Dictionary<string, object?>
                {
                    ["contactId"] = contact.Id.Value,
                    ["contactName"] = contact.Name,
                    ["promiseId"] = promise.Id,
                }));
            deltas.AddRange(claimUpdated.Deltas);
            if (!claimUpdated.Applied)
            {
                transaction.Rollback();
                RemoveRangeFrom(deltas, deltaStart);
                deltas.AddRange(FailureDiagnostics(claimUpdated.Deltas));
                continue;
            }

            var message = _engine.ApplyConsequence(WorldConsequence.Message(
                $"talk:{contact.Id.Value}",
                $"Objective complete: you found {contact.Name} and heard what they know.",
                targetEntityId: contact.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: contact.Id.Value,
                evidence: promise.Text,
                reason: "Meeting a promised contact produced a legible completion receipt.",
                operation: "objectiveCompleteMessage",
                details: new Dictionary<string, object?>
                {
                    ["contactId"] = contact.Id.Value,
                    ["contactName"] = contact.Name,
                    ["promiseId"] = promise.Id,
                    ["claimId"] = promise.SourceClaimId,
                }));
            deltas.AddRange(message.Deltas);
            if (!message.Applied)
            {
                transaction.Rollback();
                RemoveRangeFrom(deltas, deltaStart);
                deltas.AddRange(FailureDiagnostics(message.Deltas));
                continue;
            }

            transaction.Commit();
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyClaimSeeds(
        Entity source,
        string trigger,
        IReadOnlyList<ClaimSeed>? suppliedSeeds = null)
    {
        var seeds = suppliedSeeds;
        if (seeds is null && source.TryGet<ClaimSourceComponent>(out var claimSource))
        {
            seeds = claimSource.Claims;
        }

        if (seeds is null || seeds.Count == 0)
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        for (var index = 0; index < seeds.Count; index++)
        {
            var seed = seeds[index];
            if (string.IsNullOrWhiteSpace(seed.Text))
            {
                continue;
            }

            var sourceKey = $"{trigger}:{source.Id.Value}:{index}";
            var tags = new[] { "claim_source", trigger, source.Id.Value }
                .Concat(seed.Tags ?? Array.Empty<string>())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var transaction = GameTransaction.Begin(State);
            var deltaStart = deltas.Count;
            var claim = _engine.ApplyConsequence(WorldConsequence.RecordClaim(
                sourceKey,
                source.Id.Value,
                PlayerSoulId(),
                seed.Text,
                seed.Category,
                seed.Subject,
                seed.Salience,
                seed.Confidence,
                seed.PlayerVisible,
                tags,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: source.Id.Value,
                evidence: seed.Text,
                reason: $"A {trigger} interaction surfaced a structured claim seed.",
                operation: $"{trigger}Claim",
                details: new Dictionary<string, object?>
                {
                    ["claimSeedIndex"] = index,
                    ["sourceTrigger"] = trigger,
                    ["sourceEntityId"] = source.Id.Value,
                    ["playerVisible"] = seed.PlayerVisible,
                    ["consequenceVisibility"] = WorldConsequenceVisibility.Hidden,
                }));
            deltas.AddRange(claim.Deltas);
            var duplicateClaim = claim.Details.TryGetValue("duplicate", out var duplicate) && duplicate is true;
            if (!claim.Applied)
            {
                RollBackClaimSeedTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    source,
                    trigger,
                    index,
                    claim.Deltas,
                    claim.Error ?? "claim_record_rejected");
                continue;
            }

            if (duplicateClaim)
            {
                transaction.Commit();
                continue;
            }

            if (claim.Applied
                && !string.IsNullOrWhiteSpace(claim.TargetId)
                && State.Claims.Records.FirstOrDefault(record =>
                    record.Id.Equals(claim.TargetId, StringComparison.OrdinalIgnoreCase)) is { } claimRecord
                && RumorSystem.ConsequenceFromClaim(State, claimRecord, "claim_source") is { } rumor)
            {
                var rumorApplied = _engine.ApplyConsequence(rumor);
                deltas.AddRange(rumorApplied.Deltas);
                if (!rumorApplied.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        rumorApplied.Deltas,
                        rumorApplied.Error ?? "rumor_mint_rejected");
                    continue;
                }
            }

            if (!seed.BindAsPromise)
            {
                transaction.Commit();
                continue;
            }

            var claimSeedTriggerHint = string.IsNullOrWhiteSpace(seed.TriggerHint) ? trigger : seed.TriggerHint;
            var promise = _engine.ApplyConsequence(WorldConsequence.CreatePromise(
                sourceKey,
                string.IsNullOrWhiteSpace(seed.PromiseKind) ? "rumor" : seed.PromiseKind,
                seed.ObjectiveText ?? seed.Text,
                triggerHint: claimSeedTriggerHint,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: source.Id.Value,
                evidence: seed.Text,
                reason: $"A {trigger} interaction bound a structured claim seed as a promise.",
                operation: $"{trigger}ClaimPromise",
                playerVisible: seed.PlayerVisible,
                salience: seed.Salience,
                subject: seed.Subject,
                claimedPlace: seed.ClaimedPlace,
                realizationKind: seed.RealizationKind,
                bindPlace: GameSession.ShouldBindToRegion(claimSeedTriggerHint, seed.RealizationKind) ? State.RegionId : null,
                sourceClaimId: claim.TargetId,
                sourceSpeakerId: source.Id.Value,
                sourceListenerSoulId: PlayerSoulId(),
                sourceConfidence: seed.Confidence,
                useCurrentRegionAsClaimedPlace: string.IsNullOrWhiteSpace(seed.ClaimedPlace),
                emitMessage: false,
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = claim.TargetId,
                    ["claimSeedIndex"] = index,
                    ["sourceTrigger"] = trigger,
                    ["sourceEntityId"] = source.Id.Value,
                    ["playerVisible"] = seed.PlayerVisible,
                    ["consequenceVisibility"] = WorldConsequenceVisibility.Hidden,
                }));
            deltas.AddRange(promise.Deltas);
            if (!promise.Applied)
            {
                RollBackClaimSeedTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    source,
                    trigger,
                    index,
                    promise.Deltas,
                    promise.Error ?? "promise_create_rejected");
                continue;
            }

            if (!string.IsNullOrWhiteSpace(claim.TargetId))
            {
                var claimBound = _engine.ApplyConsequence(WorldConsequence.UpdateClaim(
                    sourceKey,
                    claim.TargetId!,
                    status: "bound",
                    boundPromiseId: promise.TargetId,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: source.Id.Value,
                    evidence: seed.Text,
                    reason: "The structured claim seed bound a promise.",
                    operation: $"{trigger}ClaimBound",
                    details: new Dictionary<string, object?>
                    {
                        ["claimSeedIndex"] = index,
                        ["promiseId"] = promise.TargetId,
                        ["sourceTrigger"] = trigger,
                        ["sourceEntityId"] = source.Id.Value,
                        ["playerVisible"] = seed.PlayerVisible,
                        ["consequenceVisibility"] = WorldConsequenceVisibility.Hidden,
                    }));
                deltas.AddRange(claimBound.Deltas);
                if (!claimBound.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        claimBound.Deltas,
                        claimBound.Error ?? "claim_status_rejected");
                    continue;
                }
            }

            var objectiveKind = TagValue(seed.Tags, "objective_kind:");
            var objectiveItem = TagValue(seed.Tags, "objective_item:");
            if (objectiveKind?.Equals("delivery", StringComparison.OrdinalIgnoreCase) == true
                && !string.IsNullOrWhiteSpace(objectiveItem))
            {
                var deliveryItem = _engine.ApplyConsequence(WorldConsequence.ModifyInventory(
                    sourceKey,
                    State.ControlledEntityId.Value,
                    objectiveItem,
                    op: "add",
                    amount: 1,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: source.Id.Value,
                    evidence: seed.Text,
                    reason: "A delivery objective placed its concrete parcel in the player's inventory.",
                    operation: "objectiveDeliveryItem",
                    details: new Dictionary<string, object?>
                    {
                        ["claimId"] = claim.TargetId,
                        ["promiseId"] = promise.TargetId,
                        ["objectiveKind"] = objectiveKind,
                        ["giverEntityId"] = source.Id.Value,
                    }));
                deltas.AddRange(deliveryItem.Deltas);
                if (!deliveryItem.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        deliveryItem.Deltas,
                        deliveryItem.Error ?? "delivery_item_rejected");
                    continue;
                }
            }

            if (!string.IsNullOrWhiteSpace(seed.SpokenText)
                && (trigger.Equals("talk", StringComparison.OrdinalIgnoreCase)
                    || trigger.Equals("rescue", StringComparison.OrdinalIgnoreCase)))
            {
                var spoken = _engine.ApplyConsequence(WorldConsequence.Message(
                    sourceKey,
                    $"{source.Name} says, \"{seed.SpokenText.Trim()}\"",
                    targetEntityId: source.Id.Value,
                    visibility: WorldConsequenceVisibility.Message,
                    sourceEntityId: source.Id.Value,
                    evidence: seed.Text,
                    reason: "A generated objective handoff became audible to the player.",
                    operation: "objectiveHandoffMessage",
                    details: new Dictionary<string, object?>
                    {
                        ["claimId"] = claim.TargetId,
                        ["promiseId"] = promise.TargetId,
                        ["sourceTrigger"] = trigger,
                        ["sourceEntityId"] = source.Id.Value,
                        ["objectiveText"] = seed.ObjectiveText ?? seed.Text,
                    }));
                deltas.AddRange(spoken.Deltas);
                if (!spoken.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        spoken.Deltas,
                        spoken.Error ?? "objective_handoff_message_rejected");
                    continue;
                }

                var remembered = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
                    sourceKey,
                    source.Id.Value,
                    $"{source.Name} sent the sorcerer onward: {seed.ObjectiveText ?? seed.Text}",
                    "objective_handoff",
                    seed.Salience,
                    shareable: true,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: source.Id.Value,
                    evidence: seed.Text,
                    reason: "The speaker remembered giving an actionable lead.",
                    operation: "objectiveHandoffMemory",
                    details: new Dictionary<string, object?>
                    {
                        ["claimId"] = claim.TargetId,
                        ["promiseId"] = promise.TargetId,
                        ["sourceTrigger"] = trigger,
                        ["objectiveText"] = seed.ObjectiveText ?? seed.Text,
                    }));
                deltas.AddRange(remembered.Deltas);
                if (!remembered.Applied)
                {
                    RollBackClaimSeedTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        source,
                        trigger,
                        index,
                        remembered.Deltas,
                        remembered.Error ?? "objective_handoff_memory_rejected");
                    continue;
                }
            }

            transaction.Commit();
        }

        return deltas;
    }

    private static void RollBackClaimSeedTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        Entity source,
        string trigger,
        int claimSeedIndex,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "claimSeedSkipped",
            source.Id.Value,
            $"Claim seed rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["sourceEntityId"] = source.Id.Value,
                ["sourceTrigger"] = trigger,
                ["claimSeedIndex"] = claimSeedIndex,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }
}
