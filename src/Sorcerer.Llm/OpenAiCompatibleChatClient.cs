using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace Sorcerer.Llm;

internal sealed class OpenAiCompatibleChatClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _endpoint;
    private readonly string _model;
    private readonly string? _apiKey;

    public OpenAiCompatibleChatClient(
        string endpoint,
        string model,
        HttpClient? httpClient = null,
        string? apiKey = null)
    {
        _endpoint = NormalizeEndpoint(endpoint);
        _model = model;
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? DefaultApiKey() : apiKey;
        _httpClient = httpClient ?? CreateHttpClient();
    }

    public async Task<OpenAiChatResult> ChatAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken,
        string label = "llm")
    {
        // Record the prompt before the request leaves so the debug view shows it while the model works.
        var traceId = Diagnostics.LlmTrace.Begin(label, _model, system, user);
        var result = await SendAsync(system, user, temperature, maxTokens, cancellationToken);
        Diagnostics.LlmTrace.End(traceId, result.Success ? result.Content : result.RawText, result.Error);
        return result;
    }

    private async Task<OpenAiChatResult> SendAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var raw = string.Empty;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = _model,
                    messages = new[]
                    {
                        new { role = "system", content = system },
                        new { role = "user", content = user },
                    },
                    temperature,
                    max_tokens = maxTokens,
                    response_format = new { type = "json_object" },
                }, options: JsonOptions),
            };

            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            }

            var response = await _httpClient.SendAsync(request, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new OpenAiChatResult(false, "", raw, $"OpenAI-compatible endpoint returned HTTP {(int)response.StatusCode}.");
            }

            var content = ExtractContent(raw);
            return string.IsNullOrWhiteSpace(content)
                ? new OpenAiChatResult(false, "", raw, "OpenAI-compatible endpoint returned an empty message.")
                : new OpenAiChatResult(true, content, raw, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        {
            return new OpenAiChatResult(false, "", raw, ex.Message);
        }
    }

    private static string ExtractContent(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }

            if (choice.TryGetProperty("text", out var text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
        }

        if (root.TryGetProperty("message", out var topMessage)
            && topMessage.ValueKind == JsonValueKind.Object
            && topMessage.TryGetProperty("content", out var topContent)
            && topContent.ValueKind == JsonValueKind.String)
        {
            return topContent.GetString() ?? string.Empty;
        }

        if (root.TryGetProperty("content", out var directContent)
            && directContent.ValueKind == JsonValueKind.String)
        {
            return directContent.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeEndpoint(string endpoint)
    {
        var trimmed = string.IsNullOrWhiteSpace(endpoint)
            ? "https://api.openai.com/v1"
            : endpoint.Trim();
        return trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"{trimmed.TrimEnd('/')}/chat/completions";
    }

    private static string? DefaultApiKey() =>
        Environment.GetEnvironmentVariable("SORCERER_OPENAI_API_KEY")
        ?? Environment.GetEnvironmentVariable("SORCERER_API_KEY")
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

internal sealed record OpenAiChatResult(
    bool Success,
    string Content,
    string RawText,
    string? Error);
