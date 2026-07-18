using System.Text.Json;

namespace Sorcerer.Core.World;

/// <summary>
/// WP8 (docs/CONTENT_SPRINT_PLAN.md): curated cost profiles for curses, debts, and altered items.
/// These are guidance/content — a curse/debt/altered-item is described by its mechanical condition,
/// its cause, how it surfaces in the journal, and at least one general route to clear, transfer,
/// exploit, bargain over, or endure it — never a spell-phrase handler. Readers (resolver capability
/// guidance, journal, dialogue context) draw on these so a magical cost creates future play instead
/// of merely subtracting HP or mana.
/// </summary>
public sealed record CostProfile(
    string Id,
    string Kind,
    string Name,
    string Condition,
    string Cause,
    string JournalSurface,
    IReadOnlyList<string> ClearRoutes,
    IReadOnlyList<string> Tags);

public sealed class CostProfileCatalog
{
    private readonly Dictionary<string, CostProfile> _profiles = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CostProfile> Profiles => _profiles.Values;

    public IReadOnlyList<CostProfile> OfKind(string kind) =>
        _profiles.Values.Where(p => p.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase))
            .OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public CostProfile? Find(string id) => _profiles.TryGetValue(id, out var profile) ? profile : null;

    private void Add(CostProfile profile) => _profiles[profile.Id] = profile;

    private static readonly Lazy<CostProfileCatalog> DefaultCatalog = new(LoadDefault);

    public static CostProfileCatalog Default => DefaultCatalog.Value;

    public static CostProfileCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "costs");
            if (Directory.Exists(directory))
            {
                var catalog = new CostProfileCatalog();
                foreach (var path in Directory.EnumerateFiles(directory, "*.json", SearchOption.AllDirectories)
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    ReadDocument(catalog, File.ReadAllText(path));
                }

                if (catalog._profiles.Count > 0)
                {
                    return catalog;
                }
            }
        }

        return LoadEmbedded();
    }

    public static CostProfileCatalog LoadEmbedded()
    {
        var catalog = new CostProfileCatalog();
        var assembly = typeof(CostProfileCatalog).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Content.Costs.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream);
            ReadDocument(catalog, reader.ReadToEnd());
        }

        return catalog;
    }

    private static void ReadDocument(CostProfileCatalog catalog, string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in profiles.EnumerateArray())
        {
            var profile = new CostProfile(
                Str(element, "id"),
                Str(element, "kind"),
                Str(element, "name"),
                Str(element, "condition"),
                Str(element, "cause"),
                Str(element, "journalSurface"),
                StrList(element, "clearRoutes"),
                StrList(element, "tags"));
            if (!string.IsNullOrWhiteSpace(profile.Id))
            {
                catalog.Add(profile);
            }
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static IReadOnlyList<string> StrList(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString()!)
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : Array.Empty<string>();

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
