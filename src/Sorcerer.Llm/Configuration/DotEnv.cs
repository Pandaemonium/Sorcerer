namespace Sorcerer.Llm.Configuration;

/// <summary>
/// Minimal dependency-free .env loader for standalone builds. Values already present in the
/// process environment always win, so CI, launch scripts, and purpose-specific overrides retain
/// authority. The loader never logs keys or values.
/// </summary>
public static class DotEnv
{
    public static string? Load(string? path = null)
    {
        var resolved = string.IsNullOrWhiteSpace(path) ? FindDefaultPath() : Path.GetFullPath(path);
        if (resolved is null || !File.Exists(resolved))
        {
            return null;
        }

        foreach (var rawLine in File.ReadLines(resolved))
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.OrdinalIgnoreCase))
            {
                line = line[7..].TrimStart();
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!ValidKey(key) || Environment.GetEnvironmentVariable(key) is not null)
            {
                continue;
            }

            Environment.SetEnvironmentVariable(key, ParseValue(line[(separator + 1)..]));
        }

        return resolved;
    }

    private static string? FindDefaultPath()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null && visited.Add(directory.FullName))
            {
                var candidate = Path.Combine(directory.FullName, ".env");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                directory = directory.Parent;
            }
        }

        return null;
    }

    private static bool ValidKey(string key) =>
        key.Length > 0
        && (char.IsLetter(key[0]) || key[0] == '_')
        && key.All(character => char.IsLetterOrDigit(character) || character == '_');

    private static string ParseValue(string raw)
    {
        var value = raw.Trim();
        if (value.Length >= 2 && value[0] == value[^1] && value[0] is '\'' or '"')
        {
            value = value[1..^1];
        }

        return value
            .Replace("\\n", "\n", StringComparison.Ordinal)
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal)
            .Replace("\\\\", "\\", StringComparison.Ordinal);
    }
}
