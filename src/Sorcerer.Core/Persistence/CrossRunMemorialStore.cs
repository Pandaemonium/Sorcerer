using System.Text.Json;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Persistence;

public static class CrossRunMemorialStore
{
    public static string DefaultPath => Path.Combine("runs", "memorials.jsonl");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static RunChronicleRecord AppendLatestChronicle(GameState state, string? path = null)
    {
        var chronicle = RunChronicle.Build(state);
        Append(chronicle, path ?? DefaultPath);
        return chronicle;
    }

    public static void Append(RunChronicleRecord chronicle, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.AppendAllText(path, JsonSerializer.Serialize(chronicle, JsonOptions) + Environment.NewLine);
    }

    public static IReadOnlyList<RunChronicleRecord> Load(string path)
    {
        if (!File.Exists(path))
        {
            return Array.Empty<RunChronicleRecord>();
        }

        var records = new List<RunChronicleRecord>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<RunChronicleRecord>(line, JsonOptions);
                if (record is not null)
                {
                    records.Add(record);
                }
            }
            catch (JsonException)
            {
                // Memorials are commemorative only. A bad line should never block a new run.
            }
        }

        return records;
    }

    public static IReadOnlyList<RunChronicleRecord> LoadDefault() => Load(DefaultPath);
}
