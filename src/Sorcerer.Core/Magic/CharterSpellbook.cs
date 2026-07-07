using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sorcerer.Core.Magic;

/// <summary>
/// A charter spell is an authored operation bundle: fixed effects, fixed price, fixed
/// narration, resolved through the ordinary wild-magic pipeline with the provider stage
/// removed (docs/CHARTER_MAGIC.md). Charter spells are content, not code: bundles load from
/// content/charter/*.json and each cast materializes into the same resolution JSON the
/// resolver would have produced, so validation, transactional apply, costs, and auditing
/// are byte-for-byte the shared path.
/// </summary>
public sealed record CharterSpell(
    string Id,
    string Name,
    string Summary,
    string Line,
    IReadOnlyList<string> EffectJson,
    IReadOnlyList<string> EffectTypes,
    IReadOnlyDictionary<string, int> Cost,
    string Targeting,
    int LicenseTier)
{
    public string CostText =>
        Cost.Count == 0
            ? "free"
            : string.Join(", ", Cost.Select(entry => $"{entry.Value} {entry.Key}"));

    /// <summary>The fixed resolution this spell always materializes into. Instant: no model.</summary>
    public string BuildResolvedMagicJson()
    {
        var effects = new JsonArray();
        foreach (var effect in EffectJson)
        {
            effects.Add(JsonNode.Parse(effect));
        }

        var costs = new JsonArray();
        foreach (var entry in Cost)
        {
            costs.Add(new JsonObject
            {
                ["type"] = entry.Key,
                ["amount"] = entry.Value,
            });
        }

        var root = new JsonObject
        {
            ["accepted"] = true,
            ["severity"] = "minor",
            ["outcomeText"] = Line,
            ["effects"] = effects,
            ["costs"] = costs,
            ["rejectedReason"] = null,
        };
        return root.ToJsonString();
    }
}

public sealed class CharterSpellbook
{
    private static readonly Lazy<CharterSpellbook> DefaultBook = new(LoadDefault);

    private readonly Dictionary<string, CharterSpell> _spells;
    private readonly List<string> _order = new();

    private CharterSpellbook(IEnumerable<CharterSpell> spells)
    {
        _spells = new Dictionary<string, CharterSpell>(StringComparer.OrdinalIgnoreCase);
        foreach (var spell in spells)
        {
            if (_spells.TryAdd(spell.Id, spell))
            {
                _order.Add(spell.Id);
            }
        }
    }

    public static CharterSpellbook Default => DefaultBook.Value;

    public IReadOnlyList<CharterSpell> Spells => _order.Select(id => _spells[id]).ToArray();

    /// <summary>Matches by id or display name, tolerant of spaces-for-underscores.</summary>
    public CharterSpell? Find(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return null;
        }

        var trimmed = reference.Trim();
        if (_spells.TryGetValue(trimmed, out var byId)
            || _spells.TryGetValue(trimmed.Replace(' ', '_'), out byId))
        {
            return byId;
        }

        return _spells.Values.FirstOrDefault(spell =>
            spell.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public static CharterSpellbook LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "charter");
            var spells = LoadFrom(directory);
            if (spells.Count > 0)
            {
                return new CharterSpellbook(spells);
            }
        }

        return new CharterSpellbook(Array.Empty<CharterSpell>());
    }

    public static IReadOnlyList<CharterSpell> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<CharterSpell>();
        }

        var spells = new List<CharterSpell>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("spells", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                spells.AddRange(array.EnumerateArray().Select(ReadSpell));
            }
            else if (root.ValueKind == JsonValueKind.Array)
            {
                spells.AddRange(root.EnumerateArray().Select(ReadSpell));
            }
        }

        return spells
            .Where(spell => !string.IsNullOrWhiteSpace(spell.Id) && spell.EffectJson.Count > 0)
            .ToArray();
    }

    private static CharterSpell ReadSpell(JsonElement root)
    {
        var effectJson = new List<string>();
        var effectTypes = new List<string>();
        if (root.TryGetProperty("effects", out var effects) && effects.ValueKind == JsonValueKind.Array)
        {
            foreach (var effect in effects.EnumerateArray())
            {
                if (effect.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                effectJson.Add(effect.GetRawText());
                if (effect.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                {
                    effectTypes.Add(type.GetString() ?? "");
                }
            }
        }

        var cost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("cost", out var costRoot) && costRoot.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in costRoot.EnumerateObject())
            {
                if (entry.Value.TryGetInt32(out var amount) && amount > 0)
                {
                    cost[entry.Name] = amount;
                }
            }
        }

        var id = ReadString(root, "id", "");
        return new CharterSpell(
            id,
            ReadString(root, "name", id),
            ReadString(root, "summary", ""),
            ReadString(root, "line", "The charter form completes itself, cold and exact."),
            effectJson,
            effectTypes,
            cost,
            ReadString(root, "targeting", "self"),
            root.TryGetProperty("licenseTier", out var tier) && tier.TryGetInt32(out var tierValue) ? tierValue : 1);
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
}
