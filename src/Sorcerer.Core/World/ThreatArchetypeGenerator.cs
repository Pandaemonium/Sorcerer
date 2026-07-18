using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record ThreatArchetype(
    string Name,
    string Faction,
    char Glyph,
    string Material,
    int Hp,
    int Attack,
    IReadOnlyList<string> Tags,
    string FlavorText);

/// <summary>
/// Generates a varied, region-flavored threat for a realized "threat" promise, replacing what
/// used to be a single fixed stat block (hp 8 / attack 3) and a name with only three possible
/// outputs. Deterministic (seeded <see cref="IRng"/>) so replay stays reproducible, and
/// modeled directly on the same region-keyed combinatorial pattern already proven by
/// <see cref="TextureGrammar.ZoneFeature"/> and <c>CurioGenerator.Generate</c>.
/// </summary>
public static class ThreatArchetypeGenerator
{
    // None of these words may contain "promise"/"promises"/"promised"/"prophecy"/"prophecies"/
    // "omen"/"omens"/"oath"/"oaths" -- WildMagicController.PromiseIntentNeedsLedger scans the
    // player's own spell text for those tokens, and a generated name containing one would trip
    // its narrative-honesty guard the moment a player referred to this entity by name in a
    // spell (the exact bug that got the old fallback name "promised threat" renamed away).
    // TextureGrammar's own ImperialAdjectives pool includes "oath-polished" for this reason --
    // do not reuse it here.
    private static readonly string[] ImperialAdjectives =
    {
        "numbered",
        "ledger-marked",
        "quietly persistent",
        "procedurally overdue",
    };

    private static readonly string[] ImperialNouns =
    {
        "enforcer",
        "file-closer",
        "informant",
        "claims agent",
    };

    public static ThreatArchetype Generate(WorldPromise promise, RegionDefinition region, RealmProfile realm, IRng rng)
    {
        var salience = Math.Clamp(promise.Salience, 1, 5);
        var hp = 6 + (salience * 3) + Math.Max(0, region.WildnessBase) + rng.NextInt(0, 3);
        var attack = 2 + salience + rng.NextInt(0, 2);
        var faction = IsImperialThreat(promise) ? "empire" : "independent";
        var name = ResolveName(promise, region, rng);
        var tags = new[] { "promise", "threat", NormalizeToken(promise.Kind), NormalizeToken(promise.RealizationKind ?? "threat") }
            .Concat(region.TerrainTags.Take(2))
            .Concat(region.VoiceTags.Take(2))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var flavorText = FlavorTextFor(region, realm);
        return new ThreatArchetype(name, faction, 'D', "flesh", hp, attack, tags, flavorText);
    }

    private static string ResolveName(WorldPromise promise, RegionDefinition region, IRng rng)
    {
        var lower = $"{promise.Subject} {promise.Text}".ToLowerInvariant();
        var adjective = Pick(AdjectivesFor(region), rng);
        if (lower.Contains("collector"))
        {
            return $"{adjective} debt collector";
        }

        if (IsImperialThreat(promise))
        {
            return $"{adjective} imperial claimant";
        }

        // The region-flavored generic tier: this is the common case (most threat promises
        // won't literally say "collector" or "imperial"), and is what actually fixes the
        // "every threat looks the same" complaint. AdjectivesFor/NounsFor always resolve
        // (their switches both have a default arm), so this tier always produces a real name.
        return $"{adjective} {Pick(NounsFor(region), rng)}";
    }

    /// <summary>
    /// A threat promise only reads as imperial when its own text says so; otherwise it is a
    /// private grudge (a debt collector, a rival, a personal enemy) and must not be spawned
    /// under the Empire's faction, or killing it would feed Censorate heat and warrant pressure
    /// for a threat that had nothing to do with the Empire.
    /// </summary>
    private static bool IsImperialThreat(WorldPromise promise)
    {
        var lower = $"{promise.Subject} {promise.Text}".ToLowerInvariant();
        return lower.Contains("soldier") || lower.Contains("empire") || lower.Contains("imperial");
    }

    private static IReadOnlyList<string> AdjectivesFor(RegionDefinition region)
    {
        var vocab = region.Vocabulary?.ThreatAdjectives;
        return vocab is { Count: > 0 } ? vocab : ImperialAdjectives;
    }

    private static IReadOnlyList<string> NounsFor(RegionDefinition region)
    {
        var vocab = region.Vocabulary?.ThreatNouns;
        return vocab is { Count: > 0 } ? vocab : ImperialNouns;
    }

    private static string FlavorTextFor(RegionDefinition region, RealmProfile realm)
    {
        var prose = region.Vocabulary?.ThreatEntryProse;
        return string.IsNullOrWhiteSpace(prose)
            ? "A file has come due, and someone has arrived to close it:"
            : prose!.Replace("{ruler}", realm.Ruler, StringComparison.OrdinalIgnoreCase)
                .Replace("{status}", realm.Status, StringComparison.OrdinalIgnoreCase)
                .Replace("{realm}", realm.Name, StringComparison.OrdinalIgnoreCase);
    }

    private static string Pick(IReadOnlyList<string> values, IRng rng) =>
        values[Math.Clamp(rng.NextInt(0, values.Count), 0, values.Count - 1)];

    private static string NormalizeToken(string text)
    {
        var chars = (text ?? "")
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "threat" : normalized;
    }
}
