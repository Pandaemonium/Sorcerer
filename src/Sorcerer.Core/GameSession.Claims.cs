using System.Diagnostics;
using System.Text.Json;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

/// <summary>
/// <see cref="GameSession"/> claim intake and binding: applying completed dialogue claim extractions, binding claims as promises, and the claim/promise realization vocabulary.
/// Split from the GameSession orchestrator (Phase 0.3); ExecuteAsync, the command
/// switch, View/Observation, and the post-action pipeline stay in GameSession.cs.
/// </summary>
public sealed partial class GameSession
{
    private async Task<ActionResult> FlushPendingClaimExtractionsAsync(ActionResult result)
    {
        if (_pendingClaimExtractions.Count == 0)
        {
            return result;
        }

        var pendingCount = _pendingClaimExtractions.Count;
        try
        {
            await Task.WhenAll(_pendingClaimExtractions.Take(pendingCount).Select(pending => pending.Task));
        }
        catch
        {
            // Faulted and canceled extraction tasks are converted to explicit deltas below.
        }

        return ApplyCompletedClaimExtractions(result, pendingCount);
    }

    private ActionResult ApplyCompletedClaimExtractions(ActionResult result, int maxExclusive)
    {
        if (maxExclusive <= 0 || _pendingClaimExtractions.Count == 0)
        {
            return result;
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        var extractionRecords = new List<DialogueClaimExtractionRecord>();
        var parseRecords = new List<DialogueParseRecord>();
        var completed = _pendingClaimExtractions
            .Take(maxExclusive)
            .Where(pending => pending.Task.IsCompleted)
            .ToArray();
        foreach (var pending in completed)
        {
            _pendingClaimExtractions.Remove(pending);
            if (pending.Task.IsFaulted)
            {
                var error = pending.Task.Exception?.GetBaseException().Message ?? "unknown claim extraction error";
                extractionRecords.Add(FailedClaimExtractionRecord(
                    pending,
                    _dialogueParser.Name,
                    error));
                parseRecords.Add(FailedDialogueParseRecord(
                    pending,
                    _dialogueParser.Name,
                    error));
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    $"Dialogue claim extraction failed: {error}",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = _dialogueParser.Name,
                        ["error"] = error,
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            if (pending.Task.IsCanceled)
            {
                extractionRecords.Add(FailedClaimExtractionRecord(
                    pending,
                    _dialogueParser.Name,
                    "canceled"));
                parseRecords.Add(FailedDialogueParseRecord(
                    pending,
                    _dialogueParser.Name,
                    "canceled"));
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    pending.Request.SpeakerId,
                    "Dialogue claim extraction was canceled.",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = _dialogueParser.Name,
                        ["error"] = "canceled",
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            var materialized = pending.Task.Result;
            var extraction = materialized.Parse;
            var parseRequest = materialized.Request;
            var claims = extraction.Proposals?.Claims ?? Array.Empty<DialogueClaimProposal>();
            extractionRecords.Add(new DialogueClaimExtractionRecord(
                parseRequest,
                extraction.Provider,
                extraction.RawText,
                extraction.TechnicalFailure,
                extraction.Error,
                claims,
                pending.RequiresSpokenTextSupport));
            parseRecords.Add(new DialogueParseRecord(
                parseRequest,
                extraction.Provider,
                extraction.RawText,
                extraction.TechnicalFailure,
                extraction.Error,
                extraction.Proposals,
                pending.RequiresSpokenTextSupport,
                materialized.ParserRoute));
            if (extraction.TechnicalFailure)
            {
                deltas.Add(new StateDelta(
                    "claimExtractionFailed",
                    parseRequest.SpeakerId,
                    $"Dialogue claim extraction failed: {extraction.Error ?? "unknown error"}",
                    new Dictionary<string, object?>
                    {
                        ["provider"] = extraction.Provider,
                        ["error"] = extraction.Error,
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            ApplyDialogueProposalSet(
                ParserPreparedTurn(parseRequest),
                parseRequest,
                extraction.Provider,
                string.Join(" ", parseRequest.DialogueLines),
                intent: null,
                extraction.Proposals,
                pending.RequiresSpokenTextSupport,
                parserOrigin: true,
                messages,
                deltas);
        }

        if (messages.Count == 0 && deltas.Count == 0 && extractionRecords.Count == 0 && parseRecords.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(messages).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
            DialogueClaimExtractions = result.DialogueClaimExtractions.Concat(extractionRecords).ToArray(),
            DialogueParses = result.DialogueParses.Concat(parseRecords).ToArray(),
        };
    }

    private static DialogueClaimExtractionRecord FailedClaimExtractionRecord(
        PendingClaimExtraction pending,
        string provider,
        string error) =>
        new(
            pending.Request,
            provider,
            RawText: "",
            TechnicalFailure: true,
            Error: error,
            Claims: Array.Empty<DialogueClaimProposal>(),
            pending.RequiresSpokenTextSupport);

    private static DialogueParseRecord FailedDialogueParseRecord(
        PendingClaimExtraction pending,
        string provider,
        string error) =>
        new(
            pending.Request,
            provider,
            RawText: "",
            TechnicalFailure: true,
            Error: error,
            Proposals: null,
            pending.RequiresSpokenTextSupport);

    private static PreparedDialogueTurn ParserPreparedTurn(DialogueClaimRequest request) =>
        new(
            request.Turn,
            request.PlayerText,
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.ListenerSoulId,
            SpeakerHostile: false,
            SpeakerProfile: null,
            SpeakerFaction: null,
            BondSummary: null);

    private void ApplyClaimProposal(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        if (proposal.PlayerAuthored || string.IsNullOrWhiteSpace(proposal.Text))
        {
            return;
        }

        var salience = Math.Clamp(proposal.Salience, 1, 5);
        var confidence = Math.Clamp(proposal.Confidence, 0, 100);
        var category = NormalizeToken(proposal.Category, "memory");
        var subject = string.IsNullOrWhiteSpace(proposal.Subject)
            ? proposal.Text.Trim()
            : proposal.Subject.Trim();
        var playerVisible = salience >= VisibleClaimSalience;
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", category })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var intakeTransaction = GameTransaction.Begin(Engine.State);
        var deltaStart = deltas.Count;
        var messageStart = messages.Count;
        var appliedClaim = Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            $"dialogue:{provider}",
            request.SpeakerId,
            request.ListenerSoulId,
            proposal.Text,
            category,
            subject,
            salience,
            confidence,
            playerVisible,
            tags,
            sourceEntityId: request.SpeakerId,
            evidence: proposal.Text,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
            }));
        messages.AddRange(appliedClaim.Messages);
        deltas.AddRange(appliedClaim.Deltas);
        if (!appliedClaim.Applied || string.IsNullOrWhiteSpace(appliedClaim.TargetId))
        {
            RollBackClaimIntakeTransaction(
                intakeTransaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                proposal.Text,
                appliedClaim.Deltas,
                appliedClaim.Error ?? "claim_record_rejected");
            return;
        }

        var record = Engine.State.Claims.Records.FirstOrDefault(claim =>
            claim.Id.Equals(appliedClaim.TargetId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            RollBackClaimIntakeTransaction(
                intakeTransaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                proposal.Text,
                Array.Empty<StateDelta>(),
                "claim_record_missing");
            return;
        }

        var memory = AddClaimMemory(request, record, messages, deltas);
        if (!memory.Applied)
        {
            RollBackClaimIntakeTransaction(
                intakeTransaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                record.Text,
                memory.Deltas,
                memory.Error ?? "claim_memory_rejected");
            return;
        }

        if (RumorSystem.ConsequenceFromClaim(Engine.State, record) is { } rumorConsequence)
        {
            var appliedRumor = Engine.ApplyConsequence(rumorConsequence);
            messages.AddRange(appliedRumor.Messages);
            deltas.AddRange(appliedRumor.Deltas);
            if (!appliedRumor.Applied)
            {
                RollBackClaimIntakeTransaction(
                    intakeTransaction,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    record.Text,
                    appliedRumor.Deltas,
                    appliedRumor.Error ?? "claim_rumor_rejected");
                return;
            }
        }

        intakeTransaction.Commit();

        if (proposal.UpdateBond)
        {
            ApplyBondClaim(request, provider, proposal, record, messages, deltas);
        }

        var immediateApplied = false;
        if (category.Equals("merchant_stock", StringComparison.OrdinalIgnoreCase))
        {
            immediateApplied |= ApplyMerchantStockClaim(request, proposal, record, messages, deltas);
        }

        if (category.Equals("service", StringComparison.OrdinalIgnoreCase))
        {
            immediateApplied |= ApplyServiceClaim(request, proposal, record, messages, deltas);
        }

        if (category.Equals("trade", StringComparison.OrdinalIgnoreCase))
        {
            immediateApplied |= ApplyTradeClaim(request, proposal, record, messages, deltas);
        }

        if (proposal.BindAsCanon)
        {
            immediateApplied |= ApplyCanonClaim(request, provider, proposal, record, category, messages, deltas);
        }

        if (ShouldBindClaimAsPromise(proposal, record, category, immediateApplied))
        {
            BindClaimAsPromise(request, proposal, record, messages, deltas);
        }
        else if (playerVisible)
        {
            AddVisibleClaimMessage(
                record,
                $"A claim settles into your journal: {record.Text}",
                messages,
                deltas,
                "claimJournalMessage");
        }
    }

    private static bool ShouldBindClaimAsPromise(
        DialogueClaimProposal proposal,
        ClaimRecord record,
        string category,
        bool immediateApplied)
    {
        if (proposal.BindAsPromise)
        {
            return true;
        }

        if (record.Salience < 3)
        {
            return false;
        }

        if (immediateApplied && category is "merchant_stock" or "service" or "trade")
        {
            return false;
        }

        return category is "site"
            or "town"
            or "landmark"
            or "person"
            or "item"
            or "merchant_stock"
            or "service"
            or "trade"
            or "threat"
            or "escape_route"
            or "route"
            or "prophecy"
            or "door_rule";
    }

    private void ApplyBondClaim(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.UpdateBond(
                $"dialogue_claim:{provider}",
                request.SpeakerId,
                request.ListenerSoulId,
                proposal.LoyaltyDelta,
                proposal.FearDelta,
                proposal.AdmirationDelta,
                proposal.ResentmentDelta,
                proposal.BondPosture,
                record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimBondShift",
                maxDelta: DialogueBondDeltaLimit,
                details: new Dictionary<string, object?>
                {
                    ["provider"] = provider,
                    ["proposalType"] = "bond",
                    ["claimId"] = record.Id,
                }),
            request.SpeakerId,
            request.SpeakerId,
            record.Text,
            "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "bond",
            });
    }

    private WorldConsequenceApplyResult ApplyClaimUpdate(
        ClaimRecord record,
        string? status,
        string? boundPromiseId,
        string? appliedTo,
        string? sourceEntityId,
        string? evidence,
        string operation,
        List<string> messages,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var applied = Engine.ApplyConsequence(WorldConsequence.UpdateClaim(
            $"dialogue_claim:{record.Id}",
            record.Id,
            status,
            boundPromiseId,
            appliedTo,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: sourceEntityId,
            evidence: evidence,
            operation: operation,
            details: details));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        return applied;
    }

    private bool ApplyClaimConsequenceWithStatus(
        ClaimRecord record,
        WorldConsequence consequence,
        string? appliedTo,
        string? sourceEntityId,
        string? evidence,
        string operation,
        List<string> messages,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var transaction = GameTransaction.Begin(Engine.State);
        var deltaStart = deltas.Count;
        var messageStart = messages.Count;
        var applied = Engine.ApplyConsequence(consequence);
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            RollBackClaimApplicationTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                record,
                applied.Deltas,
                applied.Error ?? $"{consequence.Type}_rejected",
                details);
            return false;
        }

        var update = ApplyClaimUpdate(
            record,
            status: "applied",
            boundPromiseId: null,
            appliedTo: appliedTo ?? applied.TargetId,
            sourceEntityId: sourceEntityId,
            evidence: evidence,
            operation: operation,
            messages,
            deltas,
            details: details);
        if (!update.Applied)
        {
            RollBackClaimApplicationTransaction(
                transaction,
                deltas,
                deltaStart,
                messages,
                messageStart,
                record,
                update.Deltas,
                update.Error ?? "claim_status_rejected",
                details);
            return false;
        }

        transaction.Commit();
        return true;
    }

    private static void RollBackClaimApplicationTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        ClaimRecord record,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure,
        IReadOnlyDictionary<string, object?>? details)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payload["claimId"] = record.Id;
        payload["failure"] = failure;
        payload["rejectedCount"] = diagnostics.Count;
        payload["auditOnly"] = true;
        payload["playerVisible"] = false;
        deltas.Add(new StateDelta(
            "claimApplicationSkipped",
            record.Id,
            $"Claim application rolled back: {failure}.",
            payload));
    }

    private static void RollBackClaimIntakeTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        string claimText,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "claimIntakeSkipped",
            "dialogue_claim",
            $"Claim intake rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["claimText"] = claimText,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private bool ApplyCanonClaim(
        DialogueClaimRequest request,
        string provider,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        string category,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var attachedTo = FirstNonBlank(proposal.TargetEntityId, proposal.MerchantId, request.SpeakerId)!;
        var kind = NormalizeToken(FirstNonBlank(proposal.CanonKind, category, "dialogue_fact")!, "dialogue_fact");
        var summary = FirstNonBlank(proposal.CanonSummary, record.Subject, record.Text)!;
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", "dialogue_claim", "canonized", kind })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.AddCanon(
                $"dialogue_claim:{record.Id}",
                kind,
                attachedTo,
                record.Text,
                summary,
                tags,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                reason: "dialogue claim: canonize fact",
                operation: "claimAddCanon",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = provider,
                    ["proposalType"] = "add_canon",
                    ["playerVisible"] = false,
                }),
            attachedTo,
            request.SpeakerId,
            record.Text,
            "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "add_canon",
                ["canonKind"] = kind,
            });
    }

    private bool ApplyMerchantStockClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var merchant = ResolveMerchantForClaim(request, proposal);
        if (merchant is null)
        {
            return false;
        }

        var itemName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject);
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return false;
        }

        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.AddMerchantStock(
                $"dialogue_claim:{record.Id}",
                merchant.Id.Value,
                itemName,
                visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimMerchantStock",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = "dialogue_claim",
                }),
            merchant.Id.Value,
            request.SpeakerId,
            record.Text,
            "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = "dialogue_claim",
                ["proposalType"] = "merchant_stock",
            });
    }

    private bool ApplyServiceClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var provider = ResolveServiceProviderForClaim(request, proposal, record);
        if (provider is null)
        {
            return false;
        }

        var serviceName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject, "quiet service")!;
        var serviceId = NormalizeToken(serviceName, "service");
        if (ProviderAlreadyOffersService(provider, serviceId, serviceName))
        {
            ApplyClaimUpdate(
                record,
                status: "applied",
                boundPromiseId: null,
                appliedTo: provider.Id.Value,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimApplied",
                messages,
                deltas,
                details: new Dictionary<string, object?>
                {
                    ["provider"] = "dialogue_claim",
                    ["proposalType"] = "service",
                    ["matchedExistingService"] = true,
                    ["serviceId"] = serviceId,
                });
            return true;
        }

        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.OfferService(
                $"dialogue_claim:{record.Id}",
                provider.Id.Value,
                serviceId,
                serviceName,
                record.Text,
                InferServiceEffect(record.Text, serviceName),
                targetHint: proposal.TriggerHint,
                tags: proposal.Tags,
                visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimOfferService",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = "dialogue_claim",
                }),
            appliedTo: null,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = "dialogue_claim",
                ["proposalType"] = "service",
            });
    }

    private static bool ProviderAlreadyOffersService(Entity provider, string serviceId, string serviceName) =>
        provider.TryGet<ServiceComponent>(out var services)
        && services.Offers.Any(offer =>
            offer.Id.Equals(serviceId, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(offer.Name, "service").Equals(serviceId, StringComparison.OrdinalIgnoreCase)
            || offer.Name.Equals(serviceName, StringComparison.OrdinalIgnoreCase));

    private bool ApplyTradeClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var traderId = FirstNonBlank(proposal.MerchantId, proposal.TargetEntityId, request.SpeakerId);
        if (traderId is null)
        {
            return false;
        }

        var itemName = FirstNonBlank(proposal.ItemName, proposal.Subject, record.Subject);
        return ApplyClaimConsequenceWithStatus(
            record,
            WorldConsequence.OfferTrade(
                $"dialogue_claim:{record.Id}",
                traderId,
                itemName,
                visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimOfferTrade",
                details: new Dictionary<string, object?>
                {
                    ["claimId"] = record.Id,
                    ["provider"] = "dialogue_claim",
                }),
            appliedTo: null,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimApplied",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["provider"] = "dialogue_claim",
                ["proposalType"] = "trade",
            });
    }

    private void BindClaimAsPromise(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var realizationKind = NormalizeRealizationKind(proposal.RealizationKind, proposal.Category, record.Text);
        var triggerHint = NormalizePromiseTrigger(request, proposal, record, realizationKind);
        var existing = MatchingActivePromise(record, proposal, triggerHint, realizationKind);
        if (existing is not null)
        {
            var transaction = GameTransaction.Begin(Engine.State);
            var deltaStart = deltas.Count;
            var mergedTriggerHint = MergeTriggerHints(existing.TriggerHint, triggerHint);
            var mergedRealizationKind = MergeRealizationKind(existing.RealizationKind, realizationKind);
            var linkedPromise = existing;
            if (ShouldRebindExistingPromise(existing, mergedTriggerHint, mergedRealizationKind))
            {
                var updateResult = Engine.ApplyConsequence(WorldConsequence.UpdatePromise(
                    $"dialogue_claim:{record.Id}",
                    existing.Id,
                    status: "bound",
                    boundPlace: ShouldBindToRegion(mergedTriggerHint, mergedRealizationKind)
                        ? existing.BoundPlace ?? Engine.State.RegionId
                        : existing.BoundPlace,
                    boundTargetId: existing.BoundTargetId,
                    triggerHint: mergedTriggerHint,
                    realizationKind: mergedRealizationKind,
                    visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
                    sourceEntityId: request.SpeakerId,
                    evidence: record.Text,
                    operation: "claimPromiseRebound",
                    emitMessage: false,
                    message: $"A repeated claim sharpens an existing promise: {existing.Text}",
                    details: new Dictionary<string, object?>
                    {
                        ["claimId"] = record.Id,
                        ["provider"] = "dialogue_claim",
                    }));
                messages.AddRange(updateResult.Messages);
                deltas.AddRange(updateResult.Deltas);
                if (!updateResult.Applied)
                {
                    RollBackClaimPromiseTransaction(
                        transaction,
                        deltas,
                        deltaStart,
                        record,
                        updateResult.Deltas,
                        updateResult.Error ?? "promise_rebind_rejected");
                    return;
                }

                if (updateResult.Applied)
                {
                    linkedPromise = Engine.State.PromiseLedger.Promises.FirstOrDefault(promise =>
                        promise.Id.Equals(existing.Id, StringComparison.OrdinalIgnoreCase)) ?? existing;
                }
            }

            var updateClaim = ApplyClaimUpdate(
                record,
                status: "promised",
                boundPromiseId: linkedPromise.Id,
                appliedTo: null,
                sourceEntityId: request.SpeakerId,
                evidence: record.Text,
                operation: "claimPromiseLinked",
                messages,
                deltas,
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = linkedPromise.Id,
                    ["promiseStatus"] = linkedPromise.Status,
                    ["triggerHint"] = linkedPromise.TriggerHint,
                    ["realizationKind"] = linkedPromise.RealizationKind,
                    ["message"] = $"A repeated claim points back to an existing promise: {linkedPromise.Text}",
                });
            if (!updateClaim.Applied)
            {
                RollBackClaimPromiseTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    record,
                    updateClaim.Deltas,
                    updateClaim.Error ?? "claim_status_rejected");
                return;
            }

            transaction.Commit();
            return;
        }

        var createTransaction = GameTransaction.Begin(Engine.State);
        var createDeltaStart = deltas.Count;
        var shouldBindToRegion = ShouldBindToRegion(triggerHint, realizationKind);
        var applied = Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            $"dialogue_claim:{record.Id}",
            string.IsNullOrWhiteSpace(proposal.PromiseKind) ? "rumor" : proposal.PromiseKind.Trim(),
            record.Text,
            triggerHint: triggerHint,
            visibility: record.PlayerVisible ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimPromise",
            playerVisible: record.PlayerVisible,
            salience: record.Salience,
            subject: record.Subject,
            claimedPlace: string.IsNullOrWhiteSpace(proposal.ClaimedPlace) ? null : proposal.ClaimedPlace,
            realizationKind: realizationKind,
            bindPlace: shouldBindToRegion ? Engine.State.RegionId : null,
            sourceClaimId: record.Id,
            sourceSpeakerId: request.SpeakerId,
            sourceListenerSoulId: request.ListenerSoulId,
            sourceConfidence: record.Confidence,
            useCurrentRegionAsClaimedPlace: false,
            autoBind: false,
            emitMessage: false,
            message: shouldBindToRegion
                ? $"A claim becomes a bound promise: {record.Text}"
                : $"A claim becomes a promise: {record.Text}",
            details: new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["provider"] = "dialogue_claim",
            }));
        if (!applied.Applied)
        {
            messages.AddRange(applied.Messages);
            deltas.AddRange(applied.Deltas);
            return;
        }

        var promiseId = applied.TargetId ?? applied.Deltas.FirstOrDefault()?.Target;
        var bound = Engine.State.PromiseLedger.Promises.FirstOrDefault(promise =>
            promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
        if (bound is null)
        {
            RollBackClaimPromiseTransaction(
                createTransaction,
                deltas,
                createDeltaStart,
                record,
                applied.Deltas,
                "promise_missing_after_create");
            return;
        }

        deltas.AddRange(applied.Deltas);
        var updated = ApplyClaimUpdate(
            record,
            status: "promised",
            boundPromiseId: bound.Id,
            appliedTo: null,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimPromiseStatus",
            messages,
            deltas,
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = bound.Id,
                ["promiseStatus"] = bound.Status,
                ["triggerHint"] = bound.TriggerHint,
                ["realizationKind"] = bound.RealizationKind,
            });
        if (!updated.Applied)
        {
            RollBackClaimPromiseTransaction(
                createTransaction,
                deltas,
                createDeltaStart,
                record,
                updated.Deltas,
                updated.Error ?? "claim_status_rejected");
            return;
        }

        createTransaction.Commit();
        var message = bound.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            ? $"A claim becomes a bound promise: {bound.Text}"
            : $"A claim becomes a promise: {bound.Text}";
        if (record.PlayerVisible)
        {
            AddVisibleClaimMessage(
                record,
                message,
                messages,
                deltas,
                "claimPromiseMessage",
                new Dictionary<string, object?>
                {
                    ["promiseId"] = bound.Id,
                    ["promiseStatus"] = bound.Status,
                    ["triggerHint"] = bound.TriggerHint,
                    ["realizationKind"] = bound.RealizationKind,
                });
        }
    }

    private static void RollBackClaimPromiseTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        ClaimRecord record,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "claimPromiseSkipped",
            record.Id,
            $"Claim-promise binding rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
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

    // Below this Jaccard overlap of significant (4+ char) tokens, two claim texts are treated as
    // unrelated facts rather than a restatement of the same one, even from the same speaker.
    private const double FuzzyClaimPromiseTextOverlapThreshold = 0.6;

    private WorldPromise? MatchingActivePromise(
        ClaimRecord record,
        DialogueClaimProposal proposal,
        string triggerHint,
        string realizationKind)
    {
        var candidates = Engine.State.PromiseLedger.Promises
            .Where(promise =>
                !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
                && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                && PromiseRealizationKindsCompatible(promise.RealizationKind, realizationKind)
                && TriggerHintsOverlap(promise.TriggerHint, triggerHint)
                && (string.IsNullOrWhiteSpace(proposal.PromiseKind)
                    || promise.Kind.Equals(proposal.PromiseKind, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        var exact = candidates.FirstOrDefault(promise =>
            promise.Text.Equals(record.Text, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // A model that restates the same fact in different words should re-link to the existing
        // promise/rumor thread instead of forking a parallel one (ledger sprawl). Guardrail:
        // require shared provenance (same speaker or same non-empty subject) in addition to text
        // overlap, so two genuinely different facts that merely share common words never match.
        return candidates.FirstOrDefault(promise =>
            (SameNonBlank(promise.SourceSpeakerId, record.SpeakerId)
                || SameNonBlank(promise.Subject, record.Subject))
            && SignificantTokenOverlap(promise.Text, record.Text) >= FuzzyClaimPromiseTextOverlapThreshold);
    }

    private static bool SameNonBlank(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left)
        && !string.IsNullOrWhiteSpace(right)
        && left.Equals(right, StringComparison.OrdinalIgnoreCase);

    private static double SignificantTokenOverlap(string left, string right)
    {
        var leftTokens = SignificantTokens(left);
        var rightTokens = SignificantTokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)intersection / union;
    }

    private static HashSet<string> SignificantTokens(string text) =>
        text
            .ToLowerInvariant()
            .Split(
                new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '\'', '"', '-', '_' },
                StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 4)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool ShouldRebindExistingPromise(
        WorldPromise promise,
        string? triggerHint,
        string? realizationKind) =>
        !string.Equals(promise.TriggerHint, triggerHint, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(promise.RealizationKind, realizationKind, StringComparison.OrdinalIgnoreCase)
        || (ShouldBindToRegion(triggerHint, realizationKind) && string.IsNullOrWhiteSpace(promise.BoundPlace));

    private static bool PromiseRealizationKindsCompatible(string? existing, string requested)
    {
        var left = NormalizeToken(existing ?? "", "");
        var right = NormalizeToken(requested, "");
        return string.IsNullOrWhiteSpace(left)
            || string.IsNullOrWhiteSpace(right)
            || left.Equals(right, StringComparison.OrdinalIgnoreCase)
            || (IsPlaceLikeRealization(left) && IsPlaceLikeRealization(right))
            || (IsStockLikeRealization(left) && IsStockLikeRealization(right));
    }

    private static string? MergeRealizationKind(string? existing, string requested)
    {
        var left = NormalizeToken(existing ?? "", "");
        var right = NormalizeToken(requested, "");
        if (string.IsNullOrWhiteSpace(left))
        {
            return string.IsNullOrWhiteSpace(right) ? null : right;
        }

        if (string.IsNullOrWhiteSpace(right) || left.Equals(right, StringComparison.OrdinalIgnoreCase))
        {
            return left;
        }

        if ((IsPlaceLikeRealization(left) && right is "escape_route" or "route" or "door_rule")
            || (IsStockLikeRealization(left) && right is "merchant_stock" or "stock"))
        {
            return right;
        }

        if ((IsPlaceLikeRealization(right) && left is "escape_route" or "route" or "door_rule")
            || (IsStockLikeRealization(right) && left is "merchant_stock" or "stock"))
        {
            return left;
        }

        return right;
    }

    private static bool IsPlaceLikeRealization(string kind) =>
        kind is "site" or "town" or "landmark" or "escape_route" or "route" or "door_rule";

    private static bool IsStockLikeRealization(string kind) =>
        kind is "merchant_stock" or "stock" or "trade";

    private WorldConsequenceApplyResult AddClaimMemory(
        DialogueClaimRequest request,
        ClaimRecord record,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var applied = Engine.ApplyConsequence(WorldConsequence.RecordMemory(
            $"claim:{record.Id}",
            request.SpeakerId,
            record.Text,
            $"claim:{record.Id}",
            record.Salience,
            shareable: true,
            sourceEntityId: request.SpeakerId,
            evidence: record.Text,
            operation: "claimMemory",
            details: new Dictionary<string, object?>
            {
                ["claimId"] = record.Id,
                ["speakerId"] = record.SpeakerId,
                ["category"] = record.Category,
            }));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        return applied;
    }

    private Entity? ResolveMerchantForClaim(DialogueClaimRequest request, DialogueClaimProposal proposal)
    {
        foreach (var id in new[] { proposal.MerchantId, proposal.TargetEntityId, request.SpeakerId })
        {
            if (!string.IsNullOrWhiteSpace(id)
                && Engine.EntityById(id) is { } merchant
                && merchant.Has<MerchantComponent>())
            {
                return merchant;
            }
        }

        return Engine.State.Entities.Values
            .OrderBy(entity => entity.Id.Value)
            .FirstOrDefault(entity => entity.Has<MerchantComponent>());
    }

    private Entity? ResolveServiceProviderForClaim(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record)
    {
        if (!string.IsNullOrWhiteSpace(proposal.TargetEntityId)
            && Engine.EntityById(proposal.TargetEntityId) is { } target)
        {
            if (!target.Id.Value.Equals(request.SpeakerId, StringComparison.OrdinalIgnoreCase)
                || ClaimNamesSpeakerAsProvider(record.Text, request.SpeakerName))
            {
                return target;
            }

            return null;
        }

        return ClaimNamesSpeakerAsProvider(record.Text, request.SpeakerName)
            ? Engine.EntityById(request.SpeakerId)
            : null;
    }

    private IReadOnlyList<WorldMemoryRecord> RecentMemoriesFor(string speakerId) =>
        Engine.State.Memories.Records
            .Where(record => record.SubjectId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("gift", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("claim:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(DialogueRecentMemoryLimit)
            .ToArray();

    private void AddVisibleClaimMessage(
        ClaimRecord record,
        string message,
        List<string> messages,
        List<StateDelta> deltas,
        string operation,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var mergedDetails = new Dictionary<string, object?>(details ?? new Dictionary<string, object?>(), StringComparer.OrdinalIgnoreCase)
        {
            ["claimId"] = record.Id,
            ["category"] = record.Category,
            ["subject"] = record.Subject,
        };
        var applied = Engine.ApplyConsequence(WorldConsequence.Message(
            $"dialogue_claim:{record.Id}",
            message,
            visibility: WorldConsequenceVisibility.Journal,
            sourceEntityId: record.SpeakerId,
            evidence: record.Text,
            operation: operation,
            details: mergedDetails));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    // Shared with InteractionSystem's document/prop claim-seed promise binding, so a promise
    // whose realization kind can only ever pay off through travel (site/town/escape_route/etc.)
    // binds to the current region regardless of whether it came from dialogue or a read/examine
    // claim seed. Without this, non-anchored promise kinds that CanBindToRegion doesn't recognize
    // (e.g. "rumor") would never bind and could never become travel-realization candidates.
    internal static bool ShouldBindToRegion(string? triggerHint, string? realizationKind) =>
        TriggerMatches(triggerHint, "travel")
        && NormalizeToken(realizationKind ?? "", "site") is "site" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "service" or "escape_route" or "door_rule" or "route";

    private static bool TriggerMatches(string? hint, string trigger) =>
        string.IsNullOrWhiteSpace(hint)
        || hint.Equals(trigger, StringComparison.OrdinalIgnoreCase)
        || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase)
        || hint.Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Equals(trigger, StringComparison.OrdinalIgnoreCase));

    private static bool TriggerHintsOverlap(string? left, string? right)
    {
        var leftParts = TriggerHintParts(left);
        var rightParts = TriggerHintParts(right);
        return leftParts.Count == 0
            || rightParts.Count == 0
            || leftParts.Contains("encounter")
            || rightParts.Contains("encounter")
            || leftParts.Overlaps(rightParts);
    }

    private static HashSet<string> TriggerHintParts(string? hint) =>
        string.IsNullOrWhiteSpace(hint)
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : hint.Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => part.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? MergeTriggerHints(string? existing, string? requested)
    {
        var parts = TriggerHintParts(existing);
        parts.UnionWith(TriggerHintParts(requested));
        if (parts.Count == 0)
        {
            return string.IsNullOrWhiteSpace(existing)
                ? string.IsNullOrWhiteSpace(requested) ? null : requested.Trim()
                : existing.Trim();
        }

        var preferredOrder = new[] { "travel", "talk", "read", "open", "inspect", "buy", "trade", "encounter" };
        return string.Join(
            ",",
            preferredOrder.Where(parts.Contains)
                .Concat(parts.Except(preferredOrder, StringComparer.OrdinalIgnoreCase).OrderBy(part => part, StringComparer.OrdinalIgnoreCase)));
    }

    private string NormalizePromiseTrigger(
        DialogueClaimRequest request,
        DialogueClaimProposal proposal,
        ClaimRecord record,
        string realizationKind)
    {
        var triggerHint = string.IsNullOrWhiteSpace(proposal.TriggerHint) ? "travel" : proposal.TriggerHint.Trim();
        var categoryKind = NormalizeClaimCategoryAsRealization(record.Category);
        if ((categoryKind.Equals("service", StringComparison.OrdinalIgnoreCase)
                || realizationKind.Equals("service", StringComparison.OrdinalIgnoreCase))
            && ResolveServiceProviderForClaim(request, proposal, record) is null)
        {
            return "travel";
        }

        if (LooksLikeRoutePromise(record.Text) && !TriggerMatches(triggerHint, "travel"))
        {
            return "travel";
        }

        return triggerHint;
    }

    private static string NormalizeRealizationKind(string? realizationKind, string category, string? text = null)
    {
        var categoryKind = NormalizeClaimCategoryAsRealization(category);
        var requestedKind = NormalizeRealizationToken(realizationKind ?? category);
        if (LooksLikeMerchantStockPromise(text))
        {
            return "merchant_stock";
        }

        if ((categoryKind.Equals("site", StringComparison.OrdinalIgnoreCase)
                || requestedKind.Equals("site", StringComparison.OrdinalIgnoreCase))
            && LooksLikeRoutePromise(text))
        {
            return "escape_route";
        }

        if (CategoryControlsRealization(categoryKind) && !requestedKind.Equals(categoryKind, StringComparison.OrdinalIgnoreCase))
        {
            return categoryKind;
        }

        return requestedKind;
    }

    private static string NormalizeClaimCategoryAsRealization(string category) =>
        NormalizeRealizationToken(category);

    private static string NormalizeRealizationToken(string? text)
    {
        var normalized = NormalizeToken(text ?? "", "memory");
        return normalized switch
        {
            "place" or "site" or "town" or "landmark" => "site",
            "merchant_stock" or "stock" or "ware" or "wares" or "trade" => "merchant_stock",
            "npc" or "person" or "relative" => "person",
            "enemy" or "danger" or "threat" => "threat",
            "item" or "blade" or "weapon" => "item",
            "service" or "folk_magic" or "folk_magic_service" => "service",
            "route" or "escape_route" or "hidden_exit" or "tunnel" => "escape_route",
            "door" or "door_rule" or "lock" => "door_rule",
            _ => normalized,
        };
    }

    private static bool CategoryControlsRealization(string categoryKind) =>
        categoryKind is "merchant_stock" or "service" or "escape_route" or "door_rule" or "threat";

    private static bool LooksLikeRoutePromise(string? text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("route", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("road", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("hidden exit", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("escape", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("drain", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("grate", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("passage", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("way out", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeMerchantStockPromise(string? text)
    {
        var lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("merchant", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("seller", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("sells", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("sell you", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("wares", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("trades", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("for sale", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ClaimNamesSpeakerAsProvider(string text, string speakerName)
    {
        var lower = $" {text.Trim().ToLowerInvariant()} ";
        if (lower.Contains(" i can ", StringComparison.OrdinalIgnoreCase)
            || lower.Contains(" i know ", StringComparison.OrdinalIgnoreCase)
            || lower.Contains(" i will ", StringComparison.OrdinalIgnoreCase)
            || lower.Contains(" i'll ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var name in SpeakerNameTokens(speakerName))
        {
            if (lower.Contains($" {name} can ", StringComparison.OrdinalIgnoreCase)
                || lower.Contains($" {name} knows ", StringComparison.OrdinalIgnoreCase)
                || lower.Contains($" {name} offers ", StringComparison.OrdinalIgnoreCase)
                || lower.Contains($" {name} will ", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> SpeakerNameTokens(string speakerName)
    {
        var names = new List<string>();
        var full = speakerName.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(full))
        {
            names.Add(full);
        }

        var first = full.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(first))
        {
            names.Add(first);
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string InferServiceEffect(string text, string serviceName)
    {
        var lower = $"{text} {serviceName}".ToLowerInvariant();
        if (lower.Contains("door", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("lock", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("unlock", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("ward", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("key", StringComparison.OrdinalIgnoreCase))
        {
            return "open_or_unlock";
        }

        if (lower.Contains("route", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("drain", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("passage", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("escape", StringComparison.OrdinalIgnoreCase))
        {
            return "create_route";
        }

        return "record_memory";
    }
}
