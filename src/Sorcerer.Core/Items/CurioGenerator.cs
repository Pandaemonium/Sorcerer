using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Items;

public sealed record GeneratedCurio(
    string Id,
    string Name,
    int Value,
    string Material,
    IReadOnlyList<string> Tags,
    string Description,
    string SpellBias)
{
    public ItemDefinition ToDefinition() =>
        new(
            Id,
            Name,
            '*',
            "curio",
            Material,
            Tags,
            Value,
            StackPolicy: "unique",
            UseProfile: "inert",
            SpellBias: SpellBias);
}

public static class CurioGenerator
{
    private static readonly string[] Forms =
    {
        "bell",
        "comb",
        "spoon",
        "lens",
        "button",
        "thimble",
        "idol",
        "ribbon spool",
        // WildMagic-flavored props/curios (props.py), kept wonder-forward not dread-forward.
        "reliquary",
        "effigy",
        "sigil-stone",
        "charm",
        "token",
        "whistle",
        "die",
        "vial",
    };

    private static readonly string[] Textures =
    {
        "glossy",
        "salt-bitten",
        "reed-wrapped",
        "warm",
        "moth-pale",
        "crooked",
        "hummed-thin",
        "rain-dark",
        "chime-tuned",
        "ember-warm",
        "quartz-veined",
        "brass-bright",
        "ink-stained",
    };

    public static GeneratedCurio Generate(RegionDefinition region, RealmProfile realm, IRng rng)
    {
        var material = Pick(region.TerrainTags.Count > 0 ? region.TerrainTags : region.VoiceTags, rng, "wood");
        var texture = Pick(Textures, rng, "odd");
        var form = Pick(Forms, rng, "charm");
        var tradition = string.IsNullOrWhiteSpace(region.TraditionId) ? "local" : region.TraditionId;
        var realmTag = realm.Tags.Count > 0 ? Pick(realm.Tags, rng, realm.RealmId) : realm.RealmId;
        var name = $"{texture} {material} {form}";
        var id = NormalizeId($"curio_{region.Id}_{texture}_{material}_{form}");
        var tags = new[]
            {
                "item",
                "curio",
                "generated",
                material,
                tradition,
                realmTag,
            }
            .Concat(region.TerrainTags.Take(2))
            .Concat(region.VoiceTags.Take(2))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(NormalizeId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var value = 8 + region.WildnessBase + rng.NextInt(0, 9) + Math.Max(0, realm.ImperialGripDelta);
        var description = $"A {form} from {region.Name}, {texture} with {material}; it smells faintly of {tradition.Replace('_', ' ')}.";
        var spellBias = string.Join(", ", new[] { material, tradition, realmTag }.Distinct(StringComparer.OrdinalIgnoreCase));
        return new GeneratedCurio(id, name, value, material, tags, description, spellBias);
    }

    private static string Pick(IReadOnlyList<string> values, IRng rng, string fallback) =>
        values.Count == 0 ? fallback : values[rng.NextInt(0, values.Count)];

    private static string NormalizeId(string value)
    {
        var cleaned = string.Join(
            '_',
            value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? "curio" : cleaned;
    }
}
