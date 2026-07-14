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
/// <see cref="WorldConsequenceApplier"/> handlers for compound player-visible outcomes that transact several ordinary consequences as one event (free_captive).
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyFreeCaptive(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var captiveId = FirstNonBlank(
            ReadString(payload, "captiveId"),
            ReadString(payload, "captive_id"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(captiveId))
        {
            return Reject(consequence, "Free-captive consequence did not include a captive entity id.");
        }

        var captive = EntityById(captiveId);
        if (captive is null)
        {
            return Reject(consequence, $"Captive does not exist: {captiveId}");
        }

        if (!captive.TryGet<ActorComponent>(out var captiveActor) || !captiveActor.Alive)
        {
            return Reject(consequence, "Free-captive target is not a living actor.");
        }

        var liberatorId = FirstNonBlank(
            ReadString(payload, "liberatorId"),
            ReadString(payload, "liberator_id"),
            ReadString(payload, "actorId"),
            ReadString(payload, "actor_id"),
            consequence.SourceEntityId,
            _state.ControlledEntityId.Value);
        var liberator = string.IsNullOrWhiteSpace(liberatorId) ? null : EntityById(liberatorId);
        if (liberator is null)
        {
            return Reject(consequence, $"Liberator does not exist: {liberatorId}");
        }

        var shouldRecordDeed = ReadBool(payload, "recordDeed") ?? ReadBool(payload, "record_deed") ?? true;
        if (shouldRecordDeed && _engine is null)
        {
            return Reject(consequence, "Free-captive deed recording requires engine services.");
        }

        var snapshot = GameStateSnapshot.Capture(_state);
        var deltas = new List<StateDelta>();
        var messages = new List<string>();
        WorldConsequenceApplyResult ApplyChild(WorldConsequence child)
        {
            var applied = Apply(child);
            deltas.AddRange(applied.Deltas);
            messages.AddRange(applied.Messages);
            return applied;
        }

        var source = FirstNonBlank(ReadString(payload, "childSource"), ReadString(payload, "child_source"), consequence.Source)!;
        var evidence = FirstNonBlank(consequence.Evidence, $"{captive.Name} was released.");
        var faction = NormalizeToken(
            FirstNonBlank(ReadString(payload, "faction"), ReadString(payload, "factionId"), "player")!,
            "player");
        var roles = NormalizeTags(ReadStringList(payload, "roles"));
        if (roles.Count == 0)
        {
            roles = new[] { "rescued", "follower" };
        }

        var preserveMembership = ReadBool(payload, "preserveMembership")
            ?? ReadBool(payload, "preserve_membership")
            ?? true;
        var factionApplied = ApplyChild(WorldConsequence.ChangeFaction(
            source,
            captive.Id.Value,
            faction,
            roles,
            preserveMembership,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: liberator.Id.Value,
            evidence: evidence,
            reason: "A captive release changed faction allegiance through the shared consequence lifecycle.",
            operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Faction",
            details: new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["playerVisible"] = false,
            }));
        if (!factionApplied.Applied)
        {
            return RollBackFreeCaptive(consequence, snapshot, deltas, messages, factionApplied.Error ?? "faction_update_rejected");
        }

        var aiPolicy = FirstNonBlank(ReadString(payload, "aiPolicyId"), ReadString(payload, "ai_policy_id"), "follower");
        var controlApplied = ApplyChild(WorldConsequence.UpdateControl(
            source,
            captive.Id.Value,
            "ai",
            aiPolicy,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: liberator.Id.Value,
            evidence: evidence,
            reason: "A captive release changed controller policy through the shared consequence lifecycle.",
            operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Control",
            details: new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["playerVisible"] = false,
            }));
        if (!controlApplied.Applied)
        {
            return RollBackFreeCaptive(consequence, snapshot, deltas, messages, controlApplied.Error ?? "control_update_rejected");
        }

        var bondMaxDelta = Math.Max(1, ReadInt(payload, "bondMaxDelta") ?? ReadInt(payload, "bond_max_delta") ?? 5);
        var bondApplied = ApplyChild(WorldConsequence.UpdateBond(
            source,
            captive.Id.Value,
            SoulIdFor(liberator),
            ReadInt(payload, "loyaltyDelta") ?? ReadInt(payload, "loyalty_delta") ?? 4,
            ReadInt(payload, "fearDelta") ?? ReadInt(payload, "fear_delta") ?? 0,
            ReadInt(payload, "admirationDelta") ?? ReadInt(payload, "admiration_delta") ?? 2,
            ReadInt(payload, "resentmentDelta") ?? ReadInt(payload, "resentment_delta") ?? 0,
            FirstNonBlank(ReadString(payload, "posture"), "follower"),
            WorldConsequenceVisibility.Hidden,
            liberator.Id.Value,
            evidence,
            "A captive release changed the social bond through the shared consequence lifecycle.",
            $"{ReadString(payload, "operation") ?? "freeCaptive"}Bond",
            bondMaxDelta,
            new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["playerVisible"] = false,
            }));
        if (!bondApplied.Applied)
        {
            return RollBackFreeCaptive(consequence, snapshot, deltas, messages, bondApplied.Error ?? "bond_update_rejected");
        }

        var satisfyWant = ReadBool(payload, "satisfyWant") ?? ReadBool(payload, "satisfy_want") ?? true;
        if (satisfyWant && captive.Has<WantComponent>())
        {
            var wantAddTags = NormalizeTags(ReadStringList(payload, "wantAddTags")
                .Concat(ReadStringList(payload, "want_add_tags")));
            if (wantAddTags.Count == 0)
            {
                wantAddTags = new[] { "rescued", "satisfied_by_player" };
            }

            var wantRemoveTags = NormalizeTags(ReadStringList(payload, "wantRemoveTags")
                .Concat(ReadStringList(payload, "want_remove_tags")));
            if (wantRemoveTags.Count == 0)
            {
                wantRemoveTags = new[] { "escape" };
            }

            var wantApplied = ApplyChild(WorldConsequence.UpdateWant(
                source,
                captive.Id.Value,
                status: FirstNonBlank(ReadString(payload, "wantStatus"), ReadString(payload, "want_status"), "satisfied"),
                stakes: FirstNonBlank(
                    ReadString(payload, "wantStakes"),
                    ReadString(payload, "want_stakes"),
                    "The immediate escape happened; future choices can shift toward trust, danger, or a new refuge."),
                addTags: wantAddTags,
                removeTags: wantRemoveTags,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: liberator.Id.Value,
                evidence: evidence,
                reason: "Releasing the captive satisfied or redirected an active want.",
                operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Want",
                details: new Dictionary<string, object?>
                {
                    ["parentConsequenceType"] = consequence.Type,
                    ["outcome"] = "captive_released",
                    ["playerVisible"] = false,
                },
                recordMemory: true,
                memoryText: FirstNonBlank(
                    ReadString(payload, "memoryText"),
                    ReadString(payload, "memory_text"),
                    $"{captive.Name} was released, satisfying the immediate escape want."),
                memoryProvenance: FirstNonBlank(ReadString(payload, "memoryProvenance"), ReadString(payload, "memory_provenance"), "free_captive"),
                memoryShareable: false));
            if (!wantApplied.Applied)
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, wantApplied.Error ?? "want_update_rejected");
            }
        }

        if (shouldRecordDeed)
        {
            if (!liberator.TryGet<PositionComponent>(out var liberatorPosition)
                || !captive.TryGet<PositionComponent>(out var captivePosition))
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, "missing_deed_position");
            }

            var deedTags = NormalizeTags(ReadStringList(payload, "deedTags")
                .Concat(ReadStringList(payload, "deed_tags")));
            if (deedTags.Count == 0)
            {
                deedTags = new[] { "mercy", "anti_empire", "rescued" };
            }

            var deedApplied = ApplyChild(WorldConsequence.RecordDeed(
                source,
                liberator.Id.Value,
                NormalizeToken(FirstNonBlank(ReadString(payload, "deedKind"), ReadString(payload, "deed_kind"), "freed_prisoner")!, "freed_prisoner"),
                Math.Clamp(ReadInt(payload, "deedMagnitude") ?? ReadInt(payload, "deed_magnitude") ?? consequence.Salience, 1, 999),
                liberatorPosition.Position.X,
                liberatorPosition.Position.Y,
                captivePosition.Position.X,
                captivePosition.Position.Y,
                deedTags,
                sourceEntityId: liberator.Id.Value,
                evidence: evidence,
                reason: "A captive release became a world-reactive deed.",
                operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Deed"));
            if (!deedApplied.Applied)
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, deedApplied.Error ?? "deed_record_rejected");
            }
        }

        var operation = ReadString(payload, "operation") ?? "freeCaptive";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"{captive.Name} is free enough to choose {ObjectName(liberator)}, for now.")!;
        if (ReadBool(payload, "emitMessage") ?? IsVisible(consequence.Visibility))
        {
            var messageApplied = ApplyChild(WorldConsequence.Message(
                source,
                summary,
                targetEntityId: captive.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: liberator.Id.Value,
                evidence: evidence,
                reason: "A captive release produced a legible player-facing receipt.",
                operation: $"{operation}Message",
                details: new Dictionary<string, object?>
                {
                    ["parentConsequenceType"] = consequence.Type,
                    ["faction"] = faction,
                }));
            if (!messageApplied.Applied)
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, messageApplied.Error ?? "message_rejected");
            }
        }

        var offerObjectiveHandoff = ReadBool(payload, "offerObjectiveHandoff")
            ?? ReadBool(payload, "offer_objective_handoff")
            ?? true;
        if (offerObjectiveHandoff && _engine is not null)
        {
            var handoff = _engine.ApplyGeneratedObjectiveHandoff(captive, "rescue");
            if (handoff is not null)
            {
                deltas.AddRange(handoff.Deltas);
                messages.AddRange(handoff.Messages);
                if (!handoff.Applied)
                {
                    return RollBackFreeCaptive(
                        consequence,
                        snapshot,
                        deltas,
                        messages,
                        handoff.Error ?? "objective_handoff_rejected");
                }
            }
        }

        var delta = new StateDelta(
            operation,
            captive.Id.Value,
            summary,
            Details(
                consequence,
                ("captiveId", captive.Id.Value),
                ("liberatorId", liberator.Id.Value),
                ("faction", faction),
                ("roles", roles),
                ("aiPolicyId", aiPolicy),
                ("recordDeed", shouldRecordDeed),
                ("satisfyWant", satisfyWant),
                ("offerObjectiveHandoff", offerObjectiveHandoff)));
        deltas.Add(delta);
        return new WorldConsequenceApplyResult(
            true,
            captive.Id.Value,
            null,
            messages,
            deltas,
            Details(
                consequence,
                ("captiveId", captive.Id.Value),
                ("liberatorId", liberator.Id.Value),
                ("faction", faction),
                ("roles", roles),
                ("aiPolicyId", aiPolicy)));
    }
}
