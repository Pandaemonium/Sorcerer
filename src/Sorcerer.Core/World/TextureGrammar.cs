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

    public static TextureFeature ZoneFeature(RegionDefinition region, RealmProfile realm, IRng rng)
    {
        var isEmpire = region.RealmId.Equals("empire", StringComparison.OrdinalIgnoreCase);
        var vocab = region.Vocabulary ?? RegionVocabulary.Empty;
        var adjective = Pick(AdjectivesFor(region, vocab, isEmpire), rng);
        var noun = vocab.FixtureNouns.Count > 0
            ? Pick(vocab.FixtureNouns, rng)
            : isEmpire
                ? Pick(new[] { "survey marker", "censorate plinth", "permit stone", "marble witness" }, rng)
                : $"{Pick(region.TerrainTags.Count > 0 ? region.TerrainTags : new[] { "road" }, rng).Replace('_', '-')} waymark";
        var name = $"{adjective} {noun}";
        var localSubject = string.IsNullOrWhiteSpace(vocab.TextureSubject) ? region.RealmId : vocab.TextureSubject!;
        var description = !string.IsNullOrWhiteSpace(vocab.TextureDescriptionTemplate)
            ? Expand(vocab.TextureDescriptionTemplate!, name, region, realm)
            : isEmpire
                ? $"{name} records the empire's polite belief that anything strange can be measured before it matters."
                : $"{name} stands in {region.Name}, ordinary local work in a realm now {realm.Status} under {realm.Ruler}.";
        var subjects = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(new[] { localSubject, region.RealmId, region.TraditionId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new TextureFeature(name, description, subjects);
    }

    private static IReadOnlyList<string> AdjectivesFor(RegionDefinition region, RegionVocabulary vocab, bool isEmpire)
    {
        if (vocab.FixtureAdjectives.Count > 0)
        {
            return vocab.FixtureAdjectives;
        }

        var descriptors = isEmpire
            ? ImperialAdjectives
            : region.VoiceTags
                .Concat(region.TerrainTags)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value.Replace('_', '-'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        return descriptors.Length > 0 ? descriptors : new[] { "weathered" };
    }

    private static string Expand(string template, string name, RegionDefinition region, RealmProfile realm) =>
        template
            .Replace("{name}", name, StringComparison.OrdinalIgnoreCase)
            .Replace("{region}", region.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{realm}", realm.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{ruler}", realm.Ruler, StringComparison.OrdinalIgnoreCase)
            .Replace("{status}", realm.Status, StringComparison.OrdinalIgnoreCase);

    private static string Pick(IReadOnlyList<string> values, IRng rng) =>
        values[Math.Clamp(rng.NextInt(0, values.Count), 0, values.Count - 1)];
}
