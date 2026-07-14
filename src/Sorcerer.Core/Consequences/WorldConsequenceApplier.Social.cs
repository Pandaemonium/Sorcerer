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
/// <see cref="WorldConsequenceApplier"/> handlers for social records: memory, bond, want, claim, rumor, deed, suspicion, legend, and canon.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyRecordSuspicion(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var effectX = ReadInt(payload, "effectX") ?? ReadInt(payload, "effect_x") ?? ReadInt(payload, "x");
        var effectY = ReadInt(payload, "effectY") ?? ReadInt(payload, "effect_y") ?? ReadInt(payload, "y");
        if (effectX is null || effectY is null)
        {
            return Reject(consequence, "Suspicion consequence did not include an effect coordinate.");
        }

        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "actor_entity_id"),
            consequence.TargetEntityId,
            consequence.SourceEntityId);
        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        if (!string.IsNullOrWhiteSpace(actorId) && actor is null)
        {
            return Reject(consequence, "Suspicion consequence actor entity does not exist.");
        }

        var kind = NormalizeToken(FirstNonBlank(ReadString(payload, "kind"), "suspicion")!, "suspicion");
        var operation = ReadString(payload, "operation") ?? "recordSuspicion";
        var plans = engine.PlanEffectSuspicion(new GridPoint(effectX.Value, effectY.Value), kind, actor);
        var records = plans
            .Select(plan => _state.Suspicions.Append(
                _state.Turn,
                plan.WitnessSoulId,
                plan.Kind,
                plan.EffectPoint,
                plan.Status,
                plan.SuspectedSoulId,
                plan.AttributedTurn,
                plan.ExpiresTurn))
            .ToArray();
        var deltas = records
            .Select(record => new StateDelta(
                operation,
                record.WitnessSoulId,
                $"Suspicion recorded: {kind} seen by {record.WitnessSoulId} ({record.Status}).",
                Details(
                    consequence,
                    ("suspicionId", record.Id),
                    ("witnessSoulId", record.WitnessSoulId),
                    ("kind", record.Kind),
                    ("effectX", record.EffectPoint.X),
                    ("effectY", record.EffectPoint.Y),
                    ("status", record.Status),
                    ("suspectedSoulId", record.SuspectedSoulId),
                    ("attributedTurn", record.AttributedTurn),
                    ("expiresTurn", record.ExpiresTurn))))
            .ToArray();
        return new WorldConsequenceApplyResult(
            true,
            actor?.Id.Value,
            null,
            Array.Empty<string>(),
            deltas,
            Details(consequence, ("created", records.Length), ("kind", kind)));
    }

    private WorldConsequenceApplyResult ApplyUpdateSuspicion(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var suspicionId = FirstNonBlank(ReadString(payload, "suspicionId"), ReadString(payload, "suspicion_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(suspicionId))
        {
            return Reject(consequence, "Update-suspicion consequence did not include a suspicion id.");
        }

        var existing = _state.Suspicions.Records.FirstOrDefault(record =>
            record.Id.Equals(suspicionId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, "Suspicion record does not exist.");
        }

        var status = NormalizeToken(FirstNonBlank(ReadString(payload, "status"), existing.Status)!, existing.Status);
        var suspectedSoulId = FirstNonBlank(ReadString(payload, "suspectedSoulId"), ReadString(payload, "suspected_soul_id"), existing.SuspectedSoulId);
        var attributedTurn = ReadInt(payload, "attributedTurn") ?? ReadInt(payload, "attributed_turn") ?? existing.AttributedTurn;
        var updated = existing with
        {
            Status = status,
            SuspectedSoulId = suspectedSoulId,
            AttributedTurn = attributedTurn,
        };
        _state.Suspicions.Replace(updated);

        var operation = ReadString(payload, "operation") ?? "updateSuspicion";
        var summary = $"Suspicion {updated.Id} is now {updated.Status}.";
        var delta = new StateDelta(
            operation,
            updated.Id,
            summary,
            Details(
                consequence,
                ("suspicionId", updated.Id),
                ("witnessSoulId", updated.WitnessSoulId),
                ("kind", updated.Kind),
                ("status", updated.Status),
                ("suspectedSoulId", updated.SuspectedSoulId),
                ("attributedTurn", updated.AttributedTurn),
                ("expiresTurn", updated.ExpiresTurn)));
        return Applied(consequence, updated.Id, MaybeVisibleMessage(consequence, summary), delta, ("suspicionId", updated.Id), ("status", updated.Status));
    }

    private WorldConsequenceApplyResult ApplyRecordDeed(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actor = RequiredEntity(consequence, "Deed consequence did not include an actor entity id.");
        if (actor.Result is not null)
        {
            return actor.Result;
        }

        var originX = ReadInt(payload, "originX") ?? ReadInt(payload, "origin_x");
        var originY = ReadInt(payload, "originY") ?? ReadInt(payload, "origin_y");
        if (originX is null || originY is null)
        {
            return Reject(consequence, "Deed consequence did not include an origin coordinate.");
        }

        var effectX = ReadInt(payload, "effectX") ?? ReadInt(payload, "effect_x");
        var effectY = ReadInt(payload, "effectY") ?? ReadInt(payload, "effect_y");
        if ((effectX is null) != (effectY is null))
        {
            return Reject(consequence, "Deed consequence included an incomplete effect coordinate.");
        }

        var kind = NormalizeToken(FirstNonBlank(ReadString(payload, "kind"), "deed")!, "deed");
        var magnitude = Math.Clamp(ReadInt(payload, "magnitude") ?? 1, 1, 999);
        var origin = new GridPoint(originX.Value, originY.Value);
        var effectPoint = effectX is null ? (GridPoint?)null : new GridPoint(effectX.Value, effectY!.Value);
        var tags = NormalizeTags(ReadStringList(payload, "tags"));
        var capture = engine.PlanDeedCapture(
            actor.Entity!,
            kind,
            magnitude,
            origin,
            effectPoint,
            tags);
        var plan = capture.Plan;
        var deed = _state.Deeds.Append(
            plan.Turn,
            plan.ActorSoulId,
            plan.Kind,
            plan.Magnitude,
            plan.PlaceKey,
            plan.Visibility,
            plan.Witnesses,
            plan.Tags,
            plan.EffectWitnesses,
            plan.AttributedSoulId,
            plan.AttributionStatus);
        var operation = ReadString(payload, "operation") ?? "recordDeed";
        var summary = $"Deed recorded: {deed.Kind} ({deed.Visibility}).";
        var delta = new StateDelta(
            operation,
            actor.Entity!.Id.Value,
            summary,
            Details(
                consequence,
                ("deedId", deed.Id),
                ("actorSoulId", deed.ActorSoulId),
                ("kind", deed.Kind),
                ("magnitude", deed.Magnitude),
                ("placeKey", deed.PlaceKey),
                ("visibility", deed.Visibility),
                ("witnesses", deed.Witnesses),
                ("effectWitnesses", deed.EffectWitnesses ?? Array.Empty<string>()),
                ("attributedSoulId", deed.AttributedSoulId),
                ("attributionStatus", deed.AttributionStatus),
                ("tags", deed.Tags),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        var messages = MaybeVisibleMessage(consequence, summary);
        return new WorldConsequenceApplyResult(
            true,
            actor.Entity.Id.Value,
            null,
            messages,
            capture.Deltas.Concat(new[] { delta }).ToArray(),
            Details(consequence, ("deedId", deed.Id), ("visibility", deed.Visibility)));
    }

    private WorldConsequenceApplyResult ApplyUpdateDeed(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var deedId = FirstNonBlank(ReadString(payload, "deedId"), ReadString(payload, "deed_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(deedId))
        {
            return Reject(consequence, "Deed update consequence did not include a deed id.");
        }

        var deed = _state.Deeds.Records.FirstOrDefault(record =>
            record.Id.Equals(deedId, StringComparison.OrdinalIgnoreCase));
        if (deed is null)
        {
            return Reject(consequence, $"Deed update target does not exist: {deedId}");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "mark_applied")!, "mark_applied");
        if (action is not ("mark_applied" or "applied" or "apply"))
        {
            return Reject(consequence, $"Unknown deed update action: {action}");
        }

        var wasApplied = _state.Deeds.IsApplied(deed.Id);
        _state.Deeds.MarkApplied(deed.Id);
        var operation = ReadString(payload, "operation") ?? "updateDeed";
        var summary = wasApplied
            ? $"Deed already applied: {deed.Id}."
            : $"Deed marked applied: {deed.Id}.";
        var delta = new StateDelta(
            operation,
            deed.Id,
            summary,
            Details(
                consequence,
                ("deedId", deed.Id),
                ("kind", deed.Kind),
                ("visibility", deed.Visibility),
                ("action", action),
                ("applied", true),
                ("wasApplied", wasApplied),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(
            consequence,
            deed.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("deedId", deed.Id),
            ("applied", true),
            ("wasApplied", wasApplied));
    }

    private WorldConsequenceApplyResult ApplyAddLegend(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actorSoulId = FirstNonBlank(ReadString(payload, "actorSoulId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(actorSoulId))
        {
            return Reject(consequence, "Legend consequence did not include an actor soul id.");
        }

        var tag = CleanLedgerKey(FirstNonBlank(ReadString(payload, "tag"), "uncanny")!, "uncanny");
        var weight = Math.Clamp(ReadInt(payload, "weight") ?? 1, 1, 999);
        var sourceId = FirstNonBlank(ReadString(payload, "sourceId"), ReadString(payload, "source_id"), consequence.Source, "unknown")!;
        _state.Legend.Add(actorSoulId, tag, weight, sourceId);
        var operation = ReadString(payload, "operation") ?? "addLegend";
        var summary = $"{actorSoulId} gains legend tag {tag} ({weight}).";
        var delta = new StateDelta(
            operation,
            actorSoulId,
            summary,
            Details(
                consequence,
                ("tag", tag),
                ("weight", weight),
                ("sourceId", sourceId),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, actorSoulId, MaybeVisibleMessage(consequence, summary), delta, ("tag", tag), ("weight", weight));
    }

    private WorldConsequenceApplyResult ApplyAddCanon(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var kind = CleanLedgerKey(FirstNonBlank(ReadString(payload, "kind"), "canon")!, "canon");
        var attachedTo = FirstNonBlank(ReadString(payload, "attachedTo"), ReadString(payload, "attached_to"), consequence.TargetEntityId, "world")!;
        var text = FirstNonBlank(ReadString(payload, "text"), consequence.Evidence, "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Canon consequence did not include text.");
        }

        var summary = FirstNonBlank(ReadString(payload, "summary"), text)!;
        var tags = ReadStringList(payload, "tags");
        var record = _state.Canon.Add(
            kind,
            attachedTo,
            text,
            summary,
            tags,
            FirstNonBlank(ReadString(payload, "canonSource"), ReadString(payload, "source"), consequence.Source, "world_consequence")!,
            _state.Turn);
        var operation = ReadString(payload, "operation") ?? "addCanon";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(consequence, ("kind", kind), ("attachedTo", attachedTo), ("tags", tags)));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("canonId", record.Id));
    }

    private WorldConsequenceApplyResult ApplyEditMemory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Edit-memory consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var op = NormalizeToken(FirstNonBlank(ReadString(payload, "op"), "add")!, "add");
        return op is "remove" or "erase" or "forget"
            ? RemoveMemory(consequence, target.Entity!, payload)
            : AddOrAlterMemory(consequence, target.Entity!, payload, op == "alter" ? "altered by wild magic" : "planted by wild magic");
    }

    private WorldConsequenceApplyResult ApplyRecordRumor(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Rumor consequence did not include text.");
        }

        var sourceKind = NormalizeToken(FirstNonBlank(ReadString(payload, "sourceKind"), ReadString(payload, "source_kind"), consequence.Source)!, "unknown");
        var sourceId = NormalizeToken(FirstNonBlank(ReadString(payload, "sourceId"), ReadString(payload, "source_id"), consequence.TargetEntityId, "unknown")!, "unknown");
        if (_state.Rumors.HasSource(sourceKind, sourceId))
        {
            return new WorldConsequenceApplyResult(
                false,
                sourceId,
                "duplicate_rumor_source",
                Array.Empty<string>(),
                Array.Empty<StateDelta>(),
                Details(consequence, ("sourceKind", sourceKind), ("sourceId", sourceId)));
        }

        var originRegionId = FirstNonBlank(ReadString(payload, "originRegionId"), ReadString(payload, "origin_region_id"), _state.RegionId)!;
        var currentRegionId = FirstNonBlank(ReadString(payload, "currentRegionId"), ReadString(payload, "current_region_id"), originRegionId)!;
        var salience = Math.Clamp(ReadInt(payload, "salience") ?? Math.Max(1, consequence.Salience), 1, 5);
        var status = FirstNonBlank(ReadString(payload, "status"), "active")!;
        var originalText = FirstNonBlank(ReadString(payload, "originalText"), ReadString(payload, "original_text"));
        var hops = Math.Max(0, ReadInt(payload, "hops") ?? 0);
        var rumor = _state.Rumors.Append(
            _state.Turn,
            sourceKind,
            sourceId,
            originRegionId,
            currentRegionId,
            text,
            salience,
            ReadStringList(payload, "carrierIds").Concat(ReadStringList(payload, "carriers")),
            ReadStringList(payload, "tags"),
            status,
            ReadStringList(payload, "distortionHistory").Concat(ReadStringList(payload, "distortion_history")),
            hops,
            originalText);
        var operation = ReadString(payload, "operation") ?? "recordRumor";
        var summary = ReadString(payload, "summary") ?? $"A rumor begins: {rumor.Text}";
        var delta = new StateDelta(
            operation,
            rumor.Id,
            summary,
            Details(
                consequence,
                ("rumorId", rumor.Id),
                ("sourceKind", rumor.SourceKind),
                ("sourceId", rumor.SourceId),
                ("originRegionId", rumor.OriginRegionId),
                ("currentRegionId", rumor.CurrentRegionId),
                ("salience", rumor.Salience),
                ("carrierIds", rumor.CarrierIds),
                ("tags", rumor.Tags)));
        return Applied(
            consequence,
            rumor.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("rumorId", rumor.Id),
            ("sourceKind", rumor.SourceKind),
            ("sourceId", rumor.SourceId),
            ("salience", rumor.Salience));
    }

    private WorldConsequenceApplyResult ApplyUpdateRumor(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var rumorId = FirstNonBlank(ReadString(payload, "rumorId"), ReadString(payload, "rumor_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(rumorId))
        {
            return Reject(consequence, "Update-rumor consequence did not include a rumor id.");
        }

        var existing = _state.Rumors.Records.FirstOrDefault(rumor =>
            rumor.Id.Equals(rumorId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Rumor not found: {rumorId}");
        }

        IEnumerable<string> carrierIds = HasAnyKey(payload, "carrierIds", "carrier_ids", "carriers")
            ? ReadStringList(payload, "carrierIds")
                .Concat(ReadStringList(payload, "carrier_ids"))
                .Concat(ReadStringList(payload, "carriers"))
            : existing.CarrierIds;
        carrierIds = carrierIds
            .Concat(ReadStringList(payload, "addCarrierIds"))
            .Concat(ReadStringList(payload, "add_carrier_ids"))
            .Concat(ReadStringList(payload, "newCarriers"));

        IEnumerable<string> tags = HasAnyKey(payload, "tags")
            ? ReadStringList(payload, "tags")
            : existing.Tags;
        tags = tags
            .Concat(ReadStringList(payload, "addTags"))
            .Concat(ReadStringList(payload, "add_tags"));

        IEnumerable<string> history = HasAnyKey(payload, "distortionHistory", "distortion_history")
            ? ReadStringList(payload, "distortionHistory")
                .Concat(ReadStringList(payload, "distortion_history"))
            : existing.DistortionHistory;
        history = history
            .Concat(ReadStringList(payload, "appendDistortionHistory"))
            .Concat(ReadStringList(payload, "append_distortion_history"))
            .Concat(ReadStringList(payload, "historyEntry"))
            .TakeLast(12);

        var salience = Math.Clamp(ReadInt(payload, "salience") ?? existing.Salience, 1, 5);
        var hops = ReadInt(payload, "hops")
            ?? existing.Hops + (ReadBool(payload, "incrementHops") == true ? 1 : 0);
        var updated = existing with
        {
            LastTurn = ReadInt(payload, "lastTurn") ?? ReadInt(payload, "last_turn") ?? _state.Turn,
            CurrentRegionId = FirstNonBlank(ReadString(payload, "currentRegionId"), ReadString(payload, "current_region_id"), existing.CurrentRegionId)!,
            Text = FirstNonBlank(ReadString(payload, "text"), existing.Text)!,
            OriginalText = FirstNonBlank(ReadString(payload, "originalText"), ReadString(payload, "original_text"), existing.OriginalText)!,
            Salience = salience,
            Status = FirstNonBlank(ReadString(payload, "status"), existing.Status)!,
            CarrierIds = carrierIds.ToArray(),
            Tags = tags.ToArray(),
            DistortionHistory = history.ToArray(),
            Hops = Math.Max(0, hops),
        };

        var rumor = _state.Rumors.Replace(updated);
        if (rumor is null)
        {
            return Reject(consequence, $"Rumor not found: {rumorId}");
        }

        var operation = ReadString(payload, "operation") ?? "updateRumor";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"Rumor {rumor.Id} changes: {rumor.Text}")!;
        var delta = new StateDelta(
            operation,
            rumor.Id,
            summary,
            Details(
                consequence,
                ("rumorId", rumor.Id),
                ("sourceKind", rumor.SourceKind),
                ("sourceId", rumor.SourceId),
                ("originRegionId", rumor.OriginRegionId),
                ("currentRegionId", rumor.CurrentRegionId),
                ("salience", rumor.Salience),
                ("status", rumor.Status),
                ("carrierIds", rumor.CarrierIds),
                ("tags", rumor.Tags),
                ("hops", rumor.Hops)));
        return Applied(
            consequence,
            rumor.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("rumorId", rumor.Id),
            ("salience", rumor.Salience),
            ("status", rumor.Status),
            ("hops", rumor.Hops));
    }

    private WorldConsequenceApplyResult ApplyRecordClaim(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Claim consequence did not include text.");
        }

        var speakerId = FirstNonBlank(ReadString(payload, "speakerId"), ReadString(payload, "speaker_id"), consequence.TargetEntityId, consequence.SourceEntityId, "unknown")!;
        var listenerSoulId = FirstNonBlank(
            ReadString(payload, "listenerSoulId"),
            ReadString(payload, "listener_soul_id"),
            SoulIdFor(_state.ControlledEntity),
            "unknown")!;
        var category = NormalizeToken(FirstNonBlank(ReadString(payload, "category"), ReadString(payload, "kind"), "memory")!, "memory");
        var subject = FirstNonBlank(ReadString(payload, "subject"), text)!;
        var salience = Math.Clamp(ReadInt(payload, "salience") ?? Math.Max(1, consequence.Salience), 1, 5);
        var confidence = Math.Clamp(ReadInt(payload, "confidence") ?? Math.Clamp(consequence.Confidence, 0, 100), 0, 100);
        var playerVisible = ReadBool(payload, "playerVisible")
            ?? ReadBool(payload, "player_visible")
            ?? IsVisible(consequence.Visibility);
        var source = FirstNonBlank(ReadString(payload, "claimSource"), ReadString(payload, "claim_source"), consequence.Source, "unknown")!;
        var status = FirstNonBlank(ReadString(payload, "status"), "reported")!;
        var duplicate = _state.Claims.Records.FirstOrDefault(record =>
            record.SpeakerId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
            && record.ListenerSoulId.Equals(listenerSoulId, StringComparison.OrdinalIgnoreCase)
            && record.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
            && NormalizeClaimText(record.Text).Equals(NormalizeClaimText(text), StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            var duplicateOperation = ReadString(payload, "duplicateOperation") ?? "claimDuplicate";
            var duplicateSummary = $"Claim already recorded: {duplicate.Text}";
            var duplicateDelta = new StateDelta(
                duplicateOperation,
                duplicate.Id,
                duplicateSummary,
                Details(
                    consequence,
                    ("claimId", duplicate.Id),
                    ("speakerId", duplicate.SpeakerId),
                    ("listenerSoulId", duplicate.ListenerSoulId),
                    ("category", duplicate.Category),
                    ("subject", duplicate.Subject),
                    ("salience", duplicate.Salience),
                    ("confidence", duplicate.Confidence),
                    ("playerVisible", false),
                    ("status", duplicate.Status),
                    ("tags", duplicate.Tags),
                    ("duplicate", true)));
            return Applied(
                consequence,
                duplicate.Id,
                Array.Empty<string>(),
                duplicateDelta,
                ("claimId", duplicate.Id),
                ("duplicate", true),
                ("playerVisible", false));
        }

        var record = _state.Claims.Append(
            _state.Turn,
            source,
            speakerId,
            listenerSoulId,
            text,
            category,
            subject,
            salience,
            confidence,
            playerVisible,
            ReadStringList(payload, "tags"),
            status);

        var operation = ReadString(payload, "operation") ?? "claimRecorded";
        var summary = ReadString(payload, "summary") ?? $"A reported claim is recorded: {record.Text}";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("claimId", record.Id),
                ("speakerId", record.SpeakerId),
                ("listenerSoulId", record.ListenerSoulId),
                ("category", record.Category),
                ("subject", record.Subject),
                ("salience", record.Salience),
                ("confidence", record.Confidence),
                ("playerVisible", record.PlayerVisible),
                ("status", record.Status),
                ("tags", record.Tags)));
        return Applied(
            consequence,
            record.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("claimId", record.Id),
            ("speakerId", record.SpeakerId),
            ("category", record.Category),
            ("salience", record.Salience),
            ("confidence", record.Confidence),
            ("playerVisible", record.PlayerVisible));
    }

    private WorldConsequenceApplyResult ApplyUpdateClaim(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var claimId = FirstNonBlank(ReadString(payload, "claimId"), ReadString(payload, "claim_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(claimId))
        {
            return Reject(consequence, "Claim update did not include a claim id.");
        }

        var existing = _state.Claims.Records.FirstOrDefault(record =>
            record.Id.Equals(claimId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Claim update target does not exist: {claimId}");
        }

        var status = FirstNonBlank(ReadString(payload, "status"));
        var boundPromiseId = FirstNonBlank(ReadString(payload, "boundPromiseId"), ReadString(payload, "bound_promise_id"), ReadString(payload, "promiseId"));
        var appliedTo = FirstNonBlank(ReadString(payload, "appliedTo"), ReadString(payload, "applied_to"), ReadString(payload, "targetId"));
        var updated = _state.Claims.Update(existing.Id, status, boundPromiseId, appliedTo);
        if (updated is null)
        {
            return Reject(consequence, $"Claim update target does not exist: {claimId}");
        }

        var operation = ReadString(payload, "operation") ?? "updateClaim";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"Claim {updated.Id} is now {updated.Status}.")!;
        var delta = new StateDelta(
            operation,
            updated.Id,
            summary,
            Details(
                consequence,
                ("claimId", updated.Id),
                ("previousStatus", existing.Status),
                ("status", updated.Status),
                ("boundPromiseId", updated.BoundPromiseId),
                ("appliedTo", updated.AppliedTo),
                ("category", updated.Category),
                ("subject", updated.Subject),
                ("playerVisible", updated.PlayerVisible)));
        var messages = AddMessageIfAllowed(
                consequence,
                payload,
                summary,
                defaultEmitMessage: false,
                playerVisible: updated.PlayerVisible)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, updated.Id, messages, delta, ("claimId", updated.Id), ("status", updated.Status));
    }

    private WorldConsequenceApplyResult AddOrAlterMemory(
        WorldConsequence consequence,
        Entity target,
        IReadOnlyDictionary<string, object?> payload,
        string fallbackProvenance)
    {
        var text = FirstNonBlank(ReadString(payload, "text"), ReadString(payload, "subject"), "something that did not happen")!;
        var strength = Math.Clamp(ReadInt(payload, "strength") ?? consequence.Salience, 1, 5);
        var provenance = FirstNonBlank(ReadString(payload, "provenance"), fallbackProvenance, consequence.Source)!;
        var shareable = ReadBool(payload, "shareable") ?? strength >= 4;
        var operation = ReadString(payload, "operation") ?? "editMemory";
        var summary = $"{Possessive(target)} memory shifts: {text}";
        var memory = Apply(WorldConsequence.RecordMemory(
            consequence.Source,
            target.Id.Value,
            text,
            provenance,
            strength,
            shareable,
            consequence.Visibility,
            consequence.SourceEntityId,
            consequence.Evidence,
            "Memory edit added or altered a concrete remembered fact through the shared memory lifecycle.",
            operation,
            new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["parentOperation"] = operation,
                ["op"] = "add",
                ["strength"] = strength,
                ["summary"] = summary,
            }));
        if (!memory.Applied)
        {
            return memory;
        }

        return new WorldConsequenceApplyResult(
            true,
            target.Id.Value,
            null,
            memory.Messages,
            memory.Deltas,
            Details(consequence, ("op", "add"), ("text", text), ("strength", strength), ("provenance", provenance)));
    }

    private WorldConsequenceApplyResult RemoveMemory(
        WorldConsequence consequence,
        Entity target,
        IReadOnlyDictionary<string, object?> payload)
    {
        var snapshot = GameStateSnapshot.Capture(_state);
        var subject = FirstNonBlank(ReadString(payload, "subject"), ReadString(payload, "text"), "") ?? "";
        var inferredAboutCaster = subject.Contains("caster", StringComparison.OrdinalIgnoreCase)
            || subject.Contains("player", StringComparison.OrdinalIgnoreCase)
            || subject.Equals("me", StringComparison.OrdinalIgnoreCase);
        var aboutCaster = ReadBool(payload, "aboutCaster") ?? inferredAboutCaster;
        if (target.TryGet<MemoryComponent>(out var memory))
        {
            var remaining = memory.Records
                .Where(record => !RecordMentionsMemory(record, subject, aboutCaster))
                .ToArray();
            target.Set(new MemoryComponent(remaining));
        }

        var operation = ReadString(payload, "operation") ?? "editMemory";
        var summary = aboutCaster
            ? $"{target.Name} no longer remembers the caster; the hostility drains out of them."
            : $"{Possessive(target)} memory of {subject} fades.";
        var delta = new StateDelta(
            operation,
            target.Id.Value,
            summary,
            Details(consequence, ("op", "remove"), ("subject", subject), ("aboutCaster", aboutCaster)));
        var messages = MaybeVisibleMessage(consequence, summary).ToList();
        var deltas = new List<StateDelta> { delta };
        var bondDelta = 0;
        if (aboutCaster && target.TryGet<SoulComponent>(out var npcSoul))
        {
            var playerSoulId = _state.ControlledEntity.TryGet<SoulComponent>(out var playerSoul)
                ? playerSoul.SoulId
                : _state.ControlledEntityId.Value;
            var loyaltyFloor = Math.Clamp(ReadInt(payload, "loyaltyFloor") ?? 5, -10, 10);
            var currentLoyalty = _state.Bonds.TryGet(npcSoul.SoulId, playerSoulId, out var existingBond)
                ? existingBond.Loyalty
                : 0;
            bondDelta = Math.Max(0, loyaltyFloor - currentLoyalty);
            if (bondDelta > 0)
            {
                var bond = Apply(WorldConsequence.UpdateBond(
                    consequence.Source,
                    target.Id.Value,
                    playerSoulId,
                    bondDelta,
                    0,
                    0,
                    0,
                    posture: null,
                    sourceEntityId: consequence.SourceEntityId,
                    evidence: consequence.Evidence,
                    reason: "Forgetting the caster calms hostility through the shared bond lifecycle.",
                    operation: "memoryBondFloor",
                    maxDelta: bondDelta,
                    details: new Dictionary<string, object?>
                    {
                        ["parentConsequenceType"] = consequence.Type,
                        ["parentOperation"] = operation,
                        ["loyaltyFloor"] = loyaltyFloor,
                        ["aboutCaster"] = true,
                        ["playerVisible"] = false,
                    }));
                if (!bond.Applied)
                {
                    return RollBackEditMemory(
                        consequence,
                        snapshot,
                        target.Id.Value,
                        operation,
                        subject,
                        bond.Deltas,
                        bond.Messages,
                        bond.Error ?? "memory_bond_floor_rejected");
                }

                messages.AddRange(bond.Messages);
                deltas.AddRange(bond.Deltas);
            }
        }

        return new WorldConsequenceApplyResult(
            true,
            target.Id.Value,
            null,
            messages,
            deltas,
            Details(consequence, ("op", "remove"), ("subject", subject), ("bondDelta", bondDelta)));
    }

    private WorldConsequenceApplyResult RollBackEditMemory(
        WorldConsequence consequence,
        GameStateSnapshot snapshot,
        string targetId,
        string operation,
        string subject,
        IReadOnlyList<StateDelta> failedDeltas,
        IReadOnlyList<string> failedMessages,
        string failure)
    {
        snapshot.Restore(_state);
        var skipped = new StateDelta(
            "editMemorySkipped",
            targetId,
            $"Memory edit rolled back: {failure}.",
            Details(
                consequence,
                ("operation", operation),
                ("op", "remove"),
                ("subject", subject),
                ("failure", failure),
                ("rejectedCount", failedDeltas.Count(delta =>
                    delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            targetId,
            failure,
            Array.Empty<string>(),
            failedDeltas.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("error", failure),
                ("operation", operation),
                ("op", "remove"),
                ("subject", subject),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count)));
    }

    private bool RequireEngine(
        WorldConsequence consequence,
        out GameEngine engine,
        out WorldConsequenceApplyResult result)
    {
        if (_engine is not null)
        {
            engine = _engine;
            result = WorldConsequenceApplyResult.Empty();
            return true;
        }

        engine = null!;
        result = Reject(consequence, "This consequence type requires engine services.");
        return false;
    }

    /// <summary>
    /// Animation makes an existing world entity act: a defeated actor rises again, or an inert
    /// fixture/prop/floor item gains a bounded body. Stats are capped well below summoned-boss
    /// range regardless of what the payload asks for, and the result is marked summoned so zone
    /// rules treat it like any other conjured ally.
    /// </summary>
    private WorldConsequenceApplyResult ApplyRecordMemory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Memory consequence did not include text.");
        }

        var ownerId = FirstNonBlank(consequence.TargetEntityId, consequence.SourceEntityId, _state.ControlledEntityId.Value)!;
        var salience = Math.Clamp(consequence.Salience, 1, 5);
        var provenance = FirstNonBlank(ReadString(payload, "provenance"), consequence.Source) ?? consequence.Source;
        var shareable = ReadBool(payload, "shareable") ?? true;
        var requireOwnerEntity = ReadBool(payload, "requireOwnerEntity")
            ?? ReadBool(payload, "require_owner_entity")
            ?? false;
        var owner = EntityById(ownerId);
        if (requireOwnerEntity && owner is null)
        {
            return Reject(consequence, "Memory consequence owner entity does not exist.");
        }

        var worldMemory = _state.Memories.Append(ownerId, text, provenance, salience, shareable);
        if (owner is not null)
        {
            var memories = owner.TryGet<MemoryComponent>(out var existing)
                ? existing.Records.ToList()
                : new List<EntityMemoryRecord>();
            memories.Add(new EntityMemoryRecord(
                $"memory_{NormalizeToken(consequence.Source, "source")}_{_state.Turn}_{memories.Count + 1}",
                text,
                consequence.Source,
                provenance,
                salience,
                shareable));
            owner.Set(new MemoryComponent(memories.TakeLast(24).ToArray()));
        }

        var operation = ReadString(payload, "operation") ?? "recordMemory";
        var summary = ReadString(payload, "summary") ?? $"Memory recorded: {text}";
        var delta = new StateDelta(
            operation,
            ownerId,
            summary,
            Details(
                consequence,
                ("memoryId", worldMemory.Id),
                ("salience", salience),
                ("provenance", provenance),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, ownerId, MaybeVisibleMessage(consequence, summary), delta, ("memoryId", worldMemory.Id), ("salience", salience), ("provenance", provenance));
    }

    private WorldConsequenceApplyResult ApplyUpdateBond(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var entityId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Reject(consequence, "Bond consequence did not include an entity id.");
        }

        var operation = ReadString(payload, "operation") ?? "updateBond";
        var entity = EntityById(entityId);
        if (entity is null)
        {
            return new WorldConsequenceApplyResult(
                false,
                entityId,
                "missing_entity",
                Array.Empty<string>(),
                new[]
                {
                    new StateDelta(
                        "worldConsequenceRejected",
                        entityId,
                        "Bond consequence rejected because the entity no longer exists.",
                        Details(consequence, ("proposalType", "bond"), ("operation", operation))),
                },
                Details(consequence, ("proposalType", "bond"), ("operation", operation)));
        }

        var targetSoulId = ReadString(payload, "targetSoulId");
        if (string.IsNullOrWhiteSpace(targetSoulId))
        {
            return Reject(consequence, "Bond consequence did not include a target soul id.");
        }

        var maxDelta = Math.Max(0, ReadInt(payload, "maxDelta") ?? _defaultBondDeltaLimit);
        var bond = _state.Bonds.Adjust(
            SoulIdFor(entity),
            targetSoulId,
            ClampDelta(ReadInt(payload, "loyaltyDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "fearDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "admirationDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "resentmentDelta") ?? 0, maxDelta),
            FirstNonBlank(ReadString(payload, "posture")));
        var summary = $"{Possessive(entity)} posture shifts: {BondSummary(bond)}.";
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("loyalty", bond.Loyalty),
                ("fear", bond.Fear),
                ("admiration", bond.Admiration),
                ("resentment", bond.Resentment),
                ("posture", bond.Posture)));
        return Applied(
            consequence,
            entity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("loyalty", bond.Loyalty),
            ("fear", bond.Fear),
            ("admiration", bond.Admiration),
            ("resentment", bond.Resentment),
            ("posture", bond.Posture));
    }

    private WorldConsequenceApplyResult ApplyUpdateWant(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var entityId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Reject(consequence, "Want consequence did not include an entity id.");
        }

        var entity = EntityById(entityId);
        if (entity is null)
        {
            return Reject(consequence, $"Want target does not exist: {entityId}");
        }

        var hasText = HasAnyKey(payload, "text");
        var hasSalience = HasAnyKey(payload, "salience");
        var hasStatus = HasAnyKey(payload, "status");
        var hasStakes = HasAnyKey(payload, "stakes");
        var hasTags = HasAnyKey(payload, "tags");
        var addTags = NormalizeTags(ReadStringList(payload, "addTags").Concat(ReadStringList(payload, "add_tags")));
        var removeTags = NormalizeTags(ReadStringList(payload, "removeTags").Concat(ReadStringList(payload, "remove_tags")));
        if (!hasText && !hasSalience && !hasStatus && !hasStakes && !hasTags && addTags.Count == 0 && removeTags.Count == 0)
        {
            return Reject(consequence, "Want update did not include any changes.");
        }

        var operation = ReadString(payload, "operation") ?? "updateWant";
        var recordMemory = ReadBool(payload, "recordMemory") ?? ReadBool(payload, "record_memory") ?? false;
        var snapshot = recordMemory
            ? GameStateSnapshot.Capture(_state)
            : null;
        var existing = entity.TryGet<WantComponent>(out var want)
            ? want
            : null;
        var nextText = hasText
            ? FirstNonBlank(ReadString(payload, "text"), existing?.Text)
            : existing?.Text;
        if (string.IsNullOrWhiteSpace(nextText))
        {
            return Reject(consequence, "A new want needs text.");
        }

        var nextId = existing?.Id ?? $"want_{NormalizeToken(entity.Id.Value, "entity")}";
        var nextSalience = hasSalience
            ? Math.Clamp(ReadInt(payload, "salience") ?? existing?.Salience ?? consequence.Salience, 1, 5)
            : existing?.Salience ?? Math.Clamp(consequence.Salience, 1, 5);
        var nextStatus = hasStatus
            ? NormalizeToken(FirstNonBlank(ReadString(payload, "status"), existing?.Status, "active")!, "active")
            : existing?.Status ?? "active";
        var nextStakes = hasStakes
            ? FirstNonBlank(ReadString(payload, "stakes"), "")!
            : existing?.Stakes ?? "";
        var nextTags = hasTags
            ? NormalizeTags(ReadStringList(payload, "tags"))
            : NormalizeTags(existing?.Tags ?? Array.Empty<string>());
        nextTags = NormalizeTags(nextTags.Concat(addTags).Where(tag => !removeTags.Contains(tag, StringComparer.OrdinalIgnoreCase)));

        var updated = new WantComponent(
            nextId,
            nextText,
            nextSalience,
            nextStatus,
            nextStakes,
            nextTags);
        entity.Set(updated);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{entity.Name}'s want shifts: {updated.Text}")!;
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("hadWant", existing is not null),
                ("previousWantId", existing?.Id),
                ("previousText", existing?.Text),
                ("previousSalience", existing?.Salience),
                ("previousStatus", existing?.Status),
                ("previousStakes", existing?.Stakes),
                ("previousTags", existing?.Tags),
                ("wantId", updated.Id),
                ("text", updated.Text),
                ("salience", updated.Salience),
                ("status", updated.Status),
                ("stakes", updated.Stakes),
                ("tags", updated.Tags)));
        var messages = IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>();
        var deltas = new List<StateDelta> { delta };
        if (recordMemory)
        {
            var memoryText = FirstNonBlank(
                ReadString(payload, "memoryText"),
                ReadString(payload, "memory_text"),
                consequence.Reason,
                $"{entity.Name}'s want changed: {updated.Text}")!;
            var memory = Apply(WorldConsequence.RecordMemory(
                consequence.Source,
                entity.Id.Value,
                memoryText,
                FirstNonBlank(
                    ReadString(payload, "memoryProvenance"),
                    ReadString(payload, "memory_provenance"),
                    consequence.Source,
                    operation)!,
                Math.Clamp(
                    ReadInt(payload, "memorySalience")
                    ?? ReadInt(payload, "memory_salience")
                    ?? updated.Salience,
                    1,
                    5),
                (ReadBool(payload, "memoryShareable")
                    ?? ReadBool(payload, "memory_shareable")
                    ?? updated.Salience >= 4),
                WorldConsequenceVisibility.Hidden,
                consequence.SourceEntityId,
                consequence.Evidence,
                "Want update requested durable memory through the shared consequence lifecycle.",
                $"{operation}Memory",
                new Dictionary<string, object?>
                {
                    ["parentConsequenceType"] = consequence.Type,
                    ["parentOperation"] = operation,
                    ["wantId"] = updated.Id,
                    ["previousWantStatus"] = existing?.Status,
                    ["wantStatus"] = updated.Status,
                    ["playerVisible"] = false,
                }));
            deltas.AddRange(memory.Deltas);
            if (!memory.Applied)
            {
                snapshot!.Restore(_state);
                var skipped = new StateDelta(
                    "updateWantSkipped",
                    entity.Id.Value,
                    $"Want update rolled back: {memory.Error ?? "memory_record_rejected"}.",
                    Details(
                        consequence,
                        ("operation", operation),
                        ("failure", memory.Error ?? "memory_record_rejected"),
                        ("rolledBackDeltaCount", deltas.Count),
                        ("wantId", updated.Id),
                        ("previousWantId", existing?.Id),
                        ("previousStatus", existing?.Status),
                        ("attemptedStatus", updated.Status),
                        ("auditOnly", true),
                        ("playerVisible", false)));
                return new WorldConsequenceApplyResult(
                    false,
                    entity.Id.Value,
                    memory.Error ?? "memory_record_rejected",
                    Array.Empty<string>(),
                    memory.Deltas.Concat(new[] { skipped }).ToArray(),
                    Details(
                        consequence,
                        ("error", memory.Error ?? "memory_record_rejected"),
                        ("operation", operation),
                        ("rolledBackDeltaCount", deltas.Count)));
            }
        }

        return new WorldConsequenceApplyResult(
            true,
            entity.Id.Value,
            null,
            messages,
            deltas,
            Details(
                consequence,
                ("hadWant", existing is not null),
                ("previousWantId", existing?.Id),
                ("previousText", existing?.Text),
                ("previousSalience", existing?.Salience),
                ("previousStatus", existing?.Status),
                ("previousStakes", existing?.Stakes),
                ("previousTags", existing?.Tags),
                ("wantId", updated.Id),
                ("text", updated.Text),
                ("salience", updated.Salience),
                ("status", updated.Status),
                ("stakes", updated.Stakes),
                ("tags", updated.Tags)));
    }
}
