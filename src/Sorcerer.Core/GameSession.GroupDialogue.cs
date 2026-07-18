using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;

namespace Sorcerer.Core;

/// <summary>
/// WP6 (docs/CONTENT_SPRINT_PLAN.md): the multi-participant conversation turn. The player opens the
/// floor to two-to-four eligible nearby participants, and a bounded exchange of speaker-attributed
/// utterances resolves through ordinary state — no modal conversation game, and no code that checks
/// a Bralli region or a specific character id. Eligibility comes from roles/tags (tale-circle,
/// household, market) and the exchange shape from the participants' own tags: a Bralli tale-circle
/// one-ups a provenance-bearing story; a Hollowmere household or market disagrees honestly about
/// stability versus freedom. Every utterance's speaker resolves to an authorized participant, and
/// the exchange leaves shareable memories (retellings that keep provenance) rather than inventing
/// critical claims. The live path asks the provider once for a bounded array of speaker-attributed
/// utterances; the deterministic exchange remains the provider-disabled fallback.
/// </summary>
public sealed partial class GameSession
{
    private readonly Dictionary<string, int> _recentGroupExchange = new(StringComparer.OrdinalIgnoreCase);

    // A gathering is a room-scale interaction rather than a whispered one-on-one exchange. Four
    // tiles lets a visible market or tale-hall ensemble participate without requiring the player
    // to herd deterministic residents onto adjacent squares first.
    private const int GroupTalkReach = 4;

