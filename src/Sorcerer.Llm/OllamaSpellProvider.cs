using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public sealed class OllamaSpellProvider : ISpellProvider
{
    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly OperationRegistry _registry;

    public OllamaSpellProvider(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        OperationRegistry? registry = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _httpClient = httpClient ?? new HttpClient();
        _registry = registry ?? OperationRegistry.CreateDefault();
    }

    public string Name => "ollama";

    public async Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken)
    {
        var supported = string.Join(", ", request.SupportedOperations);
        var system = "You are the wild magic resolver for Sorcerer. Return exactly one JSON object. "
            + "Use this shape: {\"accepted\":true,\"severity\":\"minor|moderate|major|catastrophic\","
            + "\"outcomeText\":\"short vivid result\",\"effects\":[],\"costs\":[],\"rejectedReason\":null}. "
            + $"Supported effect types: {supported}. The engine validates everything.";

        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = request.SpellText },
            },
        };

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"{_host}/api/chat",
                payload,
                cancellationToken);
            var rawResponse = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return Failure(rawResponse, $"Ollama returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(rawResponse);
            var content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            if (string.IsNullOrWhiteSpace(content))
            {
                return Failure(rawResponse, "Ollama returned an empty message.");
            }

            var resolution = SpellResolutionJson.Parse(content, _registry);
            return new SpellProviderResult(
                Name,
                RawText: content,
                Resolution: resolution,
                TechnicalFailure: false,
                Error: null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return Failure("", ex.Message);
        }
    }

    private SpellProviderResult Failure(string raw, string error) =>
        new(
            Name,
            RawText: raw,
            Resolution: null,
            TechnicalFailure: true,
            Error: error);
}

