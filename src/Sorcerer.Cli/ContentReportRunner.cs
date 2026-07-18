using System.Text;
using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;

namespace Sorcerer.Cli;

/// <summary>
/// WP0 baseline instrument (docs/CONTENT_SPRINT_PLAN.md). Read-only: samples the first-hour
/// reference transect (Marble Containment Yard -> Hollowmere Margin -> Brall) deterministically over
/// many seeds and reports the exact content each seed produces plus cross-seed diversity metrics.
/// It drives the real <see cref="GameSession"/> generation path with the mock provider so what it
/// measures is exactly what a player would walk through. Later claims of "shockingly lush" compare
/// against this committed baseline rather than memory.
/// </summary>
public static class ContentReportRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(int seeds, int startSeed, string? outputPath, bool json)
    {
        var reports = new List<SeedReport>();
        for (var offset = 0; offset < seeds; offset++)
        {
            reports.Add(await SampleSeedAsync(startSeed + offset));
        }

        var summary = Summarize(reports);
        var document = new ContentReportDocument(
            DateTimeOffset.UtcNow,
            startSeed,
            seeds,
            summary,
            reports);

        var payload = JsonSerializer.Serialize(document, JsonOptions);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await File.WriteAllTextAsync(outputPath, payload);
        }

        if (json)
        {
            Console.WriteLine(payload);
        }
        else
        {
            Console.WriteLine(RenderHuman(document));
            if (!string.IsNullOrWhiteSpace(outputPath))
            {
                Console.WriteLine();
                Console.WriteLine($"Raw report written to {outputPath}.");
            }
        }

        return 0;
    }

    private static async Task<SeedReport> SampleSeedAsync(int seed)
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: seed);
        DisableWandering(session);

        var regions = RegionCatalog.LoadDefault();
        var brall = regions.Region("brall_whaleholds");
        var route = BuildRoute(seed, brall);

        var zones = new List<ZoneReport>
        {
            CaptureZone(session),
        };
        foreach (var target in route)
        {
            if (await TravelToAsync(session, target))
            {
                zones.Add(CaptureZone(session));
            }
        }

        return new SeedReport(seed, zones);
    }

    /// <summary>
    /// A bounded first-hour route: the three Hollowmere-margin zones next to the yard, then a walk
    /// toward Brall's seed-placed anchor. Every zone stepped through is captured (the walk itself is
    /// the transect), and duplicates collapse so the route stays short.
    /// </summary>
    private static IReadOnlyList<(int X, int Y)> BuildRoute(int seed, RegionDefinition? brall)
    {
        var waypoints = new List<(int X, int Y)>
        {
            (1, 0),
            (1, 1),
            (0, 1),
        };
        if (brall is not null)
        {
            waypoints.Add(WorldPlaceGraph.AnchorFor(seed, brall));
        }

        return waypoints;
    }

    private static async Task<bool> TravelToAsync(GameSession session, (int X, int Y) destination)
    {
        var current = ParseZoneId(session.Engine.State.CurrentZoneId);
        var guard = 0;
        while ((current.X != destination.X || current.Y != destination.Y) && guard++ < 40)
        {
            Direction direction;
            if (current.X != destination.X)
            {
                direction = current.X < destination.X ? Direction.East : Direction.West;
            }
            else
            {
                direction = current.Y < destination.Y ? Direction.South : Direction.North;
            }

            var travel = await session.ExecuteAsync(new TravelCommand(direction));
            if (!travel.Success)
            {
                return false;
            }

            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        return true;
    }

    private static ZoneReport CaptureZone(GameSession session)
    {
        var engine = session.Engine;
        var state = engine.State;
        var place = engine.CurrentPlace;
        var controlledId = state.ControlledEntityId.Value;

        var items = new List<string>();
        var commodityItems = new List<string>();
        var props = new List<string>();
        var documents = new List<string>();
        var residents = new List<string>();
        var archetypes = new List<string>();
        var encounterCasts = new List<string>();
        var creatures = new List<string>();
        var services = new List<string>();
        var merchants = new List<string>();
        var wants = 0;
        var claimSources = 0;
        var charterOpportunities = new List<string>();
        var contextEntities = 0;
        var unresolved = new List<string>();

        foreach (var entity in state.Entities.Values)
        {
            if (entity.Id.Value.Equals(controlledId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var tags = entity.TryGet<TagsComponent>(out var tagsComponent)
                ? tagsComponent.Tags
                : Array.Empty<string>();
            var name = entity.Name?.Trim() ?? "";
            var hasName = !string.IsNullOrWhiteSpace(name);

            if (entity.TryGet<ItemComponent>(out var item))
            {
                if (!hasName)
                {
                    unresolved.Add($"nameless item {entity.Id.Value}");
                }
                else if (item.StackPolicy.Equals("commodity", StringComparison.OrdinalIgnoreCase))
                {
                    commodityItems.Add(name);
                }
                else
                {
                    items.Add(name);
                }

                if (HasCharterAffinity(name, tags))
                {
                    charterOpportunities.Add(name);
                }

                continue;
            }

            var readable = entity.TryGet<ReadableComponent>(out _);
            var claimSource = entity.TryGet<ClaimSourceComponent>(out var claims);
            if (claimSource)
            {
                claimSources++;
            }

            if (entity.TryGet<FixtureComponent>(out _)
                || (!entity.TryGet<ActorComponent>(out _) && (readable || claimSource)))
            {
                contextEntities++;
                if (!hasName)
                {
                    unresolved.Add($"nameless prop {entity.Id.Value}");
                    continue;
                }

                if (readable || claimSource)
                {
                    documents.Add(name);
                }
                else
                {
                    props.Add(name);
                }

                if (HasCharterAffinity(name, tags))
                {
                    charterOpportunities.Add(name);
                }

                continue;
            }

            if (entity.TryGet<ActorComponent>(out var actor))
            {
                contextEntities++;
                if (!hasName)
                {
                    unresolved.Add($"nameless actor {entity.Id.Value}");
                }

                var isEncounter = tags.Contains("encounter_cast", StringComparer.OrdinalIgnoreCase);
                var isCreature = tags.Contains("creature", StringComparer.OrdinalIgnoreCase)
                    || tags.Contains("beast", StringComparer.OrdinalIgnoreCase)
                    || tags.Contains("pet", StringComparer.OrdinalIgnoreCase);
                if (isEncounter)
                {
                    encounterCasts.Add(name);
                }
                else if (isCreature)
                {
                    creatures.Add(name);
                }
                else if (tags.Contains("resident", StringComparer.OrdinalIgnoreCase))
                {
                    residents.Add(name);
                }

                var archetype = tags.FirstOrDefault(tag =>
                    tag.StartsWith("archetype_", StringComparison.OrdinalIgnoreCase))
                    ?? tags.FirstOrDefault(tag => tag.EndsWith("_clerk", StringComparison.OrdinalIgnoreCase)
                        || tag.EndsWith("_keeper", StringComparison.OrdinalIgnoreCase));
                if (archetype is not null)
                {
                    archetypes.Add(archetype);
                }

                if (entity.TryGet<WantComponent>(out _))
                {
                    wants++;
                }

                if (entity.TryGet<ServiceComponent>(out var service))
                {
                    foreach (var offer in service.Offers)
                    {
                        services.Add(offer.Name);
                        if (HasCharterAffinity(offer.Name, offer.Tags ?? Array.Empty<string>()))
                        {
                            charterOpportunities.Add(offer.Name);
                        }
                    }
                }

                if (entity.TryGet<MerchantComponent>(out var merchant) && merchant.Wares.Count > 0)
                {
                    merchants.Add(name);
                    // Stock is part of first-hour item variety (the diversity gate combines loot,
                    // stock, carried gear, documents, and encounter objects).
                    foreach (var ware in merchant.Wares.Where(pair => pair.Value > 0).Select(pair => pair.Key))
                    {
                        items.Add(ware);
                    }
                }

                continue;
            }

            if (entity.TryGet<ServiceComponent>(out var standaloneService))
            {
                foreach (var offer in standaloneService.Offers)
                {
                    services.Add(offer.Name);
                }
            }
        }

        var residentCount = residents.Count + creatures.Count;
        var density = residentCount == 0 && encounterCasts.Count == 0
            ? "empty"
            : contextEntities >= 10
                ? "dense"
                : "moderate";

        return new ZoneReport(
            state.CurrentZoneId,
            state.RegionId,
            place.Kind,
            place.Settlement?.Name,
            place.District?.Name,
            density,
            contextEntities,
            Distinct(items),
            Distinct(commodityItems),
            Distinct(props),
            Distinct(documents),
            Distinct(residents),
            Distinct(creatures),
            Distinct(archetypes),
            Distinct(encounterCasts),
            Distinct(services),
            Distinct(merchants),
            Distinct(charterOpportunities),
            wants,
            claimSources,
            unresolved);
    }

    private static bool HasCharterAffinity(string name, IReadOnlyList<string> tags)
    {
        if (tags.Any(tag => tag.Contains("charter", StringComparison.OrdinalIgnoreCase)
            || tag.Contains("manual", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return name.Contains("charter", StringComparison.OrdinalIgnoreCase)
            || name.Contains("manual", StringComparison.OrdinalIgnoreCase)
            || name.Contains("notice", StringComparison.OrdinalIgnoreCase);
    }

    private static ContentReportSummary Summarize(IReadOnlyList<SeedReport> reports)
    {
        var perSeedItemSets = reports.ToDictionary(
            report => report.Seed,
            report => report.Zones
                .SelectMany(zone => zone.Items)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray());

        var itemNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var propNameCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var report in reports)
        {
            foreach (var name in report.Zones.SelectMany(zone => zone.Items).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                itemNameCounts[name] = itemNameCounts.GetValueOrDefault(name) + 1;
            }

            foreach (var name in report.Zones.SelectMany(zone => zone.Props).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                propNameCounts[name] = propNameCounts.GetValueOrDefault(name) + 1;
            }
        }

        var overlaps = new List<double>();
        var seedList = reports.Select(report => report.Seed).ToArray();
        for (var i = 0; i < seedList.Length; i++)
        {
            for (var j = i + 1; j < seedList.Length; j++)
            {
                overlaps.Add(JaccardOverlap(perSeedItemSets[seedList[i]], perSeedItemSets[seedList[j]]));
            }
        }

        var totalZones = reports.Sum(report => report.Zones.Count);
        var emptyZones = reports.Sum(report => report.Zones.Count(zone => zone.Density == "empty"));
        var denseZones = reports.Sum(report => report.Zones.Count(zone => zone.Density == "dense"));

        return new ContentReportSummary(
            DistinctCount(reports, zone => zone.Items),
            DistinctCount(reports, zone => zone.CommodityItems),
            DistinctCount(reports, zone => zone.Props),
            DistinctCount(reports, zone => zone.Documents),
            DistinctCount(reports, zone => zone.Residents),
            DistinctCount(reports, zone => zone.Creatures),
            DistinctCount(reports, zone => zone.EncounterCasts),
            DistinctCount(reports, zone => zone.Services),
            reports.Average(report => report.Zones
                .SelectMany(zone => zone.Items)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count()),
            overlaps.Count == 0 ? 0 : overlaps.Average(),
            overlaps.Count == 0 ? 0 : overlaps.Max(),
            totalZones,
            emptyZones,
            denseZones,
            reports.Average(report => report.Zones.Average(zone => (double)zone.ContextEntities)),
            reports.Sum(report => report.Zones.Sum(zone => zone.Unresolved.Count)),
            TopRepeats(itemNameCounts),
            TopRepeats(propNameCounts));
    }

    private static IReadOnlyList<NameCount> TopRepeats(Dictionary<string, int> counts) =>
        counts
            .Where(pair => pair.Value > 1)
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(15)
            .Select(pair => new NameCount(pair.Key, pair.Value))
            .ToArray();

    private static int DistinctCount(IReadOnlyList<SeedReport> reports, Func<ZoneReport, IEnumerable<string>> selector) =>
        reports
            .SelectMany(report => report.Zones)
            .SelectMany(selector)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

    private static double JaccardOverlap(IReadOnlyCollection<string> a, IReadOnlyCollection<string> b)
    {
        if (a.Count == 0 && b.Count == 0)
        {
            return 0;
        }

        var setA = new HashSet<string>(a, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b, StringComparer.OrdinalIgnoreCase);
        var intersection = setA.Count(item => setB.Contains(item));
        var union = new HashSet<string>(setA, StringComparer.OrdinalIgnoreCase);
        union.UnionWith(setB);
        return union.Count == 0 ? 0 : (double)intersection / union.Count;
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
        values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();

    private static string RenderHuman(ContentReportDocument document)
    {
        var summary = document.Summary;
        var builder = new StringBuilder();
        builder.AppendLine($"Content report: seeds {document.StartSeed}..{document.StartSeed + document.SeedCount - 1} ({document.SeedCount} seeds), {summary.TotalZones} zones sampled.");
        builder.AppendLine();
        builder.AppendLine("Distinct across all sampled seeds:");
        builder.AppendLine($"  distinctive items {summary.DistinctItems}, commodity items {summary.DistinctCommodityItems}");
        builder.AppendLine($"  props {summary.DistinctProps}, documents {summary.DistinctDocuments}");
        builder.AppendLine($"  residents {summary.DistinctResidents}, creatures {summary.DistinctCreatures}, encounter casts {summary.DistinctEncounterCasts}");
        builder.AppendLine($"  services {summary.DistinctServices}");
        builder.AppendLine();
        builder.AppendLine("Freshness:");
        builder.AppendLine($"  mean distinctive items per seed: {summary.MeanDistinctiveItemsPerSeed:0.0}");
        builder.AppendLine($"  pairwise item-set overlap: mean {summary.MeanPairwiseItemOverlap:0.00}, max {summary.MaxPairwiseItemOverlap:0.00}");
        builder.AppendLine($"  rhythm: {summary.EmptyZones} empty / {summary.DenseZones} dense of {summary.TotalZones} zones");
        builder.AppendLine($"  mean resolver-context entities per zone: {summary.MeanContextEntitiesPerZone:0.0}");
        builder.AppendLine($"  unresolved references: {summary.UnresolvedReferences}");
        builder.AppendLine();
        if (summary.MostRepeatedItems.Count > 0)
        {
            builder.AppendLine("Most-repeated distinctive item names (seeds containing):");
            foreach (var repeat in summary.MostRepeatedItems)
            {
                builder.AppendLine($"  {repeat.Count,3}x  {repeat.Name}");
            }

            builder.AppendLine();
        }

        if (summary.MostRepeatedProps.Count > 0)
        {
            builder.AppendLine("Most-repeated prop names (seeds containing):");
            foreach (var repeat in summary.MostRepeatedProps)
            {
                builder.AppendLine($"  {repeat.Count,3}x  {repeat.Name}");
            }
        }

        return builder.ToString();
    }

    private static void DisableWandering(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }
}

public sealed record ContentReportDocument(
    DateTimeOffset GeneratedAt,
    int StartSeed,
    int SeedCount,
    ContentReportSummary Summary,
    IReadOnlyList<SeedReport> Seeds);

public sealed record ContentReportSummary(
    int DistinctItems,
    int DistinctCommodityItems,
    int DistinctProps,
    int DistinctDocuments,
    int DistinctResidents,
    int DistinctCreatures,
    int DistinctEncounterCasts,
    int DistinctServices,
    double MeanDistinctiveItemsPerSeed,
    double MeanPairwiseItemOverlap,
    double MaxPairwiseItemOverlap,
    int TotalZones,
    int EmptyZones,
    int DenseZones,
    double MeanContextEntitiesPerZone,
    int UnresolvedReferences,
    IReadOnlyList<NameCount> MostRepeatedItems,
    IReadOnlyList<NameCount> MostRepeatedProps);

public sealed record NameCount(string Name, int Count);

public sealed record SeedReport(int Seed, IReadOnlyList<ZoneReport> Zones);

public sealed record ZoneReport(
    string ZoneId,
    string RegionId,
    string PlaceKind,
    string? SettlementName,
    string? DistrictName,
    string Density,
    int ContextEntities,
    IReadOnlyList<string> Items,
    IReadOnlyList<string> CommodityItems,
    IReadOnlyList<string> Props,
    IReadOnlyList<string> Documents,
    IReadOnlyList<string> Residents,
    IReadOnlyList<string> Creatures,
    IReadOnlyList<string> Archetypes,
    IReadOnlyList<string> EncounterCasts,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Merchants,
    IReadOnlyList<string> CharterOpportunities,
    int Wants,
    int ClaimSources,
    IReadOnlyList<string> Unresolved);