    private async Task<ActionResult> GroupTalkAsync(string opener, CancellationToken cancellationToken)
    {
        if (_dialogueProvider is NullDialogueProvider)
        {
            return GroupTalk(opener);
        }

        var state = Engine.State;
        var turnBefore = state.Turn;
        var participants = NearbyGroupParticipants();
        if (participants.Length < 2)
        {
            return ActionResult.Simple("group_talk", false, false, state.Turn, state.Turn,
                "There are not enough people close together here to open a conversation.");
        }

        var primary = participants[0];
        var prepared = PreparedGroupTurn(primary, opener);
        var assembly = DialogueContextAssembler.Build(Engine, prepared);
        var selection = assembly.Select(new DialogueRouteResult(
            "deterministic-group-context",
            "",
            TechnicalFailure: true,
            Error: null,
            SelectedCardIds: Array.Empty<string>()));
        var request = assembly.BuildDialogueRequest(selection) with
        {
            Participants = participants.Select(GroupParticipantCard).ToArray(),
            RecentDialogue = RecentDialogueFor(primary.Id.Value),
        };

        DialogueProviderResult providerResult;
        try
        {
            providerResult = await _dialogueProvider.ResolveAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            providerResult = new DialogueProviderResult(_dialogueProvider.Name, "", true, ex.Message, null);
        }

        if (providerResult.TechnicalFailure || providerResult.Response?.Utterances is not { Count: >= 2 } utterances)
        {
            var error = providerResult.Error ?? "The provider did not return a multi-speaker exchange.";
            var failure = GroupDialogueFailure(turnBefore, providerResult.Provider, providerResult.RawText, error);
            RecordDialogueAudit(request, providerResult, failure, new[] { error });
            return failure with { Dialogue = ToDialogueResolutionRecord(providerResult) };
        }

        var authorized = participants.Select(participant => participant.Id.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var playerId = state.ControlledEntityId.Value;
        if (utterances.Any(utterance =>
                !authorized.Contains(utterance.SpeakerEntityId)
                || string.IsNullOrWhiteSpace(utterance.SpokenText)
                || !GroupProposalTargetsAuthorized(utterance.Proposals, authorized, playerId)))
        {
            const string error = "Group dialogue contained an unauthorized speaker or consequence target.";
            var failure = GroupDialogueFailure(turnBefore, providerResult.Provider, providerResult.RawText, error);
            RecordDialogueAudit(request, providerResult, failure, new[] { error });
            return failure with { Dialogue = ToDialogueResolutionRecord(providerResult) };
        }

        var mode = ExchangeMode(participants);
        var placeName = Engine.CurrentPlace.DisplayName;
        var provenance = mode == ExchangeKind.StoryCircle
            ? $"the tale-circle at {placeName}"
            : mode == ExchangeKind.Disagreement
                ? $"the argument at {placeName}"
                : $"the talk at {placeName}";
        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        if (!string.IsNullOrWhiteSpace(opener))
        {
            var opening = Engine.ApplyConsequence(WorldConsequence.Message(
                "player_command",
                $"You say to the gathering, \"{opener.Trim()}\"",
                targetEntityId: playerId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: playerId,
                evidence: opener,
                reason: "The player opened a live group conversation.",
                operation: "groupTalkOpener"));
            messages.AddRange(opening.Messages);
            deltas.AddRange(opening.Deltas);
        }

        foreach (var utterance in utterances.Take(6))
        {
            var speaker = participants.First(participant => participant.Id.Value.Equals(utterance.SpeakerEntityId, StringComparison.OrdinalIgnoreCase));
            var text = TrimGroupLine(utterance.SpokenText);
            var line = $"{speaker.Name}: \"{text}\"";
            var speech = Engine.ApplyConsequence(WorldConsequence.Message(
                "group_talk",
                line,
                targetEntityId: speaker.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: speaker.Id.Value,
                evidence: text,
                reason: "An authorized participant spoke in one live group exchange.",
                operation: "groupTalkUtterance",
                details: new Dictionary<string, object?>
                {
                    ["speakerId"] = speaker.Id.Value,
                    ["speakerName"] = speaker.Name,
                    ["provider"] = providerResult.Provider,
                    ["exchangeMode"] = mode.ToString(),
                    ["playerVisible"] = true,
                }));
            messages.AddRange(speech.Messages);
            deltas.AddRange(speech.Deltas);

            var memory = Engine.ApplyConsequence(WorldConsequence.RecordMemory(
                "group_talk",
                speaker.Id.Value,
                text,
                provenance,
                salience: 2,
                shareable: true,
                sourceEntityId: playerId,
                evidence: text,
                reason: "A live group utterance became a shareable memory.",
                operation: "groupTalkMemory"));
            deltas.AddRange(memory.Deltas);

            var speakerTurn = PreparedGroupTurn(speaker, opener);
            var claimRequest = new DialogueClaimRequest(
                state.Turn,
                state.RegionId,
                state.CurrentZoneId,
                speaker.Id.Value,
                speaker.Name,
                TagsFor(speaker),
                SoulIdFor(state.ControlledEntity),
                opener,
                new[] { text },
                RecentMemoriesFor(speaker.Id.Value),
                state.Claims.Records.TakeLast(DialogueRecentClaimLimit).ToArray(),
                playerId);
            ApplyDialogueProposalSet(
                speakerTurn,
                claimRequest,
                providerResult.Provider,
                text,
                utterance.Intent,
                utterance.Proposals,
                requiresSpokenTextSupport: true,
                parserOrigin: false,
                messages,
                deltas);
            RememberDialogueExchange(speakerTurn, opener, text);
        }

        var turnDeltas = Engine.AdvanceTurn();
        deltas.AddRange(turnDeltas);
        var result = new ActionResult
        {
            Action = "group_talk",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = state.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas,
            Dialogue = ToDialogueResolutionRecord(providerResult),
        };
        RecordDialogueAudit(request, providerResult, result, Array.Empty<string>());
        return result;
    }

    private ActionResult GroupTalk(string opener)
    {
        var state = Engine.State;
        var turnBefore = state.Turn;
        var player = state.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var playerPosition))
        {
            return ActionResult.Simple("group_talk", false, false, state.Turn, state.Turn,
                "You are in no position to gather anyone.");
        }

        var participants = NearbyGroupParticipants();

        if (participants.Length < 2)
        {
            return ActionResult.Simple("group_talk", false, false, state.Turn, state.Turn,
                "There are not enough people close together here to open a conversation.");
        }

