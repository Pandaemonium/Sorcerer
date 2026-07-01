using System.Text.Json;

namespace Sorcerer.Core.Characters;

public sealed record OriginDefinition(
    string Id,
    string DisplayName,
    string Tradition,
    int BodyVigor,
    int SoulAttunement,
    int SoulComposure,
    IReadOnlyDictionary<string, int> StartingItems,
    IReadOnlyDictionary<string, int> FactionFirstReactions,
    string PublicName,
    string Appearance,
    string MagicalSignature,
    string Backstory);

public sealed class OriginCatalog
{
    private readonly Dictionary<string, OriginDefinition> _origins;

    private OriginCatalog(IEnumerable<OriginDefinition> origins)
    {
        _origins = origins.ToDictionary(origin => origin.Id, StringComparer.OrdinalIgnoreCase);
        if (_origins.Count == 0)
        {
            _origins[DefaultOrigin.Id] = DefaultOrigin;
        }
    }

    public IReadOnlyCollection<OriginDefinition> Origins => _origins.Values;

    public OriginDefinition Default => Find("fugitive_wild_sorcerer") ?? _origins.Values.First();

    public OriginDefinition? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return _origins.TryGetValue(id.Trim(), out var origin) ? origin : null;
    }

    public OriginDefinition Resolve(string? id) => Find(id) ?? Default;

    public static OriginCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "origins");
            var origins = LoadFrom(directory);
            if (origins.Count > 0)
            {
                return new OriginCatalog(origins);
            }
        }

        return new OriginCatalog(new[] { DefaultOrigin });
    }

    public static IReadOnlyList<OriginDefinition> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<OriginDefinition>();
        }

        var origins = new List<OriginDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("origins", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                origins.AddRange(array.EnumerateArray().Select(ReadOrigin));
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                origins.AddRange(root.EnumerateArray().Select(ReadOrigin));
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                origins.Add(ReadOrigin(root));
            }
        }

        return origins
            .Where(origin => !string.IsNullOrWhiteSpace(origin.Id))
            .ToArray();
    }

    private static OriginDefinition ReadOrigin(JsonElement root) =>
        new(
            ReadString(root, "id", ""),
            ReadString(root, "displayName", ReadString(root, "display_name", ReadString(root, "id", ""))),
            ReadString(root, "tradition", ""),
            ReadInt(root, "bodyVigor", ReadInt(root, "body_vigor", 4)),
            ReadInt(root, "soulAttunement", ReadInt(root, "soul_attunement", 4)),
            ReadInt(root, "soulComposure", ReadInt(root, "soul_composure", 3)),
            ReadStringIntMap(root, "startingItems", "starting_items"),
            ReadStringIntMap(root, "factionFirstReactions", "faction_first_reactions"),
            ReadString(root, "publicName", ReadString(root, "public_name", "the sorcerer")),
            ReadString(root, "appearance", "a fugitive bright with badly behaved magic"),
            ReadString(root, "magicalSignature", ReadString(root, "magical_signature", "color leaking through marble law")),
            ReadString(root, "backstory", ""));

    private static IReadOnlyDictionary<string, int> ReadStringIntMap(JsonElement root, string camel, string snake)
    {
        var property = root.TryGetProperty(camel, out var camelValue) ? camelValue :
            root.TryGetProperty(snake, out var snakeValue) ? snakeValue :
            default;
        if (property.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return property.EnumerateObject()
            .ToDictionary(
                item => item.Name,
                item => item.Value.TryGetInt32(out var value) ? value : 0,
                StringComparer.OrdinalIgnoreCase);
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int ReadInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

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

    private static OriginDefinition DefaultOrigin { get; } = new(
        "fugitive_wild_sorcerer",
        "Fugitive Wild Sorcerer",
        "unlicensed wild magic",
        4,
        4,
        3,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["grave salt"] = 2,
            ["moon pearl"] = 1,
            ["charcoal wand"] = 1,
            ["gold"] = 15,
        },
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["empire"] = -2,
            ["hollowmere"] = 1,
        },
        "the sorcerer",
        "a fugitive bright with badly behaved magic",
        "color leaking through marble law",
        "You were already a problem before the room learned your name.");
}
