using System.Text.Json;
using Sorcerer.Core.Telemetry;

namespace Sorcerer.Magic.Auditing;

/// <summary>
/// Opt-in resolver self-critique. When enabled, the wild-magic prompt asks the model two extra
/// questions — what capability would have let it resolve the spell more faithfully, and which
/// provided context was irrelevant — and each answer is appended to a JSONL corpus. The point is a
/// standing body of independent, resolver-side feedback we can mine to grow real capabilities and
/// to trim the prompt toward only the context that actually earns its tokens (latency work).
///
/// This is a side channel: the feedback never affects resolution, validation, cost, or apply, and
/// the whole feature is inert unless the flag is set. Enable via SORCERER_RESOLVER_FEEDBACK=1 (an
/// .env entry works — DotEnv.Load runs at startup).
/// </summary>
public static class ResolverFeedbackConfig
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("SORCERER_RESOLVER_FEEDBACK")?.Trim().ToLowerInvariant()
            is "1" or "true" or "yes" or "on";

    public static string CorpusPath =>
        Environment.GetEnvironmentVariable("SORCERER_RESOLVER_FEEDBACK_PATH") is { Length: > 0 } path
            ? path
            : Path.Combine("logs", "resolver_feedback.jsonl");
}

/// <summary>The two critique fields the resolver is asked to return, parsed back out of its answer.</summary>
public sealed record ResolverFeedback(string? MissingCapability, string? UnusedContext)
{
    public bool HasContent =>
        !string.IsNullOrWhiteSpace(MissingCapability) || !string.IsNullOrWhiteSpace(UnusedContext);

    /// <summary>
    /// Pull the optional "feedback" object out of a resolver answer. Tolerant of prose around the
    /// JSON and of camelCase/snake_case keys; returns null when no usable feedback is present so a
    /// model that ignored the ask (or a mock provider) simply writes nothing.
    /// </summary>
    public static ResolverFeedback? TryExtract(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return null;
        }

        if (!TryParseObject(rawText, out var root)
            || !root.TryGetProperty("feedback", out var feedback)
            || feedback.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var missing = ReadString(feedback, "missingCapability", "missing_capability");
        var unused = ReadString(feedback, "unusedContext", "unused_context");
        var result = new ResolverFeedback(missing, unused);
        return result.HasContent ? result : null;
    }

    private static bool TryParseObject(string rawText, out JsonElement root)
    {
        var text = rawText.Trim();
        try
        {
            using var document = JsonDocument.Parse(text);
            root = document.RootElement.Clone();
            return root.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            // The answer may carry stray prose; retry on the outermost brace-delimited slice.
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                root = default;
                return false;
            }

            try
            {
                using var document = JsonDocument.Parse(text[start..(end + 1)]);
                root = document.RootElement.Clone();
                return root.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                root = default;
                return false;
            }
        }
    }

    private static string? ReadString(JsonElement element, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (element.TryGetProperty(key, out var value)
                && value.ValueKind == JsonValueKind.String
                && value.GetString() is { } text
                && !string.IsNullOrWhiteSpace(text))
            {
                return text.Trim();
            }
        }

        return null;
    }
}

/// <summary>One corpus row: the critique plus the metadata needed to act on it — how big the
/// context was, what was routed in, and how the call performed.</summary>
public sealed record ResolverFeedbackEntry(
    DateTimeOffset Timestamp,
    string Provider,
    string SpellText,
    bool? Accepted,
    string? MissingCapability,
    string? UnusedContext,
    IReadOnlyList<string> SelectedCapabilities,
    int SupportedOperationCount,
    int ContextPayloadBytes,
    ProviderCallStats? ProviderStats);

public interface IResolverFeedbackSink
{
    void Record(ResolverFeedbackEntry entry);
}

public sealed class NullResolverFeedbackSink : IResolverFeedbackSink
{
    public static NullResolverFeedbackSink Instance { get; } = new();

    private NullResolverFeedbackSink()
    {
    }

    public void Record(ResolverFeedbackEntry entry)
    {
    }
}

public sealed class JsonlResolverFeedbackSink : IResolverFeedbackSink
{
    private readonly string _path;
    private readonly JsonSerializerOptions _options = new(JsonSerializerDefaults.Web);

    public JsonlResolverFeedbackSink(string path)
    {
        _path = path;
    }

    public void Record(ResolverFeedbackEntry entry)
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.AppendAllText(_path, JsonSerializer.Serialize(entry, _options) + Environment.NewLine);
        }
        catch
        {
            // A feedback-logging failure must never break a cast.
        }
    }
}
