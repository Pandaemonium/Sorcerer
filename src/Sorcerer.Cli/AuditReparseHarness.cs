using System.Text.Json;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Cli;

/// <summary>
/// Re-runs the current <see cref="SpellResolutionJson"/> parser over the raw model outputs captured
/// in a spell audit JSONL file (see <c>JsonlSpellAuditSink</c>). This lets a parser change be
/// validated against real recorded responses without calling any provider again: it reports which
/// raw outputs now parse, which <em>recovered</em> (were unparsed when recorded but parse now), and
/// which changed shape (different effect count or acceptance). Phase 1 / Phase 9 of the WildMagic
/// import.
/// </summary>
public static class AuditReparseHarness
{
    public sealed record ReparseRow(
        string SpellText,
        bool Parsed,
        string? Error,
        bool RecordedParsed,
        int RecordedEffectCount,
        int CurrentEffectCount,
        bool RecordedAccepted,
        bool CurrentAccepted,
        bool Recovered,
        bool Changed);

    public sealed record ReparseSummary(
        int Total,
        int Parsed,
        int Failed,
        int Recovered,
        int Changed,
        IReadOnlyList<ReparseRow> Rows);

    /// <summary>
    /// Pure core: reparse each JSONL audit line and diff against the recorded resolution. Malformed
    /// or non-audit lines (no <c>rawText</c>) are skipped rather than throwing, so a partially
    /// written log still yields a report.
    /// </summary>
    public static ReparseSummary RunFromLines(IEnumerable<string> lines, OperationRegistry registry)
    {
        var rows = new List<ReparseRow>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                var rawText = ReadString(root, "rawText");
                if (string.IsNullOrWhiteSpace(rawText))
                {
                    continue;
                }

                var spellText = ReadString(root, "spellText");
                var (recordedParsed, recordedAccepted, recordedEffectCount) = ReadRecorded(root);

                bool parsed;
                string? error = null;
                var currentEffectCount = 0;
                var currentAccepted = false;
                try
                {
                    var resolution = SpellResolutionJson.Parse(rawText, registry);
                    parsed = true;
                    currentEffectCount = resolution.Effects.Count;
                    currentAccepted = resolution.Accepted;
                }
                catch (Exception exception)
                {
                    parsed = false;
                    error = exception.GetType().Name;
                }

                var recovered = parsed && !recordedParsed;
                var changed = parsed && recordedParsed
                    && (currentEffectCount != recordedEffectCount || currentAccepted != recordedAccepted);

                rows.Add(new ReparseRow(
                    spellText,
                    parsed,
                    error,
                    recordedParsed,
                    recordedEffectCount,
                    currentEffectCount,
                    recordedAccepted,
                    currentAccepted,
                    recovered,
                    changed));
            }
        }

        return new ReparseSummary(
            rows.Count,
            rows.Count(row => row.Parsed),
            rows.Count(row => !row.Parsed),
            rows.Count(row => row.Recovered),
            rows.Count(row => row.Changed),
            rows);
    }

    public static Task<int> RunAsync(string path, bool json, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"Audit file not found: {path}");
            return Task.FromResult(1);
        }

        var summary = RunFromLines(File.ReadLines(path), OperationRegistry.CreateDefault());

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                summary,
                new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
            return Task.FromResult(0);
        }

        Console.WriteLine(
            $"Audit reparse: {summary.Parsed}/{summary.Total} parse, {summary.Recovered} recovered, "
            + $"{summary.Changed} changed shape, {summary.Failed} still failing ({path}).");
        foreach (var row in summary.Rows.Where(row => row.Recovered || row.Changed || !row.Parsed))
        {
            var mark = !row.Parsed ? "FAIL" : row.Recovered ? "RECOVERED" : "CHANGED";
            var detail = row.Parsed
                ? $"effects {row.RecordedEffectCount}->{row.CurrentEffectCount}, accepted {row.RecordedAccepted}->{row.CurrentAccepted}"
                : row.Error;
            Console.WriteLine($"  {mark} | {row.SpellText} | {detail}");
        }

        return Task.FromResult(0);
    }

    private static (bool Parsed, bool Accepted, int EffectCount) ReadRecorded(JsonElement root)
    {
        if (!root.TryGetProperty("parsedResolution", out var resolution)
            || resolution.ValueKind != JsonValueKind.Object)
        {
            return (false, false, -1);
        }

        var accepted = resolution.TryGetProperty("accepted", out var acceptedValue)
            && acceptedValue.ValueKind is JsonValueKind.True or JsonValueKind.False
            && acceptedValue.GetBoolean();
        var effectCount = resolution.TryGetProperty("effects", out var effects)
            && effects.ValueKind == JsonValueKind.Array
            ? effects.GetArrayLength()
            : 0;
        return (true, accepted, effectCount);
    }

    private static string ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
