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
            _ => Pick(new[] { "survey marker", "censorate plinth", "permit stone", "marble witness" }, rng),
        };
        var name = $"{adjective} {noun}";
        var localSubject = region.Id switch
        {
            "hollowmere_margin" => "hollowmere",
            "wild_border" => "wild_border",
            _ => "vigovia",
        };
        var description = region.Id switch
        {
            "hollowmere_margin" =>
                $"{name} keeps local water-memory in public view, under the careful occupation of {realm.Ruler}.",
            "wild_border" =>
                $"{name} treats rule and weather as negotiable neighbors under {realm.Ruler}.",
            _ =>
                $"{name} records the empire's polite belief that anything strange can be measured before it matters.",
        };
        var subjects = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(new[] { localSubject, region.RealmId, region.TraditionId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TextureFeature(name, description, subjects);
    }

    private static IReadOnlyList<string> AdjectivesFor(RegionDefinition region) =>
        region.Id switch
        {
            "hollowmere_margin" => HollowmereAdjectives,
            "wild_border" => WildAdjectives,
            _ => ImperialAdjectives,
        };

    private static string Pick(IReadOnlyList<string> values, IRng rng) =>
        values[Math.Clamp(rng.NextInt(0, values.Count), 0, values.Count - 1)];
}