        var key = string.Join("|", participants.Select(p => p.Id.Value).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
        var round = _recentGroupExchange.TryGetValue(key, out var previous) ? previous + 1 : 0;
        _recentGroupExchange[key] = round;

        var placeName = Engine.CurrentPlace.DisplayName;
        var mode = ExchangeMode(participants);
        var utterances = BuildExchange(mode, participants, opener, round, placeName);

        // Every speaker id must resolve to an authorized participant — invalid speech is dropped
        // rather than inventing a voice.
        var authorized = participants.Select(p => p.Id.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        utterances = utterances.Where(u => authorized.Contains(u.SpeakerId)).ToList();
        if (utterances.Count == 0)
        {
            return ActionResult.Simple("group_talk", false, false, state.Turn, state.Turn,
                "The gathering falls awkwardly silent.");
        }

        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        if (!string.IsNullOrWhiteSpace(opener))
        {
            var opening = Engine.ApplyConsequence(WorldConsequence.Message(
                "player_command",
                $"You say to the gathering, \"{opener.Trim()}\"",
                targetEntityId: state.ControlledEntityId.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: state.ControlledEntityId.Value,
                evidence: opener,
                reason: "The player opened a group conversation.",
                operation: "groupTalkOpener",
                details: new Dictionary<string, object?> { ["playerVisible"] = true }));
            deltas.AddRange(opening.Deltas);
            messages.Add($"You say to the gathering, \"{opener.Trim()}\"");
        }

        var provenance = mode == ExchangeKind.StoryCircle
            ? $"the tale-circle at {placeName}"
            : mode == ExchangeKind.Disagreement
                ? $"the argument at {placeName}"
                : $"the talk at {placeName}";

        foreach (var utterance in utterances)
        {
            var line = $"{utterance.SpeakerName}: \"{utterance.Text}\"";
            var applied = Engine.ApplyConsequence(WorldConsequence.Message(
                "group_talk",
                line,
                targetEntityId: utterance.SpeakerId,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: utterance.SpeakerId,
                evidence: utterance.Text,
                reason: "A participant spoke in the group conversation.",
                operation: "groupTalkUtterance",
                details: new Dictionary<string, object?>
                {
                    ["playerVisible"] = true,
                    ["speakerId"] = utterance.SpeakerId,
                    ["speakerName"] = utterance.SpeakerName,
                    ["exchangeMode"] = mode.ToString(),
                }));
            deltas.AddRange(applied.Deltas);
            messages.Add(line);

            // Real, replayable state: each speaker keeps a shareable memory of what was said, with
            // provenance, so the story/argument can travel as an ordinary rumor.
            var memory = Engine.ApplyConsequence(WorldConsequence.RecordMemory(
                "group_talk",
                utterance.SpeakerId,
                utterance.Text,
                provenance,
                salience: 2,
                shareable: true,
                sourceEntityId: state.ControlledEntityId.Value,
                evidence: utterance.Text,
                reason: "A group utterance became a shareable memory.",
                operation: "groupTalkMemory"));
            deltas.AddRange(memory.Deltas);
        }

        var turnDeltas = Engine.AdvanceTurn();
        deltas.AddRange(turnDeltas);
        return new ActionResult
        {
            Action = "group_talk",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = state.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas,
        };
    }

    private Entity[] NearbyGroupParticipants()
    {
        var state = Engine.State;
        var player = state.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var playerPosition))
        {
            return Array.Empty<Entity>();
        }

