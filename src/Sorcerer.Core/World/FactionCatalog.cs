using System.Text.Json;

namespace Sorcerer.Core.World;

public sealed record FactionDefinition(
    string Id,
    string Name,
    string Role,
    IReadOnlyDictionary<string, int> Standing,
    IReadOnlyDictionary<string, int> Resources,
    IReadOnlyList<string> HostileRoles);

public sealed class FactionCatalog
{
    private readonly Dictionary<string, FactionDefinition> _factions;

    private FactionCatalog(IEnumerable<FactionDefinition> factions)
    {
        _factions = factions.ToDictionary(faction => faction.Id, StringComparer.OrdinalIgnoreCase);
        if (_factions.Count == 0)
        {
            foreach (var faction in DefaultFactions)
            {
                _factions[faction.Id] = faction;
            }
        }
    }

    public IReadOnlyCollection<FactionDefinition> Factions => _factions.Values;

    public void ApplyTo(FactionLedger ledger)
    {
        foreach (var definition in Factions)
        {
            var faction = ledger.AddOrGet(
                definition.Id,
                definition.Name,
                definition.Role,
                definition.Resources,
                definition.HostileRoles);
            foreach (var pair in definition.Standing)
            {
                faction.Standing[pair.Key] = pair.Value;
            }
        }
    }

    public static FactionCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "factions");
            var factions = LoadFrom(directory);
            if (factions.Count > 0)
            {
                return new FactionCatalog(factions);
            }
        }

        return new FactionCatalog(DefaultFactions);
    }

    public static IReadOnlyList<FactionDefinition> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<FactionDefinition>();
        }

        var factions = new List<FactionDefinition>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("factions", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                factions.AddRange(array.EnumerateArray().Select(ReadFaction));
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                factions.AddRange(root.EnumerateArray().Select(ReadFaction));
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                factions.Add(ReadFaction(root));
            }
        }

        return factions
            .Where(faction => !string.IsNullOrWhiteSpace(faction.Id))
            .ToArray();
    }

    private static FactionDefinition ReadFaction(JsonElement root) =>
        new(
            ReadString(root, "id", ""),
            ReadString(root, "name", ReadString(root, "displayName", ReadString(root, "id", ""))),
            ReadString(root, "role", "unknown"),
            ReadStringIntMap(root, "standing"),
            ReadStringIntMap(root, "resources"),
            ReadStringList(root, "hostileRoles", "hostile_roles"));

    private static IReadOnlyDictionary<string, int> ReadStringIntMap(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return value.EnumerateObject()
            .ToDictionary(
                item => item.Name,
                item => item.Value.TryGetInt32(out var parsed) ? parsed : 0,
                StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadStringList(JsonElement root, string camel, string snake)
    {
        var property = root.TryGetProperty(camel, out var camelValue) ? camelValue :
            root.TryGetProperty(snake, out var snakeValue) ? snakeValue :
            default;
        if (property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
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

    private static IReadOnlyList<FactionDefinition> DefaultFactions { get; } = new[]
    {
        new FactionDefinition(
            "player",
            "The Sorcerer",
            "player",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new[] { "empire_bloc" }),
        new FactionDefinition(
            "empire",
            "Grand Empire",
            "empire_bloc",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["heat"] = 0,
                ["patrols"] = 2,
                ["max_patrols"] = 2,
                ["informants"] = 1,
                ["max_informants"] = 1,
                ["warrants"] = 1,
                ["max_warrants"] = 1,
                ["defenses"] = 3,
                ["max_defenses"] = 3,
                ["response_cooldown_until"] = 0,
            },
            new[] { "player", "resistance" }),
        new FactionDefinition(
            "hollowmere",
            "Hollowmere",
            "resistance",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["support"] = 1,
                ["max_support"] = 1,
            },
            new[] { "empire_bloc" }),
    };
}
