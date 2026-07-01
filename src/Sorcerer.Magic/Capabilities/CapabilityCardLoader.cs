using System.Text.Json;

namespace Sorcerer.Magic.Capabilities;

public static class CapabilityCardLoader
{
    public static IReadOnlyList<CapabilityCard> LoadDefaultContentCards()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "capabilities");
            var cards = LoadFrom(directory);
            if (cards.Count > 0)
            {
                return cards;
            }
        }

        return Array.Empty<CapabilityCard>();
    }

    public static IReadOnlyList<CapabilityCard> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<CapabilityCard>();
        }

        var cards = new List<CapabilityCard>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in capabilities.EnumerateArray())
                {
                    cards.Add(ReadCard(item));
                }
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                cards.Add(ReadCard(root));
            }
        }

        return cards;
    }

    private static CapabilityCard ReadCard(JsonElement root)
    {
        var id = ReadString(root, "id", "");
        return new CapabilityCard(
            id,
            ReadStringArray(root, "triggers"),
            ReadString(root, "indexLine", id),
            ReadStringArray(root, "effectTypes"),
            ReadStringArray(root, "requiredContext"),
            ReadString(root, "promptBlock", ""),
            ReadStringArray(root, "examples"),
            ReadStringArray(root, "commonCombos"),
            root.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.Number
                ? version.GetInt32()
                : 1);
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToArray()
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
