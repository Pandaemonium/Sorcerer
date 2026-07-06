using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Magic.Capabilities;

namespace Sorcerer.Llm;

/// <summary>
/// Capability router backed by a local Ollama chat model (typically the same resident model as the
/// resolver, so no second model is loaded). The call is tiny: JSON format, no chain-of-thought, a
/// small output budget. Any failure returns <see cref="SpellRouteResult.TechnicalFailure"/> so the
/// controller falls back to keyword routing.
/// </summary>
public sealed class OllamaSpellRouter : ISpellRouter
{
    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public OllamaSpellRouter(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _httpClient = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "ollama-router";

    public async Task<SpellRouteResult> RouteAsync(
        string spellText,
        string capabilityIndex,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var system = SpellRouterPrompt.System(capabilityIndex);
        var user = SpellRouterPrompt.User(spellText);
        var traceId = Diagnostics.LlmTrace.Begin("wild-router", _model, system, user);
        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            think = false,
            options = new
            {
                temperature = 0.0,
                num_ctx = OllamaDefaults.NumCtx,
                num_predict = 128,
            },
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user },
            },
        };

        var rawResponse = string.Empty;
        try
        {
            var response = await _httpClient.PostAsJsonAsync($"{_host}/api/chat", payload, timeout.Token);
            rawResponse = await response.Content.ReadAsStringAsync(timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var httpError = $"Ollama router returned HTTP {(int)response.StatusCode}.";
                Diagnostics.LlmTrace.End(traceId, rawResponse, httpError);
                return SpellRouterPrompt.Failure(rawResponse, httpError);
            }

            using var document = JsonDocument.Parse(rawResponse);
            var stats = Diagnostics.OllamaStats.From(document.RootElement);
            var content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            Diagnostics.LlmTrace.End(traceId, content, null, stats);
            return SpellRouterPrompt.From(Name, rawResponse, content);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            Diagnostics.LlmTrace.End(traceId, rawResponse, ex.Message);
            return SpellRouterPrompt.Failure(rawResponse, ex.Message);
        }
    }
}
