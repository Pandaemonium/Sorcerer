using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GroupDialogueTests
{
    [Fact]
    public async Task LiveGroupPathUsesExactlyOneCallAndOnlyAuthorizedSpeakers()
    {
        var provider = new CountingGroupProvider();
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            seed: 7);
        var origin = new GridPoint(1, 1);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(origin));
        var ids = SeatParticipants(session, origin, new[]
        {
            ("one_call_a", "Ada One-Call", new[] { "resident", "witness" }),
            ("one_call_b", "Bela One-Call", new[] { "resident", "witness" }),
        });
        provider.Speakers = ids;

        var result = await session.ExecuteAsync(new GroupTalkCommand("What did you each see?"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Equal(1, provider.Calls);
        Assert.Equal(ids.Order(), result.Messages
            .Where(message => message.Contains(": \"", StringComparison.Ordinal))
            .Select(message => ids.Single(id => message.Contains(session.Engine.EntityById(id)!.Name, StringComparison.Ordinal)))
            .Order());
        Assert.NotNull(provider.LastRequest?.Participants);
        Assert.All(provider.LastRequest!.Participants!, participant => Assert.Contains(participant.EntityId, ids));
        Assert.All(ids, id => Assert.Contains(
            session.Engine.EntityById(id)!.Get<MemoryComponent>().Records,
            memory => memory.Text.Contains("Shared witness account", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task LiveGroupRejectsGenericConsequencesEvenWhenTheirSurfaceTargetIsAuthorized()
    {
        var provider = new CountingGroupProvider
        {
            ProposalFactory = (speaker, _) => new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "consequence",
                        TargetEntityId: speaker,
                        ConsequenceType: "damage",
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["targetEntityId"] = "soldier_1",
                            ["amount"] = 99,
                        }),
                }),
        };
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            seed: 7);
        var origin = new GridPoint(1, 1);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(origin));
        var ids = SeatParticipants(session, origin, new[]
        {
            ("guarded_a", "Ada Guarded", new[] { "resident", "witness" }),
            ("guarded_b", "Bela Guarded", new[] { "resident", "witness" }),
        });
        provider.Speakers = ids;
        var hpBefore = session.Engine.EntityById("soldier_1")!.Get<ActorComponent>().HitPoints;

        var result = await session.ExecuteAsync(new GroupTalkCommand("Destroy the soldier for us."));

        Assert.False(result.Success);
        Assert.True(result.TechnicalFailure);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(hpBefore, session.Engine.EntityById("soldier_1")!.Get<ActorComponent>().HitPoints);
        Assert.Contains(result.Messages, message => message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerboseLiveGroupLinesAreTrimmedAtACompleteSentence()
    {
        var provider = new CountingGroupProvider
        {
            Lines = new[]
            {
                "The ferry families will hide their ledgers before they surrender another name. "
                    + new string('x', 260),
                "The road stays open only while somebody keeps the bell schedule honest. "
                    + new string('y', 260),
            },
        };
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            seed: 7);
        var origin = new GridPoint(1, 1);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(origin));
        var ids = SeatParticipants(session, origin, new[]
        {
            ("sentence_a", "Ada Sentence", new[] { "resident", "witness" }),
            ("sentence_b", "Bela Sentence", new[] { "resident", "witness" }),
        });
        provider.Speakers = ids;

        var result = await session.ExecuteAsync(new GroupTalkCommand("What will you protect?"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        var spoken = result.Messages.Where(message => message.Contains(": \"")).ToArray();
        Assert.Contains(spoken, line => line.EndsWith("another name.\"", StringComparison.Ordinal));
        Assert.Contains(spoken, line => line.EndsWith("schedule honest.\"", StringComparison.Ordinal));
        Assert.DoesNotContain(spoken, line => line.EndsWith("…\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GroupTalkGathersNearbyParticipantsAndLeavesShareableMemories()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var players = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var ids = SeatParticipants(session, players, new[]
        {
            ("teller_a", "Ragna Bone-Tale, tale-witness", new[] { "resident", "witness", "tale", "hall" }),
            ("teller_b", "Ulf Storm-Sworn, harpoon mender", new[] { "resident", "story", "brall" }),
            ("teller_c", "Sef Ale-Oath, cask-keep", new[] { "resident", "hall", "ale" }),
        });

        var result = await session.ExecuteAsync(new GroupTalkCommand("Tell me what happened off the quay."));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.Equal(result.TurnBefore + 1, result.TurnAfter);
        // At least two named participants spoke, and every spoken line is attributed to a seated one.
        var spokenNames = result.Messages.Where(m => m.Contains(": \"")).Select(m => m.Split(':')[0].Trim()).ToArray();
        Assert.True(spokenNames.Length >= 2, $"expected a multi-speaker exchange, got {spokenNames.Length}");
        Assert.DoesNotContain(result.Messages, message => message.Contains("Want id", StringComparison.OrdinalIgnoreCase));

        // Real, replayable state: participants kept shareable memories with provenance.
        var withMemory = ids.Select(id => session.Engine.EntityById(id))
            .Count(e => e is not null && e.TryGet<MemoryComponent>(out var m) && m.Records.Count > 0);
        Assert.True(withMemory >= 2, "group utterances should become shareable memories on the speakers");
    }

    [Fact]
    public async Task HollowmereGroupSplitsHonestlyBetweenStabilityAndFreedom()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var players = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        SeatParticipants(session, players, new[]
        {
            ("loyalist", "Maren Client-True, ferry clerk", new[] { "resident", "hollowmere", "empire", "client" }),
            ("free", "Nim Red-Thread, oathkeeper", new[] { "resident", "hollowmere", "free_folk", "shelter" }),
        });

        var result = await session.ExecuteAsync(new GroupTalkCommand("Should this house take the stranger in?"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        var transcript = string.Join("\n", result.Messages);
        // Both sides of the honest disagreement are present: the pro-stability voice frets about
        // roads and reaping, the pro-freedom voice about sheltering strangers.
        Assert.Contains("roads", transcript, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("strangers", transcript, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GroupTalkNeedsEnoughPeople()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        // Move the player somewhere empty so no one is in reach.
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(1, 1)));

        var result = await session.ExecuteAsync(new GroupTalkCommand("Anyone?"));

        Assert.False(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("not enough people", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OrdinaryWitnessesDoNotAccidentallyBecomeABralliStoryCircle()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var players = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        SeatParticipants(session, players, new[]
        {
            ("witness_a", "Adra Gray Ribbon", new[] { "resident", "witness" }),
            ("witness_b", "Beren Ninth-Desk", new[] { "resident", "witness" }),
        });

        var result = await session.ExecuteAsync(new GroupTalkCommand("Who moved the evidence crate?"));
        var transcript = string.Join("\n", result.Messages);

        Assert.True(result.Success);
        Assert.DoesNotContain("cold quay", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("whale", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Who moved the evidence crate?", transcript, StringComparison.Ordinal);
        Assert.DoesNotContain("On \"", transcript, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GroupTalkPreservesMeaningfulVoicesWhenCloserChatterersWouldFillTheCircle()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var players = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        SeatParticipants(session, players, new[]
        {
            ("chatter_a", "Aru", new[] { "resident" }),
            ("chatter_b", "Bela", new[] { "resident" }),
            ("chatter_c", "Caro", new[] { "resident" }),
            ("chatter_d", "Dena", new[] { "resident" }),
            ("loyalist_far", "Maren Client-True", new[] { "resident", "empire", "stability" }),
            ("free_far", "Nim Red-Thread", new[] { "resident", "free_folk", "shelter" }),
        });

        var result = await session.ExecuteAsync(new GroupTalkCommand("Who should Hollowmere trust?"));
        var transcript = string.Join("\n", result.Messages);

        Assert.True(result.Success, transcript);
        Assert.Contains("Maren Client-True:", transcript, StringComparison.Ordinal);
        Assert.Contains("Nim Red-Thread:", transcript, StringComparison.Ordinal);
        Assert.Contains("roads", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("strangers", transcript, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("You ask, \"", transcript, StringComparison.Ordinal);
    }

    private static string[] SeatParticipants(
        GameSession session,
        GridPoint origin,
        (string Id, string Name, string[] Tags)[] people)
    {
        var state = session.Engine.State;
        var placed = new List<string>();
        var offsets = new[]
        {
            new GridPoint(1, 0), new GridPoint(-1, 0), new GridPoint(0, 1), new GridPoint(1, 1),
            new GridPoint(-2, 0), new GridPoint(2, 0), new GridPoint(0, -2), new GridPoint(2, 1),
        };
        for (var i = 0; i < people.Length; i++)
        {
            var (id, name, tags) = people[i];
            var entity = new Entity(EntityId.Create(id), name);
            var pos = new GridPoint(origin.X + offsets[i].X, origin.Y + offsets[i].Y);
            entity.Set(new PositionComponent(pos));
            entity.Set(new ActorComponent(8, 8, 0, 0, 0, 0, "neutral"));
            entity.Set(new TagsComponent(tags));
            entity.Set(new WantComponent(id + "_want", "someone to hear them out", 3));
            state.Entities[entity.Id] = entity;
            placed.Add(id);
        }

        return placed.ToArray();
    }

    private sealed class CountingGroupProvider : IDialogueProvider
    {
        public string Name => "counting-group";

        public int Calls { get; private set; }

        public string[] Speakers { get; set; } = Array.Empty<string>();

        public string[] Lines { get; set; } = Array.Empty<string>();

        public DialogueRequest? LastRequest { get; private set; }

        public Func<string, int, DialogueProposalSet>? ProposalFactory { get; set; }

        public Task<DialogueProviderResult> ResolveAsync(DialogueRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            var utterances = Speakers.Select((speaker, index) => new DialogueUtteranceResponse(
                speaker,
                Lines.ElementAtOrDefault(index)
                    ?? (index == 0 ? "I saw the dispatch change hands." : "I saw who looked away."),
                Intent: "inform",
                Proposals: ProposalFactory?.Invoke(speaker, index) ?? new DialogueProposalSet(
                    Memories: new[]
                    {
                        new DialogueMemoryProposal(speaker, $"Shared witness account {index}.", Shareable: true),
                    }))).ToArray();
            return Task.FromResult(new DialogueProviderResult(
                Name,
                "fixture",
                false,
                null,
                new DialogueResponse("", Intent: "group_exchange", Utterances: utterances)));
        }
    }
}
