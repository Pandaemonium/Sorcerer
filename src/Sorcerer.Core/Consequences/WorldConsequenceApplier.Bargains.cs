using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

/// <summary>Typed social terms. Speech may propose them; only this handler can make them real.</summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyOfferBargain(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var promiseId = FirstNonBlank(ReadString(payload, "promiseId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(promiseId))
        {
            return Reject(consequence, "Bargain offer did not include a promise id.");
        }

        var promise = Promise(promiseId);
        if (promise is null || promise.Status is "cleared" or "fulfilled" or "breached")
        {
            return Reject(consequence, "Bargain offer does not refer to an active promise.");
        }

        if (!payload.TryGetValue("offer", out var rawOffer) || rawOffer is not BargainOffer offer)
        {
            return Reject(consequence, "Bargain offer did not include typed options.");
        }

        if (!TryValidateOffer(offer, out var normalized, out var error))
        {
            return Reject(consequence, error ?? "Bargain offer is invalid.");
        }

        var updated = _state.PromiseLedger.SetBargainOffer(promise.Id, normalized);
        if (updated is null)
        {
            return Reject(consequence, "Bargain offer could not be attached to its promise.");
        }

        _state.PromiseLedger.SetStatus(promise.Id, "offered");
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            $"{EntityById(normalized.ClaimantEntityId)?.Name ?? normalized.ClaimantEntityId} offers {normalized.Options.Count} concrete way(s) to settle: {normalized.Summary}")!;
        var messages = AddMessageIfAllowed(consequence, payload, summary) ? new[] { summary } : Array.Empty<string>();
        return Applied(
            consequence,
            promise.Id,
            messages,
            new StateDelta(
                ReadString(payload, "operation") ?? "offerBargain",
                promise.Id,
                summary,
                Details(
                    consequence,
                    ("promiseId", promise.Id),
                    ("claimantEntityId", normalized.ClaimantEntityId),
                    ("optionIds", normalized.Options.Select(option => option.Id).ToArray()),
                    ("playerVisible", true))),
            ("promiseId", promise.Id));
    }

    private WorldConsequenceApplyResult ApplyAcceptBargain(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var promiseId = FirstNonBlank(ReadString(payload, "promiseId"), consequence.TargetEntityId);
        var optionId = FirstNonBlank(ReadString(payload, "optionId"), ReadString(payload, "option"));
        var actorId = FirstNonBlank(ReadString(payload, "actorEntityId"), consequence.SourceEntityId);
        var promise = string.IsNullOrWhiteSpace(promiseId) ? null : Promise(promiseId);
        if (promise?.BargainOffer is not { } offer)
        {
            return Reject(consequence, "There is no typed bargain offer to accept.");
        }

        if (offer.ExpiresTurn is { } expiry && expiry < _state.Turn)
        {
            return Reject(consequence, "Those terms have expired.");
        }

        var option = offer.Options.FirstOrDefault(candidate =>
            candidate.Id.Equals(optionId, StringComparison.OrdinalIgnoreCase)
            || candidate.Label.Equals(optionId, StringComparison.OrdinalIgnoreCase));
        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        var claimant = EntityById(offer.ClaimantEntityId);
        if (option is null || actor is null || claimant is null)
        {
            return Reject(consequence, option is null ? "That bargain option does not exist." : "A bargain participant no longer exists.");
        }

        var snapshot = GameStateSnapshot.Capture(_state);
        var childDeltas = new List<StateDelta>();
        var fulfilledTerms = new List<BargainTerm>();
        foreach (var term in option.Terms)
        {
            if (term.Kind.Equals(BargainTermKinds.Deadline, StringComparison.OrdinalIgnoreCase))
            {
                fulfilledTerms.Add(term with { Status = "constraint" });
                continue;
            }

            if (term.Kind.Equals(BargainTermKinds.Service, StringComparison.OrdinalIgnoreCase))
            {
                fulfilledTerms.Add(term with { Status = "pending" });
                continue;
            }

            WorldConsequence? child = term.Kind.ToLowerInvariant() switch
            {
                BargainTermKinds.Currency => WorldConsequence.TransferItem(
                    consequence.Source,
                    actor.Id.Value,
                    "give",
                    "gold",
                    term.Quantity,
                    recipientEntityId: claimant.Id.Value,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: actor.Id.Value,
                    evidence: consequence.Evidence,
                    reason: "A selected currency term is paid atomically at acceptance.",
                    operation: "payBargainCurrency"),
                BargainTermKinds.Item => WorldConsequence.TransferItem(
                    consequence.Source,
                    actor.Id.Value,
                    "give",
                    term.ResourceId!,
                    term.Quantity,
                    recipientEntityId: claimant.Id.Value,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: actor.Id.Value,
                    evidence: consequence.Evidence,
                    reason: "A selected item term is transferred atomically at acceptance.",
                    operation: "payBargainItem"),
                BargainTermKinds.Standing => WorldConsequence.AdjustFactionStanding(
                    consequence.Source,
                    term.FactionId!,
                    term.StandingAxis!,
                    term.StandingDelta,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: actor.Id.Value,
                    evidence: consequence.Evidence,
                    reason: "A selected standing concession is accepted atomically.",
                    operation: "applyBargainStanding"),
                BargainTermKinds.Concession => WorldConsequence.AddCanon(
                    consequence.Source,
                    "concession",
                    claimant.Id.Value,
                    term.Text,
                    term.Text,
                    new[] { "bargain", "concession", promise!.Id },
                    sourceEntityId: actor.Id.Value,
                    evidence: consequence.Evidence,
                    reason: "A selected concession becomes durable canon.",
                    operation: "recordBargainConcession"),
                _ => null,
            };
            if (child is null)
            {
                snapshot.Restore(_state);
                return Reject(consequence, $"Unsupported bargain term: {term.Kind}.");
            }

            var applied = Apply(child);
            if (!applied.Applied)
            {
                snapshot.Restore(_state);
                return Reject(consequence, applied.Error ?? $"Could not fulfill term {term.Id}.");
            }

            childDeltas.AddRange(applied.Deltas);
            fulfilledTerms.Add(term with { Status = "fulfilled" });
        }

        var hasPending = fulfilledTerms.Any(term => term.Status.Equals("pending", StringComparison.OrdinalIgnoreCase));
        var agreement = new BargainAgreement(
            claimant.Id.Value,
            option.Id,
            fulfilledTerms,
            _state.Turn,
            hasPending ? "active" : "fulfilled",
            SoulIdFor(actor));
        _state.PromiseLedger.SetBargainOffer(promise!.Id, null);
        _state.PromiseLedger.SetBargainAgreement(promise.Id, agreement, hasPending ? "agreement" : "cleared");

        var bond = Apply(WorldConsequence.UpdateBond(
            consequence.Source,
            claimant.Id.Value,
            SoulIdFor(actor),
            loyaltyDelta: 5,
            fearDelta: 0,
            admirationDelta: 0,
            resentmentDelta: -2,
            posture: hasPending ? "agreement" : "settled",
            sourceEntityId: actor.Id.Value,
            evidence: consequence.Evidence,
            reason: "Accepted enforceable terms make the claimant stand down.",
            operation: "bargainStandDown",
            maxDelta: 5));
        if (!bond.Applied)
        {
            snapshot.Restore(_state);
            return Reject(consequence, bond.Error ?? "The claimant could not honor the bargain.");
        }
        childDeltas.AddRange(bond.Deltas);

        var control = Apply(WorldConsequence.UpdateControl(
            consequence.Source,
            claimant.Id.Value,
            "ai",
            aiPolicyId: "resident",
            sourceEntityId: actor.Id.Value,
            evidence: consequence.Evidence,
            reason: "The claimant stops attacking once concrete terms are accepted.",
            operation: "bargainStandDownControl"));
        if (!control.Applied)
        {
            snapshot.Restore(_state);
            return Reject(consequence, control.Error ?? "The claimant could not stand down.");
        }
        childDeltas.AddRange(control.Deltas);

        var summary = hasPending
            ? $"You accept {option.Label}. Immediate terms are paid; {PendingTermText(fulfilledTerms)} remains in the journal."
            : $"You fulfill {option.Label}. {claimant.Name}'s claim is cleared.";
        var messages = MaybeVisibleMessage(consequence, summary);
        var delta = new StateDelta(
            ReadString(payload, "operation") ?? "acceptBargain",
            promise.Id,
            summary,
            Details(
                consequence,
                ("promiseId", promise.Id),
                ("optionId", option.Id),
                ("claimantEntityId", claimant.Id.Value),
                ("agreementStatus", agreement.Status),
                ("termStatuses", fulfilledTerms.ToDictionary(term => term.Id, term => (object?)term.Status)),
                ("playerVisible", true)));
        return new WorldConsequenceApplyResult(
            true,
            promise.Id,
            null,
            messages,
            childDeltas.Concat(new[] { delta }).ToArray(),
            Details(consequence, ("promiseId", promise.Id), ("optionId", option.Id)));
    }

    private WorldConsequenceApplyResult ApplyFulfillBargain(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var promiseId = FirstNonBlank(ReadString(payload, "promiseId"), consequence.TargetEntityId);
        var termId = FirstNonBlank(ReadString(payload, "termId"), ReadString(payload, "term"));
        var actorId = FirstNonBlank(ReadString(payload, "actorEntityId"), consequence.SourceEntityId);
        var action = NormalizeToken(ReadString(payload, "action") ?? "fulfill", "fulfill");
        var promise = string.IsNullOrWhiteSpace(promiseId) ? null : Promise(promiseId);
        if (promise?.BargainAgreement is not { } agreement || !agreement.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(consequence, "There is no active bargain agreement to fulfill.");
        }

        if (action.Equals("breach", StringComparison.OrdinalIgnoreCase))
        {
            var breachSnapshot = GameStateSnapshot.Capture(_state);
            var childDeltas = new List<StateDelta>();
            var breachClaimant = EntityById(agreement.ClaimantEntityId);
            var actorSoulId = FirstNonBlank(agreement.ActorSoulId, SoulIdFor(_state.ControlledEntity));
            if (breachClaimant is not null && !string.IsNullOrWhiteSpace(actorSoulId))
            {
                var bond = Apply(WorldConsequence.UpdateBond(
                    consequence.Source,
                    breachClaimant.Id.Value,
                    actorSoulId,
                    loyaltyDelta: -5,
                    fearDelta: 0,
                    admirationDelta: -2,
                    resentmentDelta: 5,
                    posture: "betrayer",
                    sourceEntityId: consequence.SourceEntityId,
                    evidence: consequence.Evidence,
                    reason: "Breaking accepted terms reverses the claimant's stand-down and becomes a hostile relationship fact.",
                    operation: "bargainBreachBond",
                    maxDelta: 5));
                if (!bond.Applied)
                {
                    breachSnapshot.Restore(_state);
                    return Reject(consequence, bond.Error ?? "The broken agreement could not update its claimant.");
                }

                childDeltas.AddRange(bond.Deltas);
                if (breachClaimant.Has<ControllerComponent>())
                {
                    var control = Apply(WorldConsequence.UpdateControl(
                        consequence.Source,
                        breachClaimant.Id.Value,
                        "ai",
                        aiPolicyId: "hostile",
                        sourceEntityId: consequence.SourceEntityId,
                        evidence: consequence.Evidence,
                        reason: "An agreement claimant resumes hostile agency after a breach.",
                        operation: "bargainBreachControl"));
                    if (!control.Applied)
                    {
                        breachSnapshot.Restore(_state);
                        return Reject(consequence, control.Error ?? "The claimant could not react to the breach.");
                    }

                    childDeltas.AddRange(control.Deltas);
                }
            }

            var breached = agreement with
            {
                Status = "breached",
                Terms = agreement.Terms.Select(term =>
                    term.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)
                        ? term with { Status = "breached" }
                        : term).ToArray(),
            };
            _state.PromiseLedger.SetBargainAgreement(promise.Id, breached, "breached");
            var breachSummary = $"Agreement breached: {promise.Text}";
            var breachMessages = MaybeVisibleMessage(consequence, breachSummary);
            var breachDelta = new StateDelta(
                    ReadString(payload, "operation") ?? "breachBargain",
                    promise.Id,
                    breachSummary,
                    Details(consequence, ("promiseId", promise.Id), ("agreementStatus", "breached"), ("playerVisible", true)));
            return new WorldConsequenceApplyResult(
                true,
                promise.Id,
                null,
                breachMessages,
                childDeltas.Concat(new[] { breachDelta }).ToArray(),
                Details(consequence, ("promiseId", promise.Id), ("agreementStatus", "breached")));
        }

        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        var claimant = EntityById(agreement.ClaimantEntityId);
        var term = agreement.Terms.FirstOrDefault(candidate => candidate.Id.Equals(termId, StringComparison.OrdinalIgnoreCase));
        if (actor is null || claimant is null || term is null)
        {
            return Reject(consequence, term is null ? "That agreement term does not exist." : "An agreement participant no longer exists.");
        }

        if (!term.Kind.Equals(BargainTermKinds.Service, StringComparison.OrdinalIgnoreCase)
            || !term.Status.Equals("pending", StringComparison.OrdinalIgnoreCase))
        {
            return Reject(consequence, "Only a pending service term can be performed later.");
        }

        if (!actor.TryGet<PositionComponent>(out var actorPosition)
            || !claimant.TryGet<PositionComponent>(out var claimantPosition)
            || GameEngine.StepDistance(actorPosition.Position, claimantPosition.Position) > 2)
        {
            return Reject(consequence, $"You must be near {claimant.Name} to perform that service.");
        }

        if (!claimant.TryGet<WantComponent>(out var want)
            || !want.Id.Equals(term.ResourceId, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(consequence, $"The service term no longer identifies a concrete want belonging to {claimant.Name}.");
        }

        var snapshot = GameStateSnapshot.Capture(_state);
        var wantResult = Apply(WorldConsequence.UpdateWant(
            consequence.Source,
            claimant.Id.Value,
            status: "satisfied",
            addTags: new[] { "bargain_fulfilled", "satisfied_by_player" },
            sourceEntityId: actor.Id.Value,
            evidence: consequence.Evidence,
            reason: "Performing a typed service fulfills the claimant's identified want before the agreement can clear.",
            operation: "fulfillBargainWant",
            recordMemory: true,
            memoryText: $"The sorcerer performed the agreed service: {term.Text}",
            memoryProvenance: $"bargain:{promise.Id}:{term.Id}",
            memorySalience: 3,
            memoryShareable: true));
        if (!wantResult.Applied)
        {
            snapshot.Restore(_state);
            return Reject(consequence, wantResult.Error ?? "The agreed service did not fulfill its claimant's want.");
        }

        var terms = agreement.Terms
            .Select(candidate => candidate.Id.Equals(term.Id, StringComparison.OrdinalIgnoreCase)
                ? candidate with { Status = "fulfilled" }
                : candidate)
            .ToArray();
        var complete = terms.All(candidate =>
            candidate.Kind.Equals(BargainTermKinds.Deadline, StringComparison.OrdinalIgnoreCase)
            || candidate.Status.Equals("fulfilled", StringComparison.OrdinalIgnoreCase));
        var updatedAgreement = agreement with { Terms = terms, Status = complete ? "fulfilled" : "active" };
        _state.PromiseLedger.SetBargainAgreement(promise.Id, updatedAgreement, complete ? "cleared" : "agreement");

        var summary = complete
            ? $"You perform {term.Text}. The agreement with {claimant.Name} is fulfilled and clears."
            : $"You perform {term.Text}. Other agreement terms remain.";
        var messages = MaybeVisibleMessage(consequence, summary);
        var delta = new StateDelta(
            ReadString(payload, "operation") ?? "fulfillBargain",
            promise.Id,
            summary,
            Details(
                consequence,
                ("promiseId", promise.Id),
                ("termId", term.Id),
                ("agreementStatus", updatedAgreement.Status),
                ("playerVisible", true)));
        return new WorldConsequenceApplyResult(
            true,
            promise.Id,
            null,
            messages,
            wantResult.Deltas.Concat(new[] { delta }).ToArray(),
            Details(consequence, ("promiseId", promise.Id), ("termId", term.Id)));
    }

    private WorldPromise? Promise(string id) => _state.PromiseLedger.Promises.FirstOrDefault(promise =>
        promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    private bool TryValidateOffer(BargainOffer offer, out BargainOffer normalized, out string? error)
    {
        normalized = offer;
        error = null;
        var claimant = EntityById(offer.ClaimantEntityId);
        if (claimant is null)
        {
            error = "Bargain claimant does not exist.";
            return false;
        }

        if (offer.Options.Count is < 1 or > 4)
        {
            error = "A bargain must contain one to four alternatives.";
            return false;
        }

        var optionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new List<BargainOption>();
        foreach (var sourceOption in offer.Options)
        {
            var optionId = NormalizeToken(sourceOption.Id, "option");
            if (!optionIds.Add(optionId) || sourceOption.Terms.Count is < 1 or > 5)
            {
                error = "Bargain option ids must be unique and contain one to five terms.";
                return false;
            }

            var termIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var terms = new List<BargainTerm>();
            foreach (var sourceTerm in sourceOption.Terms)
            {
                var termId = NormalizeToken(sourceTerm.Id, "term");
                var kind = NormalizeToken(sourceTerm.Kind, "");
                if (!termIds.Add(termId) || !BargainTermKinds.All.Contains(kind))
                {
                    error = "Bargain term ids must be unique and use a supported typed term.";
                    return false;
                }

                if ((kind is BargainTermKinds.Item or BargainTermKinds.Service) && string.IsNullOrWhiteSpace(sourceTerm.ResourceId)
                    || kind == BargainTermKinds.Standing && (string.IsNullOrWhiteSpace(sourceTerm.FactionId)
                        || string.IsNullOrWhiteSpace(sourceTerm.StandingAxis) || sourceTerm.StandingDelta == 0)
                    || kind == BargainTermKinds.Deadline && (sourceTerm.DueTurn is null || sourceTerm.DueTurn <= _state.Turn))
                {
                    error = $"Bargain term {termId} is missing required typed fields.";
                    return false;
                }

                if (kind == BargainTermKinds.Service
                    && (!claimant.TryGet<WantComponent>(out var claimantWant)
                        || !claimantWant.Id.Equals(sourceTerm.ResourceId, StringComparison.OrdinalIgnoreCase)))
                {
                    error = $"Bargain service {termId} must identify the claimant's exact want id.";
                    return false;
                }

                terms.Add(sourceTerm with
                {
                    Id = termId,
                    Kind = kind,
                    Text = string.IsNullOrWhiteSpace(sourceTerm.Text) ? kind : sourceTerm.Text.Trim(),
                    Quantity = Math.Clamp(sourceTerm.Quantity, 1, 99),
                    StandingDelta = Math.Clamp(sourceTerm.StandingDelta, -5, 5),
                    DueTurn = sourceTerm.DueTurn is null ? null : Math.Min(sourceTerm.DueTurn.Value, _state.Turn + 999),
                    Status = "pending",
                });
            }

            if (terms.All(term => term.Kind.Equals(BargainTermKinds.Deadline, StringComparison.OrdinalIgnoreCase)))
            {
                error = "A deadline can constrain a settlement but cannot be the settlement by itself.";
                return false;
            }

            if (terms.Any(term => term.Kind.Equals(BargainTermKinds.Deadline, StringComparison.OrdinalIgnoreCase))
                && !terms.Any(term => term.Kind.Equals(BargainTermKinds.Service, StringComparison.OrdinalIgnoreCase)))
            {
                error = "A bargain deadline must constrain a service term in the same option.";
                return false;
            }

            options.Add(new BargainOption(
                optionId,
                string.IsNullOrWhiteSpace(sourceOption.Label) ? optionId.Replace('_', ' ') : sourceOption.Label.Trim(),
                terms));
        }

        normalized = offer with
        {
            Summary = string.IsNullOrWhiteSpace(offer.Summary) ? "Choose one concrete settlement." : offer.Summary.Trim(),
            Options = options,
            CreatedTurn = _state.Turn,
            ExpiresTurn = offer.ExpiresTurn is null ? null : Math.Clamp(offer.ExpiresTurn.Value, _state.Turn + 1, _state.Turn + 999),
        };
        return true;
    }

    private static string PendingTermText(IEnumerable<BargainTerm> terms) =>
        string.Join("; ", terms.Where(term => term.Status.Equals("pending", StringComparison.OrdinalIgnoreCase)).Select(term => term.Text));
}
