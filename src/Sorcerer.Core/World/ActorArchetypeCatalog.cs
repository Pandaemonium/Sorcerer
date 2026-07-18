using System.Text.Json;

namespace Sorcerer.Core.World;

/// <summary>
/// A shared, data-authored actor archetype (WP4, docs/CONTENT_SPRINT_PLAN.md): the single source of
/// truth for culturally specific enemies, named pressures, and Hollowmere's exotic pets. Encounter
/// casts, threat realizations, and creature spawns all draw from these rather than repeating small
/// stat blocks. A hostile carries a telegraphed <see cref="Intent"/>, an inspectable
/// <see cref="Weakness"/>, and at least one non-damage <see cref="Counter"/>; a pet carries a
/// <see cref="Temperament"/> and actionable <see cref="Verbs"/>. Every field is data.
/// </summary>
public sealed record ActorArchetypeDefinition(
    string Id,
    string Category,
    string Name,
    char Glyph,
    string Faction,
    string Material,
    int MinHitPoints,
    int MaxHitPoints,
    int MinAttack,
    int MaxAttack,
    int Defense,
    string AiPolicyId,
    IReadOnlyList<string> BehaviorTags,
    IReadOnlyList<string> TerrainPreference,
    string Intent,
    string Weakness,
    string Counter,
    string Temperament,
    string WantText,
    string WantStakes,
    IReadOnlyList<string> Verbs,
    IReadOnlyList<string> Tags,
    IReadOnlyDictionary<string, int>? RegionWeights = null,
    IReadOnlyDictionary<string, int>? HabitatWeights = null)
{
    public bool IsPet => Category.Equals("pet", StringComparison.OrdinalIgnoreCase);

    public bool IsHostile =>
        Category.Equals("hostile", StringComparison.OrdinalIgnoreCase)
        || Category.Equals("pressure", StringComparison.OrdinalIgnoreCase);

    public int RegionWeight(string regionId)
    {
        if (RegionWeights is not null && RegionWeights.TryGetValue(regionId, out var weight))
        {
            return Math.Max(0, weight);
        }

        return RegionWeights is { Count: > 0 } ? 0 : 5;
    }

    public int HabitatWeight(string habitat)
    {
        if (HabitatWeights is not null && HabitatWeights.TryGetValue(habitat, out var weight))
        {
            return Math.Max(0, weight);
        }

        return HabitatWeights is { Count: > 0 } ? 1 : 5;
    }

    /// <summary>The one-line tactical read a player gets on inspect: what it is poised to do and how
    /// to beat it without trading blows.</summary>
    public string InspectLine()
    {
        if (IsPet)
        {
            return string.IsNullOrWhiteSpace(Temperament) ? "" : $"Temperament: {Temperament}.";
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(Intent))
        {
            parts.Add($"Intent: {Intent}");
        }

        if (!string.IsNullOrWhiteSpace(Weakness))
        {
            parts.Add($"weakness: {Weakness}");
        }

        if (!string.IsNullOrWhiteSpace(Counter))
        {
            parts.Add($"counter: {Counter}");
        }

        return parts.Count == 0 ? "" : string.Join("; ", parts) + ".";
    }
}

public sealed class ActorArchetypeCatalog
{
    private readonly Dictionary<string, ActorArchetypeDefinition> _archetypes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ActorArchetypeDefinition> Archetypes => _archetypes.Values;

    public void Add(ActorArchetypeDefinition archetype)
    {
        if (string.IsNullOrWhiteSpace(archetype.Id))
        {
            throw new ContentPackException("Actor archetypes require a non-empty id.");
        }

        if (!_archetypes.TryAdd(archetype.Id, archetype))
        {
            throw new ContentPackException($"Actor archetype id '{archetype.Id}' is defined more than once.");
        }
    }

    public ActorArchetypeDefinition? Find(string id) =>
        _archetypes.TryGetValue(id, out var archetype) ? archetype : null;

