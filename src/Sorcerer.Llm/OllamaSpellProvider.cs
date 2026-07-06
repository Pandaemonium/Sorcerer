using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public sealed class OllamaSpellProvider : ISpellProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly OperationRegistry _registry;
    private readonly TimeSpan _timeout;

    public OllamaSpellProvider(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        OperationRegistry? registry = null,
        TimeSpan? timeout = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _httpClient = httpClient ?? CreateHttpClient();
        _registry = registry ?? OperationRegistry.CreateDefault();
        _timeout = timeout ?? TimeSpan.FromSeconds(240);
    }

    public string Name => "ollama";

    public async Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var rawResponse = string.Empty;
        var content = string.Empty;
        var system = SpellPromptBuilder.System(request);
        var user = SpellPromptBuilder.User(request);
        var traceId = Diagnostics.LlmTrace.Begin("wild", _model, system, user);

        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            think = false,
            options = new
            {
                temperature = 0.2,
                num_ctx = OllamaDefaults.NumCtx,
                num_predict = 1200,
            },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_host}/api/chat",
                payload,
                timeout.Token);
            rawResponse = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var httpError = $"Ollama returned HTTP {(int)response.StatusCode}.";
                Diagnostics.LlmTrace.End(traceId, rawResponse, httpError);
                return Failure(rawResponse, httpError);
            }

            using var document = JsonDocument.Parse(rawResponse);
            var stats = Diagnostics.OllamaStats.From(document.RootElement);
            content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(content))
            {
                Diagnostics.LlmTrace.End(traceId, rawResponse, "Ollama returned an empty message.", stats);
                return Failure(rawResponse, "Ollama returned an empty message.");
            }

            // The model answered; record it now. A JSON-repair retry (below) is logged separately.
            Diagnostics.LlmTrace.End(traceId, content, null, stats);

            if (SpellResolutionJson.TryReadNeedsCapability(content) is { } requestedCapability)
            {
                return new SpellProviderResult(Name, content, Resolution: null, TechnicalFailure: false, Error: null, stats, requestedCapability);
            }

            try
            {
                var resolution = SpellResolutionJson.Parse(content, _registry);
                return Success(content, resolution, stats);
            }
            catch (JsonException ex)
            {
                return await RetryAfterInvalidJsonAsync(
                    request,
                    content,
                    ex.Message,
                    timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var raw = string.IsNullOrWhiteSpace(content) ? rawResponse : content;
            Diagnostics.LlmTrace.End(traceId, raw, ex.Message);
            return Failure(raw, ex.Message);
        }
    }

    private async Task<SpellProviderResult> RetryAfterInvalidJsonAsync(
        SpellRequest request,
        string invalidContent,
        string parseError,
        CancellationToken cancellationToken)
    {
        var rawResponse = string.Empty;
        var repairContent = string.Empty;
        var repairSystem = SpellPromptBuilder.RepairSystem();
        var repairUser = SpellPromptBuilder.RepairUser(request, invalidContent, parseError);
        var traceId = Diagnostics.LlmTrace.Begin("wild-repair", _model, repairSystem, repairUser);

        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            think = false,
            options = new
            {
                temperature = 0.1,
                num_ctx = OllamaDefaults.NumCtx,
                num_predict = 1200,
            },
            messages = new[]
            {
                new { role = "system", content = repairSystem },
                new { role = "user", content = repairUser },
            },
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_host}/api/chat",
                payload,
                cancellationToken);
            rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var httpError = $"Ollama returned HTTP {(int)response.StatusCode} after JSON repair.";
                Diagnostics.LlmTrace.End(traceId, rawResponse, httpError);
                return Failure(rawResponse, httpError);
            }

            using var document = JsonDocument.Parse(rawResponse);
            var stats = Diagnostics.OllamaStats.From(document.RootElement);
            repairContent = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(repairContent))
            {
                Diagnostics.LlmTrace.End(traceId, rawResponse, "Ollama returned invalid JSON, then an empty repair message.", stats);
                return Failure(invalidContent, "Ollama returned invalid JSON, then an empty repair message.");
            }

            Diagnostics.LlmTrace.End(traceId, repairContent, null, stats);
            var resolution = SpellResolutionJson.Parse(repairContent, _registry);
            return Success(repairContent, resolution, stats);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var raw = string.IsNullOrWhiteSpace(repairContent)
                ? string.IsNullOrWhiteSpace(rawResponse) ? invalidContent : rawResponse
                : repairContent;
            Diagnostics.LlmTrace.End(traceId, raw, $"Ollama returned invalid JSON, then repair failed: {ex.Message}");
            return Failure(raw, $"Ollama returned invalid JSON, then repair failed: {ex.Message}");
        }
    }

    private SpellProviderResult Success(string raw, SpellResolution resolution, Sorcerer.Core.Telemetry.ProviderCallStats? stats = null) =>
        new(
            Name,
            RawText: raw,
            Resolution: resolution,
            TechnicalFailure: false,
            Error: null,
            Stats: stats);

    private SpellProviderResult Failure(string raw, string error) =>
        new(
            Name,
            RawText: raw,
            Resolution: null,
            TechnicalFailure: true,
            Error: error);

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
