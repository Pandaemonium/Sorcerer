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
        Assert.Equal(
            loose.Templates.Select(template => template.Id).OrderBy(id => id).ToArray(),
            embedded.Templates.Select(template => template.Id).OrderBy(id => id).ToArray());
        Assert.All(loose.Templates, template =>
        {
            Assert.Equal("item", template.RealizationKind);
            Assert.Contains("{landmark}", template.ClaimPattern, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("{destinationZone}", template.ClaimPattern, StringComparison.OrdinalIgnoreCase);
        });
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
        var examine = await session.ExecuteAsync(new ExamineCommand(giver.Name));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.SourceSpeakerId == giver.Id.Value
            && promise.RealizationKind == "item");
        var journalBefore = await session.ExecuteAsync(new JournalCommand());

        Assert.True(examine.Success);
        Assert.Contains("generated_journey", want.Tags, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("drowned memory mill", want.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("bound", promise.Status);
        Assert.True(promise.PlayerVisible);
        Assert.NotNull(promise.ClaimedPlace);
        Assert.Contains(journalBefore.Messages, message =>
            message.StartsWith("Lead:", StringComparison.OrdinalIgnoreCase)
            && message.Contains(promise.ClaimedPlace!, StringComparison.OrdinalIgnoreCase)
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
            message.StartsWith("Lead:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("realized", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "item"
            && record.Text.Contains(evidence.Name, StringComparison.OrdinalIgnoreCase));
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
