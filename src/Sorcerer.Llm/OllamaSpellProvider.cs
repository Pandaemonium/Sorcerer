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
        var rawResponse = string.Empty;
        var content = string.Empty;
        var supported = string.Join(", ", request.SupportedOperations);
        var contextJson = JsonSerializer.Serialize(
            request.Context,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            });
        var system = "You are the wild magic resolver for Sorcerer. Return exactly one JSON object. "
            + "Use this shape: {\"accepted\":true,\"severity\":\"minor|moderate|major|catastrophic\","
            + "\"outcomeText\":\"short vivid result\",\"effects\":[],\"costs\":[],\"rejectedReason\":null}. "
            + $"Supported effect types: {supported}. "
            + "Use only supported effect types and the provided target references. "
            + "Prefer reusable operations over custom mechanics. "
            + "Reject spells that are too broad, too remote, or impossible in the current encounter. "
            + "Technical JSON mistakes are failures, but intentional in-world rejection should be accepted:false. "
            + "The engine validates everything and applies all effects transactionally.";

        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            think = false,
            options = new
            {
                temperature = 0.2,
                num_predict = 1200,
            },
            messages = new[]
            {
                new { role = "system", content = system },
                new
                {
                    role = "user",
                    content = $"Spell: {request.SpellText}\n\nCurrent magic context JSON:\n{contextJson}",
                },
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
                return Failure(rawResponse, $"Ollama returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(rawResponse);
            content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

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
            var raw = string.IsNullOrWhiteSpace(content) ? rawResponse : content;
            return Failure(raw, ex.Message);
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
