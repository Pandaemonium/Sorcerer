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
        var supported = string.Join(", ", request.SupportedOperations);
        var contextJson = JsonSerializer.Serialize(
            request.Context,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = false,
            });
        var system = BuildSystemPrompt(supported, request.CapabilityIndex, request.SelectedCapabilities);

        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            think = false,
            options = new
            {
                temperature = 0.2,
                num_ctx = 8192,
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
                timeout.Token);
            rawResponse = await response.Content.ReadAsStringAsync(timeout.Token);
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

            try
            {
                var resolution = SpellResolutionJson.Parse(content, _registry);
                return Success(content, resolution);
            }
            catch (JsonException ex)
            {
                return await RetryAfterInvalidJsonAsync(
                    request,
                    supported,
                    contextJson,
                    content,
                    ex.Message,
                    timeout.Token);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var raw = string.IsNullOrWhiteSpace(content) ? rawResponse : content;
            return Failure(raw, ex.Message);
        }
    }

    internal static string BuildSystemPrompt(
        string supported,
        string? capabilityIndex,
        IReadOnlyList<CapabilityCard>? selectedCapabilities)
    {
        var core = BuildCorePrompt(supported);
        if (string.IsNullOrWhiteSpace(capabilityIndex))
        {
            return core;
        }

        var parts = new List<string>
        {
            core,
            "Capability index (mechanics that can be loaded when a spell needs them):",
            capabilityIndex,
        };

        if (selectedCapabilities is { Count: > 0 })
        {
            parts.Add("Mechanics loaded for this spell:");
            parts.Add(string.Join(
                "\n",
                selectedCapabilities.Select(card =>
                    string.Join("\n", new[] { card.PromptBlock }.Concat(card.Examples)))));
        }

        return string.Join("\n\n", parts);
    }

    private static string BuildCorePrompt(string supported) =>
        "You are the wild magic resolver for Sorcerer. Return exactly one JSON object. "
        + "Use this shape: {\"accepted\":true,\"severity\":\"minor|moderate|major|catastrophic\","
        + "\"outcomeText\":\"short vivid result\",\"effects\":[],\"costs\":[],\"rejectedReason\":null}. "
        + $"Supported effect types: {supported}. "
        + "Use only supported effect types and the provided target references. "
        + "Every target-taking effect must include target or targetId from the context; for the caster, use target:\"player\" unless the caster id differs. "
        + "Effects must be an array of flat objects with a type field, such as {\"type\":\"addStatus\",\"target\":\"player\",\"status\":\"river_concealed\",\"duration\":4}; never write {\"addStatus\":{...}} or put two operation keys inside one effect object. "
        + "Prefer reusable operations over custom mechanics. "
        + "Outcome text must describe only what the listed effects make true. "
        + "Do not claim a target is immobilized, asleep, dead, transformed, summoned, moved, healed, harmed, cursed, or allied unless a matching effect operation is present. "
        + "If the spell should stop movement or action, include an addStatus effect with a binding status such as rooted, webbed, pinned, asleep, or petrified. "
        + "If a spell asks a local place, room, shrine, terrain, fixture, or object to help, hide, protect, reveal, remember, or answer, convert that appeal into concrete effects on existing targets, nearby terrain, statuses, traits, summons, or messages. "
        + "Use second person only for the controlled player/caster; name non-player targets instead of calling them 'you' or 'your'. "
        + "If the spell names part of an entity's appearance, clothing, gear, rope, hair, voice, shadow, or body, target that owning entity with addTrait, addStatus, or transformEntity; do not pick an unrelated item just because it is object-like. "
        + "If context.resolverLens is present, use it as soft guidance for magnitude, volatility, costs, and recurring magical signature. "
        + "If context.reagents is present, those are unprotected carried materials available as spell fuel; use their material, tags, and spellBias as soft guidance for costs or theming, but do not assume protected inventory is spendable. "
        + "If context.lore is present, use those lore cards as canon and voice guidance, but only make lore mechanically true through supported effect operations. "
        + "Costs must use type fields such as {\"type\":\"mana\",\"amount\":4} or {\"type\":\"item\",\"item\":\"grave salt\",\"quantity\":1}; never use an item name as the cost type. "
        + "Reject spells that are too broad, too remote, or impossible in the current encounter. "
        + "Technical JSON mistakes are failures, but intentional in-world rejection should be accepted:false. "
        + "The engine validates everything and applies all effects transactionally.";

    private async Task<SpellProviderResult> RetryAfterInvalidJsonAsync(
        SpellRequest request,
        string supported,
        string contextJson,
        string invalidContent,
        string parseError,
        CancellationToken cancellationToken)
    {
        var rawResponse = string.Empty;
        var repairContent = string.Empty;
        var repairSystem = BuildSystemPrompt(supported, request.CapabilityIndex, request.SelectedCapabilities)
            + " This is a repair attempt after invalid output. Return JSON only; no prose before or after the object.";
        var previous = invalidContent.Length > 600
            ? invalidContent[..600]
            : invalidContent;
        var repairUser = "The previous resolver answer was not valid engine JSON. "
            + $"Parse error: {parseError}\n"
            + $"Previous invalid answer:\n{previous}\n\n"
            + "Convert the same spell into the required JSON object using supported operations. "
            + "Each effect must be a flat object with a type field; rewrite keyed or nested effects into separate flat effect objects. "
            + "For hiding, cover, protection, disguise, or attention-shifting requests, prefer addStatus on the caster/target, createTile/createTiles near the caster, addTrait on an entity, or message when those operations fit. "
            + $"Spell: {request.SpellText}\n\nCurrent magic context JSON:\n{contextJson}";

        var payload = new
        {
            model = _model,
            stream = false,
            format = "json",
            think = false,
            options = new
            {
                temperature = 0.1,
                num_ctx = 8192,
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
                return Failure(rawResponse, $"Ollama returned HTTP {(int)response.StatusCode} after JSON repair.");
            }

            using var document = JsonDocument.Parse(rawResponse);
            repairContent = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(repairContent))
            {
                return Failure(invalidContent, "Ollama returned invalid JSON, then an empty repair message.");
            }

            var resolution = SpellResolutionJson.Parse(repairContent, _registry);
            return Success(repairContent, resolution);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            var raw = string.IsNullOrWhiteSpace(repairContent)
                ? string.IsNullOrWhiteSpace(rawResponse) ? invalidContent : rawResponse
                : repairContent;
            return Failure(raw, $"Ollama returned invalid JSON, then repair failed: {ex.Message}");
        }
    }

    private SpellProviderResult Success(string raw, SpellResolution resolution) =>
        new(
            Name,
            RawText: raw,
            Resolution: resolution,
            TechnicalFailure: false,
            Error: null);

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
