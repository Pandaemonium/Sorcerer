using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record TextureFeature(
    string Name,
    string Description,
    IReadOnlyList<string> Subjects);

public static class TextureGrammar
{
    private static readonly string[] ImperialAdjectives =
    {
        "numbered",
        "ledger-stamped",
        "oath-polished",
        "quietly surveilling",
    };

    private static readonly string[] HollowmereAdjectives =
    {
        "reed-bound",
        "mire-silvered",
        "water-named",
        "eelglass",
    };

    private static readonly string[] WildAdjectives =
    {
        "flower-mouthed",
        "rain-boned",
        "color-bitten",
        "law-broken",
    };

    public static TextureFeature ZoneFeature(RegionDefinition region, RealmProfile realm, IRng rng)
    {
        var adjective = Pick(AdjectivesFor(region), rng);
        var noun = region.Id switch
        {
            "hollowmere_margin" => Pick(new[] { "memory shrine", "reed ledger", "mudwise post", "listening basket" }, rng),
            "wild_border" => Pick(new[] { "boneflower marker", "rain altar", "broken-law cairn", "color snare" }, rng),
            _ when region.RealmId.Equals("empire", StringComparison.OrdinalIgnoreCase) =>
                Pick(new[] { "survey marker", "censorate plinth", "permit stone", "marble witness" }, rng),
            _ => $"{Pick(region.TerrainTags.Count > 0 ? region.TerrainTags : new[] { "road" }, rng).Replace('_', '-')} waymark",
        };
        var name = $"{adjective} {noun}";
        var localSubject = region.Id switch
        {
            "hollowmere_margin" => "hollowmere",
            "wild_border" => "wild_border",
            _ => region.RealmId,
        };
        var description = region.Id switch
        {
            "hollowmere_margin" =>
                $"{name} keeps local water-memory in public view, under the careful occupation of {realm.Ruler}.",
            "wild_border" =>
                $"{name} treats rule and weather as negotiable neighbors under {realm.Ruler}.",
            _ when region.RealmId.Equals("empire", StringComparison.OrdinalIgnoreCase) =>
                $"{name} records the empire's polite belief that anything strange can be measured before it matters.",
            _ =>
                $"{name} stands in {region.Name}, ordinary local work in a realm now {realm.Status} under {realm.Ruler}.",
        };
        var subjects = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(new[] { localSubject, region.RealmId, region.TraditionId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TextureFeature(name, description, subjects);
    }

    private static IReadOnlyList<string> AdjectivesFor(RegionDefinition region)
    {
        var descriptors = region.Id switch
        {
            "hollowmere_margin" => HollowmereAdjectives,
            "wild_border" => WildAdjectives,
            _ when region.RealmId.Equals("empire", StringComparison.OrdinalIgnoreCase) => ImperialAdjectives,
            _ => region.VoiceTags
                .Concat(region.TerrainTags)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Replace('_', '-'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
        };
        return descriptors.Length > 0 ? descriptors : new[] { "weathered" };
    }

    private static string Pick(IReadOnlyList<string> values, IRng rng) =>
        values[Math.Clamp(rng.NextInt(0, values.Count), 0, values.Count - 1)];
}
