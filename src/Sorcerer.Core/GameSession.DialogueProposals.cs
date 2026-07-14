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
/// <see cref="GameSession"/> dialogue proposal applier (Phase 0.3): normalizes the generated
/// dialogue proposal set (memory/bond/want/action/recruit/typed-consequence) into ordinary
/// consequences and command-like actor actions. Split from GameSession.Dialogue.cs, which owns
/// the turn itself (routing, the provider call, and response normalization).
/// </summary>
public sealed partial class GameSession
{
    private ActionResult ApplyGeneratedDialogueProposals(
        ActionResult result,
        PreparedDialogueTurn turn,
        DialogueRequest request,
        string provider,
        DialogueResponse response)
    {
        var proposals = response.Proposals;
        if (proposals is null)
        {
            var exchangeDeltas = AddDialogueExchangeMemory(turn, response.SpokenText);
            return exchangeDeltas.Count == 0
                ? result
                : result with { Deltas = result.Deltas.Concat(exchangeDeltas).ToArray() };
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        var claimRequest = new DialogueClaimRequest(
            Engine.State.Turn,
            Engine.State.RegionId,
            Engine.State.CurrentZoneId,
            turn.SpeakerId,
            turn.SpeakerName,
            turn.SpeakerTags,
            turn.ListenerSoulId,
            turn.PlayerText,
            new[] { response.SpokenText },
            RecentMemoriesFor(turn.SpeakerId),
            Engine.State.Claims.Records.TakeLast(DialogueRecentClaimLimit).ToArray(),
            Engine.State.ControlledEntityId.Value);

        ApplyDialogueProposalSet(
            turn,
            claimRequest,
            provider,
            response.SpokenText,
            response.Intent,
            proposals,
            requiresSpokenTextSupport: true,
            parserOrigin: false,
            messages,
            deltas);

        deltas.AddRange(AddDialogueExchangeMemory(turn, response.SpokenText));
        if (messages.Count == 0 && deltas.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(messages).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
        };
    }

    private void ApplyDialogueProposalSet(
        PreparedDialogueTurn turn,
        DialogueClaimRequest claimRequest,
        string provider,
        string spokenText,
        string? intent,
        DialogueProposalSet? proposals,
        bool requiresSpokenTextSupport,
        bool parserOrigin,
        List<string> messages,
        List<StateDelta> deltas)
    {
        if (proposals is null)
        {
            return;
        }

        var actions = proposals.Actions ?? Array.Empty<DialogueActionProposal>();
        var preAppliedActionIndexes = new HashSet<int>();
        for (var index = 0; index < actions.Count; index++)
        {
            if (NormalizeDialogueActionType(actions[index].Type) != "reveal_service")
            {
                continue;
            }

            ApplyDialogueActionProposal(provider, turn, intent, spokenText, actions[index], messages, deltas);
            preAppliedActionIndexes.Add(index);
        }

        foreach (var claim in proposals.Claims ?? Array.Empty<DialogueClaimProposal>())
        {
            if (requiresSpokenTextSupport
                && !GeneratedClaimIsSupportedBySpokenText(claim, spokenText))
            {
                var operation = parserOrigin ? "claimExtractionSkipped" : "dialogueProposalSkipped";
                var summary = parserOrigin
                    ? "Dialogue claim extraction skipped because it was not supported by spoken text."
                    : "Dialogue claim proposal skipped because it was not supported by spoken text.";
                deltas.Add(new StateDelta(
                    operation,
                    turn.SpeakerId,
                    summary,
                    new Dictionary<string, object?>
                    {
                        ["provider"] = provider,
                        ["proposalType"] = "claim",
                        ["claimText"] = claim.Text,
                        ["reason"] = "unsupported_by_spoken_text",
                        ["auditOnly"] = true,
                        ["playerVisible"] = false,
                    }));
                continue;
            }

            ApplyClaimProposal(claimRequest, provider, claim, messages, deltas);
        }

        foreach (var memory in proposals.Memories ?? Array.Empty<DialogueMemoryProposal>())
        {
            ApplyDialogueMemoryProposal(provider, memory, messages, deltas);
        }

        if (proposals.Bond is not null)
        {
            ApplyDialogueBondProposal(provider, proposals.Bond, messages, deltas);
        }

        if (proposals.Want is not null)
        {
            ApplyDialogueWantProposal(provider, proposals.Want, messages, deltas);
        }

        for (var index = 0; index < actions.Count; index++)
        {
            if (preAppliedActionIndexes.Contains(index))
            {
                continue;
            }

            var action = actions[index];
            ApplyDialogueActionProposal(provider, turn, intent, spokenText, action, messages, deltas);
        }
    }

    private static bool GeneratedClaimIsSupportedBySpokenText(DialogueClaimProposal claim, string spokenText)
    {
        if (string.IsNullOrWhiteSpace(claim.Text))
        {
            return false;
        }

        var spokenTokens = ClaimSupportTokens(spokenText);
        if (spokenTokens.Count == 0)
        {
            return false;
        }

        var namedTokens = ProperNameTokens($"{claim.Text} {claim.Subject} {claim.ItemName}");
        if (namedTokens.Count > 0 && !namedTokens.Any(spokenTokens.Contains))
        {
            return false;
        }

        var claimTokens = ClaimSupportTokens($"{claim.Text} {claim.Subject} {claim.ItemName}");
        if (claimTokens.Count == 0)
        {
            return false;
        }

        var overlap = claimTokens.Count(spokenTokens.Contains);
        var required = Math.Min(3, Math.Max(1, claimTokens.Count / 2));
        return overlap >= required;
    }

    private static bool GeneratedFactIsSupportedBySpokenText(string factText, string? subject, string spokenText)
    {
        if (string.IsNullOrWhiteSpace(factText))
        {
            return false;
        }

        var spokenTokens = ClaimSupportTokens(spokenText);
        if (spokenTokens.Count == 0)
        {
            return false;
        }

        var factSource = $"{factText} {subject}";
        var namedTokens = ProperNameTokens(factSource);
        if (namedTokens.Count > 0 && !namedTokens.Any(spokenTokens.Contains))
        {
            return false;
        }

        var factTokens = ClaimSupportTokens(factSource);
        if (factTokens.Count == 0)
        {
            return false;
        }

        var overlap = factTokens.Count(spokenTokens.Contains);
        var required = Math.Min(3, Math.Max(1, factTokens.Count / 2));
        return overlap >= required;
    }

    private static HashSet<string> ClaimSupportTokens(string text) =>
        new string(text
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeClaimSupportToken)
            .Where(token => token.Length >= 3)
            .Where(token => !ClaimSupportStopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizeClaimSupportToken(string token)
    {
        if (token.Length > 5 && token.EndsWith("ies", StringComparison.OrdinalIgnoreCase))
        {
            return $"{token[..^3]}y";
        }

        if (token.Length > 5 && token.EndsWith("ing", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^3];
        }

        if (token.Length > 4 && token.EndsWith("ed", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^2];
        }

        if (token.Length > 3
            && token.EndsWith("s", StringComparison.OrdinalIgnoreCase)
            && !token.EndsWith("ss", StringComparison.OrdinalIgnoreCase))
        {
            return token[..^1];
        }

        return token;
    }

    private static HashSet<string> ProperNameTokens(string text) =>
        text.Split(new[] { ' ', ',', '.', ';', ':', '"', '\'', '(', ')', '[', ']', '{', '}', '/', '|', '-' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim())
            .Where(token => token.Length >= 4)
            .Where(token => char.IsUpper(token[0]))
            .Select(token => new string(token.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant())
            .Where(token => token.Length >= 4)
            .Where(token => !ClaimSupportStopWords.Contains(token))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ClaimSupportStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about",
        "after",
        "before",
        "behind",
        "break",
        "bring",
        "can",
        "could",
        "from",
        "have",
        "into",
        "keep",
        "keeps",
        "know",
        "knows",
        "lead",
        "leads",
        "left",
        "lower",
        "marks",
        "named",
        "near",
        "need",
        "offer",
        "offers",
        "only",
        "past",
        "quietly",
        "sells",
        "should",
        "that",
        "there",
        "they",
        "this",
        "through",
        "until",
        "wards",
        "where",
        "with",
        "would",
    };

    private void ApplyDialogueMemoryProposal(
        string provider,
        DialogueMemoryProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        if (string.IsNullOrWhiteSpace(proposal.Text))
        {
            return;
        }

        var ownerId = string.IsNullOrWhiteSpace(proposal.OwnerEntityId)
            ? Engine.State.ControlledEntityId.Value
            : proposal.OwnerEntityId.Trim();
        var salience = Math.Clamp(proposal.Salience, 1, 5);
        var consequence = WorldConsequence.RecordMemory(
            $"dialogue:{provider}",
            ownerId,
            proposal.Text.Trim(),
            string.IsNullOrWhiteSpace(proposal.Provenance) ? "conversation" : proposal.Provenance.Trim(),
            salience,
            proposal.Shareable,
            sourceEntityId: ownerId,
            operation: "dialogueMemory",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "memory",
                ["requireOwnerEntity"] = true,
            },
            reason: "Dialogue response proposed a durable memory.");
        var applied = Engine.ApplyConsequence(consequence);
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddDialogueProposalSkipped(
                provider,
                "memory",
                ownerId,
                applied.Error ?? "Dialogue memory proposal could not be applied.",
                deltas,
                new Dictionary<string, object?>
                {
                    ["memoryText"] = proposal.Text.Trim(),
                });
        }
    }

    private void ApplyDialogueBondProposal(
        string provider,
        DialogueBondProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var applied = Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            $"dialogue:{provider}",
            proposal.EntityId,
            SoulIdFor(Engine.State.ControlledEntity),
            proposal.LoyaltyDelta,
            proposal.FearDelta,
            proposal.AdmirationDelta,
            proposal.ResentmentDelta,
            proposal.Posture,
            WorldConsequenceVisibility.Message,
            operation: "dialogueBondShift",
            maxDelta: DialogueBondDeltaLimit,
            reason: proposal.Reason,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "bond",
            }));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddDialogueProposalSkipped(
                provider,
                "bond",
                proposal.EntityId,
                applied.Error ?? "Dialogue bond proposal could not be applied.",
                deltas,
                new Dictionary<string, object?>
                {
                    ["posture"] = proposal.Posture,
                    ["providerReason"] = proposal.Reason,
                });
        }
    }

    private void ApplyDialogueWantProposal(
        string provider,
        DialogueWantProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var entityId = string.IsNullOrWhiteSpace(proposal.EntityId)
            ? Engine.State.ControlledEntityId.Value
            : proposal.EntityId.Trim();
        var applied = Engine.ApplyConsequence(WorldConsequence.UpdateWant(
            $"dialogue:{provider}",
            entityId,
            proposal.Text,
            proposal.Salience,
            proposal.Status,
            proposal.Stakes,
            proposal.Tags,
            proposal.AddTags,
            proposal.RemoveTags,
            WorldConsequenceVisibility.Hidden,
            sourceEntityId: entityId,
            reason: proposal.Reason,
            operation: "dialogueWantShift",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "want",
            },
            recordMemory: true,
            memoryText: FirstNonBlank(
                proposal.Reason,
                string.IsNullOrWhiteSpace(proposal.Text)
                    ? "A conversation with the sorcerer changed the speaker's active want."
                    : $"A conversation with the sorcerer changed the speaker's active want: {proposal.Text.Trim()}"),
            memoryProvenance: "conversation",
            memorySalience: proposal.Salience,
            memoryShareable: null));
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddDialogueProposalSkipped(
                provider,
                "want",
                entityId,
                applied.Error ?? "Dialogue want proposal could not be applied.",
                deltas,
                new Dictionary<string, object?>
                {
                    ["status"] = proposal.Status,
                    ["providerReason"] = proposal.Reason,
                });
        }
    }

    private static void AddDialogueProposalSkipped(
        string provider,
        string proposalType,
        string? target,
        string reason,
        List<StateDelta> deltas,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payload["provider"] = provider;
        payload["proposalType"] = proposalType;
        payload["reason"] = reason;
        payload["auditOnly"] = true;
        payload["playerVisible"] = false;
        deltas.Add(new StateDelta(
            "dialogueProposalSkipped",
            target ?? "",
            $"Dialogue {proposalType} proposal skipped: {reason}",
            payload));
    }

    private void ApplyDialogueActionProposal(
        string provider,
        PreparedDialogueTurn turn,
        string? intent,
        string spokenText,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var type = NormalizeDialogueActionType(proposal.Type);
        if (type == "none")
        {
            return;
        }

        if (IsDirectWorldConsequenceActionType(type))
        {
            proposal = proposal with
            {
                Type = "consequence",
                ConsequenceType = FirstNonBlank(proposal.ConsequenceType, type),
            };
            type = "consequence";
        }

        if (IsRefusalIntent(intent) && IsCooperativeDialogueAction(type))
        {
            RejectDialogueAction(provider, proposal, "The NPC refused, so cooperative action was not applied.", deltas);
            return;
        }

        switch (type)
        {
            case "step_aside":
                ApplyDialogueMoveAction(provider, turn.SpeakerId, flee: false, messages, deltas);
                return;
            case "flee":
                ApplyDialogueMoveAction(provider, turn.SpeakerId, flee: true, messages, deltas);
                return;
            case "open_door":
                ApplyDialogueOpenDoor(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "give_item":
                ApplyDialogueGiveItem(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "call_help":
                ApplyDialogueCallHelp(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "attack":
                ApplyDialogueAttack(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "create_promise":
                ApplyDialogueCreatePromise(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "add_canon":
                ApplyDialogueAddCanon(provider, turn.SpeakerId, spokenText, proposal, messages, deltas);
                return;
            case "offer_trade":
                ApplyDialogueOfferTrade(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "mark_location":
            case "spawn_fixture":
                ApplyDialogueSpawnFixture(provider, turn.SpeakerId, type, proposal, messages, deltas);
                return;
            case "reveal_service":
                ApplyDialogueRevealService(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "recruit":
                ApplyDialogueRecruit(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
            case "consequence":
                ApplyDialogueTypedConsequence(provider, turn.SpeakerId, proposal, messages, deltas);
                return;
        }

        RejectDialogueAction(provider, proposal, "Dialogue action handlers are not implemented for this action type yet.", deltas);
    }

    private static bool IsRefusalIntent(string? intent) =>
        NormalizeToken(intent ?? "", "").Equals("refuse", StringComparison.OrdinalIgnoreCase);

    private static bool IsCooperativeDialogueAction(string type) =>
        type is "step_aside" or "give_item" or "open_door" or "create_promise" or "add_canon" or "offer_trade" or "mark_location" or "reveal_service" or "recruit" or "consequence";

    private static bool IsDirectWorldConsequenceActionType(string type) =>
        WorldConsequenceTypes.IsKnown(type)
        && !type.Equals(WorldConsequenceTypes.CreatePromise, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(WorldConsequenceTypes.AddCanon, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(WorldConsequenceTypes.SpawnFixture, StringComparison.OrdinalIgnoreCase)
        && !type.Equals(WorldConsequenceTypes.OfferTrade, StringComparison.OrdinalIgnoreCase);

    private static string NormalizeDialogueActionType(string? type)
    {
        var normalized = NormalizeToken(type ?? "", "none");
        return normalized switch
        {
            "" or "none" or "no_action" => "none",
            "open" or "unlock" or "open_or_unlock" => "open_door",
            "give" or "hand_over" or "transfer" or "transfer_item" => "give_item",
            "move_aside" or "make_room" or "step_out_of_the_way" => "step_aside",
            "run" or "run_away" or "retreat" => "flee",
            "summon_help" or "raise_alarm" or "call_for_help" => "call_help",
            "strike" or "hit" or "attack_entity" => "attack",
            "promise" or "make_promise" or "bind_promise" or "create_promise_now" => "create_promise",
            "canonize" or "canonize_fact" or "canonicalize_fact" or "add_canon" or "record_canon" or "record_fact" => "add_canon",
            "trade" or "offer_trade_now" or "open_trade" => "offer_trade",
            "service" or "offer_service" or "reveal_service_now" => "reveal_service",
            "world_consequence" or "worldconsequence" or "typed_consequence" or "apply_consequence" => "consequence",
            "mark" or "mark_place" or "mark_site" or "mark_point" => "mark_location",
            "create_fixture" or "create_marker" or "place_fixture" => "spawn_fixture",
            "join" or "join_me" or "follow" or "follow_me" or "come_with" or "come_with_me" or "ally" => "recruit",
            _ => normalized,
        };
    }

    private void ApplyDialogueRecruit(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var result = Engine.RecruitFromDialogue(actorId, provider, proposal.Reason);
        if (result.Deltas.Count == 0)
        {
            RejectDialogueAction(
                provider,
                proposal,
                result.Messages.FirstOrDefault() ?? "Dialogue recruit action could not be applied.",
                deltas);
            return;
        }

        messages.AddRange(result.Messages);
        deltas.AddRange(result.Deltas);
    }

    private void ApplyDialogueTypedConsequence(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var consequenceType = FirstNonBlank(
            proposal.ConsequenceType,
            TextPayload(proposal.ConsequencePayload, "consequenceType"),
            TextPayload(proposal.ConsequencePayload, "consequence_type"),
            TextPayload(proposal.ConsequencePayload, "worldConsequenceType"),
            TextPayload(proposal.ConsequencePayload, "world_consequence_type"));
        if (string.IsNullOrWhiteSpace(consequenceType))
        {
            RejectDialogueAction(provider, proposal, "Generic dialogue consequence did not include consequenceType.", deltas);
            return;
        }

        var timing = WorldConsequenceTiming.Normalize(FirstNonBlank(
            proposal.ConsequenceTiming,
            TextPayload(proposal.ConsequencePayload, "timing"),
            TextPayload(proposal.ConsequencePayload, "consequenceTiming"),
            TextPayload(proposal.ConsequencePayload, "consequence_timing"),
            WorldConsequenceTiming.Immediate));

        var normalizedConsequenceType = NormalizeDialogueConsequenceType(consequenceType);
        var payload = proposal.ConsequencePayload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(proposal.ConsequencePayload, StringComparer.OrdinalIgnoreCase);
        PromoteDialogueActionFieldsToPayload(proposal, normalizedConsequenceType, payload);
        payload["operation"] = FirstNonBlank(TextPayload(payload, "operation"), "dialogueConsequence");
        payload["provider"] = provider;
        payload["proposalType"] = "consequence";
        payload["speakerId"] = actorId;
        if (proposal.PlayerVisible is not null)
        {
            payload["playerVisible"] = proposal.PlayerVisible.Value;
        }

        var targetEntityId = FirstNonBlank(
            proposal.TargetEntityId,
            TextPayload(payload, "targetEntityId"),
            TextPayload(payload, "target_entity_id"),
            TextPayload(payload, "targetId"),
            TextPayload(payload, "target_id"),
            TextPayload(payload, "entityId"),
            TextPayload(payload, "entity_id"),
            TextPayload(payload, "target"),
            normalizedConsequenceType.Equals(WorldConsequenceTypes.RequestService, StringComparison.OrdinalIgnoreCase)
                ? actorId
                : null);
        if (normalizedConsequenceType.Equals(WorldConsequenceTypes.RequestService, StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(TextPayload(payload, "actorEntityId"))
            && string.IsNullOrWhiteSpace(TextPayload(payload, "actor_entity_id"))
            && string.IsNullOrWhiteSpace(TextPayload(payload, "requesterEntityId"))
            && string.IsNullOrWhiteSpace(TextPayload(payload, "requester_entity_id")))
        {
            payload["actorEntityId"] = Engine.State.ControlledEntityId.Value;
        }

        var sourceEntityId = FirstNonBlank(
            TextPayload(payload, "sourceEntityId"),
            TextPayload(payload, "source_entity_id"),
            actorId);
        var visibility = NormalizeToken(FirstNonBlank(
            proposal.ConsequenceVisibility,
            TextPayload(payload, "visibility"),
            TextPayload(payload, "consequenceVisibility"),
            TextPayload(payload, "consequence_visibility"),
            proposal.PlayerVisible == true ? WorldConsequenceVisibility.Message : WorldConsequenceVisibility.Hidden)!, WorldConsequenceVisibility.Hidden);
        var salience = Math.Clamp(proposal.Salience ?? IntPayload(payload, "salience") ?? 1, 1, 5);
        var confidence = Math.Clamp(proposal.ConsequenceConfidence ?? IntPayload(payload, "confidence") ?? 100, 0, 100);

        var applied = Engine.ApplyConsequence(new WorldConsequence(
            normalizedConsequenceType,
            $"dialogue:{provider}",
            SourceEntityId: sourceEntityId,
            TargetEntityId: targetEntityId,
            Salience: salience,
            Confidence: confidence,
            Visibility: visibility,
            Evidence: FirstNonBlank(proposal.Reason, TextPayload(payload, "evidence")),
            Reason: FirstNonBlank(proposal.Reason, TextPayload(payload, "reason"), $"Dialogue action proposed {consequenceType}."),
            Payload: payload,
            Timing: timing));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? $"Dialogue consequence {consequenceType} could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private static string NormalizeDialogueConsequenceType(string type) =>
        WorldConsequenceTypes.Normalize(type);

    private static void PromoteDialogueActionFieldsToPayload(
        DialogueActionProposal proposal,
        string normalizedConsequenceType,
        Dictionary<string, object?> payload)
    {
        PutPayloadIfMissing(payload, "targetEntityId", proposal.TargetEntityId);
        PutPayloadIfMissing(payload, "itemName", proposal.ItemName);
        PutPayloadIfMissing(payload, "item", proposal.ItemName);
        PutPayloadIfMissing(payload, "quantity", proposal.Quantity);
        PutPayloadIfMissing(payload, "gold", proposal.Gold);
        PutPayloadIfMissing(payload, "name", proposal.Name);
        PutPayloadIfMissing(payload, "description", proposal.Description);
        PutPayloadIfMissing(payload, "fixtureType", proposal.FixtureType);
        PutPayloadIfMissing(payload, "material", proposal.Material);
        PutPayloadIfMissing(payload, "tags", proposal.Tags);
        PutPayloadIfMissing(payload, "interactableVerbs", proposal.InteractableVerbs);
        PutPayloadIfMissing(payload, "x", proposal.X);
        PutPayloadIfMissing(payload, "y", proposal.Y);
        PutPayloadIfMissing(payload, "blocksMovement", proposal.BlocksMovement);
        PutPayloadIfMissing(payload, "blocksSight", proposal.BlocksSight);
        PutPayloadIfMissing(payload, "serviceId", proposal.ServiceId);
        PutPayloadIfMissing(payload, "service", proposal.ServiceId);
        PutPayloadIfMissing(payload, "serviceName", proposal.Name);
        PutPayloadIfMissing(payload, "effectKind", proposal.EffectKind);
        PutPayloadIfMissing(payload, "targetHint", proposal.TargetHint);
        PutPayloadIfMissing(payload, "itemCost", proposal.ItemCost);
        PutPayloadIfMissing(payload, "goldCost", proposal.GoldCost);
        PutPayloadIfMissing(payload, "triggerHint", proposal.TriggerHint);
        PutPayloadIfMissing(payload, "realizationKind", proposal.RealizationKind);
        PutPayloadIfMissing(payload, "claimedPlace", proposal.ClaimedPlace);
        PutPayloadIfMissing(payload, "subject", proposal.Subject);
        PutPayloadIfMissing(payload, "playerVisible", proposal.PlayerVisible);
        PutPayloadIfMissing(payload, "salience", proposal.Salience);
        PutPayloadIfMissing(payload, "autoBind", proposal.AutoBind);
        PutPayloadIfMissing(payload, "stackExisting", proposal.StackExisting);
        PutPayloadIfMissing(payload, "wantStatusOnComplete", proposal.WantStatusOnComplete);
        PutPayloadIfMissing(payload, "wantStakesOnComplete", proposal.WantStakesOnComplete);
        PutPayloadIfMissing(payload, "wantAddTagsOnComplete", proposal.WantAddTagsOnComplete);
        PutPayloadIfMissing(payload, "wantRemoveTagsOnComplete", proposal.WantRemoveTagsOnComplete);

        if (normalizedConsequenceType.Equals(WorldConsequenceTypes.CreatePromise, StringComparison.OrdinalIgnoreCase))
        {
            PutPayloadIfMissing(payload, "text", proposal.PromiseText);
            PutPayloadIfMissing(payload, "kind", proposal.PromiseKind);
        }
        else if (normalizedConsequenceType.Equals(WorldConsequenceTypes.AddCanon, StringComparison.OrdinalIgnoreCase))
        {
            PutPayloadIfMissing(payload, "text", proposal.CanonText);
            PutPayloadIfMissing(payload, "kind", proposal.CanonKind);
            PutPayloadIfMissing(payload, "summary", proposal.CanonSummary);
        }
        else if (normalizedConsequenceType is WorldConsequenceTypes.Message or WorldConsequenceTypes.RecordMemory)
        {
            PutPayloadIfMissing(payload, "text", FirstNonBlank(proposal.Description, proposal.Name, proposal.Reason));
        }
    }

    private static void PutPayloadIfMissing(Dictionary<string, object?> payload, string key, object? value)
    {
        if (value is null || payload.ContainsKey(key))
        {
            return;
        }

        if (value is string text)
        {
            if (!string.IsNullOrWhiteSpace(text))
            {
                payload[key] = text.Trim();
            }

            return;
        }

        if (value is IEnumerable<string> strings)
        {
            var values = strings
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .Select(item => item.Trim())
                .ToArray();
            if (values.Length > 0)
            {
                payload[key] = values;
            }

            return;
        }

        payload[key] = value;
    }

    private void ApplyDialogueMoveAction(
        string provider,
        string actorId,
        bool flee,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        if (actor is null || !actor.TryGet<PositionComponent>(out var position))
        {
            RejectDialogueAction(
                provider,
                new DialogueActionProposal(flee ? "flee" : "step_aside", actorId),
                "The speaker is missing or has no position.",
                deltas);
            return;
        }

        var playerPosition = Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var destination = DialogueMoveDestination(position.Position, playerPosition, flee);
        if (destination is null)
        {
            RejectDialogueAction(
                provider,
                new DialogueActionProposal(flee ? "flee" : "step_aside", actorId),
                "No open adjacent tile was available.",
                deltas);
            return;
        }

        var operation = flee ? "dialogueFlee" : "dialogueStepAside";
        var applied = Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            $"dialogue:{provider}",
            actor.Id.Value,
            destination.Value.X,
            destination.Value.Y,
            operation,
            WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            reason: flee ? "dialogue action: flee" : "dialogue action: step aside",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = flee ? "flee" : "step_aside",
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, new DialogueActionProposal(flee ? "flee" : "step_aside", actorId), applied.Error ?? "Dialogue movement could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private GridPoint? DialogueMoveDestination(GridPoint origin, GridPoint awayFrom, bool flee)
    {
        var currentDistance = GameEngine.Distance(origin, awayFrom);
        var candidates = AdjacentOffsets()
            .Select(offset => origin.Translate(offset.X, offset.Y))
            .Where(point => Engine.InBounds(point)
                && !Engine.State.BlockingTerrain.Contains(point)
                && Engine.BlockingEntityAt(point) is null)
            .Select(point => new
            {
                Point = point,
                Distance = GameEngine.Distance(point, awayFrom),
            })
            .OrderByDescending(item => item.Distance)
            .ThenBy(item => item.Point.X)
            .ThenBy(item => item.Point.Y)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        if (flee)
        {
            return candidates[0].Point;
        }

        return candidates.FirstOrDefault(item => item.Distance > currentDistance)?.Point
            ?? candidates[0].Point;
    }

    private void ApplyDialogueOpenDoor(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        var door = ResolveDialogueDoor(actor, proposal.TargetEntityId);
        if (actor is null || door is null)
        {
            RejectDialogueAction(provider, proposal, "No reachable door was found.", deltas);
            return;
        }

        var result = Engine.OpenDoor(
            actor,
            door,
            WorldActionContext.Dialogue(provider, "dialogue_action", "dialogueOpenDoor"));
        if (!result.Success)
        {
            RejectDialogueAction(provider, proposal, string.Join(" ", result.Messages), deltas);
            return;
        }

        messages.AddRange(result.Messages);
        deltas.AddRange(result.Deltas);
    }

    private Entity? ResolveDialogueDoor(Entity? actor, string? targetEntityId)
    {
        if (!string.IsNullOrWhiteSpace(targetEntityId)
            && Engine.EntityById(targetEntityId) is { } target)
        {
            return target;
        }

        if (actor is null || !actor.TryGet<PositionComponent>(out var actorPosition))
        {
            return null;
        }

        return Engine.State.Entities.Values
            .Where(entity => entity.Has<DoorComponent>() && entity.TryGet<PositionComponent>(out _))
            .Where(entity => GameEngine.Distance(actorPosition.Position, entity.Get<PositionComponent>().Position) <= 1)
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private void ApplyDialogueGiveItem(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var giver = Engine.EntityById(actorId);
        if (giver is null
            || !giver.TryGet<InventoryComponent>(out var giverInventory))
        {
            RejectDialogueAction(provider, proposal, "The speaker has no inventory to give from.", deltas);
            return;
        }

        var item = FindInventoryKey(giverInventory, proposal.ItemName ?? "");
        if (item is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker does not have the proposed item.", deltas);
            return;
        }

        if (giverInventory.TreasuredItems.Contains(item))
        {
            RejectDialogueAction(provider, proposal, "The proposed item is protected.", deltas);
            return;
        }

        var receiver = string.IsNullOrWhiteSpace(proposal.TargetEntityId)
            ? Engine.State.ControlledEntity
            : Engine.EntityById(proposal.TargetEntityId) ?? Engine.State.ControlledEntity;
        var transfer = Engine.ApplyConsequence(WorldConsequence.TransferItem(
            $"dialogue:{provider}",
            giver.Id.Value,
            "give",
            item,
            quantity: 1,
            recipientEntityId: receiver.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: giver.Id.Value,
            evidence: proposal.Reason,
            operation: "dialogueGiveItem",
            message: $"{giver.Name} gives {item} to {receiver.Name}.",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["speakerId"] = giver.Id.Value,
                ["receiverId"] = receiver.Id.Value,
                ["inventoryKey"] = item,
            }));
        if (!transfer.Applied)
        {
            RejectDialogueAction(provider, proposal, transfer.Error ?? "The speaker could not give the proposed item.", deltas);
            deltas.AddRange(transfer.Deltas);
            return;
        }

        messages.AddRange(transfer.Messages);
        deltas.AddRange(transfer.Deltas);
    }

    private void ApplyDialogueCallHelp(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        if (actor is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var imperial = actor.TryGet<ActorComponent>(out var actorComponent)
            && actorComponent.Faction.Equals("empire", StringComparison.OrdinalIgnoreCase)
            || TagsFor(actor).Any(tag => tag.Equals("imperial", StringComparison.OrdinalIgnoreCase));
        var kind = imperial ? "empire_patrol" : "dialogue_help_call";
        var text = imperial
            ? $"{actor.Name}'s call for help reaches an imperial ear."
            : $"{actor.Name} calls for help, loud enough that someone nearby could come.";
        var message = $"{actor.Name} calls for help.";
        var applied = Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            $"dialogue:{provider}",
            kind,
            turns: 2,
            eventPayload: new Dictionary<string, object?>
            {
                ["text"] = text,
                ["source"] = "dialogue",
                ["provider"] = provider,
                ["reason"] = proposal.Reason,
            },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: proposal.Reason,
            operation: "dialogueCallHelp",
            message: message,
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["kind"] = kind,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "Help could not be scheduled.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueAttack(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var attacker = Engine.EntityById(actorId);
        if (attacker is null || !attacker.TryGet<ActorComponent>(out var attackerActor) || !attackerActor.Alive)
        {
            RejectDialogueAction(provider, proposal, "The speaker is not a living actor who can attack.", deltas);
            return;
        }

        var target = ResolveDialogueAttackTarget(attacker, proposal.TargetEntityId);
        if (target is null || !target.TryGet<ActorComponent>(out var targetActor) || !targetActor.Alive)
        {
            RejectDialogueAction(provider, proposal, "No living attack target was found.", deltas);
            return;
        }

        if (target.Id == attacker.Id)
        {
            RejectDialogueAction(provider, proposal, "The speaker cannot attack themself through dialogue.", deltas);
            return;
        }

        if (!attacker.TryGet<PositionComponent>(out var attackerPosition)
            || !target.TryGet<PositionComponent>(out var targetPosition)
            || GameEngine.Distance(attackerPosition.Position, targetPosition.Position) > 1)
        {
            RejectDialogueAction(provider, proposal, "The attack target is not adjacent.", deltas);
            return;
        }

        var attackDeltas = Engine.AttackEntity(
            attacker,
            target,
            source: $"dialogue:{provider}",
            evidence: proposal.Reason ?? $"{attacker.Name} attacked {target.Name} during dialogue.",
            reason: "dialogue action: attack",
            operation: "dialogueAttack",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "attack",
                ["targetEntityId"] = target.Id.Value,
            });
        if (attackDeltas.Count == 0)
        {
            RejectDialogueAction(provider, proposal, "The attack produced no consequence.", deltas);
            return;
        }

        messages.AddRange(attackDeltas.PlayerMessages());
        deltas.AddRange(attackDeltas);
    }

    private Entity? ResolveDialogueAttackTarget(Entity attacker, string? targetEntityId)
    {
        if (!string.IsNullOrWhiteSpace(targetEntityId)
            && Engine.EntityById(targetEntityId) is { } target)
        {
            return target;
        }

        return Engine.State.ControlledEntity.Id == attacker.Id
            ? null
            : Engine.State.ControlledEntity;
    }

    private void ApplyDialogueCreatePromise(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var speaker = Engine.EntityById(actorId);
        if (speaker is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        Entity? anchor = null;
        string? unresolvedTargetHint = null;
        if (!string.IsNullOrWhiteSpace(proposal.TargetEntityId))
        {
            anchor = Engine.EntityById(proposal.TargetEntityId);
            if (anchor is null)
            {
                unresolvedTargetHint = proposal.TargetEntityId.Trim();
            }
        }

        var text = FirstNonBlank(proposal.PromiseText, proposal.Description, proposal.Name, proposal.ItemName);
        if (string.IsNullOrWhiteSpace(text))
        {
            RejectDialogueAction(provider, proposal, "The promise action did not include promise text.", deltas);
            return;
        }

        var triggerHint = FirstNonBlank(proposal.TriggerHint, proposal.TargetHint, unresolvedTargetHint, "");
        var realizationKind = string.IsNullOrWhiteSpace(proposal.RealizationKind)
            ? null
            : proposal.RealizationKind.Trim();
        var applied = Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            $"dialogue:{provider}",
            string.IsNullOrWhiteSpace(proposal.PromiseKind) ? "rumor" : proposal.PromiseKind.Trim(),
            text.Trim(),
            anchorEntityId: anchor?.Id.Value,
            triggerHint: triggerHint ?? "",
            visibility: (proposal.PlayerVisible ?? true) ? WorldConsequenceVisibility.Journal : WorldConsequenceVisibility.Hidden,
            sourceEntityId: speaker.Id.Value,
            evidence: proposal.Reason ?? text.Trim(),
            reason: "dialogue action: create promise",
            operation: "dialogueCreatePromise",
            stackExisting: proposal.StackExisting ?? true,
            playerVisible: proposal.PlayerVisible ?? true,
            salience: Math.Clamp(proposal.Salience ?? 3, 1, 5),
            subject: FirstNonBlank(proposal.Subject, proposal.Name, proposal.ItemName, unresolvedTargetHint),
            claimedPlace: FirstNonBlank(proposal.ClaimedPlace, unresolvedTargetHint),
            realizationKind: realizationKind,
            autoBind: proposal.AutoBind ?? (anchor is not null),
            emitMessage: proposal.PlayerVisible ?? true,
            message: $"A promise enters the world through {speaker.Name}: {text.Trim()}",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "create_promise",
                ["speakerId"] = speaker.Id.Value,
                ["unresolvedTargetHint"] = unresolvedTargetHint,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The promise could not be created.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueAddCanon(
        string provider,
        string actorId,
        string spokenText,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var speaker = Engine.EntityById(actorId);
        if (speaker is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var text = FirstNonBlank(
            proposal.CanonText,
            proposal.Description,
            proposal.PromiseText,
            proposal.Name,
            proposal.ItemName);
        var subject = FirstNonBlank(proposal.Subject, proposal.Name, proposal.TargetHint, proposal.TargetEntityId);
        if (string.IsNullOrWhiteSpace(text))
        {
            RejectDialogueAction(provider, proposal, "The canon action did not include fact text.", deltas);
            return;
        }

        if (!GeneratedFactIsSupportedBySpokenText(text, subject, spokenText))
        {
            RejectDialogueAction(provider, proposal, "The canon fact was not supported by the NPC's spoken line.", deltas);
            return;
        }

        var attachedTo = FirstNonBlank(proposal.TargetEntityId, speaker.Id.Value)!;
        var kind = NormalizeToken(
            FirstNonBlank(
                proposal.CanonKind,
                proposal.FixtureType,
                proposal.PromiseKind,
                proposal.RealizationKind,
                "dialogue_fact")!,
            "dialogue_fact");
        var summary = FirstNonBlank(proposal.CanonSummary, proposal.Name, proposal.Subject, text)!;
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", "canonized" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var applied = Engine.ApplyConsequence(WorldConsequence.AddCanon(
            $"dialogue:{provider}",
            kind,
            attachedTo,
            text.Trim(),
            summary.Trim(),
            tags,
            visibility: (proposal.PlayerVisible ?? false)
                ? WorldConsequenceVisibility.Message
                : WorldConsequenceVisibility.Hidden,
            sourceEntityId: speaker.Id.Value,
            evidence: spokenText,
            reason: "dialogue action: canonize fact",
            operation: "dialogueAddCanon",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "add_canon",
                ["speakerId"] = speaker.Id.Value,
                ["playerVisible"] = proposal.PlayerVisible ?? false,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The canon fact could not be recorded.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueOfferTrade(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var merchant = Engine.EntityById(actorId);
        if (merchant is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var itemName = string.IsNullOrWhiteSpace(proposal.ItemName)
            ? null
            : proposal.ItemName.Trim();
        var applied = Engine.ApplyConsequence(WorldConsequence.OfferTrade(
            $"dialogue:{provider}",
            merchant.Id.Value,
            itemName,
            quantity: Math.Max(1, proposal.Quantity ?? 1),
            gold: Math.Max(0, proposal.Gold ?? 30),
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: merchant.Id.Value,
            evidence: proposal.Reason,
            reason: "dialogue action: offer trade",
            operation: "dialogueOfferTrade",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "offer_trade",
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The trade offer could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private void ApplyDialogueSpawnFixture(
        string provider,
        string actorId,
        string type,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var actor = Engine.EntityById(actorId);
        if (actor is null || !actor.TryGet<PositionComponent>(out var actorPosition))
        {
            RejectDialogueAction(provider, proposal, "The speaker is missing or has no local position.", deltas);
            return;
        }

        var point = ResolveDialogueFixturePoint(actorPosition.Position, proposal);
        if (point is null)
        {
            RejectDialogueAction(provider, proposal, "No valid nearby tile was available for the fixture.", deltas);
            return;
        }

        var isMarker = type == "mark_location";
        var name = FirstNonBlank(proposal.Name, proposal.ItemName, proposal.Description, isMarker ? "marked place" : "strange fixture")!;
        var fixtureType = NormalizeToken(
            FirstNonBlank(proposal.FixtureType, isMarker ? "marker" : "feature")!,
            isMarker ? "marker" : "feature");
        var material = NormalizeToken(
            FirstNonBlank(proposal.Material, isMarker ? "chalk" : "wood")!,
            isMarker ? "chalk" : "wood");
        var tags = (proposal.Tags ?? Array.Empty<string>())
            .Concat(new[] { "dialogue", isMarker ? "marked" : "spawned" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var verbs = proposal.InteractableVerbs is { Count: > 0 }
            ? proposal.InteractableVerbs
            : new[] { "examine" };
        var blocksMovement = proposal.BlocksMovement ?? !isMarker;
        var blocksSight = proposal.BlocksSight ?? false;
        var operation = isMarker ? "dialogueMarkLocation" : "dialogueSpawnFixture";
        var applied = Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            $"dialogue:{provider}",
            name,
            point.Value.X,
            point.Value.Y,
            prefix: NormalizeToken(name, fixtureType),
            glyph: isMarker ? '*' : '#',
            palette: fixtureType,
            fixtureType: fixtureType,
            material: material,
            tags: tags,
            blocksMovement: blocksMovement,
            blocksSight: blocksSight,
            description: proposal.Description,
            interactableVerbs: verbs,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: proposal.Reason,
            reason: isMarker ? "dialogue action: mark location" : "dialogue action: spawn fixture",
            operation: operation,
            message: isMarker
                ? $"{actor.Name} marks {name} nearby."
                : $"{actor.Name} reveals {name} nearby.",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = type,
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The fixture could not be created.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private GridPoint? ResolveDialogueFixturePoint(GridPoint actorPosition, DialogueActionProposal proposal)
    {
        if (proposal.X is not null && proposal.Y is not null)
        {
            var point = new GridPoint(proposal.X.Value, proposal.Y.Value);
            return GameEngine.Distance(actorPosition, point) <= 2 && IsOpenDialogueFixturePoint(point)
                ? point
                : null;
        }

        return AdjacentOffsets()
            .Select(offset => actorPosition.Translate(offset.X, offset.Y))
            .Where(IsOpenDialogueFixturePoint)
            .OrderBy(point => GameEngine.Distance(point, Engine.State.ControlledEntity.Get<PositionComponent>().Position))
            .ThenBy(point => point.X)
            .ThenBy(point => point.Y)
            .Select(point => (GridPoint?)point)
            .FirstOrDefault();
    }

    private bool IsOpenDialogueFixturePoint(GridPoint point) =>
        Engine.InBounds(point)
        && !Engine.State.BlockingTerrain.Contains(point)
        && Engine.BlockingEntityAt(point) is null;

    private void ApplyDialogueRevealService(
        string provider,
        string actorId,
        DialogueActionProposal proposal,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var serviceProvider = Engine.EntityById(actorId);
        if (serviceProvider is null)
        {
            RejectDialogueAction(provider, proposal, "The speaker no longer exists.", deltas);
            return;
        }

        var serviceName = FirstNonBlank(proposal.Name, proposal.ItemName, "quiet service")!;
        var description = FirstNonBlank(proposal.Description, proposal.Reason, serviceName)!;
        var serviceId = NormalizeToken(FirstNonBlank(proposal.ServiceId, serviceName)!, "service");
        var effectKind = NormalizeToken(FirstNonBlank(proposal.EffectKind, InferServiceEffect(description, serviceName))!, "record_memory");
        var applied = Engine.ApplyConsequence(WorldConsequence.OfferService(
            $"dialogue:{provider}",
            serviceProvider.Id.Value,
            serviceId,
            serviceName,
            description,
            effectKind,
            goldCost: Math.Max(0, proposal.GoldCost ?? proposal.Gold ?? 0),
            itemCost: proposal.ItemCost,
            targetHint: proposal.TargetHint,
            tags: proposal.Tags,
            wantStatusOnComplete: proposal.WantStatusOnComplete,
            wantStakesOnComplete: proposal.WantStakesOnComplete,
            wantAddTagsOnComplete: proposal.WantAddTagsOnComplete,
            wantRemoveTagsOnComplete: proposal.WantRemoveTagsOnComplete,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: serviceProvider.Id.Value,
            evidence: proposal.Reason,
            reason: "dialogue action: reveal service",
            operation: "dialogueRevealService",
            details: new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["proposalType"] = "reveal_service",
            }));
        if (!applied.Applied)
        {
            RejectDialogueAction(provider, proposal, applied.Error ?? "The service offer could not be applied.", deltas);
            deltas.AddRange(applied.Deltas);
            return;
        }

        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private static string? FindInventoryKey(InventoryComponent inventory, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        return inventory.Items.Keys
            .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(key =>
                key.Equals(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || key.Contains(item.Trim(), StringComparison.OrdinalIgnoreCase)
                || item.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private void RejectDialogueAction(
        string provider,
        DialogueActionProposal proposal,
        string reason,
        List<StateDelta> deltas)
    {
        var type = NormalizeToken(proposal.Type, "none");
        deltas.Add(new StateDelta(
            "dialogueActionRejected",
            proposal.TargetEntityId ?? "",
            $"Dialogue action proposal rejected: {type}.",
            new Dictionary<string, object?>
            {
                ["provider"] = provider,
                ["type"] = type,
                ["normalizedType"] = NormalizeDialogueActionType(type),
                ["itemName"] = proposal.ItemName,
                ["reason"] = reason,
                ["providerReason"] = proposal.Reason,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private static IEnumerable<GridPoint> AdjacentOffsets() =>
        new[]
        {
            new GridPoint(-1, -1),
            new GridPoint(0, -1),
            new GridPoint(1, -1),
            new GridPoint(-1, 0),
            new GridPoint(1, 0),
            new GridPoint(-1, 1),
            new GridPoint(0, 1),
            new GridPoint(1, 1),
        };
}
