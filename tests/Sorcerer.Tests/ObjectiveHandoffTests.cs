using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ObjectiveHandoffTests
{
    [Fact]
    public async Task FreeingAnyCaptiveProducesOneSpokenActionablePromiseHandoff()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        var turnBefore = session.Engine.State.Turn;

        var open = await FreeOpeningCaptive(session);

        Assert.True(open.Success, string.Join(" | ", open.Messages));
        Assert.True(open.ConsumedTurn);
        Assert.Equal(turnBefore + 2, open.TurnAfter); // pickup and open; the handoff adds no extra turn
        Assert.Contains(open.Messages, message =>
            message.Contains("Lio of Hollowmere says", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "objectiveHandoffMessage"
            && Equals(delta.Details["sourceTrigger"], "rescue"));

        var claim = Assert.Single(session.Engine.State.Claims.Records, claim =>
            claim.SpeakerId == "prisoner_1"
            && claim.Tags.Contains("generated_objective", StringComparer.OrdinalIgnoreCase));
        var objective = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.SourceClaimId == claim.Id);
        Assert.Equal("bound", objective.Status);
        Assert.Equal("person", objective.RealizationKind);
        Assert.Equal("travel", objective.TriggerHint);
        Assert.Matches(@"^-?\d+,-?\d+$", objective.ClaimedPlace!);
        Assert.StartsWith("Reach ", objective.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(objective.Subject, objective.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.SubjectId == "prisoner_1"
            && memory.Provenance == "objective_handoff"
            && memory.Text.Contains(objective.Text, StringComparison.OrdinalIgnoreCase));

        var journal = await session.ExecuteAsync(new JournalCommand());
        Assert.Contains(journal.Messages, message =>
            message.StartsWith("Objective:", StringComparison.OrdinalIgnoreCase)
            && message.Contains(objective.Text, StringComparison.OrdinalIgnoreCase)
            && message.Contains("heard from Lio of Hollowmere", StringComparison.OrdinalIgnoreCase));
        var card = Assert.IsType<Sorcerer.Core.Views.ObjectiveCard>(session.View().CurrentObjective);
        Assert.Equal("meet", card.Kind);
        Assert.Equal(objective.Text, card.NextStep);
        var inspect = await session.ExecuteAsync(new InspectCommand());
        Assert.Contains(inspect.Messages, message =>
            message.Equals($"Next: {card.NextStep}", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OpeningHandoffVariesAcrossSeedsButAlwaysNamesReachableWorldFacts()
    {
        var signatures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var seed = 1; seed <= 8; seed++)
        {
            var session = GameSession.CreateImperialEncounter(seed: seed);
            DisableImperialAi(session);

            await FreeOpeningCaptive(session);

            var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
                item.SourceSpeakerId == "prisoner_1"
                && item.RealizationKind == "person");
            var destination = ParseZoneId(promise.ClaimedPlace!);
            var graph = WorldPlaceGraph.Create(seed, RegionCatalog.LoadDefault());
            var settlement = Assert.Single(graph.Settlements, settlement =>
                settlement.CenterX == destination.X
                && settlement.CenterY == destination.Y);
            Assert.Contains(settlement.Name, promise.Text, StringComparison.OrdinalIgnoreCase);
            Assert.InRange(Math.Abs(destination.X) + Math.Abs(destination.Y), 1, 3);
            Assert.InRange(RegionCatalog.LoadDefault().Region(settlement.RegionId)!.ImperialPresence, 0, 80);
            signatures.Add($"{promise.ClaimedPlace}|{promise.Subject}|{promise.Text}");
        }

        Assert.True(signatures.Count >= 6, $"Expected strong seed variation, got {signatures.Count}: {string.Join(" || ", signatures)}");
    }

    [Fact]
    public async Task RescueHandoffDoesNotDependOnLiosNameOrIdentity()
    {
        var session = GameSession.CreateImperialEncounter(seed: 11);
        DisableImperialAi(session);
        var captive = session.Engine.EntityById("prisoner_1")!;
        captive.Name = "Tavi Reed-Under-Rain";

        var open = await FreeOpeningCaptive(session);

        Assert.Contains(open.Messages, message =>
            message.Contains("Tavi Reed-Under-Rain says", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.SourceSpeakerId == captive.Id.Value
            && promise.RealizationKind == "person"
            && promise.Text.StartsWith("Reach ", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PromisedContactMaterializesAndNaturallyCreatesTheNextObjective()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        await FreeOpeningCaptive(session);
        var first = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.SourceSpeakerId == "prisoner_1"
            && promise.RealizationKind == "person");

        var arrival = await TravelTo(session, first.ClaimedPlace!);

        var realized = session.Engine.State.PromiseLedger.Promises.Single(promise => promise.Id == first.Id);
        Assert.Equal("realized", realized.Status);
        var contact = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals(first.Subject, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("objective_contact", contact.Get<TagsComponent>().Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(arrival.Messages, message =>
            message.Contains(contact.Name, StringComparison.OrdinalIgnoreCase));
        var arrivalJournal = await session.ExecuteAsync(new JournalCommand());
        Assert.Contains(arrivalJournal.Messages, message =>
            message.StartsWith("Objective:", StringComparison.OrdinalIgnoreCase)
            && message.Contains(contact.Name, StringComparison.OrdinalIgnoreCase));

        var countBefore = session.Engine.State.PromiseLedger.Promises.Count;
        var talk = await session.ExecuteAsync(new TalkCommand(contact.Name));

        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.True(talk.ConsumedTurn);
        Assert.Contains(talk.Messages, message =>
            message.Contains($"{contact.Name} says", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Messages, message =>
            message.Contains("Objective complete", StringComparison.OrdinalIgnoreCase)
            && message.Contains(contact.Name, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(talk.Messages, message =>
            message.Contains("promise binds to", StringComparison.OrdinalIgnoreCase)
            || message.Contains("reported claim is recorded", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("Claim claim_", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            "cleared",
            session.Engine.State.PromiseLedger.Promises.Single(promise => promise.Id == first.Id).Status);
        Assert.Equal(
            "applied",
            session.Engine.State.Claims.Records.Single(claim => claim.Id == first.SourceClaimId).Status);
        var next = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.SourceSpeakerId == contact.Id.Value);
        var contract = Assert.IsType<PromiseObjectiveContract>(PromiseObjectiveContracts.For(session.Engine.State, next));
        Assert.Contains(contract.Kind, new[]
        {
            "fetch",
            "delivery",
            "escort",
            "threat",
            "folk_service",
            "rumor_verification",
            "social_leverage",
        });
        Assert.Equal("bound", next.Status);
        Assert.NotEqual(session.Engine.State.CurrentZoneId, next.ClaimedPlace);
        Assert.True(contract.ReturnToGiver);
        Assert.Contains(contact.Name, next.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(countBefore + 1, session.Engine.State.PromiseLedger.Promises.Count);

        var repeated = await session.ExecuteAsync(new TalkCommand(contact.Name));
        Assert.True(repeated.Success);
        Assert.Equal(countBefore + 1, session.Engine.State.PromiseLedger.Promises.Count);
        Assert.DoesNotContain(repeated.Deltas, delta => delta.Operation == "objectiveHandoffMessage");
    }

    [Fact]
    public async Task GeneratedSpokenObjectiveSeedSurvivesSaveLoad()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var giver = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Has<ClaimSourceComponent>());
        var seed = Assert.Single(giver.Get<ClaimSourceComponent>().Claims);
        Assert.False(string.IsNullOrWhiteSpace(seed.SpokenText));
        Assert.StartsWith("Travel ", seed.ObjectiveText, StringComparison.OrdinalIgnoreCase);

        var json = GameSaveService.Serialize(
            session.Engine.State,
            savedAt: new DateTimeOffset(2026, 7, 12, 0, 0, 0, TimeSpan.Zero));
        var loaded = GameSaveService.Deserialize(json).State;
        var loadedSeed = Assert.Single(loaded.Entities[giver.Id].Get<ClaimSourceComponent>().Claims);

        Assert.Equal(seed.SpokenText, loadedSeed.SpokenText);
        Assert.Equal(seed.ObjectiveText, loadedSeed.ObjectiveText);
    }

    [Fact]
    public async Task RejectedObjectivePromiseRollsBackClaimRumorSpeechAndMemory()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        var speaker = session.Engine.EntityById("prisoner_1")!;
        speaker.Set(new ClaimSourceComponent(new[]
        {
            new ClaimSeed(
                "A valid reported fact whose objective is malformed.",
                "journey",
                "malformed destination",
                Salience: 5,
                Confidence: 95,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "lead",
                RealizationKind: "person",
                TriggerHint: "travel",
                ClaimedPlace: "1,0",
                Tags: new[] { "generated_objective" },
                SpokenText: "This speech must not survive a rejected promise.",
                ObjectiveText: ""),
        }));
        var door = session.Engine.EntityById("cell_door_1")!;
        door.Set(door.Get<DoorComponent>() with { IsOpen = true });
        door.Set(door.Get<PhysicalComponent>() with { BlocksMovement = false, BlocksSight = false });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var claimsBefore = session.Engine.State.Claims.Records.Count;
        var rumorsBefore = session.Engine.State.Rumors.Records.Count;

        var talk = await session.ExecuteAsync(new TalkCommand(speaker.Name));

        Assert.True(talk.Success);
        Assert.True(talk.ConsumedTurn);
        Assert.Equal(claimsBefore, session.Engine.State.Claims.Records.Count);
        Assert.Equal(rumorsBefore, session.Engine.State.Rumors.Records.Count);
        Assert.DoesNotContain(session.Engine.State.Memories.Records, memory =>
            memory.Provenance == "objective_handoff"
            && memory.Text.Contains("malformed destination", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Subject == "malformed destination");
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("must not survive", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], "create_promise"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "claimSeedSkipped"
            && Equals(delta.Details["failure"], "Promise consequence did not include text.")
            && !delta.IsPlayerVisible());
    }

    private static async Task<ActionResult> FreeOpeningCaptive(GameSession session)
    {
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(7, 6)));
        var pickup = await session.ExecuteAsync(new PickupCommand("key"));
        Assert.True(pickup.Success, string.Join(" | ", pickup.Messages));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        return await session.ExecuteAsync(new OpenCommand("cell"));
    }

    private static async Task<ActionResult> TravelTo(GameSession session, string zoneId)
    {
        var destination = ParseZoneId(zoneId);
        var current = ParseZoneId(session.Engine.State.CurrentZoneId);
        ActionResult? last = null;
        while (current.X != destination.X)
        {
            last = await session.ExecuteAsync(new TravelCommand(current.X < destination.X ? Direction.East : Direction.West));
            Assert.True(last.Success, string.Join(" | ", last.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        while (current.Y != destination.Y)
        {
            last = await session.ExecuteAsync(new TravelCommand(current.Y < destination.Y ? Direction.South : Direction.North));
            Assert.True(last.Success, string.Join(" | ", last.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        return Assert.IsType<ActionResult>(last);
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static void DisableImperialAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
