using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Core.Telemetry;

namespace Sorcerer.Llm;

/// <summary>
/// Native Anthropic Messages API client. Claude Sonnet 5 does not accept non-default sampling
/// parameters, so this client deliberately ignores the shared temperature hint and uses adaptive
/// thinking plus the configured effort level. Provider output remains untrusted JSON prose; the
/// ordinary Sorcerer parser/repair/validation path still decides what is real.
/// </summary>
internal sealed class AnthropicMessagesClient : IJsonChatClient
{
    private const string ApiVersion = "2023-06-01";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string _effort;
    private readonly string? _apiKey;

    public AnthropicMessagesClient(
        string endpoint,
        string model,
        string? effort = null,
        HttpClient? httpClient = null,
        string? apiKey = null)
    {
        _endpoint = NormalizeEndpoint(endpoint);
        _model = string.IsNullOrWhiteSpace(model) ? "claude-sonnet-5" : model.Trim();
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
                    // Sonnet 5 counts adaptive thinking and visible JSON against the same hard
                    // ceiling. Existing provider hints were sized for visible output only, so
                    // retain at least enough headroom for medium-effort reasoning.
                    max_tokens = Math.Max(4096, maxTokens),
                    system,
                    messages = new[]
                    {
                        new { role = "user", content = user },
                    },
                    thinking = new { type = "adaptive" },
                    output_config = new { effort = _effort },
                }, options: JsonOptions),
            };
            request.Headers.TryAddWithoutValidation("anthropic-version", ApiVersion);
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
            var stats = ParseStats(raw, stopwatch.Elapsed.TotalMilliseconds);
            if (!response.IsSuccessStatusCode)
            {
                return new JsonChatResult(
                    false,
                    "",
                    raw,
                    $"Anthropic Messages API returned HTTP {(int)response.StatusCode}.",
                    stats);
            }

            var content = ExtractContent(raw);
            return string.IsNullOrWhiteSpace(content)
                ? new JsonChatResult(false, "", raw, "Anthropic Messages API returned no text content.", stats)
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
        if (!document.RootElement.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return string.Join(
            "\n",
            content.EnumerateArray()
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

            var uncachedInput = ReadInt(usage, "input_tokens");
            var cacheWrite = ReadInt(usage, "cache_creation_input_tokens");
            var cacheRead = ReadInt(usage, "cache_read_input_tokens");
            var output = ReadInt(usage, "output_tokens");
            int? thinking = null;
            if (usage.TryGetProperty("output_tokens_details", out var details)
                && details.ValueKind == JsonValueKind.Object)
            {
                thinking = ReadInt(details, "thinking_tokens");
            }

            var totalInput = new[] { uncachedInput, cacheWrite, cacheRead }
                .Where(value => value.HasValue)
                .Sum(value => value!.Value);
            return new ProviderCallStats(
                PromptTokens: totalInput > 0 ? totalInput : null,
                OutputTokens: output,
                TotalMs: elapsedMs,
                CacheReadTokens: cacheRead,
                CacheWriteTokens: cacheWrite,
                ThinkingTokens: thinking);
        }
        catch (JsonException)
        {
            return new ProviderCallStats(TotalMs: elapsedMs);
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
            ? "https://api.anthropic.com/v1"
            : endpoint.Trim();
        return trimmed.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed.TrimEnd('/')}/messages";
    }

    private static string NormalizeEffort(string? effort)
    {
        var normalized = effort?.Trim().ToLowerInvariant();
        return normalized is "low" or "medium" or "high" or "xhigh" or "max"
            ? normalized
            : "medium";
    }

    private static string? DefaultApiKey() =>
        Environment.GetEnvironmentVariable("SORCERER_ANTHROPIC_API_KEY")
        ?? Environment.GetEnvironmentVariable("SORCERER_API_KEY")
        ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
