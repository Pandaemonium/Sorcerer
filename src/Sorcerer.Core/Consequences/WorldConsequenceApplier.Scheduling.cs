using Sorcerer.Core.Characters;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

/// <summary>
/// <see cref="WorldConsequenceApplier"/> handlers for deferred effects: promises, scheduled events, triggers, and background jobs.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyCreatePromise(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Promise consequence did not include text.");
        }

        var anchor = string.IsNullOrWhiteSpace(consequence.TargetEntityId)
            ? null
            : EntityById(consequence.TargetEntityId);
        if (!string.IsNullOrWhiteSpace(consequence.TargetEntityId) && anchor is null)
        {
            return Reject(consequence, "Promise consequence anchor does not exist.");
        }

        var kind = NormalizeToken(FirstNonBlank(ReadString(payload, "kind"), "omen")!, "omen");
        if (ReadBool(payload, "stackExisting") == true)
        {
            var existing = _state.PromiseLedger.FindActive(kind, text, anchor?.Id.Value)
                ?? (anchor is null
                    ? _state.PromiseLedger.Promises.FirstOrDefault(promise =>
                        promise.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                        && !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
                        && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                        && promise.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
                    : null);
            if (existing is not null)
            {
                var stacked = _state.PromiseLedger.Stack(existing.Id);
                var stackOperation = ReadString(payload, "operation") ?? "createPromise";
                var stackMessage = FormatStackMessage(
                    FirstNonBlank(
                        ReadString(payload, "stackMessageTemplate"),
                        ReadString(payload, "stackMessage"),
                        ReadString(payload, "stackSummary"),
                        ""),
                    text,
                    kind,
                    stacked.Stacks);
                AddMessageIfAllowed(consequence, payload, stackMessage, playerVisible: stacked.PlayerVisible);

                return AppliedFromDelta(consequence, new StateDelta(
                    stackOperation,
                    stacked.Id,
                    stackMessage,
                    Details(
                        consequence,
                        ("kind", kind),
                        ("promiseId", stacked.Id),
                        ("stacks", stacked.Stacks),
                        ("playerVisible", stacked.PlayerVisible))));
            }
        }

        var triggerHint = FirstNonBlank(ReadString(payload, "triggerHint"), ReadString(payload, "trigger"), ReadString(payload, "trigger_hint"), "")!;
        var realizationKind = FirstNonBlank(ReadString(payload, "realizationKind"), ReadString(payload, "realization_kind"), InferPromiseRealizationKind(kind, text));
        var subject = FirstNonBlank(ReadString(payload, "subject"), SoulIdFor(_state.ControlledEntity))!;
        var claimedPlace = FirstNonBlank(ReadString(payload, "claimedPlace"), ReadString(payload, "claimed_place"));
        if ((ReadBool(payload, "useCurrentRegionAsClaimedPlace") ?? true)
            && string.IsNullOrWhiteSpace(claimedPlace))
        {
            claimedPlace = _state.RegionId;
        }

        var promise = _state.PromiseLedger.Add(
            kind,
            text,
            playerVisible: ReadBool(payload, "playerVisible") ?? true,
            source: consequence.Source,
            salience: ReadInt(payload, "salience") ?? Math.Max(1, consequence.Salience),
            subject: subject,
            claimedPlace: claimedPlace,
            triggerHint: triggerHint,
            realizationKind: realizationKind,
            sourceClaimId: FirstNonBlank(ReadString(payload, "sourceClaimId"), ReadString(payload, "source_claim_id"), ReadString(payload, "claimId"), ReadString(payload, "claim_id")),
            sourceSpeakerId: FirstNonBlank(ReadString(payload, "sourceSpeakerId"), ReadString(payload, "source_speaker_id"), ReadString(payload, "speakerId"), ReadString(payload, "speaker_id")),
            sourceListenerSoulId: FirstNonBlank(ReadString(payload, "sourceListenerSoulId"), ReadString(payload, "source_listener_soul_id"), ReadString(payload, "listenerSoulId"), ReadString(payload, "listener_soul_id")),
            sourceConfidence: ReadInt(payload, "sourceConfidence") ?? ReadInt(payload, "source_confidence") ?? ReadInt(payload, "confidence"));
        var bound = BindPromiseIfPossible(
            promise,
            anchor,
            triggerHint,
            FirstNonBlank(ReadString(payload, "bindPlace"), ReadString(payload, "boundPlace"), ReadString(payload, "bind_place"), ReadString(payload, "bound_place")),
            realizationKind,
            ReadBool(payload, "autoBind") ?? true);
        var finalPromise = bound ?? promise;
        var promiseNoun = finalPromise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase) ? "curse" : "promise";
        var defaultMessage = finalPromise.Status == "bound"
            ? $"A {promiseNoun} binds to {finalPromise.BoundTargetId ?? finalPromise.BoundPlace}: {finalPromise.Text}"
            : $"A {promiseNoun} enters the world: {finalPromise.Text}";
        var message = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultMessage)!;
        AddMessageIfAllowed(consequence, payload, message, playerVisible: finalPromise.PlayerVisible);

        var operation = ReadString(payload, "operation") ?? "createPromise";
        var delta = new StateDelta(
            operation,
            finalPromise.Id,
            message,
            Details(
                consequence,
                ("kind", finalPromise.Kind),
                ("status", finalPromise.Status),
                ("promiseId", finalPromise.Id),
                ("subject", finalPromise.Subject),
                ("playerVisible", finalPromise.PlayerVisible),
                ("salience", finalPromise.Salience),
                ("stacks", finalPromise.Stacks),
                ("claimedPlace", finalPromise.ClaimedPlace),
                ("boundPlace", finalPromise.BoundPlace),
                ("boundTargetId", finalPromise.BoundTargetId),
                ("triggerHint", finalPromise.TriggerHint),
                ("realizationKind", finalPromise.RealizationKind),
                ("sourceClaimId", finalPromise.SourceClaimId),
                ("sourceSpeakerId", finalPromise.SourceSpeakerId),
                ("sourceListenerSoulId", finalPromise.SourceListenerSoulId),
                ("sourceConfidence", finalPromise.SourceConfidence)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyUpdatePromise(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var promiseId = FirstNonBlank(ReadString(payload, "promiseId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(promiseId))
        {
            return Reject(consequence, "Promise update did not include a promise id.");
        }

        var existing = _state.PromiseLedger.Promises.FirstOrDefault(promise =>
            promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Promise does not exist: {promiseId}");
        }

        var status = NormalizeToken(ReadString(payload, "status") ?? "", "");
        var boundPlace = FirstNonBlank(ReadString(payload, "bindPlace"), ReadString(payload, "boundPlace"), ReadString(payload, "bind_place"), ReadString(payload, "bound_place"));
        var boundTargetId = FirstNonBlank(ReadString(payload, "boundTargetId"), ReadString(payload, "targetId"), ReadString(payload, "bound_target_id"));
        // Deliberately does not fall back to a bare "trigger" key: that key is used throughout
        // PromiseRealizationSystem purely as audit context (which trigger evaluated the promise
        // this time), not as a request to rebind the promise's TriggerHint.
        var triggerHint = FirstNonBlank(ReadString(payload, "triggerHint"), ReadString(payload, "trigger_hint"));
        var realizationKind = FirstNonBlank(ReadString(payload, "realizationKind"), ReadString(payload, "realization_kind"));
        var realizedIn = FirstNonBlank(ReadString(payload, "realizedIn"), ReadString(payload, "realized_in"));
        var hasEligibilityFailure = HasAnyKey(payload, "lastEligibilityFailure", "last_eligibility_failure", "eligibilityFailure", "eligibility_failure");
        var eligibilityFailure = FirstNonBlank(
            ReadString(payload, "lastEligibilityFailure"),
            ReadString(payload, "last_eligibility_failure"),
            ReadString(payload, "eligibilityFailure"),
            ReadString(payload, "eligibility_failure"));
        var eligibilityContext = FirstNonBlank(
            ReadString(payload, "lastEligibilityContext"),
            ReadString(payload, "last_eligibility_context"),
            ReadString(payload, "eligibilityContext"),
            ReadString(payload, "eligibility_context"));
        var eligibilityTurn = ReadInt(payload, "lastEligibilityTurn")
            ?? ReadInt(payload, "last_eligibility_turn")
            ?? ReadInt(payload, "eligibilityTurn")
            ?? ReadInt(payload, "eligibility_turn");
        var clearEligibilityFailure = ReadBool(payload, "clearEligibilityFailure")
            ?? ReadBool(payload, "clear_eligibility_failure")
            ?? false;
        var wantsBinding = !string.IsNullOrWhiteSpace(boundPlace)
            || !string.IsNullOrWhiteSpace(boundTargetId)
            || !string.IsNullOrWhiteSpace(triggerHint)
            || !string.IsNullOrWhiteSpace(realizationKind)
            || status.Equals("bound", StringComparison.OrdinalIgnoreCase);
        var wantsEligibilityUpdate = clearEligibilityFailure || hasEligibilityFailure;
        if (!wantsBinding && string.IsNullOrWhiteSpace(status) && !wantsEligibilityUpdate)
        {
            return Reject(consequence, "Promise update did not include any changes.");
        }

        if (!string.IsNullOrWhiteSpace(boundTargetId))
        {
            var target = EntityById(boundTargetId);
            if (target is null
                && !boundTargetId.Equals(existing.BoundTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(consequence, "Promise update target does not exist.");
            }

            if (target is not null)
            {
                AttachPromiseAnchor(target, existing.Id);
            }
        }

        var updated = existing;
        if (wantsBinding)
        {
            updated = _state.PromiseLedger.Bind(
                existing.Id,
                boundPlace,
                boundTargetId,
                triggerHint,
                realizationKind) ?? existing;
        }

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("bound", StringComparison.OrdinalIgnoreCase))
        {
            updated = _state.PromiseLedger.SetStatus(updated.Id, status, realizedIn) ?? updated;
        }

        if (wantsEligibilityUpdate)
        {
            updated = clearEligibilityFailure
                ? _state.PromiseLedger.SetEligibilityFailure(updated.Id, null, null, null) ?? updated
                : _state.PromiseLedger.SetEligibilityFailure(
                    updated.Id,
                    eligibilityFailure,
                    eligibilityContext,
                    eligibilityTurn ?? _state.Turn) ?? updated;
        }

        var defaultMessage = updated.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            ? $"A promise binds to {updated.BoundTargetId ?? updated.BoundPlace}: {updated.Text}"
            : $"A promise changes: {updated.Text}";
        var message = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultMessage)!;
        var deltaPlayerVisible = ReadBool(payload, "playerVisible")
            ?? ReadBool(payload, "player_visible")
            ?? (IsVisible(consequence.Visibility) && updated.PlayerVisible);
        AddMessageIfAllowed(
            consequence,
            payload,
            message,
            defaultEmitMessage: false,
            includeVisible: false,
            playerVisible: updated.PlayerVisible);

        var operation = ReadString(payload, "operation") ?? "updatePromise";
        var delta = new StateDelta(
            operation,
            updated.Id,
            message,
            Details(
                consequence,
                ("kind", updated.Kind),
                ("status", updated.Status),
                ("subject", updated.Subject),
                ("playerVisible", deltaPlayerVisible),
                ("salience", updated.Salience),
                ("claimedPlace", updated.ClaimedPlace),
                ("boundPlace", updated.BoundPlace),
                ("boundTargetId", updated.BoundTargetId),
                ("triggerHint", updated.TriggerHint),
                ("realizationKind", updated.RealizationKind),
                ("realizedIn", updated.RealizedIn),
                ("lastEligibilityFailure", updated.LastEligibilityFailure),
                ("lastEligibilityContext", updated.LastEligibilityContext),
                ("lastEligibilityTurn", updated.LastEligibilityTurn),
                ("eligibilityUpdated", wantsEligibilityUpdate)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldPromise? BindPromiseIfPossible(
        WorldPromise promise,
        Entity? anchor,
        string triggerHint,
        string? explicitBoundPlace = null,
        string? realizationKind = null,
        bool autoBind = true)
    {
        if (anchor is null && autoBind)
        {
            anchor = ResolvePromiseAnchorFromSelectionOrText(promise.Text);
        }

        if (anchor is not null)
        {
            AttachPromiseAnchor(anchor, promise.Id);
            return _state.PromiseLedger.Bind(
                promise.Id,
                boundPlace: string.IsNullOrWhiteSpace(explicitBoundPlace) ? _state.RegionId : explicitBoundPlace,
                boundTargetId: anchor.Id.Value,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferPromiseTriggerHint(promise.Text, anchor) : triggerHint,
                realizationKind: realizationKind ?? promise.RealizationKind);
        }

        if (!string.IsNullOrWhiteSpace(explicitBoundPlace))
        {
            return _state.PromiseLedger.Bind(
                promise.Id,
                boundPlace: explicitBoundPlace,
                boundTargetId: null,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferPromiseTriggerHint(promise.Text, null) : triggerHint,
                realizationKind: realizationKind ?? promise.RealizationKind);
        }

        if (autoBind && CanBindToRegion(promise))
        {
            return _state.PromiseLedger.Bind(
                promise.Id,
                boundPlace: _state.RegionId,
                boundTargetId: null,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferPromiseTriggerHint(promise.Text, null) : triggerHint,
                realizationKind: realizationKind ?? promise.RealizationKind);
        }

        return null;
    }

    private void AttachPromiseAnchor(Entity anchor, string promiseId)
    {
        var ids = anchor.TryGet<PromiseAnchorComponent>(out var existing)
            ? existing.PromiseIds.ToList()
            : new List<string>();
        if (!ids.Contains(promiseId, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(promiseId);
        }

        anchor.Set(new PromiseAnchorComponent(ids));
    }

    private Entity? ResolvePromiseAnchorFromSelectionOrText(string text)
    {
        if (_state.SelectedTarget is { } selected)
        {
            var selectedEntity = _state.Entities.Values.FirstOrDefault(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        var tokens = text.ToLowerInvariant()
            .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origin = _state.ControlledEntity.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        return _state.Entities.Values
            .Where(entity => entity.Id != _state.ControlledEntityId)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Score = PromiseAnchorScore(entity, tokens),
                Distance = entity.TryGet<PositionComponent>(out var position)
                    ? Distance(origin, position.Position)
                    : int.MaxValue,
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Entity.Id.Value)
            .Select(candidate => candidate.Entity)
            .FirstOrDefault();
    }

    private static int PromiseAnchorScore(Entity entity, HashSet<string> tokens)
    {
        var score = 0;
        foreach (var token in entity.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tokens.Contains(token))
            {
                score += 3;
            }
        }

        if (entity.TryGet<TagsComponent>(out var tags))
        {
            score += tags.Tags.Count(tag => tokens.Contains(tag));
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            score += fixture.Tags.Count(tag => tokens.Contains(tag));
            if (tokens.Contains(fixture.FixtureType))
            {
                score += 2;
            }
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            foreach (var token in readable.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tokens.Contains(token))
                {
                    score += 2;
                }
            }
        }

        return score;
    }

    private static bool CanBindToRegion(WorldPromise promise) =>
        promise.Kind.Equals("prophecy", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("quest", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("threat", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("debt", StringComparison.OrdinalIgnoreCase);

    private static string InferPromiseTriggerHint(string text, Entity? anchor)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("read") || anchor?.Has<ReadableComponent>() == true)
        {
            return "read";
        }

        if (LooksLikeServicePromise(lower))
        {
            return "service";
        }

        if (lower.Contains("open") || lower.Contains("door") || anchor?.Has<DoorComponent>() == true)
        {
            return "open";
        }

        if (lower.Contains("buy")
            || lower.Contains("sell")
            || lower.Contains("wares")
            || lower.Contains("trade")
            || lower.Contains("merchant")
            || lower.Contains("market")
            || lower.Contains("stock"))
        {
            return "trade";
        }

        if (lower.Contains("speak") || lower.Contains("talk") || lower.Contains("name"))
        {
            return "talk";
        }

        return "encounter";
    }

    private static string InferPromiseRealizationKind(string kind, string text)
    {
        var lower = $"{kind} {text}".ToLowerInvariant();
        if (LooksLikeServicePromise(lower))
        {
            return "service";
        }

        if (lower.Contains("owe") || lower.Contains("debt"))
        {
            return "debt";
        }

        if (LooksLikeDoorRulePromise(lower))
        {
            return "door_rule";
        }

        if (lower.Contains("sell")
            || lower.Contains("sells")
            || lower.Contains("selling")
            || lower.Contains("buy")
            || lower.Contains("wares")
            || lower.Contains("trade")
            || lower.Contains("merchant")
            || lower.Contains("market")
            || lower.Contains("stock"))
        {
            return "merchant_stock";
        }

        if (lower.Contains("route")
            || lower.Contains("passage")
            || lower.Contains("hidden path")
            || lower.Contains("escape")
            || lower.Contains("drain")
            || lower.Contains("tunnel")
            || lower.Contains("grate")
            || lower.Contains("hidden exit"))
        {
            return "escape_route";
        }

        if (lower.Contains("item") || lower.Contains("blade") || lower.Contains("key"))
        {
            return "item";
        }

        if (lower.Contains("enemy") || lower.Contains("collector") || lower.Contains("threat"))
        {
            return "threat";
        }

        if (lower.Contains("quest") || lower.Contains("reward"))
        {
            return "quest";
        }

        if (lower.Contains("remember") || lower.Contains("name"))
        {
            return "memory";
        }

        return kind.Equals("debt", StringComparison.OrdinalIgnoreCase) ? "threat" : "omen";
    }

    private static bool LooksLikeServicePromise(string lower) =>
        lower.Contains("service")
        || lower.Contains("can help")
        || lower.Contains("offer a service")
        || lower.Contains("offers a service")
        || lower.Contains("offer folk")
        || lower.Contains("offers folk")
        || lower.Contains("mend")
        || lower.Contains("heal")
        || lower.Contains("guide")
        || lower.Contains("ward-breaking")
        || lower.Contains("break the ward")
        || lower.Contains("break wards")
        || lower.Contains("worry a lock")
        || lower.Contains("unlock for")
        || lower.Contains("open the lock")
        || lower.Contains("lift a curse")
        || lower.Contains("curse-lifting")
        || lower.Contains("folk charm");

    private static bool LooksLikeDoorRulePromise(string lower) =>
        (lower.Contains("door") || lower.Contains("gate") || lower.Contains("lock") || lower.Contains("ward"))
        && (lower.Contains("opens")
            || lower.Contains("open ")
            || lower.Contains("unlock")
            || lower.Contains("only to")
            || lower.Contains("when ")
            || lower.Contains("if "));

    private WorldConsequenceApplyResult ApplyQueueBackgroundJob(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var targetId = FirstNonBlank(
            ReadString(payload, "targetId"),
            ReadString(payload, "target_id"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Reject(consequence, "Background-job consequence did not include a target id.");
        }

        var targetKind = NormalizeToken(
            FirstNonBlank(ReadString(payload, "targetKind"), ReadString(payload, "target_kind"), "entity")!,
            "entity");
        var targetEntity = targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase)
            ? EntityById(targetId)
            : null;
        if (targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase) && targetEntity is null)
        {
            return Reject(consequence, "Background-job consequence target entity does not exist.");
        }

        var purpose = NormalizeToken(FirstNonBlank(ReadString(payload, "purpose"), ReadString(payload, "kind"), "detail")!, "detail");
        var priority = Math.Clamp(ReadInt(payload, "priority") ?? consequence.Salience, 0, 999);
        var operation = ReadString(payload, "operation") ?? "queueBackgroundJob";

        if (!_state.BackgroundSettings.Enabled)
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "background_disabled");
        }

        var activeCount = _state.BackgroundJobs.Jobs.Count(job =>
            job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.Completed);
        if (activeCount >= _state.BackgroundSettings.MaxQueuedJobs)
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "queue_full");
        }

        if (_state.BackgroundJobs.HasActiveJob(purpose, targetId))
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "duplicate_active_job");
        }

        if (targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase)
            && _state.Canon.Records.Any(record =>
                record.AttachedTo.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                && record.Kind.Equals(purpose, StringComparison.OrdinalIgnoreCase)))
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "canon_already_exists");
        }

        var job = _state.BackgroundJobs.Enqueue(purpose, targetId, priority, _state.Turn);
        var targetLabel = targetEntity?.Name ?? $"{targetKind}:{targetId}";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"Background job queued: {job.Purpose} for {targetLabel}.")!;
        var delta = new StateDelta(
            operation,
            job.Id,
            summary,
            Details(
                consequence,
                ("jobId", job.Id),
                ("purpose", job.Purpose),
                ("targetId", targetId),
                ("targetKind", targetKind),
                ("targetEntityId", targetEntity?.Id.Value),
                ("priority", job.Priority),
                ("state", job.State.ToString()),
                ("createdTurn", job.CreatedTurn),
                ("queued", true),
                ("skipReason", null),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        var messages = IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>();
        return Applied(consequence, job.Id, messages, delta, ("jobId", job.Id), ("purpose", job.Purpose));
    }

    private WorldConsequenceApplyResult BackgroundJobSkipped(
        WorldConsequence consequence,
        string targetId,
        string targetKind,
        string? targetName,
        string operation,
        string purpose,
        int priority,
        string reason)
    {
        var targetLabel = targetName ?? $"{targetKind}:{targetId}";
        var summary = $"Background job skipped for {targetLabel}: {reason}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("purpose", purpose),
                ("targetId", targetId),
                ("targetKind", targetKind),
                ("targetEntityId", targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase) ? targetId : null),
                ("priority", priority),
                ("state", "Skipped"),
                ("queued", false),
                ("skipReason", reason),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            targetId,
            reason,
            Array.Empty<string>(),
            new[] { delta },
            Details(consequence, ("purpose", purpose), ("skipReason", reason)));
    }

    private WorldConsequenceApplyResult ApplyUpdateBackgroundJob(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var jobId = FirstNonBlank(ReadString(payload, "jobId"), ReadString(payload, "job_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Reject(consequence, "Background job update did not include a job id.");
        }

        var existing = _state.BackgroundJobs.Jobs.FirstOrDefault(job =>
            job.Id.Equals(jobId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Background job does not exist: {jobId}.");
        }

        var stateText = FirstNonBlank(ReadString(payload, "state"), existing.State.ToString())!;
        if (!Enum.TryParse<BackgroundJobState>(stateText, ignoreCase: true, out var state))
        {
            return Reject(consequence, $"Unknown background job state: {stateText}.");
        }

        var updated = existing with
        {
            State = state,
            StartedTurn = ReadInt(payload, "startedTurn") ?? ReadInt(payload, "started_turn") ?? existing.StartedTurn,
            CompletedTurn = ReadInt(payload, "completedTurn") ?? ReadInt(payload, "completed_turn") ?? existing.CompletedTurn,
            AppliedTurn = ReadInt(payload, "appliedTurn") ?? ReadInt(payload, "applied_turn") ?? existing.AppliedTurn,
            ResultText = FirstNonBlank(ReadString(payload, "resultText"), ReadString(payload, "result_text"), existing.ResultText),
            Error = FirstNonBlank(ReadString(payload, "error"), existing.Error),
        };
        _state.BackgroundJobs.Replace(updated);

        var operation = ReadString(payload, "operation") ?? "updateBackgroundJob";
        var summary = FirstNonBlank(
            ReadString(payload, "summary"),
            $"Background job {updated.Id} is {updated.State}.")!;
        var delta = new StateDelta(
            operation,
            updated.Id,
            summary,
            Details(
                consequence,
                ("jobId", updated.Id),
                ("purpose", updated.Purpose),
                ("targetId", updated.TargetId),
                ("priority", updated.Priority),
                ("previousState", existing.State.ToString()),
                ("state", updated.State.ToString()),
                ("createdTurn", updated.CreatedTurn),
                ("startedTurn", updated.StartedTurn),
                ("completedTurn", updated.CompletedTurn),
                ("appliedTurn", updated.AppliedTurn),
                ("hasResultText", !string.IsNullOrWhiteSpace(updated.ResultText)),
                ("error", updated.Error),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        return Applied(consequence, updated.Id, MaybeVisibleMessage(consequence, summary), delta, ("jobId", updated.Id), ("state", updated.State.ToString()));
    }

    private WorldConsequenceApplyResult ApplyScheduleEvent(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var eventType = NormalizeToken(FirstNonBlank(ReadString(payload, "eventType"), ReadString(payload, "event_type"), ReadString(payload, "kind"), "wild_magic")!, "wild_magic");
        var turns = Math.Clamp(ReadInt(payload, "turns") ?? ReadInt(payload, "delay") ?? 1, 1, 999);
        var dueTurn = Math.Max(_state.Turn + 1, ReadInt(payload, "dueTurn") ?? ReadInt(payload, "due_turn") ?? (_state.Turn + turns));
        var eventPayload = ReadDictionary(payload, "eventPayload") ?? ReadDictionary(payload, "payload") ?? PayloadWithoutSchedulerKeys(payload);
        EntityId? sourceEntityId = string.IsNullOrWhiteSpace(consequence.SourceEntityId)
            ? null
            : EntityId.Create(consequence.SourceEntityId);
        var scheduled = _state.ScheduledEvents.Schedule(dueTurn, eventType, sourceEntityId, eventPayload);
        var operation = ReadString(payload, "operation") ?? "scheduleEvent";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"Something is scheduled for turn {scheduled.DueTurn}: {eventType}.")!;
        var delta = new StateDelta(
            operation,
            scheduled.Id,
            summary,
            Details(
                consequence,
                ("eventId", scheduled.Id),
                ("dueTurn", scheduled.DueTurn),
                ("eventType", eventType),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        var messages = IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>();
        return Applied(consequence, scheduled.Id, messages, delta, ("eventId", scheduled.Id), ("dueTurn", scheduled.DueTurn));
    }

    /// <summary>
    /// The three terminal outcomes every Update* consequence (scheduled event, trigger,
    /// persistent effect, behavior tag, tile flow) can end in. Each handler previously grew its
    /// own ad hoc synonym list and they silently diverged -- "expire" worked on triggers and
    /// flows but was rejected on persistent effects, "complete" worked on persistent effects but
    /// was rejected on behavior tags -- so a content-authored consequence using one handler's
    /// vocabulary would fail on another for no reason a player or content author could predict.
    /// Classifying through one shared table keeps all three recognized everywhere; each handler
    /// still picks its own verb text and may accept additional non-terminal actions of its own
    /// (a trigger's "advance", a persistent effect's "consume").
    /// </summary>
    private enum TerminalUpdateAction { Complete, Expire, Remove }

    private static TerminalUpdateAction? ClassifyTerminalAction(string action) => action switch
    {
        "complete" or "completed" => TerminalUpdateAction.Complete,
        "expire" or "expired" => TerminalUpdateAction.Expire,
        "remove" or "clear" or "delete" => TerminalUpdateAction.Remove,
        _ => null,
    };

    private WorldConsequenceApplyResult ApplyUpdateScheduledEvent(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var eventId = FirstNonBlank(ReadString(payload, "eventId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return Reject(consequence, "Scheduled-event update consequence did not include an event id.");
        }

        var record = _state.ScheduledEvents.Events.FirstOrDefault(existing =>
            existing.Id.Equals(eventId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return Reject(consequence, $"Scheduled-event update target does not exist: {eventId}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "due")!, "due");
        var terminal = action == "due" ? TerminalUpdateAction.Complete : ClassifyTerminalAction(action);
        var verb = terminal switch
        {
            TerminalUpdateAction.Complete => "came due",
            TerminalUpdateAction.Remove => "was removed",
            TerminalUpdateAction.Expire => "expired",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported scheduled-event update action: {action}.");
        }

        _state.ScheduledEvents.Remove(record.Id);
        var operation = ReadString(payload, "operation") ?? "updateScheduledEvent";
        var summary = $"{record.Kind} scheduled event {verb}.";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("eventId", record.Id),
                ("eventType", record.Kind),
                ("dueTurn", record.DueTurn),
                ("action", action),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("eventId", record.Id), ("eventType", record.Kind), ("action", action));
    }

    private WorldConsequenceApplyResult ApplyCreateTrigger(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var effectType = FirstNonBlank(ReadString(payload, "effectType"), ReadString(payload, "effect_type"), ReadString(payload, "then"), "message")!;
        var effectFields = ReadDictionary(payload, "effectFields") ?? ReadDictionary(payload, "effect") ?? new Dictionary<string, object?>();
        var anchorEntityId = FirstNonBlank(ReadString(payload, "anchorEntityId"), ReadString(payload, "anchor_entity_id"));
        GridPoint? anchorPoint = null;
        if ((ReadInt(payload, "anchorX") ?? ReadInt(payload, "x")) is { } x
            && (ReadInt(payload, "anchorY") ?? ReadInt(payload, "y")) is { } y)
        {
            anchorPoint = new GridPoint(x, y);
        }
        else if (!string.IsNullOrWhiteSpace(consequence.TargetEntityId)
            && consequence.TargetEntityId.StartsWith("tile:", StringComparison.OrdinalIgnoreCase)
            && TryReadPoint(payload, consequence.TargetEntityId, out var targetPoint))
        {
            anchorPoint = targetPoint;
        }
        else if (string.IsNullOrWhiteSpace(anchorEntityId)
            && !string.IsNullOrWhiteSpace(consequence.TargetEntityId))
        {
            anchorEntityId = consequence.TargetEntityId;
        }

        var sourceEntityId = string.IsNullOrWhiteSpace(consequence.SourceEntityId)
            ? (EntityId?)null
            : EntityId.Create(consequence.SourceEntityId);
        var safeDelay = Math.Clamp(ReadInt(payload, "delay") ?? ReadInt(payload, "turns") ?? 1, 1, 99);
        var safeInterval = Math.Clamp(ReadInt(payload, "interval") ?? 1, 1, 99);
        var safeUses = Math.Clamp(ReadInt(payload, "uses") ?? ReadInt(payload, "maxFires") ?? ReadInt(payload, "max_fires") ?? 1, 1, 20);
        var createdTurn = _state.Turn;
        var duration = ReadInt(payload, "duration");
        var record = _state.Triggers.Add(
            FirstNonBlank(ReadString(payload, "name"), ReadString(payload, "kind"), "trigger")!,
            FirstNonBlank(ReadString(payload, "kind"), "delay")!,
            createdTurn,
            createdTurn + safeDelay,
            safeInterval,
            safeUses,
            duration is null ? null : createdTurn + Math.Max(safeDelay, duration.Value),
            sourceEntityId,
            anchorEntityId,
            anchorPoint,
            Math.Clamp(ReadInt(payload, "radius") ?? 0, 0, 8),
            FirstNonBlank(ReadString(payload, "targetFilter"), ReadString(payload, "affects"), "all")!,
            effectType,
            effectFields,
            FirstNonBlank(ReadString(payload, "description"), ReadString(payload, "text"), "The delayed magic comes due.")!,
            ReadBool(payload, "playerVisible") ?? true);
        var summary = record.Kind.Equals("aura", StringComparison.OrdinalIgnoreCase)
            ? $"{record.Name} begins to pulse."
            : $"{record.Name} settles into a later turn.";
        AddMessageIfAllowed(consequence, payload, summary, includeVisible: false, playerVisible: record.PlayerVisible);

        var operation = ReadString(payload, "operation") ?? "createTrigger";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("triggerId", record.Id),
                ("triggerName", record.Name),
                ("kind", record.Kind),
                ("nextTurn", record.NextTurn),
                ("interval", record.Interval),
                ("remainingUses", record.RemainingUses),
                ("expiresTurn", record.ExpiresTurn),
                ("sourceEntityId", record.SourceEntityId?.Value),
                ("anchorEntityId", record.AnchorEntityId),
                ("anchorX", record.AnchorPoint?.X),
                ("anchorY", record.AnchorPoint?.Y),
                ("radius", record.Radius),
                ("targetFilter", record.TargetFilter),
                ("effectType", record.EffectType),
                ("playerVisible", record.PlayerVisible)));
        var messages = record.PlayerVisible && IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence)
            ? new[] { summary }
            : Array.Empty<string>();
        return Applied(consequence, record.Id, messages, delta, ("triggerId", record.Id), ("kind", record.Kind), ("nextTurn", record.NextTurn));
    }

    private WorldConsequenceApplyResult ApplyUpdateTrigger(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var triggerId = FirstNonBlank(ReadString(payload, "triggerId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(triggerId))
        {
            return Reject(consequence, "Trigger update consequence did not include a trigger id.");
        }

        var record = _state.Triggers.Records.FirstOrDefault(existing =>
            existing.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return Reject(consequence, $"Trigger update target does not exist: {triggerId}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "advance")!, "advance");
        var operation = ReadString(payload, "operation") ?? "updateTrigger";
        var summary = ClassifyTerminalAction(action) switch
        {
            TerminalUpdateAction.Complete => RemoveTrigger(record, "completed"),
            TerminalUpdateAction.Expire => RemoveTrigger(record, "expired"),
            TerminalUpdateAction.Remove => RemoveTrigger(record, "removed"),
            _ => action is "advance" or "reschedule" or "set" ? ApplyTriggerAdvance(payload, record) : null,
        };
        if (summary is null)
        {
            return Reject(consequence, $"Unsupported trigger update action: {action}.");
        }

        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("triggerId", record.Id),
                ("triggerName", record.Name),
                ("action", action),
                ("previousNextTurn", record.NextTurn),
                ("previousRemainingUses", record.RemainingUses),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("triggerId", record.Id), ("action", action));
    }

    private string? ApplyTriggerAdvance(IReadOnlyDictionary<string, object?> payload, TriggerRecord record)
    {
        var nextTurn = ReadInt(payload, "nextTurn") ?? ReadInt(payload, "next_turn");
        var remainingUses = ReadInt(payload, "remainingUses") ?? ReadInt(payload, "remaining_uses");
        if (nextTurn is null && remainingUses is null)
        {
            return null;
        }

        var updated = record with
        {
            NextTurn = Math.Max(_state.Turn + 1, nextTurn ?? record.NextTurn),
            RemainingUses = Math.Max(1, remainingUses ?? record.RemainingUses),
        };
        _state.Triggers.Replace(updated);
        return $"{record.Name} advances to turn {updated.NextTurn} with {updated.RemainingUses} use(s) left.";
    }

    private string RemoveTrigger(TriggerRecord record, string verb)
    {
        _state.Triggers.Remove(record.Id);
        return $"{record.Name} {verb}.";
    }
}
