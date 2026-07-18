using Sorcerer.Core;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ZoneLootGenerationTests
{
    [Fact]
    public void CuriosVaryAcrossZonesInsteadOfRepeatingOneForm()
    {
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!
            with
        { GroundLoot = new RegionGroundLootDefinition(Min: 1, Max: 1, EmptyChancePercent: 0, UsefulChancePercent: 0) };
        var realm = WorldRoll.Create(71).RealmFor(region.RealmId);
        var catalog = ItemCatalog.CreateMinimal();

        var names = Enumerable.Range(0, 12)
            .SelectMany(index => ZoneLootGenerator.Generate(region, realm, catalog, 71, $"tour:{index}"))
            .Select(item => item.Definition.Name)
            .ToArray();

        Assert.Equal(12, names.Length);
        Assert.True(names.Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 6);
        Assert.Contains(names, name => !name.EndsWith("button", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void LootIsDeterministicPerWorldSeedAndZone()
    {
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!;
        var realm = WorldRoll.Create(71).RealmFor(region.RealmId);
        var catalog = ItemCatalog.CreateMinimal();

        var first = Enumerable.Range(0, 10)
            .SelectMany(index => ZoneLootGenerator.Generate(region, realm, catalog, 71, $"tour:{index}"))
            .Select(LootFingerprint);
        var repeat = Enumerable.Range(0, 10)
            .SelectMany(index => ZoneLootGenerator.Generate(region, realm, catalog, 71, $"tour:{index}"))
            .Select(LootFingerprint);

        Assert.Equal(first, repeat);
    }

    [Fact]
    public void UsefulFindsResolveAgainstTheCatalogAndRespectGoldBounds()
    {
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!
            with
        {
            GroundLoot = new RegionGroundLootDefinition(
                    Min: 1,
                    Max: 1,
                    EmptyChancePercent: 0,
                    UsefulChancePercent: 100,
                    GoldMin: 3,
                    GoldMax: 10),
        };
        var realm = WorldRoll.Create(71).RealmFor(region.RealmId);
        var catalog = ItemCatalog.CreateMinimal();

        var items = Enumerable.Range(0, 20)
            .SelectMany(index => ZoneLootGenerator.Generate(region, realm, catalog, 71, $"tour:{index}"))
            .ToArray();

        Assert.Equal(20, items.Length);
        Assert.All(items, item => Assert.NotEqual(ZoneLootGenerator.KindCurio, item.LootKind));
        Assert.All(items, item => Assert.NotNull(catalog.Find(item.Definition.Id)));
        Assert.All(items, item => Assert.True(item.Definition.Kind != "key"));
        Assert.All(
            items.Where(item => item.LootKind == ZoneLootGenerator.KindGoldCache),
            item => Assert.InRange(item.Quantity, 3, 10));
        Assert.True(items.Select(item => item.LootKind).Distinct().Count() >= 2);
    }

    [Fact]
    public void RegionRichnessControlsCountAndAbsentGroundLootUsesDefaults()
    {
        var registry = RegionCatalog.LoadDefault();
        var barren = registry.Region("hollowmere_margin")!
            with
        { GroundLoot = new RegionGroundLootDefinition(Min: 1, Max: 3, EmptyChancePercent: 100) };
        var defaulted = registry.Region("hollowmere_margin")! with { GroundLoot = null };
        var realm = WorldRoll.Create(71).RealmFor(barren.RealmId);
        var catalog = ItemCatalog.CreateMinimal();

        foreach (var index in Enumerable.Range(0, 10))
        {
            Assert.Empty(ZoneLootGenerator.Generate(barren, realm, catalog, 71, $"tour:{index}"));
            Assert.InRange(
                ZoneLootGenerator.Generate(defaulted, realm, catalog, 71, $"tour:{index}").Count,
                0,
                2);
        }
    }

    [Fact]
    public void AuthoredRegionsParseGroundLootAndScaleByFlavor()
    {
        var catalog = RegionCatalog.LoadDefault();

        var imperial = catalog.Region("imperial_encounter")!.GroundLoot;
        Assert.NotNull(imperial);
        Assert.Equal(0, imperial!.Min);
        Assert.Equal(1, imperial.Max);
        Assert.Equal(45, imperial.EmptyChancePercent);
        Assert.Equal(15, imperial.UsefulChancePercent);

        var folk = catalog.Region("hollowmere_margin")!.GroundLoot;
        Assert.NotNull(folk);
        Assert.Equal(1, folk!.Min);
        Assert.Equal(3, folk.Max);

        Assert.Null(catalog.Region("wild_border")!.GroundLoot);
        Assert.Null(catalog.Region("monteary_grasslands")!.GroundLoot);
    }

    [Fact]
    public async Task TravelPlacesVariedLootAtOpenVariedPositionsDeterministically()
    {
        var first = CreateSession(seed: 71);
        var repeat = CreateSession(seed: 71);
        foreach (var session in new[] { first, repeat })
        {
            await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));
            await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));
            await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));
            await session.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));
        }

        var firstItems = GeneratedZoneItems(first);
        var repeatItems = GeneratedZoneItems(repeat);

        Assert.NotEmpty(firstItems);
        Assert.Equal(
            firstItems.Select(ItemFingerprint).OrderBy(value => value, StringComparer.Ordinal),
            repeatItems.Select(ItemFingerprint).OrderBy(value => value, StringComparer.Ordinal));

        var state = first.Engine.State;
        var oldHardcodedTile = new GridPoint((state.Width / 2) + 2, state.Height / 2);
        var positions = firstItems.Select(item => item.Entity.Get<PositionComponent>().Position).ToArray();
        Assert.All(positions, position =>
        {
            Assert.InRange(position.X, 1, state.Width - 2);
            Assert.InRange(position.Y, 1, state.Height - 2);
        });
        Assert.Contains(positions, position => position != oldHardcodedTile);
        Assert.True(positions.Distinct().Count() > 1);
        Assert.True(firstItems.Select(item => item.Entity.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);
    }

    private static GameSession CreateSession(int seed) =>
        GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);

    private static IReadOnlyList<(string ZoneId, Entity Entity)> GeneratedZoneItems(GameSession session)
    {
        var state = session.Engine.State;
        var zones = state.Zones
            .Select(pair => (pair.Key, pair.Value.Entities.Values.AsEnumerable()))
            .Append((state.CurrentZoneId, state.Entities.Values.AsEnumerable()));
        return zones
            .SelectMany(zone => zone.Item2
                .Where(entity => entity.Id.Value.StartsWith("zone_item", StringComparison.OrdinalIgnoreCase))
                .Select(entity => (zone.Item1, entity)))
            .ToArray();
    }

    private static string LootFingerprint(GeneratedZoneLootItem item) =>
        $"{item.Definition.Id}|{item.Definition.Name}|{item.Quantity}|{item.LootKind}";

    private static string ItemFingerprint((string ZoneId, Entity Entity) item) =>
        $"{item.ZoneId}|{item.Entity.Name}|{item.Entity.Get<PositionComponent>().Position.X},{item.Entity.Get<PositionComponent>().Position.Y}";
}
