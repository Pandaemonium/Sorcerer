namespace Sorcerer.Core.Lore;

public static class LoreCardLoader
{
    public static IReadOnlyList<LoreCard> LoadDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return Array.Empty<LoreCard>();
        }

        return Directory.EnumerateFiles(directory, "*.md", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => LoadMarkdown(Path.GetFileNameWithoutExtension(path), File.ReadAllText(path)))
            .Where(card => !card.Draft)
            .ToArray();
    }

    public static LoreCard LoadMarkdown(string fallbackId, string markdown)
    {
        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var metadata = ReadMetadata(lines, out var metadataStart, out var metadataEnd);
        var bodyLines = lines
            .Where((_, index) => index < metadataStart || index > metadataEnd)
            .ToArray();

        var title = ReadTitle(bodyLines)
            ?? ReadMetadataValue(metadata, "title")
            ?? fallbackId.Replace('_', ' ');
        var id = ReadMetadataValue(metadata, "id") ?? fallbackId;
        var subjects = ReadMetadataList(metadata, "subjects");
        var triggers = ReadMetadataList(metadata, "triggers");
        var draft = ReadMetadataBool(metadata, "draft");
        var sections = ReadSections(bodyLines);

        return new LoreCard(
            LoreCatalog.NormalizeToken(id),
            title.Trim(),
            subjects,
            triggers,
            sections,
            draft);
    }

    private static Dictionary<string, string> ReadMetadata(
        IReadOnlyList<string> lines,
        out int start,
        out int end)
    {
        start = -1;
        end = -1;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            start = index;
            for (var inner = index + 1; inner < lines.Count; inner++)
            {
                if (lines[inner].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    end = inner;
                    return values;
                }

                var parts = lines[inner].Split(':', 2);
                if (parts.Length == 2)
                {
                    values[parts[0].Trim()] = parts[1].Trim();
                }
            }

            break;
        }

        return values;
    }

    private static string? ReadTitle(IEnumerable<string> lines) =>
        lines
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith("# ", StringComparison.Ordinal))?
            .TrimStart('#')
            .Trim();

    private static IReadOnlyList<LoreSection> ReadSections(IReadOnlyList<string> lines)
    {
        var sections = new List<LoreSection>();
        var currentLevel = 1;
        var currentDraft = false;
        var buffer = new List<string>();

        void Flush()
        {
            var text = string.Join("\n", buffer).Trim();
            if (!string.IsNullOrWhiteSpace(text) && !currentDraft)
            {
                sections.Add(new LoreSection(currentLevel, text, currentDraft));
            }

            buffer.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Flush();
                currentLevel = ReadLevel(line);
                currentDraft = line.Contains("draft", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                continue;
            }

            buffer.Add(rawLine);
        }

        Flush();
        return sections.Count == 0
            ? Array.Empty<LoreSection>()
            : sections.OrderBy(section => section.Level).ToArray();
    }

    private static int ReadLevel(string heading)
    {
        var words = heading.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = 0; index < words.Length - 1; index++)
        {
            if (words[index].Equals("Level", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(words[index + 1], out var level))
            {
                return Math.Max(0, level);
            }
        }

        return 1;
    }

    private static string? ReadMetadataValue(IReadOnlyDictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static IReadOnlyList<string> ReadMetadataList(IReadOnlyDictionary<string, string> metadata, string key) =>
        ReadMetadataValue(metadata, key)?
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray()
        ?? Array.Empty<string>();

    private static bool ReadMetadataBool(IReadOnlyDictionary<string, string> metadata, string key) =>
        ReadMetadataValue(metadata, key) is { } value
        && bool.TryParse(value, out var parsed)
        && parsed;
}
