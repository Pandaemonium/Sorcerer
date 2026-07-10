using Sorcerer.Core;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class RegionCatalogTests
{
    [Fact]
    public void DefaultCatalogCoversEveryRolledRealmWithUsableRegionalVoice()
    {
        var catalog = RegionCatalog.LoadDefault();
        var world = WorldRoll.Create(7);

        Assert.True(catalog.Regions.Count >= 13);
        Assert.True(catalog.Traditions.Count >= 10);
        foreach (var region in catalog.Regions)
        {
            Assert.NotNull(catalog.Tradition(region.TraditionId));
            Assert.DoesNotContain(
                "unmapped",
                world.RealmFor(region.RealmId).Status,
                StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(region.VoiceSummary));
            Assert.NotEmpty(region.AmbientLines ?? Array.Empty<string>());
            Assert.NotEmpty(region.Affordances ?? Array.Empty<RegionAffordanceCard>());
            Assert.NotNull(region.Placement);
        }

        foreach (var realm in world.Realms)
        {
            Assert.Contains(catalog.Regions, region =>
                region.RealmId.Equals(realm.RealmId, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void BuiltInCatalogKeepsTheRegionalCorpusAvailableWithoutLooseContentFiles()
    {
        var catalog = RegionCatalog.LoadBuiltIn();

        Assert.Equal(14, catalog.Regions.Count);
        Assert.Equal(13, catalog.Traditions.Count);
        Assert.NotNull(catalog.Region("rentacosta_harbor"));
        Assert.NotNull(catalog.Tradition("sound_ink"));
        Assert.NotNull(catalog.Region("hollowmere_margin")?.Props);
        Assert.NotNull(catalog.Region("hollowmere_margin")?.Population);
        Assert.NotNull(catalog.Region("hollowmere_margin")?.Interiors);
    }

    [Fact]
    public void DataPlacedRegionsAreDeterministicAndReachableOnTheZoneGrid()
    {
        var first = GenerationForSeed(90210);
        var repeat = GenerationForSeed(90210);
        var mapped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var y = -20; y <= 20; y++)
        {
            for (var x = -20; x <= 20; x++)
            {
                var zoneId = $"{x},{y}";
                var firstRegion = first.RegionForZone(zoneId);
                var repeatRegion = repeat.RegionForZone(zoneId);
                Assert.Equal(firstRegion.Id, repeatRegion.Id);
                mapped.Add(firstRegion.Id);
            }
        }

        var catalog = RegionCatalog.LoadDefault();
        Assert.All(catalog.Regions, region => Assert.Contains(region.Id, mapped));
    }

    [Fact]
    public void EachSeedRollsExactlyOneFreeOldKingdomAndThreeConqueredKingdoms()
    {
        foreach (var seed in Enumerable.Range(1, 20))
        {
            var world = WorldRoll.Create(seed);
            var oldKingdoms = new[] { "stalnaz", "brall", "ryolan", "vint" }
                .Select(world.RealmFor)
                .ToArray();

            Assert.Single(oldKingdoms, realm => realm.Status == "rival");
            Assert.Equal(3, oldKingdoms.Count(realm => realm.Tags.Contains("conquered")));
            Assert.All(oldKingdoms, realm => Assert.NotEqual("unmapped", realm.Status));
        }
    }

    [Fact]
    public async Task TravelAcrossARealmBorderNamesBothSidesThroughTheSharedActionResult()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 7);

        var travel = await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));

        Assert.True(travel.Success);
        var message = Assert.Single(travel.Messages, item =>
            item.Contains("You travel east into", StringComparison.OrdinalIgnoreCase)
            && item.Contains("Hollowmere Margin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Grand Empire of Vigovia", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Hollowmere", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "travel"
            && Equals(delta.Details["fromRegionId"], "imperial_encounter")
            && Equals(delta.Details["regionId"], "hollowmere_margin"));
    }

    [Fact]
    public async Task CapitalFootprintKeepsTheEmperorInOneThroneZone()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 7);

        await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));
        await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));

        Assert.Equal("2,0", session.Engine.State.CurrentZoneId);
        Assert.DoesNotContain(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Emperor Odran of Vigovia");

        await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));

        Assert.Equal("3,0", session.Engine.State.CurrentZoneId);
        Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Emperor Odran of Vigovia");

        await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));

        Assert.Equal("4,0", session.Engine.State.CurrentZoneId);
        Assert.DoesNotContain(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Emperor Odran of Vigovia");
        Assert.Single(session.Engine.State.Zones["3,0"].Entities.Values, entity =>
            entity.Name == "Emperor Odran of Vigovia");
    }

    private static GenerationSystem GenerationForSeed(int seed)
    {
        var state = new GameState(40, 30)
        {
            Seed = seed,
            RegionId = "imperial_encounter",
            CurrentZoneId = "0,0",
        };
        return new GenerationSystem(
            state,
            ItemCatalog.CreateMinimal(),
            LoreCatalog.CreateMinimal(),
            regions: RegionCatalog.LoadDefault());
    }
}
