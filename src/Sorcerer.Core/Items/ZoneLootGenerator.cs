using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Items;

public sealed record GeneratedZoneLootItem(
    ItemDefinition Definition,
    int Quantity,
    string LootKind,
    string Description);

/// <summary>
/// Rolls the ground loot for a generated zone: mostly regional curios, occasionally a useful
/// find (a gold cache, one of the region's wares, or an authored catalog item). All draws come
/// from a per-zone stable seed so loot is deterministic per world seed and varies zone to zone —
/// never from the shared session RNG, whose draws are reverted by the detached-generation commit.
/// </summary>
public static class ZoneLootGenerator
{
    public const string KindCurio = "curio";
    public const string KindGoldCache = "gold_cache";
    public const string KindRegionalWare = "regional_ware";
    public const string KindAuthored = "authored";

    public static IReadOnlyList<GeneratedZoneLootItem> Generate(
        RegionDefinition region,
        RealmProfile realm,
        ItemCatalog catalog,
        int worldSeed,
        string zoneId,
        ISet<string>? excludeUniqueIds = null)
    {
        var loot = region.GroundLoot ?? new RegionGroundLootDefinition();
        var rng = new DeterministicRng(WorldRoll.StableSeed(worldSeed, zoneId, region.Id, "ground_loot"));
        if (rng.NextInt(0, 100) < loot.EmptyChancePercent)
        {
            return Array.Empty<GeneratedZoneLootItem>();
        }

        var max = Math.Max(loot.Min, loot.Max);
        var count = loot.Min == max ? loot.Min : rng.NextInt(loot.Min, max + 1);
        if (count <= 0)
        {
            return Array.Empty<GeneratedZoneLootItem>();
        }

        var spawnedUniques = excludeUniqueIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var items = new List<GeneratedZoneLootItem>(count);
        for (var i = 0; i < count; i++)
        {
            var useful = rng.NextInt(0, 100) < loot.UsefulChancePercent
                ? GenerateUseful(region, catalog, loot, rng, spawnedUniques)
                : null;
            var chosen = useful ?? GenerateCurio(region, realm, rng);
            if (chosen.Definition.IsUnique)
            {
                spawnedUniques.Add(chosen.Definition.Id);
            }

            items.Add(chosen);
        }

        return items;
    }

    private static GeneratedZoneLootItem GenerateCurio(RegionDefinition region, RealmProfile realm, IRng rng)
    {
        var curio = CurioGenerator.Generate(region, realm, rng);
        return new GeneratedZoneLootItem(curio.ToDefinition(), Quantity: 1, KindCurio, curio.Description);
    }

    private static GeneratedZoneLootItem? GenerateUseful(
        RegionDefinition region,
        ItemCatalog catalog,
        RegionGroundLootDefinition loot,
        IRng rng,
        ISet<string> spawnedUniques)
    {
        var warePool = WarePool(region, catalog);
        // 40 gold / 40 regional ware / 20 authored; ware weight folds into the others when the
        // region has no resolvable wares. Draw order is fixed so results stay deterministic.
        var wareWeight = warePool.Count > 0 ? 40 : 0;
        var roll = rng.NextInt(0, 60 + wareWeight);
        if (roll < 40)
        {
            return GenerateGoldCache(region, catalog, loot, rng);
        }

        if (wareWeight > 0 && roll < 80)
        {
            var ware = warePool[rng.NextInt(0, warePool.Count)];
            return new GeneratedZoneLootItem(
                ware,
                Quantity: 1,
                KindRegionalWare,
                $"A {ware.Name}, dropped or abandoned somewhere in {region.Name}.");
        }

        // Region-weighted authored draw: cultural items surface where they belong, and a unique
        // already produced this run drops out of the pool so distinctive finds never recur.
        var authoredPool = catalog.Items
            .Where(item => item.Kind is not ("currency" or "key"))
            .Where(item => !(item.IsUnique && spawnedUniques.Contains(item.Id)))
            .Select(item => (Item: item, Weight: item.RegionWeight(region.Id)))
            .Where(entry => entry.Weight > 0)
            .OrderBy(entry => entry.Item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (authoredPool.Length == 0)
        {
            return null;
        }

        var total = authoredPool.Sum(entry => entry.Weight);
        var pick = rng.NextInt(0, total);
        var authored = authoredPool[^1].Item;
        foreach (var entry in authoredPool)
        {
            pick -= entry.Weight;
            if (pick < 0)
            {
                authored = entry.Item;
                break;
            }
        }

        return new GeneratedZoneLootItem(
            authored,
            Quantity: 1,
            KindAuthored,
            string.IsNullOrWhiteSpace(authored.Description)
                ? $"A {authored.Name} left behind in {region.Name}."
                : authored.Description);
    }

    private static GeneratedZoneLootItem? GenerateGoldCache(
        RegionDefinition region,
        ItemCatalog catalog,
        RegionGroundLootDefinition loot,
        IRng rng)
    {
        var gold = catalog.Find("gold");
        if (gold is null)
        {
            return null;
        }

        var goldMax = Math.Max(loot.GoldMin, loot.GoldMax);
        var quantity = Math.Max(1, loot.GoldMin == goldMax ? loot.GoldMin : rng.NextInt(loot.GoldMin, goldMax + 1));
        return new GeneratedZoneLootItem(
            gold,
            quantity,
            KindGoldCache,
            $"A small cache of coin, lost or hidden in {region.Name}.");
    }

    private static IReadOnlyList<ItemDefinition> WarePool(RegionDefinition region, ItemCatalog catalog)
    {
        if (region.Population is null)
        {
            return Array.Empty<ItemDefinition>();
        }

        return region.Population.Archetypes
            .SelectMany(archetype => archetype.Wares)
            .Select(ware => ware.Item)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(catalog.Find)
            .Where(item => item is not null && item.Kind is not ("currency" or "key"))
            .Select(item => item!)
            .ToArray();
    }
}
