using System.Text.Json;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Items;

/// <summary>
/// The derived equipment payload an item contributes while equipped (WP2, docs/CONTENT_SPRINT_PLAN.md).
/// Effects are applied through a shared derived cache, never by mutating an actor's base stats on
/// equip/unequip. Resistances/weaknesses are damage-type → percent. FocusBias sharpens the resolver
/// only while the item is the active magical focus.
/// </summary>
public sealed record EquipmentModifier(
    int Attack = 0,
    int Defense = 0,
    IReadOnlyDictionary<string, int>? Resistances = null,
    IReadOnlyDictionary<string, int>? Weaknesses = null,
    string FocusBias = "")
{
    public static readonly EquipmentModifier None = new();

    public bool IsMeaningful =>
        Attack != 0
        || Defense != 0
        || (Resistances is { Count: > 0 })
        || (Weaknesses is { Count: > 0 })
        || !string.IsNullOrWhiteSpace(FocusBias);
}

public sealed record ItemDefinition(
    string Id,
    string Name,
    char Glyph,
    string Kind,
    string Material,
    IReadOnlyList<string> Tags,
    int Value,
    string StackPolicy = "commodity",
    string UseProfile = "inert",
    string? EquipmentSlot = null,
    string SpellBias = "",
    string Description = "",
    string Rarity = "common",
    // Spawn-repeat control, distinct from StackPolicy (which governs inventory stacking):
    // "common" may recur freely; "bounded" prefers not to recur; "unique" never recurs within a run.
    string RepeatPolicy = "common",
    IReadOnlyDictionary<string, int>? RegionWeights = null,
    IReadOnlyDictionary<string, int>? HabitatWeights = null,
    EquipmentModifier? Modifier = null)
{
    public bool IsUnique =>
        RepeatPolicy.Equals("unique", StringComparison.OrdinalIgnoreCase)
        || StackPolicy.Equals("unique", StringComparison.OrdinalIgnoreCase);

    public int RegionWeight(string regionId)
    {
        if (RegionWeights is not null && RegionWeights.TryGetValue(regionId, out var weight))
        {
            return Math.Max(0, weight);
        }

        // An item with no explicit distribution is globally eligible at a modest weight; an item
        // that names any region is scoped to those regions (weight 0 elsewhere).
        return RegionWeights is { Count: > 0 } ? 0 : 10;
    }

    public int HabitatWeight(string habitat)
    {
        if (HabitatWeights is not null && HabitatWeights.TryGetValue(habitat, out var weight))
        {
            return Math.Max(0, weight);
        }

        return HabitatWeights is { Count: > 0 } ? 1 : 10;
    }
}

public sealed class ItemCatalog
{
    private readonly Dictionary<string, ItemDefinition> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ItemDefinition> Items => _items.Values;

    public void Add(ItemDefinition item) => _items[item.Id] = item;

    public ItemDefinition? Find(string idOrName) =>
        _items.TryGetValue(idOrName, out var item)
            ? item
            : _items.Values.FirstOrDefault(candidate =>
                candidate.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The normal shipping catalog (WP2): the data-authored corpus from loose <c>content/items</c>
    /// and every region pack's <c>items.json</c>, discovered through the same
    /// <see cref="ContentPackLoader"/> as regions, with the embedded build as the offline fallback.
    /// <see cref="CreateMinimal"/> remains only as a compile-safe floor when no content is found.
    /// </summary>
    public static ItemCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var catalog = new ItemCatalog();
            LoadLooseGlobalItems(catalog, Path.Combine(root, "content", "items"));
            var packsRoot = Path.Combine(root, "content", "region-packs");
            if (ContentPackLoader.HasLoosePacks(packsRoot))
            {
                LoadEntries(catalog, ContentPackLoader.LoadLoose(packsRoot));
            }

            if (catalog._items.Count > 0)
            {
                EnsureStaples(catalog);
                return catalog;
            }
        }

        var embedded = LoadEmbedded();
        if (embedded._items.Count > 0)
        {
            EnsureStaples(embedded);
            return embedded;
        }

