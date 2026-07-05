using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class LoreLayerTests
{
    [Fact]
    public void LoreRouterGatesSectionsAndDropsDraftText()
    {
        const string markdown = """
            ```lore
            id: test_reed_lore
            title: Test Reed Lore
            subjects: hollowmere, water
            triggers: magic_context, background
            draft: false
            ```

            # Test Reed Lore

            ## Level 0

            Common water warnings reach everyone.

            ## Level 1

            Public water remembers names.

            ## Level 2

            Locked reed roads open under the mud.

            ## Draft

            Draft-only spoiler.
            """;

        var catalog = new LoreCatalog(new[] { LoreCardLoader.LoadMarkdown("test_reed_lore", markdown) });

        var commonAccess = Assert.Single(LoreRouter.Select(
            catalog,
            new LoreQuery(new[] { "hollowmere" }, new[] { "magic_context" }, AccessLevel: 0)));
        var lowAccess = Assert.Single(LoreRouter.Select(
            catalog,
            new LoreQuery(new[] { "hollowmere" }, new[] { "magic_context" }, AccessLevel: 1)));
        var highAccess = Assert.Single(LoreRouter.Select(
            catalog,
            new LoreQuery(new[] { "hollowmere" }, new[] { "magic_context" }, AccessLevel: 2)));

        Assert.Contains("Common water", commonAccess.Body);
        Assert.DoesNotContain("Public water", commonAccess.Body);
        Assert.DoesNotContain("Locked reed", commonAccess.Body);
        Assert.DoesNotContain("Draft-only", commonAccess.Body);
        Assert.Contains("Common water", lowAccess.Body);
        Assert.Contains("Public water", lowAccess.Body);
        Assert.DoesNotContain("Locked reed", lowAccess.Body);
        Assert.DoesNotContain("Draft-only", lowAccess.Body);
        Assert.Contains("Locked reed", highAccess.Body);
        Assert.DoesNotContain("Draft-only", highAccess.Body);
    }

    [Fact]
    public void LoreRouterUsesSubjectSpecificAccessWithoutLiftingUnrelatedCards()
    {
        var catalog = new LoreCatalog(new[]
        {
            new LoreCard(
                "water_secret",
                "Water Secret",
                new[] { "water" },
                new[] { "magic_context" },
                new[]
                {
                    new LoreSection(1, "Public water custom."),
                    new LoreSection(4, "Secret water-name rite."),
                }),
            new LoreCard(
                "law_secret",
                "Law Secret",
                new[] { "law" },
                new[] { "magic_context" },
                new[]
                {
                    new LoreSection(1, "Public law custom."),
                    new LoreSection(4, "Secret censorate exception."),
                }),
        });

        var routed = LoreRouter.Select(
            catalog,
            new LoreQuery(
                new[] { "water" },
                new[] { "magic_context" },
                AccessLevel: 1,
                Limit: 5,
                SubjectAccessLevels: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["water"] = 4,
                }));

        Assert.Contains(routed, card =>
            card.Id == "water_secret"
            && card.Body.Contains("Secret water-name rite.", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(routed, card =>
            card.Body.Contains("Secret censorate exception.", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MagicContextCarriesRegionLoreAfterTravel()
    {
        var session = CreateSession(seed: 7);

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var context = MagicContext(session);
        var atlas = await session.ExecuteAsync(new AtlasCommand());

        Assert.NotNull(context.Lore);
        Assert.Contains(context.Lore!, lore =>
            lore.Id == "hollowmere_reed_memory"
            && lore.Body.Contains("Hollowmere keeps names", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(context.Lore!, lore =>
            lore.Body.Contains("wet hands", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(atlas.Messages, message =>
            message.Contains("Local lore - Hollowmere Reed-Memory", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BackgroundDetailUsesRoutedLoreAndShowsOnSubsequentExamine()
    {
        var session = CreateSession(seed: 7);
        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var feature = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<FixtureComponent>(out var fixture)
            && fixture.FixtureType.Equals("zone_feature", StringComparison.OrdinalIgnoreCase));
        var featurePosition = feature.Get<PositionComponent>().Position;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(featurePosition.X - 1, featurePosition.Y)));

        var firstExamine = await session.ExecuteAsync(new ExamineCommand(feature.Name));
        await session.ExecuteAsync(new WaitCommand());
        var secondExamine = await session.ExecuteAsync(new ExamineCommand(feature.Name));

        Assert.True(firstExamine.Success);
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.AttachedTo == feature.Id.Value
            && record.Text.Contains("Hollowmere keeps names", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(secondExamine.Messages, message =>
            message.Contains("Known detail:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("Hollowmere", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ZoneEntryRumorReflectsCurrentLegend()
    {
        var session = CreateSession(seed: 7);

        await session.ExecuteAsync(new CastCommand("a plain blue fire"));
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Contains(session.Engine.State.Legend.Tags, tag =>
            tag.ActorSoulId == "player_soul"
            && tag.Tag == "uncanny");
        Assert.Contains(travel.Messages, message =>
            message.Contains("rumor", StringComparison.OrdinalIgnoreCase)
            && message.Contains("uncanny", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "zone_entry_rumor"
            && delta.Details.TryGetValue("legendTag", out var tag)
            && tag?.ToString() == "uncanny");
    }

    private static GameSession CreateSession(int seed)
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            session.Engine.EntityById(id)!.Set(new ControllerComponent(ControllerKind.None));
        }

        return session;
    }

    private static MagicContextView MagicContext(GameSession session) =>
        session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));
}
