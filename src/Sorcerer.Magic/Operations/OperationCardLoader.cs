using System.Text.Json;

namespace Sorcerer.Magic.Operations;

public static class OperationCardLoader
{
    public static IReadOnlyList<OperationCard> LoadDefaultContentCards()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "operations");
            var cards = LoadFrom(directory);
            if (cards.Count > 0)
            {
                return cards;
            }
        }

        return Array.Empty<OperationCard>();
    }

    public static IReadOnlyList<OperationCard> LoadFrom(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<OperationCard>();
        }

        var cards = new List<OperationCard>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.TryGetProperty("operations", out var operations) && operations.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in operations.EnumerateArray())
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

    private static OperationCard ReadCard(JsonElement root)
    {
        var name = ReadString(root, "name", "");
        var aliases = root.TryGetProperty("aliases", out var aliasArray) && aliasArray.ValueKind == JsonValueKind.Array
            ? aliasArray.EnumerateArray().Select(alias => alias.GetString() ?? "").Where(alias => alias.Length > 0).ToArray()
            : Array.Empty<string>();
        var fields = root.TryGetProperty("fields", out var fieldsElement) && fieldsElement.ValueKind == JsonValueKind.Object
            ? fieldsElement.EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString() ?? "", StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var examples = root.TryGetProperty("examples", out var examplesElement) && examplesElement.ValueKind == JsonValueKind.Array
            ? examplesElement.EnumerateArray().Select(example => (object)example.GetRawText()).ToArray()
            : Array.Empty<object>();
        return new OperationCard(
            name,
            aliases,
            ReadString(root, "summary", name),
            ReadString(root, "promptGuidance", ReadString(root, "prompt_guidance", "")),
            fields,
            examples);
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
