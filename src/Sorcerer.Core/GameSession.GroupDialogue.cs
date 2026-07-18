using Sorcerer.Core.Consequences;
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
/// critical claims. This is the deterministic, always-available path (WP6.6); a single-call live
/// provider exchange is a later enhancement layered on the same surface.
/// </summary>
public sealed partial class GameSession
{
    private readonly Dictionary<string, int> _recentGroupExchange = new(StringComparer.OrdinalIgnoreCase);

    private const int GroupTalkReach = 2;

    private ActionResult GroupTalk(string opener)
    {
        var state = Engine.State;
        var player = state.ControlledEntity;
        if (!player.TryGet<PositionComponent>(out var playerPosition))
        {
            return ActionResult.Simple("group_talk", false, false, state.Turn, state.Turn,
                "You are in no position to gather anyone.");
        }

        var participants = state.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(IsEligibleParticipant)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.StepDistance(playerPosition.Position, position.Position) <= GroupTalkReach)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.StepDistance(playerPosition.Position, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

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
            TurnBefore = state.Turn,
            TurnAfter = state.Turn,
            Messages = messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas,
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

    private enum ExchangeKind { StoryCircle, Disagreement, Chatter }

    private static ExchangeKind ExchangeMode(IReadOnlyList<Entity> participants)
    {
        bool AnyTag(params string[] wanted) => participants.Any(p =>
            p.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => wanted.Contains(tag, StringComparer.OrdinalIgnoreCase)));

        if (AnyTag("story", "tale", "hall", "witness", "teller", "scrimshaw", "brall", "ale"))
        {
            return ExchangeKind.StoryCircle;
        }

        if (AnyTag("shelter", "refuge", "free_folk", "empire", "client", "stability", "occupation", "hollowmere"))
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
            ExchangeKind.StoryCircle => BuildStoryCircle(participants, round, placeName),
            ExchangeKind.Disagreement => BuildDisagreement(participants, round),
            _ => BuildChatter(participants, round),
        };
    }

    /// <summary>A Bralli tale-circle: each teller takes the same deed and one-ups the detail, keeping
    /// (increasingly dubious) provenance. Escalates across repeated rounds.</summary>
    private List<GroupUtterance> BuildStoryCircle(IReadOnlyList<Entity> participants, int round, string placeName)
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
            utterances.Add(new GroupUtterance(participants[i].Id.Value, participants[i].Name, line));
        }

        return utterances;
    }

    /// <summary>A Hollowmere disagreement: participants split, by their own tags, between valuing
    /// imperial stability and valuing wild freedom, over a concrete stake.</summary>
    private List<GroupUtterance> BuildDisagreement(IReadOnlyList<Entity> participants, int round)
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
            utterances.Add(new GroupUtterance(participants[i].Id.Value, participants[i].Name, pool[round % pool.Length]));
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

    private List<GroupUtterance> BuildChatter(IReadOnlyList<Entity> participants, int round)
    {
        var utterances = new List<GroupUtterance>();
        var count = Math.Min(participants.Count, 4);
        for (var i = 0; i < count; i++)
        {
            var want = participants[i].TryGet<WantComponent>(out var wantComponent) && !string.IsNullOrWhiteSpace(wantComponent.Text)
                ? wantComponent.Text
                : "the weather and the roads";
            utterances.Add(new GroupUtterance(
                participants[i].Id.Value,
                participants[i].Name,
                $"Talk here keeps circling back to {LowerFirst(want)}"));
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
}
