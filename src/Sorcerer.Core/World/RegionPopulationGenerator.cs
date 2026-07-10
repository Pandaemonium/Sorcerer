using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record GeneratedResidentWare(string Item, int Quantity);

public sealed record GeneratedResidentProfile(
    string ArchetypeId,
    string Name,
    string Title,
    char Glyph,
    string FactionId,
    string Description,
    string WantText,
    int HitPoints,
    int Attack,
    int KnowledgeTier,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Roles,
    IReadOnlyList<GeneratedResidentWare> Wares,
    IReadOnlyList<RegionServiceDefinition> Services);

public sealed record RegionPopulationBatch(
    string Habitat,
    int DistanceToCenter,
    double ExpectedPopulation,
    IReadOnlyList<GeneratedResidentProfile> Residents);

public static class RegionPopulationGenerator
{
    public const string CenterHabitat = "center";
    public const string NearHabitat = "near";
    public const string WildHabitat = "wild";

    public static RegionPopulationBatch Generate(
        int worldSeed,
        string zoneId,
        RegionDefinition region,
        RealmProfile realm,
        (int X, int Y) placedAnchor,
        string? habitatOverride = null,
        double expectedMultiplier = 1.0)
    {
        var grammar = region.Population;
        if (grammar is null)
        {
            return new RegionPopulationBatch(WildHabitat, int.MaxValue, 0, Array.Empty<GeneratedResidentProfile>());
        }

        var (zoneX, zoneY) = ParseZoneId(zoneId);
        var centers = grammar.Centers is { Count: > 0 }
            ? grammar.Centers.Select(center => (center.X, center.Y)).ToArray()
            : new[] { placedAnchor };
        var distance = centers.Min(center => ChebyshevDistance(zoneX, zoneY, center.X, center.Y));
        var habitat = !string.IsNullOrWhiteSpace(habitatOverride)
            ? habitatOverride
            : distance <= grammar.CenterRadius
            ? CenterHabitat
            : distance <= grammar.NearRadius
                ? NearHabitat
                : WildHabitat;
        var baseMean = habitat switch
        {
            CenterHabitat => grammar.CenterMean,
            NearHabitat => grammar.NearMean,
            _ => grammar.WildMean,
        };
        var noiseRng = new DeterministicRng(WorldRoll.StableSeed(
            worldSeed,
            region.Id,
            FloorDiv(zoneX, 3).ToString(),
            FloorDiv(zoneY, 3).ToString(),
            "population_noise"));
        var expected = baseMean
            * Math.Clamp(expectedMultiplier, 0.2, 2.5)
            * (0.72 + (noiseRng.NextDouble() * 0.56));
        var rng = new DeterministicRng(WorldRoll.StableSeed(worldSeed, zoneId, region.Id, "population"));
        var count = Math.Min(grammar.MaxResidents, SamplePoisson(rng, expected));
        if (habitat == CenterHabitat && grammar.MaxResidents > 0)
        {
            count = Math.Max(count, Math.Min(3, grammar.MaxResidents));
        }
        else if (habitat == WildHabitat
            && count == 0
            && Chance(rng, grammar.HermitChancePercent))
        {
            count = 1;
        }

        var residents = new List<GeneratedResidentProfile>(count);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            var archetype = SelectArchetype(grammar, habitat, index, rng);
            if (archetype is null)
            {
                break;
            }

            var name = ForgeName(
                grammar.Names,
                archetype.Title,
                worldSeed,
                region.Id,
                archetype.Id,
                zoneX,
                zoneY,
                index,
                grammar.MaxResidents,
                usedNames);
            var description = Expand(Pick(archetype.Descriptions, rng), name, archetype.Title, region, realm);
            var want = Expand(Pick(archetype.Wants, rng), name, archetype.Title, region, realm);
            var faction = ResolveFaction(archetype.FactionId, region);
            var tags = archetype.Tags
                .Concat(region.TerrainTags)
                .Concat(region.VoiceTags)
                .Concat(new[]
                {
                    "resident",
                    "generated",
                    "regional_population",
                    NormalizeToken(region.RealmId),
                    NormalizeToken(realm.Status),
                    $"knowledge_{archetype.KnowledgeTier}",
                    $"habitat_{habitat}",
                    archetype.Id,
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var roles = archetype.Roles
                .Append("resident")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var wares = archetype.Wares
                .Select(ware => new GeneratedResidentWare(
                    ware.Item,
                    rng.NextInt(ware.MinQuantity, Math.Max(ware.MinQuantity, ware.MaxQuantity) + 1)))
                .ToArray();
            var services = archetype.Services
                .Select(service => service with
                {
                    Name = Expand(service.Name, name, archetype.Title, region, realm),
                    Description = Expand(service.Description, name, archetype.Title, region, realm),
                    TargetHint = service.TargetHint is null
                        ? null
                        : Expand(service.TargetHint, name, archetype.Title, region, realm),
                })
                .ToArray();
            residents.Add(new GeneratedResidentProfile(
                archetype.Id,
                name,
                archetype.Title,
                archetype.Glyph,
                faction,
                description,
                want,
                rng.NextInt(archetype.MinHitPoints, Math.Max(archetype.MinHitPoints, archetype.MaxHitPoints) + 1),
                rng.NextInt(archetype.MinAttack, Math.Max(archetype.MinAttack, archetype.MaxAttack) + 1),
                archetype.KnowledgeTier,
                tags,
                roles,
                wares,
                services));
        }

        return new RegionPopulationBatch(habitat, distance, expected, residents);
    }

    private static RegionResidentArchetypeDefinition? SelectArchetype(
        RegionPopulationGrammarDefinition grammar,
        string habitat,
        int index,
        IRng rng)
    {
        if (index == 0 && habitat == CenterHabitat)
        {
            var required = grammar.Archetypes.Where(archetype => archetype.RequiredAtCenter).ToArray();
            if (required.Length > 0)
            {
                return WeightedPick(required, archetype => Math.Max(1, archetype.Weight * HabitatWeight(archetype, habitat)), rng);
            }
        }

        var eligible = grammar.Archetypes
            .Where(archetype => HabitatWeight(archetype, habitat) > 0)
            .ToArray();
        return eligible.Length == 0
            ? grammar.Archetypes.FirstOrDefault()
            : WeightedPick(eligible, archetype => Math.Max(1, archetype.Weight * HabitatWeight(archetype, habitat)), rng);
    }

    private static int HabitatWeight(RegionResidentArchetypeDefinition archetype, string habitat) =>
        habitat switch
        {
            CenterHabitat => archetype.CenterWeight,
            NearHabitat => archetype.NearWeight,
            _ => archetype.WildWeight,
        };

    private static T WeightedPick<T>(IReadOnlyList<T> values, Func<T, int> weight, IRng rng)
    {
        var total = values.Sum(value => Math.Max(0, weight(value)));
        if (total <= 0)
        {
            return values[0];
        }

        var roll = rng.NextInt(0, total);
        foreach (var value in values)
        {
            roll -= Math.Max(0, weight(value));
            if (roll < 0)
            {
                return value;
            }
        }

        return values[^1];
    }

    private static string ForgeName(
        RegionNameForgeDefinition forge,
        string title,
        int worldSeed,
        string regionId,
        string archetypeId,
        int zoneX,
        int zoneY,
        int residentIndex,
        int maxResidents,
        ISet<string> used)
    {
        var total = forge.GivenNames.Count * forge.ByNames.Count;
        var neighborhoodColor = FloorMod(zoneX, 2) + (2 * FloorMod(zoneY, 2));
        var stableOffset = WorldRoll.StableSeed(worldSeed, regionId, archetypeId, "names") % total;
        var start = (stableOffset + (neighborhoodColor * maxResidents) + residentIndex) % total;
        for (var attempt = 0; attempt < total; attempt++)
        {
            var ordinal = (start + attempt) % total;
            var given = forge.GivenNames[ordinal % forge.GivenNames.Count];
            var byname = forge.ByNames[(ordinal / forge.GivenNames.Count) % forge.ByNames.Count];
            var name = $"{given} {byname}, {title}";
            if (used.Add(name))
            {
                return name;
            }
        }

        var fallback = $"{forge.GivenNames[0]} {forge.ByNames[0]}, {title} {used.Count + 1}";
        used.Add(fallback);
        return fallback;
    }

    private static string Expand(
        string template,
        string name,
        string title,
        RegionDefinition region,
        RealmProfile realm) =>
        template
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", title, StringComparison.OrdinalIgnoreCase)
            .Replace("{region}", region.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{realm}", realm.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{ruler}", realm.Ruler, StringComparison.OrdinalIgnoreCase)
            .Replace("{status}", realm.Status, StringComparison.OrdinalIgnoreCase);

    private static string ResolveFaction(string? factionId, RegionDefinition region) =>
        string.IsNullOrWhiteSpace(factionId) || factionId.Equals("realm", StringComparison.OrdinalIgnoreCase)
            ? region.RealmId switch
            {
                "empire" => "empire",
                "hollowmere" => "hollowmere",
                _ => "neutral",
            }
            : factionId;

    private static T Pick<T>(IReadOnlyList<T> values, IRng rng) =>
        values[Math.Clamp(rng.NextInt(0, values.Count), 0, values.Count - 1)];

    private static int SamplePoisson(IRng rng, double mean)
    {
        if (mean <= 0)
        {
            return 0;
        }

        var limit = Math.Exp(-mean);
        var product = 1.0;
        var count = 0;
        do
        {
            count++;
            product *= rng.NextDouble();
        }
        while (product > limit && count < 64);
        return Math.Max(0, count - 1);
    }

    private static bool Chance(IRng rng, int percent) =>
        percent > 0 && rng.NextInt(0, 100) < Math.Clamp(percent, 0, 100);

    private static int ChebyshevDistance(int x1, int y1, int x2, int y2) =>
        Math.Max(Math.Abs(x1 - x2), Math.Abs(y1 - y2));

    private static int FloorDiv(int value, int divisor) =>
        value >= 0 ? value / divisor : ((value + 1) / divisor) - 1;

    private static int FloorMod(int value, int divisor)
    {
        var remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }
}