        return CreateMinimal();
    }

    public static ItemCatalog LoadEmbedded()
    {
        var catalog = new ItemCatalog();
        var assembly = typeof(ItemCatalog).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Content.Items.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            ReadItemsDocument(catalog, reader.ReadToEnd(), impliedRegion: null);
        }

        LoadEntries(catalog, ContentPackLoader.LoadEmbedded(assembly));
        if (catalog._items.Count > 0)
        {
            EnsureStaples(catalog);
        }

        return catalog;
    }

    private static void LoadLooseGlobalItems(ItemCatalog catalog, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            ReadItemsDocument(catalog, File.ReadAllText(path), impliedRegion: null);
        }
    }

    private static void LoadEntries(ItemCatalog catalog, IReadOnlyList<ContentPackEntry> entries)
    {
        foreach (var entry in entries.Where(entry =>
            entry.FileName.Equals("items.json", StringComparison.OrdinalIgnoreCase)))
        {
            var impliedRegion = string.IsNullOrWhiteSpace(entry.PackId) ? null : entry.PackId;
            ReadItemsDocument(catalog, entry.ReadText(), impliedRegion);
        }
    }

    private static void ReadItemsDocument(ItemCatalog catalog, string json, string? impliedRegion)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in items.EnumerateArray())
        {
            var item = ReadItem(element, impliedRegion);
            if (!string.IsNullOrWhiteSpace(item.Id))
            {
                catalog.Add(item);
            }
        }
    }

    private static ItemDefinition ReadItem(JsonElement root, string? impliedRegion)
    {
        var id = Str(root, "id", "");
        var regionWeights = ReadIntMap(root, "regionWeights", "region_weights");
        if (regionWeights.Count == 0 && !string.IsNullOrWhiteSpace(impliedRegion))
        {
            regionWeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [impliedRegion!] = 20,
            };
        }

        return new ItemDefinition(
            id,
            Str(root, "name", id),
            Glyph(root),
            Str(root, "kind", "reagent"),
            Str(root, "material", "unknown"),
            StrList(root, "tags"),
            Int(root, "value", 1),
            Str(root, "stackPolicy", Str(root, "stack_policy", "commodity")),
            Str(root, "useProfile", Str(root, "use_profile", "inert")),
            NullableStr(root, "equipmentSlot", "equipment_slot"),
            Str(root, "spellBias", Str(root, "spell_bias", "")),
            Str(root, "description", ""),
            Str(root, "rarity", "common"),
            Str(root, "repeatPolicy", Str(root, "repeat_policy", "common")),
            regionWeights.Count > 0 ? regionWeights : null,
            ReadIntMapOrNull(root, "habitatWeights", "habitat_weights"),
            ReadModifier(root));
    }

    private static EquipmentModifier? ReadModifier(JsonElement root)
    {
        var value = root.TryGetProperty("modifier", out var camel)
            ? camel
            : root.TryGetProperty("equipment", out var alt)
                ? alt
                : default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var modifier = new EquipmentModifier(
            Int(value, "attack", 0),
            Int(value, "defense", 0),
            ReadIntMapOrNull(value, "resistances", "resist"),
            ReadIntMapOrNull(value, "weaknesses", "weak"),
            Str(value, "focusBias", Str(value, "focus_bias", "")));
        return modifier.IsMeaningful ? modifier : null;
    }

    private static IReadOnlyDictionary<string, int> ReadIntMap(JsonElement root, string camel, string snake)
        => ReadIntMapOrNull(root, camel, snake) ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int>? ReadIntMapOrNull(JsonElement root, string camel, string snake)
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

    private static string? NullableStr(JsonElement root, string camel, string snake)
    {
        if (root.TryGetProperty(camel, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (root.TryGetProperty(snake, out var snakeValue) && snakeValue.ValueKind == JsonValueKind.String)
        {
            return snakeValue.GetString();
        }

        return null;
    }

    private static int Int(JsonElement root, string name, int fallback) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static char Glyph(JsonElement root)
    {
        var glyph = Str(root, "glyph", "*");
        return string.IsNullOrEmpty(glyph) ? '*' : glyph[0];
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

    /// <summary>Guarantees the handful of ids the engine references by name (currency, the opening
    /// cell key) exist even if content omits them, so systems never null-out on a staple id.</summary>
    private static void EnsureStaples(ItemCatalog catalog)
    {
        foreach (var staple in CreateMinimal().Items)
        {
            if (catalog.Find(staple.Id) is null)
            {
                catalog.Add(staple);
            }
        }
    }

    public static ItemCatalog CreateMinimal()
    {
        var catalog = new ItemCatalog();
        catalog.Add(new ItemDefinition("gold", "gold", '$', "currency", "gold", new[] { "coin", "value" }, 1, SpellBias: "payment, weight, empire"));
        catalog.Add(new ItemDefinition("grave_salt", "grave salt", '*', "reagent", "salt", new[] { "death", "ward", "bone" }, 8, SpellBias: "wards, death, preservation"));
        catalog.Add(new ItemDefinition("moon_pearl", "moon pearl", 'o', "reagent", "pearl", new[] { "moon", "water", "beauty" }, 40, "unique", SpellBias: "moonlight, water, beauty"));
        catalog.Add(new ItemDefinition("red_tincture", "red tincture", '!', "consumable", "glass", new[] { "blood", "healing", "medicine" }, 12, UseProfile: "heal:6", SpellBias: "blood, healing, medicine"));
        catalog.Add(new ItemDefinition("charcoal_wand", "charcoal wand", '/', "focus", "charcoal", new[] { "wand", "focus", "burnt" }, 18, "unique", EquipmentSlot: "hand", SpellBias: "burnt lines, focus, fire"));
        catalog.Add(new ItemDefinition("imperial_cell_key", "imperial cell key", 'k', "key", "iron", new[] { "key", "imperial", "cell" }, 5, "unique", "key", SpellBias: "locks, iron, empire"));
        return catalog;
    }
}
