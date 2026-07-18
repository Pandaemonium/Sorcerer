using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Core.Telemetry;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Llm;

/// <summary>
/// Native, stateless Gemini Interactions API client. Provider output remains candidate JSON:
/// Sorcerer's existing parse, repair, normalization, validation, and transactional apply path
/// still decides what becomes authoritative game state.
/// </summary>
internal sealed class GeminiInteractionsClient : IJsonChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _effort;
    private readonly string? _apiKey;

    public GeminiInteractionsClient(
        string endpoint,
        string model,
        string? effort = null,
        HttpClient? httpClient = null,
        string? apiKey = null)
    {
        _endpoint = NormalizeEndpoint(endpoint);
        _model = string.IsNullOrWhiteSpace(model) ? "gemini-3.5-flash" : model.Trim();
        _effort = NormalizeEffort(effort);
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? DefaultApiKey() : apiKey.Trim();
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<JsonChatResult> ChatAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken,
        string label = "llm")
    {
        var traceId = Diagnostics.LlmTrace.Begin(label, $"{_model} ({_effort})", system, user);
        var result = await SendAsync(system, user, maxTokens, cancellationToken);
        Diagnostics.LlmTrace.End(
            traceId,
            result.Success ? result.Content : result.RawText,
            result.Error,
            result.Stats);
        return result;
    }

    private async Task<JsonChatResult> SendAsync(
        string system,
        string user,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var raw = string.Empty;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = _model,
                    input = user,
                    system_instruction = system,
                    // Provider state must not become a hidden second source of game truth.
                    store = false,
                    response_format = new
                    {
                        type = "text",
                        mime_type = "application/json",
                        // Every Sorcerer provider lane returns a JSON object, with the exact
                        // shape still enforced by that lane's engine-owned parser. Gemini treats
                        // an otherwise-empty object schema as forbidding every property, so the
                        // open-content rule must be explicit.
                        schema = new { type = "object", additionalProperties = true },
                    },
                    generation_config = new
                    {
                        // Shared hints describe visible JSON. Preserve room for Gemini's
                        // internal thinking, which shares the output ceiling.
                        max_output_tokens = Math.Max(4096, maxTokens),
                        thinking_level = _effort,
                    },
                }, options: JsonOptions),
            };
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.TryAddWithoutValidation("x-goog-api-key", _apiKey);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var stats = ParseStats(raw, stopwatch.Elapsed.TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                var detail = ParseError(raw);
                var error = $"Gemini Interactions API returned HTTP {(int)response.StatusCode}.";
                if (!string.IsNullOrWhiteSpace(detail))
                {
                    error = $"{error} {detail}";
                }

                return new JsonChatResult(false, "", raw, error, stats);
            }

            var content = ExtractContent(raw);
            return string.IsNullOrWhiteSpace(content)
                ? new JsonChatResult(false, "", raw, "Gemini Interactions API returned no text content.", stats)
                : new JsonChatResult(true, content, raw, null, stats);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return new JsonChatResult(
                false,
                "",
                raw,
                ex.Message,
                new ProviderCallStats(TotalMs: stopwatch.Elapsed.TotalMilliseconds));
        }
    }

    private static string ExtractContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            steps.EnumerateArray()
                .Where(step => step.ValueKind == JsonValueKind.Object
                    && step.TryGetProperty("type", out var stepType)
                    && stepType.ValueKind == JsonValueKind.String
                    && stepType.GetString()?.Equals("model_output", StringComparison.OrdinalIgnoreCase) == true
                    && step.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                .SelectMany(step => step.GetProperty("content").EnumerateArray())
                .Where(block => block.ValueKind == JsonValueKind.Object
                    && block.TryGetProperty("type", out var type)
                    && type.ValueKind == JsonValueKind.String
                    && type.GetString()?.Equals("text", StringComparison.OrdinalIgnoreCase) == true
                    && block.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                .Select(block => block.GetProperty("text").GetString())
                .Where(text => !string.IsNullOrWhiteSpace(text)))
            .Trim();
    }

    private static ProviderCallStats ParseStats(string raw, double elapsedMs)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ProviderCallStats(TotalMs: elapsedMs);
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            if (!document.RootElement.TryGetProperty("usage", out var usage)
                || usage.ValueKind != JsonValueKind.Object)
            {
                return new ProviderCallStats(TotalMs: elapsedMs);
            }

            return new ProviderCallStats(
                PromptTokens: ReadInt(usage, "total_input_tokens"),
                OutputTokens: ReadInt(usage, "total_output_tokens"),
                TotalMs: elapsedMs,
                CacheReadTokens: ReadInt(usage, "total_cached_tokens"),
                ThinkingTokens: ReadInt(usage, "total_thought_tokens"));
        }
        catch (JsonException)
        {
            return new ProviderCallStats(TotalMs: elapsedMs);
        }
    }

    private static string? ParseError(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(raw);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object
                && error.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.String
                    ? message.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var parsed)
            ? parsed
            : null;

    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = string.IsNullOrWhiteSpace(endpoint)
            ? "https://generativelanguage.googleapis.com/v1beta"
            : endpoint.Trim();
        return trimmed.EndsWith("/interactions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed.TrimEnd('/')}/interactions";
    }

    private static string NormalizeEffort(string? effort)
    {
        var normalized = effort?.Trim().ToLowerInvariant();
        return normalized is "minimal" or "low" or "medium" or "high"
            ? normalized
            : "medium";
    }

    private static string? DefaultApiKey() => GeminiApiKeySetup.Resolve();

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
