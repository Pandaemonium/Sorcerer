using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GeneratedJourneyTests
{
    [Fact]
    public void ShippedAndEmbeddedQuestTemplateCatalogsMatch()
    {
        var loose = QuestTemplateCatalog.LoadDefault();
        var embedded = QuestTemplateCatalog.LoadBuiltIn();

        Assert.Equal(4, loose.Templates.Count);
        Assert.Equal(10, loose.Handoffs.Count);
        Assert.Equal(
            loose.Templates.Select(template => template.Id).OrderBy(id => id).ToArray(),
            embedded.Templates.Select(template => template.Id).OrderBy(id => id).ToArray());
        Assert.All(loose.Templates, template =>
        {
            Assert.Equal("item", template.RealizationKind);
            Assert.Contains("{landmark}", template.ClaimPattern, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("{destinationZone}", template.ClaimPattern, StringComparison.OrdinalIgnoreCase);
        });
        Assert.Equal(
            loose.Handoffs.Select(template => template.Id).OrderBy(id => id).ToArray(),
            embedded.Handoffs.Select(template => template.Id).OrderBy(id => id).ToArray());
        Assert.Equal(
            new[] { "delivery", "escort", "fetch", "folk_service", "meet", "rumor_verification", "social_leverage", "threat" },
            loose.Handoffs.Select(template => template.ObjectiveKind).Distinct().OrderBy(kind => kind).ToArray());
        Assert.Equal(3, loose.Handoffs.Count(template => template.OpeningHandoff));
        Assert.All(loose.Handoffs.Where(template => !template.OpeningHandoff), template => Assert.True(template.ReturnToGiver));
    }

    [Fact]
    public async Task PlayerCanDiscoverAndRealizeGeneratedJourneyFromVisibleTextAlone()
    {
        const int seed = 7;
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);
        DisableAi(session);

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var giver = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Has<ClaimSourceComponent>());
        var want = giver.Get<WantComponent>();
        var talk = await session.ExecuteAsync(new TalkCommand(giver.Name));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.SourceSpeakerId == giver.Id.Value
            && promise.RealizationKind == "item");
        var journalBefore = await session.ExecuteAsync(new JournalCommand());

        Assert.True(talk.Success);
        Assert.Contains(talk.Messages, message =>
            message.Contains($"{giver.Name} says", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("generated_journey", want.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("drowned memory mill", want.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bound", promise.Status);
        Assert.True(promise.PlayerVisible);
        Assert.NotNull(promise.ClaimedPlace);
        Assert.Contains(journalBefore.Messages, message =>
            message.StartsWith("Objective:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("zone", StringComparison.OrdinalIgnoreCase)
            && message.Contains("drowned memory mill", StringComparison.OrdinalIgnoreCase));

        var destination = ParseZoneId(promise.ClaimedPlace!);
        var current = ParseZoneId(session.Engine.State.CurrentZoneId);
        while (current.X != destination.X)
        {
            var direction = current.X < destination.X ? Direction.East : Direction.West;
            var travel = await session.ExecuteAsync(new TravelCommand(direction));
            Assert.True(travel.Success);
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        while (current.Y != destination.Y)
        {
            var direction = current.Y < destination.Y ? Direction.South : Direction.North;
            var travel = await session.ExecuteAsync(new TravelCommand(direction));
            Assert.True(travel.Success);
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        var realized = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id);
        var evidence = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id, StringComparer.OrdinalIgnoreCase)
            && entity.Has<ItemComponent>());
        var journalAfter = await session.ExecuteAsync(new JournalCommand());

        Assert.Equal("realized", realized.Status);
        Assert.Equal(promise.ClaimedPlace, realized.RealizedIn);
        Assert.Contains("drowned memory mill", evidence.Name, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(journalAfter.Messages, message =>
            message.StartsWith("Objective:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("realized", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "item"
            && record.Text.Contains(evidence.Name, StringComparison.OrdinalIgnoreCase));

        var pickup = await session.ExecuteAsync(new PickupCommand(evidence.Name));
        Assert.True(pickup.Success, string.Join(" | ", pickup.Messages));
        Assert.Equal(
            "ready_to_return",
            session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id).Status);
        Assert.Contains(pickup.Messages, message =>
            message.Contains($"return to {giver.Name}", StringComparison.OrdinalIgnoreCase));

        await TravelTo(session, "1,0");
        var restoredGiver = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Id == giver.Id);
        var giverPosition = restoredGiver.Get<PositionComponent>().Position;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(giverPosition.Translate(-1, 0)));
        var goldBefore = session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items
            .TryGetValue("gold", out var coins) ? coins : 0;
        var returned = await session.ExecuteAsync(new TalkCommand(restoredGiver.Name));

        Assert.True(returned.Success, string.Join(" | ", returned.Messages));
        Assert.Equal(
            "cleared",
            session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id).Status);
        Assert.Equal("satisfied", restoredGiver.Get<WantComponent>().Status);
        Assert.DoesNotContain(
            session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items,
            pair => pair.Key.Contains(evidence.Name, StringComparison.OrdinalIgnoreCase) && pair.Value > 0);
        Assert.Contains(
            restoredGiver.Get<InventoryComponent>().Items,
            pair => pair.Key.Contains(evidence.Name, StringComparison.OrdinalIgnoreCase) && pair.Value == 1);
        Assert.Contains(returned.Messages, message =>
            message.Contains("Objective complete", StringComparison.OrdinalIgnoreCase)
            && message.Contains("gold into your hand", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(returned.Messages, message =>
            message.EndsWith("will remember this.", StringComparison.OrdinalIgnoreCase));

        // Concrete reciprocity: the giver pays for the work, visibly (4..12 gold by salience).
        var goldAfter = session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items
            .TryGetValue("gold", out var paid) ? paid : 0;
        Assert.InRange(goldAfter - goldBefore, 4, 12);
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static async Task TravelTo(GameSession session, string zoneId)
    {
        var destination = ParseZoneId(zoneId);
        var current = ParseZoneId(session.Engine.State.CurrentZoneId);
        while (current.X != destination.X)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(current.X < destination.X ? Direction.East : Direction.West));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        while (current.Y != destination.Y)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(current.Y < destination.Y ? Direction.South : Direction.North));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }
    }

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
