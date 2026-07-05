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
        var user = $"Spell: {request.SpellText}\n\nCurrent magic context JSON:\n{contextJson}";
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
                num_ctx = 8192,
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
            content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(content))
            {
                Diagnostics.LlmTrace.End(traceId, rawResponse, "Ollama returned an empty message.");
                return Failure(rawResponse, "Ollama returned an empty message.");
            }

            // The model answered; record it now. A JSON-repair retry (below) is logged separately.
            Diagnostics.LlmTrace.End(traceId, content, null);

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
            Diagnostics.LlmTrace.End(traceId, raw, ex.Message);
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
        + "When the spell wording names how to find the target - \"nearest enemy\", \"the nearest foe\", \"whatever is closest\" - use the matching selector such as nearest_enemy directly; only use target:\"selected_target\" when the spell text refers to a target the caster has already selected or is looking at (\"that\", \"there\", \"my target\"), since selected_target fails if nothing is currently selected. "
        + "Effects must be an array of flat objects with a type field, such as {\"type\":\"addStatus\",\"target\":\"player\",\"status\":\"river_concealed\",\"duration\":4}; never write {\"addStatus\":{...}} or put two operation keys inside one effect object. "
        + "Prefer reusable operations over custom mechanics. "
        + "Outcome text must describe only what the listed effects make true. "
        + "Do not claim a target is immobilized, asleep, dead, transformed, summoned, moved, healed, harmed, cursed, or allied unless a matching effect operation is present. "
        + "If the spell should stop movement or action, include an addStatus effect with a binding status such as rooted, webbed, pinned, asleep, or petrified. "
        + "If a spell asks a local place, room, shrine, terrain, fixture, or object to help, hide, protect, reveal, remember, or answer, convert that appeal into concrete effects on existing targets, nearby terrain, statuses, traits, summons, or messages. "
        + "Use second person only for the controlled player/caster; name non-player targets instead of calling them 'you' or 'your'. "
        + "Write outcomeText and any effect message field in grammatically correct person and number (\"you gain\", not \"you gains\"; \"the soldier gains\", not \"the soldier gain\"). "
        + "If the spell names part of an entity's appearance, clothing, gear, rope, hair, voice, shadow, or body, target that owning entity with addTrait, addStatus, or transformEntity; do not pick an unrelated item just because it is object-like. "
        + "If context.resolverLens is present, use it as soft guidance for magnitude, volatility, costs, and recurring magical signature. "
        + "If context.reagents is present, those are unprotected carried materials available as spell fuel; use their material, tags, and spellBias as soft guidance for costs or theming, but do not assume protected inventory is spendable. "
        + "If context.lore is present, use those lore cards as canon and voice guidance, but only make lore mechanically true through supported effect operations. "
        + "Assign costs deliberately: almost every spell that actually changes the world should cost something, and the price should scale with how much it bends reality. "
        + "The cost palette, from cheapest to gravest: mana for ordinary workings ({\"type\":\"mana\",\"amount\":2-6}); a bodily toll for spells that strain the caster ({\"type\":\"health\",\"amount\":3-8} or {\"type\":\"status\",\"status\":\"strained\",\"duration\":3}); a consumed reagent when the spell leans on a carried material; a lingering debt for dangerous, unnatural, or morally fraught magic ({\"type\":\"curse\",\"name\":\"short title\",\"description\":\"what is owed\"}); and, only for catastrophic or reality-bending spells, a permanent sacrifice ({\"type\":\"maxHealth\",\"amount\":1-3} or {\"type\":\"maxMana\",\"amount\":1-3}). "
        + "Match the cost to severity: minor spends a little mana; moderate spends more mana or a small bodily/reagent toll; major demands a real price such as health, a reagent, or a curse; catastrophic may demand max health or max mana. Prefer two mixed costs (e.g. mana plus a reagent, or health plus a curse) over one flat mana cost for anything above minor. "
        + "When a carried reagent in context.reagents thematically fits the spell - by its name, material, tags, or spellBias - prefer spending it as {\"type\":\"item\",\"item\":\"<exact reagent name from context>\",\"quantity\":1}, alongside or instead of mana; a named object powering the spell is more interesting than raw mana. "
        + "Never name an item cost that is not listed in context.reagents (the caster cannot spend what they do not carry); if no carried reagent fits, use mana, health, a status, or a curse instead. "
        + "Only a truly trivial cantrip or a pure-flavor spell may leave costs empty. "
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
                var httpError = $"Ollama returned HTTP {(int)response.StatusCode} after JSON repair.";
                Diagnostics.LlmTrace.End(traceId, rawResponse, httpError);
                return Failure(rawResponse, httpError);
            }

            using var document = JsonDocument.Parse(rawResponse);
            repairContent = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(repairContent))
            {
                Diagnostics.LlmTrace.End(traceId, rawResponse, "Ollama returned invalid JSON, then an empty repair message.");
                return Failure(invalidContent, "Ollama returned invalid JSON, then an empty repair message.");
            }

            Diagnostics.LlmTrace.End(traceId, repairContent, null);
            var resolution = SpellResolutionJson.Parse(repairContent, _registry);
            return Success(repairContent, resolution);
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
