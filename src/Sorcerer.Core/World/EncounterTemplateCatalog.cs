using System.Text.Json;

namespace Sorcerer.Core.World;

public sealed class EncounterTemplateCatalog
{
    private readonly Dictionary<string, EncounterArchetypeDefinition> _archetypes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<EncounterArchetypeDefinition> Archetypes => _archetypes.Values;

    public void Add(EncounterArchetypeDefinition archetype) => _archetypes[archetype.Id] = archetype;

    public static EncounterTemplateCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var catalog = LoadFrom(Path.Combine(root, "content", "encounters"));
            if (catalog.Archetypes.Count > 0)
            {
                return catalog;
            }
        }

        var builtIn = LoadBuiltIn();
        return builtIn.Archetypes.Count > 0 ? builtIn : CreateMinimal();
    }

    public static EncounterTemplateCatalog LoadFrom(string directory)
    {
        var catalog = new EncounterTemplateCatalog();
        if (!Directory.Exists(directory))
        {
            return catalog;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            ReadDocument(document.RootElement, catalog);
        }

        return catalog;
    }

    public static EncounterTemplateCatalog LoadBuiltIn()
    {
        var catalog = new EncounterTemplateCatalog();
        var assembly = typeof(EncounterTemplateCatalog).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Content.Encounters.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(stream);
            ReadDocument(document.RootElement, catalog);
        }

        return catalog;
    }

    public static EncounterTemplateCatalog CreateMinimal()
    {
        var catalog = new EncounterTemplateCatalog();
        catalog.Add(new EncounterArchetypeDefinition(
            "minimal_guarded_cache",
            "guarded_cache",
            MinTier: 1,
            MaxTier: 3,
            RequiresInterior: false,
            AmbientEligible: true,
            Formation: "ring",
            CanonPattern: "{item} sits under watch at {place}.",
            Weight: 2,
            Tags: new[] { "encounter", "guarded_cache" },
            Casts: new[]
            {
                new EncounterFactionCastDefinition(
                    "independent",
                    Weight: 1,
                    MinImperialPresence: 0,
                    Slots: new[]
                    {
                        new EncounterCastSlotDefinition(
                            "cache_guard",
                            "guard",
                            "wary custodian",
                            'p',
                            "guard",
                            new[] { "encounter_cast", "objective_guard" },
                            new[] { "guard" },
                            "Keep {item} exactly where it was left.",
                            "Would stand down for someone with a rightful claim, fair payment, or a name they trust.",
                            CountByTier: new[] { 1, 2, 3 }),
                    }),
            }));
        catalog.Add(new EncounterArchetypeDefinition(
            "minimal_keeper",
            "keeper",
            MinTier: 1,
            MaxTier: 3,
            RequiresInterior: false,
            AmbientEligible: true,
            Formation: "adjacent",
            CanonPattern: "{keeper} holds {item} at {place}.",
            Weight: 1,
            Tags: new[] { "encounter", "keeper" },
            Casts: new[]
            {
                new EncounterFactionCastDefinition(
                    "independent",
                    Weight: 1,
                    MinImperialPresence: 0,
                    Slots: new[]
                    {
                        new EncounterCastSlotDefinition(
                            "keeper",
                            "keeper",
                            "reluctant keeper",
                            'p',
                            "resident",
                            new[] { "encounter_cast", "objective_keeper" },
                            new[] { "keeper" },
                            "Keep {item} safe until its rightful errand is proven.",
                            "Would part with it for proof of the errand, a fair trade, or someone they have come to trust.",
                            InteractableVerbs: new[] { "talk", "give", "recruit", "examine" },
                            CountByTier: new[] { 1, 1, 1 }),
                    }),
            }));
        return catalog;
    }

    private static void ReadDocument(JsonElement root, EncounterTemplateCatalog catalog)
    {
        if (!root.TryGetProperty("archetypes", out var archetypes) || archetypes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in archetypes.EnumerateArray())
        {
            var minTier = Math.Clamp(ReadInt(item, "minTier", 1), 1, 3);
            var archetype = new EncounterArchetypeDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "kind", ""),
                minTier,
                Math.Clamp(ReadInt(item, "maxTier", 3), minTier, 3),
                ReadBool(item, "requiresInterior", false),
                ReadBool(item, "ambientEligible", true),
                ReadString(item, "formation", "ring"),
                ReadString(item, "canonPattern", "{item} waits at {place}."),
                Math.Max(1, ReadInt(item, "weight", 1)),
                ReadStrings(item, "tags"),
                ReadCasts(item),
                ReadBool(item, "promiseEligible", true));
            if (!string.IsNullOrWhiteSpace(archetype.Id)
                && !string.IsNullOrWhiteSpace(archetype.Kind)
                && archetype.Casts.Count > 0)
            {
                catalog.Add(archetype);
            }
        }
    }

    private static IReadOnlyList<EncounterFactionCastDefinition> ReadCasts(JsonElement root)
    {
        if (!root.TryGetProperty("casts", out var casts) || casts.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<EncounterFactionCastDefinition>();
        }

        return casts.EnumerateArray()
            .Select(item => new EncounterFactionCastDefinition(
                ReadString(item, "factionId", ""),
                Math.Max(1, ReadInt(item, "weight", 1)),
                Math.Max(0, ReadInt(item, "minImperialPresence", 0)),
                ReadSlots(item)))
            .Where(cast => !string.IsNullOrWhiteSpace(cast.FactionId) && cast.Slots.Count > 0)
            .ToArray();
    }

    private static IReadOnlyList<EncounterCastSlotDefinition> ReadSlots(JsonElement root)
    {
        if (!root.TryGetProperty("slots", out var slots) || slots.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<EncounterCastSlotDefinition>();
        }

        return slots.EnumerateArray()
            .Select(item =>
            {
                var minHp = Math.Max(1, ReadInt(item, "minHitPoints", 7));
                var minAttack = Math.Max(0, ReadInt(item, "minAttack", 0));
                return new EncounterCastSlotDefinition(
                    ReadString(item, "id", ""),
                    ReadString(item, "role", "guard"),
                    ReadString(item, "titlePattern", "watcher"),
                    ReadChar(item, "glyph", 'p'),
                    ReadString(item, "aiPolicyId", "guard"),
                    ReadStrings(item, "tags"),
                    ReadStrings(item, "roles"),
                    ReadString(item, "wantPattern", ""),
                    ReadString(item, "wantStakes", ""),
                    minHp,
                    Math.Max(minHp, ReadInt(item, "maxHitPoints", 10)),
                    minAttack,
                    Math.Max(minAttack, ReadInt(item, "maxAttack", 2)),
                    item.TryGetProperty("interactableVerbs", out _) ? ReadStrings(item, "interactableVerbs") : null,
                    ReadInts(item, "countByTier"),
                    item.TryGetProperty("archetypeId", out var archetypeId) && archetypeId.ValueKind == JsonValueKind.String
                        ? archetypeId.GetString()
                        : null);
            })
            .Where(slot => !string.IsNullOrWhiteSpace(slot.Id) && !string.IsNullOrWhiteSpace(slot.WantPattern))
            .ToArray();
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : Array.Empty<string>();

    private static IReadOnlyList<int>? ReadInts(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.TryGetInt32(out _))
                .Select(item => item.GetInt32())
                .ToArray()
            : null;

    private static bool ReadBool(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static int ReadInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static char ReadChar(JsonElement root, string property, char fallback)
    {
        var value = ReadString(root, property, "");
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[0];
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }
}