    public IReadOnlyList<ActorArchetypeDefinition> PetsFor(string regionId) =>
        _archetypes.Values
            .Where(archetype => archetype.IsPet && archetype.RegionWeight(regionId) > 0)
            .OrderBy(archetype => archetype.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<ActorArchetypeDefinition> HostilesFor(string regionId) =>
        _archetypes.Values
            .Where(archetype => archetype.IsHostile && archetype.RegionWeight(regionId) > 0)
            .OrderBy(archetype => archetype.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static readonly Lazy<ActorArchetypeCatalog> DefaultCatalog = new(LoadDefault);

    public static ActorArchetypeCatalog Default => DefaultCatalog.Value;

    public static ActorArchetypeCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var catalog = new ActorArchetypeCatalog();
            LoadLooseGlobal(catalog, Path.Combine(root, "content", "actors"));
            var packsRoot = Path.Combine(root, "content", "region-packs");
            if (ContentPackLoader.HasLoosePacks(packsRoot))
            {
                LoadEntries(catalog, ContentPackLoader.LoadLoose(packsRoot));
            }

            if (catalog._archetypes.Count > 0)
            {
                return catalog;
            }
        }

        return LoadEmbedded();
    }

    public static ActorArchetypeCatalog LoadEmbedded()
    {
        var catalog = new ActorArchetypeCatalog();
        var assembly = typeof(ActorArchetypeCatalog).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Content.Actors.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            ReadDocument(catalog, reader.ReadToEnd(), null);
        }

        LoadEntries(catalog, ContentPackLoader.LoadEmbedded(assembly));
        return catalog;
    }

    private static void LoadLooseGlobal(ActorArchetypeCatalog catalog, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ReadDocument(catalog, File.ReadAllText(path), null);
        }
    }

    private static void LoadEntries(ActorArchetypeCatalog catalog, IReadOnlyList<ContentPackEntry> entries)
    {
        foreach (var entry in entries.Where(entry =>
            entry.FileName.Equals("actors.json", StringComparison.OrdinalIgnoreCase)))
        {
            ReadDocument(catalog, entry.ReadText(), string.IsNullOrWhiteSpace(entry.PackId) ? null : entry.PackId);
        }
    }

    private static void ReadDocument(ActorArchetypeCatalog catalog, string json, string? impliedRegion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("actors", out var actors) || actors.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in actors.EnumerateArray())
        {
            var archetype = ReadArchetype(element, impliedRegion);
            if (!string.IsNullOrWhiteSpace(archetype.Id))
            {
                catalog.Add(archetype);
            }
        }
    }

    private static ActorArchetypeDefinition ReadArchetype(JsonElement root, string? impliedRegion)
    {
        var regionWeights = ReadIntMap(root, "regionWeights", "region_weights");
        if (regionWeights is null && !string.IsNullOrWhiteSpace(impliedRegion))
        {
            regionWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [impliedRegion!] = 6 };
        }

        return new ActorArchetypeDefinition(
            Str(root, "id", ""),
            Str(root, "category", "hostile"),
            Str(root, "name", Str(root, "id", "figure")),
            Glyph(root),
            Str(root, "faction", "independent"),
            Str(root, "material", "flesh"),
            Int(root, "minHitPoints", Int(root, "hp", 8)),
            Int(root, "maxHitPoints", Int(root, "hp", 12)),
            Int(root, "minAttack", Int(root, "attack", 2)),
            Int(root, "maxAttack", Int(root, "attack", 4)),
            Int(root, "defense", 0),
            Str(root, "aiPolicyId", Str(root, "ai", "guard")),
            StrList(root, "behaviorTags"),
            StrList(root, "terrainPreference"),
            Str(root, "intent", ""),
            Str(root, "weakness", ""),
            Str(root, "counter", ""),
            Str(root, "temperament", ""),
            Str(root, "want", ""),
            Str(root, "wantStakes", "Trade, wants, deeds, or trouble can change this."),
            StrList(root, "verbs"),
            StrList(root, "tags"),
            regionWeights,
            ReadIntMap(root, "habitatWeights", "habitat_weights"));
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

    private static string Str(JsonElement root, string name, string fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int Int(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static char Glyph(JsonElement root)
    {
        var glyph = Str(root, "glyph", "e");
        return string.IsNullOrEmpty(glyph) ? 'e' : glyph[0];
    }

    private static IReadOnlyList<string> StrList(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, int>? ReadIntMap(JsonElement root, string camel, string snake)
    {
        var value = root.TryGetProperty(camel, out var camelValue)
            ? camelValue
            : root.TryGetProperty(snake, out var snakeValue)
                ? snakeValue
                : default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.TryGetInt32(out var parsed))
            {
                map[property.Name] = parsed;
            }
        }

        return map.Count > 0 ? map : null;
    }
}