        var nearby = state.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(IsEligibleParticipant)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.StepDistance(playerPosition.Position, position.Position) <= GroupTalkReach)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.StepDistance(playerPosition.Position, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return SelectGroupParticipants(nearby, 4);
    }

    private PreparedDialogueTurn PreparedGroupTurn(Entity speaker, string opener)
    {
        var state = Engine.State;
        var tags = TagsFor(speaker);
        var faction = speaker.TryGet<FactionComponent>(out var membership) ? membership.FactionId : speaker.Get<ActorComponent>().Faction;
        var profile = speaker.TryGet<ProfileComponent>(out var identity) ? identity.Backstory : null;
        var bondSummary = state.Bonds.TryGet(SoulIdFor(speaker), SoulIdFor(state.ControlledEntity), out var bond)
            ? $"loyalty {bond.Loyalty}, fear {bond.Fear}, admiration {bond.Admiration}, resentment {bond.Resentment}, posture {bond.Posture}"
            : null;
        return new PreparedDialogueTurn(
            state.Turn,
            opener,
            speaker.Id.Value,
            speaker.Name,
            tags,
            SoulIdFor(state.ControlledEntity),
            Engine.IsHostile(speaker, state.ControlledEntity),
            profile,
            faction,
            bondSummary,
            speaker.TryGet<WantComponent>(out var want) ? $"Want id {want.Id}: {want.Text}" : null);
    }

    private DialogueParticipantCard GroupParticipantCard(Entity participant)
    {
        var turn = PreparedGroupTurn(participant, "");
        return new DialogueParticipantCard(
            participant.Id.Value,
            participant.Name,
            turn.SpeakerTags,
            turn.SpeakerFaction,
            turn.SpeakerProfile,
            participant.TryGet<DescriptionComponent>(out var description) ? description.Text : null,
            turn.BondSummary,
            participant.TryGet<InventoryComponent>(out var inventory)
                ? inventory.Items.Select(pair => $"{pair.Key} x{pair.Value}").ToArray()
                : null,
            Want: turn.SpeakerWant);
    }

    private static bool GroupProposalTargetsAuthorized(
        DialogueProposalSet? proposals,
        IReadOnlySet<string> participantIds,
        string playerId)
    {
        if (proposals is null)
        {
            return true;
        }

        bool Allowed(string? id) => string.IsNullOrWhiteSpace(id)
            || id.Equals(playerId, StringComparison.OrdinalIgnoreCase)
            || participantIds.Contains(id);
        return (proposals.Claims ?? Array.Empty<DialogueClaimProposal>()).All(claim => Allowed(claim.TargetEntityId) && Allowed(claim.MerchantId))
            && (proposals.Memories ?? Array.Empty<DialogueMemoryProposal>()).All(memory => Allowed(memory.OwnerEntityId))
            && (proposals.Bond is null || Allowed(proposals.Bond.EntityId))
            && (proposals.Want is null || Allowed(proposals.Want.EntityId))
            && (proposals.Actions ?? Array.Empty<DialogueActionProposal>()).All(action =>
                Allowed(action.TargetEntityId)
                && !action.Type.Equals("consequence", StringComparison.OrdinalIgnoreCase))
            && (proposals.Bargain is null || Allowed(proposals.Bargain.ClaimantEntityId));
    }

    private static ActionResult GroupDialogueFailure(int turnBefore, string provider, string rawText, string error)
    {
        var message = $"Group dialogue provider failed: {error}";
        return new ActionResult
        {
            Action = "group_talk",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = turnBefore,
            TurnAfter = turnBefore,
            TechnicalFailure = true,
            FailureCode = FailureCode.ProviderFailure,
            Messages = new[] { message },
            Deltas = new[]
            {
                new StateDelta(
                    "groupDialogueProviderFailed",
                    "group",
                    message,
                    new Dictionary<string, object?>
                    {
                        ["provider"] = provider,
                        ["error"] = error,
                        ["rawText"] = rawText,
                    }),
            },
        };
    }

    private static bool IsEligibleParticipant(Entity entity)
    {
        if (!entity.TryGet<TagsComponent>(out var tags))
        {
            return false;
        }

        // People with something to say: residents, witnesses, tellers, keepers, claimants — not
        // beasts, pets, or pure hostiles that would not join a conversation.
        if (tags.Tags.Contains("creature", StringComparer.OrdinalIgnoreCase)
            || tags.Tags.Contains("beast", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return entity.Has<WantComponent>()
            || tags.Tags.Any(tag => ConversationalTags.Contains(tag));
    }

    private static readonly HashSet<string> ConversationalTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "resident", "witness", "teller", "keeper", "claimant", "regional_population",
        "story", "tale", "hall", "merchant", "service_provider", "onlooker",
    };

    private static readonly string[] StabilityVoiceTags = { "empire", "client", "stability", "occupation" };
    private static readonly string[] FreedomVoiceTags = { "shelter", "refuge", "free_folk", "freedom" };
    private static readonly string[] StoryVoiceTags = { "story_circle", "story", "tale", "teller" };

    private static Entity[] SelectGroupParticipants(IReadOnlyList<Entity> nearby, int limit)
    {
        var selected = new List<Entity>(Math.Min(limit, nearby.Count));
        void AddFirstWithAnyTag(IReadOnlyList<string> wanted)
        {
            var voice = nearby.FirstOrDefault(entity => !selected.Contains(entity)
                && entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Any(tag => wanted.Contains(tag, StringComparer.OrdinalIgnoreCase)));
            if (voice is not null)
            {
                selected.Add(voice);
            }
        }

        // A gathering should preserve the meaningful range of voices already in the room. Nearest
        // still determines the fill order, but a follower standing one tile closer can no longer
        // crowd the settlement's political disagreement or tale-circle out of a four-person scene.
        AddFirstWithAnyTag(StabilityVoiceTags);
        AddFirstWithAnyTag(FreedomVoiceTags);
        AddFirstWithAnyTag(StoryVoiceTags);
        AddFirstWithAnyTag(StoryVoiceTags);
        foreach (var entity in nearby)
        {
            if (selected.Count >= limit)
            {
                break;
            }

            if (!selected.Contains(entity))
            {
                selected.Add(entity);
            }
        }

        return selected.Take(limit).ToArray();
    }

    private enum ExchangeKind { StoryCircle, Disagreement, Chatter }

    private static ExchangeKind ExchangeMode(IReadOnlyList<Entity> participants)
    {
        int CountWithTag(params string[] wanted) => participants.Count(p =>
            p.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => wanted.Contains(tag, StringComparer.OrdinalIgnoreCase)));

        if (CountWithTag("story_circle", "story", "tale", "teller") >= 2)
        {
            return ExchangeKind.StoryCircle;
        }

        var stabilityVoices = CountWithTag("empire", "client", "stability", "occupation");
        var freedomVoices = CountWithTag("shelter", "refuge", "free_folk", "freedom");
        if (stabilityVoices > 0 && freedomVoices > 0)
        {
            return ExchangeKind.Disagreement;
        }

        return ExchangeKind.Chatter;
    }

    private sealed record GroupUtterance(string SpeakerId, string SpeakerName, string Text);

    private List<GroupUtterance> BuildExchange(
        ExchangeKind mode,
        IReadOnlyList<Entity> participants,
        string opener,
        int round,
        string placeName)
    {
        return mode switch
        {
            ExchangeKind.StoryCircle => BuildStoryCircle(participants, opener, round, placeName),
            ExchangeKind.Disagreement => BuildDisagreement(participants, opener, round),
            _ => BuildChatter(participants, opener, round),
        };
    }

    /// <summary>A Bralli tale-circle: each teller takes the same deed and one-ups the detail, keeping
    /// (increasingly dubious) provenance. Escalates across repeated rounds.</summary>
    private List<GroupUtterance> BuildStoryCircle(
        IReadOnlyList<Entity> participants,
        string opener,
        int round,
        string placeName)
    {
        var subject = RecentDeedSubject() ?? "the rescue off the cold quay";
        var escalations = new[]
        {
            $"You want to hear about {subject}? I was there. Near thing, but we held.",
            $"Held? You were ashore counting casks. My own cousin pulled three from the water, and swore to it before witnesses.",
            $"Three? It was five, and a whale besides — the scrimshaw in the hall has it carved, so it must be true.",
            $"The carving was paid for by the man who tells it. I heard it was the storm that saved them, and no hand at all.",
        };
        var utterances = new List<GroupUtterance>();
        var count = Math.Min(participants.Count, 4);
        for (var i = 0; i < count; i++)
        {
            var line = escalations[(round + i) % escalations.Length];
            if (i == 0 && !string.IsNullOrWhiteSpace(opener))
            {
                line = $"Taking up that question, {LowerFirst(line)}";
            }

            utterances.Add(new GroupUtterance(participants[i].Id.Value, participants[i].Name, line));
        }

        return utterances;
    }

    /// <summary>A Hollowmere disagreement: participants split, by their own tags, between valuing
    /// imperial stability and valuing wild freedom, over a concrete stake.</summary>
    private List<GroupUtterance> BuildDisagreement(IReadOnlyList<Entity> participants, string opener, int round)
    {
        var proStability = new[]
        {
            "Keep your head down and the roads stay open. The last time someone sheltered a stranger, the reaping took two houses.",
            "The Empire is cold, not cruel. Pay the tally, and your family sleeps. That is worth something.",
        };
        var proFreedom = new[]
        {
            "Open roads for whom? For their carts and their warrants. We fed strangers before there was an Empire to forbid it.",
            "Two houses taken, and you'd give them the third yourself. Some things are worth the risk of the water.",
        };
        var utterances = new List<GroupUtterance>();
        var count = Math.Min(participants.Count, 4);
        for (var i = 0; i < count; i++)
        {
            var stability = LeansStability(participants[i], i);
            var pool = stability ? proStability : proFreedom;
            var line = pool[round % pool.Length];
            if (i == 0 && !string.IsNullOrWhiteSpace(opener))
            {
                line = $"Taking up that question, {LowerFirst(line)}";
            }

            utterances.Add(new GroupUtterance(participants[i].Id.Value, participants[i].Name, line));
        }

        return utterances;
    }

    private static bool LeansStability(Entity entity, int index)
    {
        if (entity.TryGet<TagsComponent>(out var tags))
        {
            if (tags.Tags.Any(tag => tag.Equals("empire", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("client", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("functionary", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }

            if (tags.Tags.Any(tag => tag.Equals("free_folk", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("refuge", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("shelter", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }
        }

        // No lean in the tags: alternate so the argument still has two sides.
        return index % 2 == 0;
    }

    private List<GroupUtterance> BuildChatter(IReadOnlyList<Entity> participants, string opener, int round)
    {
        var utterances = new List<GroupUtterance>();
        var count = Math.Min(participants.Count, 4);
        for (var i = 0; i < count; i++)
        {
            var want = participants[i].TryGet<WantComponent>(out var wantComponent) && !string.IsNullOrWhiteSpace(wantComponent.Text)
                ? wantComponent.Text
                : "the weather and the roads";
            var answer = string.IsNullOrWhiteSpace(opener)
                ? $"Talk here keeps circling back to {LowerFirst(want)}"
                : $"On that question, talk here keeps circling back to {LowerFirst(want)}";
            utterances.Add(new GroupUtterance(
                participants[i].Id.Value,
                participants[i].Name,
                answer));
        }

        return utterances;
    }

    private string? RecentDeedSubject()
    {
        var deed = Engine.State.Canon.Records
            .OrderByDescending(record => record.TurnCreated)
            .FirstOrDefault();
        return deed is null || string.IsNullOrWhiteSpace(deed.Summary) ? null : deed.Summary;
    }

    private static string LowerFirst(string text) =>
        string.IsNullOrEmpty(text) ? text : char.ToLowerInvariant(text[0]) + text[1..];

    private static string TrimGroupLine(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        const int lineLimit = 240;
        if (collapsed.Length <= lineLimit)
        {
            return collapsed;
        }

        // Prefer a complete sentence to a UI-shaped guillotine. Providers are asked to stay well
        // below this limit, but sentence-aware repair keeps a verbose response readable.
        var head = collapsed[..lineLimit];
        var sentenceEnd = Math.Max(head.LastIndexOf('.'), Math.Max(head.LastIndexOf('!'), head.LastIndexOf('?')));
        if (sentenceEnd >= 48)
        {
            return head[..(sentenceEnd + 1)].TrimEnd();
        }

        var wordEnd = head.LastIndexOf(' ');
        return (wordEnd > lineLimit / 2 ? head[..wordEnd] : head).TrimEnd() + "…";
    }
}
