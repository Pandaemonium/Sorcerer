using System.Text.Json;
using Sorcerer.Core.Telemetry;

namespace Sorcerer.Llm.Diagnostics;

/// <summary>
/// Reads Ollama's per-call token/timing counters from a /api/chat response root. Durations are
/// reported by Ollama in nanoseconds; they are converted to milliseconds here. Returns null when
/// no counter is present (error payloads, non-Ollama backends).
/// </summary>
internal static class OllamaStats
{
    public static ProviderCallStats? From(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var promptTokens = ReadInt(root, "prompt_eval_count");
        var outputTokens = ReadInt(root, "eval_count");
        var loadMs = ReadNanosAsMs(root, "load_duration");
        var promptMs = ReadNanosAsMs(root, "prompt_eval_duration");
        var generationMs = ReadNanosAsMs(root, "eval_duration");
        var totalMs = ReadNanosAsMs(root, "total_duration");
        if (promptTokens is null && outputTokens is null && totalMs is null)
        {
            return null;
        }

        return new ProviderCallStats(promptTokens, outputTokens, loadMs, promptMs, generationMs, totalMs);
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;

    private static double? ReadNanosAsMs(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out var nanos)
                ? nanos / 1_000_000.0
                : null;
}
