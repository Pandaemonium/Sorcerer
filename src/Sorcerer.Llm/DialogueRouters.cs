using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Core.Dialogue;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Llm;

public static class DialogueRouterFactory
{
    public static IDialogueRouter Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static IDialogueRouter Create(LlmPurposeSettings settings)
    {
        if (!settings.Enabled)
        {
            return NullDialogueRouter.Instance;
        }

        return settings.Provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockDialogueRouter(),
            "ollama" or "local" => new OllamaDialogueRouter(
                settings.Host ?? "http://127.0.0.1:11434",
                settings.Model ?? "qwen3.5:9b",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleDialogueRouter(
                settings.Host ?? "https://api.openai.com/v1",
                settings.Model ?? "default",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                apiKey: settings.ApiKey),
            "anthropic" or "claude" => new OpenAiCompatibleDialogueRouter(
                new AnthropicMessagesClient(
                    settings.Host ?? "https://api.anthropic.com/v1",
                    settings.Model ?? "claude-sonnet-5",
                    settings.Effort,
                    apiKey: settings.ApiKey),
                "anthropic-dialogue-router",
                TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "gemini" or "google" => new OpenAiCompatibleDialogueRouter(
                new GeminiInteractionsClient(
                    settings.Host ?? "https://generativelanguage.googleapis.com/v1beta",
                    settings.Model ?? "gemini-3.5-flash",
                    settings.Effort,
                    apiKey: settings.ApiKey),
                "gemini-dialogue-router",
                TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            _ => new MockDialogueRouter(),
        };
    }
}

public sealed class MockDialogueRouter : IDialogueRouter
{
    public string Name => "mock-dialogue-router";

    public Task<DialogueRouteResult> RouteAsync(
        DialogueRouteRequest request,
        CancellationToken cancellationToken)
    {
        var lower = request.PlayerText.ToLowerInvariant();
        var preferred = new List<string> { "zone.current", "npc.relationship_memory" };
        if (lower.Contains("who", StringComparison.Ordinal)
            || lower.Contains("else", StringComparison.Ordinal)
            || lower.Contains("people", StringComparison.Ordinal)
            || lower.Contains("npc", StringComparison.Ordinal)
            || lower.Contains("guard", StringComparison.Ordinal)
            || lower.Contains("soldier", StringComparison.Ordinal)
            || lower.Contains("lio", StringComparison.Ordinal))
        {
            preferred.Add("zone.npcs");
        }

        if (lower.Contains("rumor", StringComparison.Ordinal)
            || lower.Contains("gossip", StringComparison.Ordinal)
            || lower.Contains("know", StringComparison.Ordinal))
        {
            preferred.Add("rumors.full");
            preferred.Add("npc.knowledge.region");
        }

        if (lower.Contains("door", StringComparison.Ordinal)
            || lower.Contains("room", StringComparison.Ordinal)
            || lower.Contains("object", StringComparison.Ordinal)
            || lower.Contains("here", StringComparison.Ordinal))
        {
            preferred.Add("scene.object_detail");
        }

        if (lower.Contains("law", StringComparison.Ordinal)
            || lower.Contains("empire", StringComparison.Ordinal)
            || lower.Contains("vigovia", StringComparison.Ordinal))
        {
            preferred.Add("faction.law");
        }

        if (lower.Contains("service", StringComparison.Ordinal)
            || lower.Contains("trade", StringComparison.Ordinal)
            || lower.Contains("wares", StringComparison.Ordinal)
            || lower.Contains("sell", StringComparison.Ordinal))
        {
            preferred.Add("services.available");
        }

        if (lower.Contains("road", StringComparison.Ordinal)
            || lower.Contains("route", StringComparison.Ordinal)
            || lower.Contains("travel", StringComparison.Ordinal)
            || lower.Contains("north", StringComparison.Ordinal)
            || lower.Contains("south", StringComparison.Ordinal)
            || lower.Contains("east", StringComparison.Ordinal)
            || lower.Contains("west", StringComparison.Ordinal)
            || lower.Contains("where", StringComparison.Ordinal)
            || lower.Contains("go", StringComparison.Ordinal)
            || lower.Contains("leave", StringComparison.Ordinal)
            || lower.Contains("escape", StringComparison.Ordinal))
        {
            preferred.Add("region.travel");
        }

        if (lower.Contains("promise", StringComparison.Ordinal)
            || lower.Contains("owe", StringComparison.Ordinal)
            || lower.Contains("debt", StringComparison.Ordinal)
            || lower.Contains("prophecy", StringComparison.Ordinal)
            || lower.Contains("claim", StringComparison.Ordinal)
            || lower.Contains("hook", StringComparison.Ordinal))
        {
            preferred.Add("promise.hooks");
        }

        if (lower.Contains("magic", StringComparison.Ordinal)
            || lower.Contains("spell", StringComparison.Ordinal)
            || lower.Contains("deed", StringComparison.Ordinal)
            || lower.Contains("saw", StringComparison.Ordinal)
            || lower.Contains("happened", StringComparison.Ordinal))
        {
            preferred.Add("recent.magic_deeds");
        }

        preferred.Add("claims.recent");
        var available = request.AvailableCards
            .Select(card => card.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selected = preferred
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        return Task.FromResult(new DialogueRouteResult(
            Name,
            JsonSerializer.Serialize(new { selectedCardIds = selected, reason = "deterministic keyword route" }, JsonOptions),
            TechnicalFailure: false,
            Error: null,
            SelectedCardIds: selected,
            Reason: "deterministic keyword route"));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

public sealed class OpenAiCompatibleDialogueRouter : IDialogueRouter
{
    private readonly IJsonChatClient _chat;
    private readonly string _name;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleDialogueRouter(
        string endpoint = "https://api.openai.com/v1",
        string model = "default",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
        : this(
            new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey),
            "openai-compatible-dialogue-router",
            timeout)
    {
    }

    internal OpenAiCompatibleDialogueRouter(
        IJsonChatClient chat,
        string name,
        TimeSpan? timeout = null)
    {
        _chat = chat;
        _name = name;
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => _name;

    public async Task<DialogueRouteResult> RouteAsync(
        DialogueRouteRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var result = await _chat.ChatAsync(
            OllamaDialogueRouter.SystemPrompt(),
            OllamaDialogueRouter.UserPrompt(request),
            temperature: 0.0,
            maxTokens: 140,
            timeout.Token,
            label: "dialogue-context-router");
        if (!result.Success)
        {
            return Failure(result.RawText, result.Error ?? $"{Name} failed.");
        }

        return OllamaDialogueRouter.TryParseRouteResult(Name, result.Content, out var route, out var error)
            ? route!
            : Failure(result.Content, error ?? $"{Name} returned invalid JSON.");
    }

    private DialogueRouteResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, SelectedCardIds: Array.Empty<string>());
}

public sealed class OllamaDialogueRouter : IDialogueRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public OllamaDialogueRouter(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _httpClient = httpClient ?? CreateHttpClient();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "ollama-dialogue-router";

    public async Task<DialogueRouteResult> RouteAsync(
        DialogueRouteRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var result = await ChatAsync(
            SystemPrompt(),
            UserPrompt(request),
            temperature: 0.0,
            maxTokens: 140,
            timeout.Token);
        if (!result.Success)
        {
            return Failure(result.RawText, result.Error ?? "Dialogue context router failed.");
        }

        return TryParseRouteResult(Name, result.Content, out var route, out var error)
            ? route!
            : Failure(result.Content, error ?? "Dialogue context router returned invalid JSON.");
    }

    private async Task<ChatResult> ChatAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var traceId = Diagnostics.LlmTrace.Begin("dialogue-context-router", _model, system, user);
        var result = await SendAsync(system, user, temperature, maxTokens, cancellationToken);
        Diagnostics.LlmTrace.End(traceId, result.Success ? result.Content : result.RawText, result.Error, result.Stats);
        return result;
    }

    private async Task<ChatResult> SendAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var raw = string.Empty;
        try
        {
            var payload = new
            {
                model = _model,
                stream = false,
                format = "json",
                think = false,
                options = new
                {
                    temperature,
                    num_ctx = OllamaDefaults.NumCtx,
                    num_predict = maxTokens,
                },
                messages = new[]
                {
                    new { role = "system", content = system },
                    new { role = "user", content = user },
                },
            };
            var response = await _httpClient.PostAsJsonAsync($"{_host}/api/chat", payload, cancellationToken);
            raw = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new ChatResult(false, "", raw, $"Ollama returned HTTP {(int)response.StatusCode}.");
            }

            using var document = JsonDocument.Parse(raw);
            var stats = Diagnostics.OllamaStats.From(document.RootElement);
            var content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(content)
                ? new ChatResult(false, "", raw, "Ollama returned an empty message.", stats)
                : new ChatResult(true, content, raw, null, stats);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new ChatResult(false, "", raw, ex.Message);
        }
    }

    internal static string SystemPrompt() =>
        "Dialogue context router. Return only JSON: {\"selectedCardIds\":[\"card.id\"],\"reason\":\"short\"}. "
        + "Pick the smallest useful card set for the NPC's answer. Use only listed ids. "
        + "Cards are already knowledge-gated; do not add facts, dialogue, or mechanics. "
        + "For broad or unsure turns prefer zone.current plus npc.relationship_memory if listed.";

    internal static string UserPrompt(DialogueRouteRequest request) =>
        JsonSerializer.Serialize(new
        {
            t = request.Turn,
            text = request.PlayerText,
            speaker = ParticipantRouteLine(request.Speaker),
            listener = ParticipantRouteLine(request.Listener),
            scene = $"{request.Scene.RegionId}/{request.Scene.CurrentZoneId}",
            nearby = CompactNearbyLines(request.Scene).ToArray(),
            cards = request.AvailableCards.Select(card => $"{card.Id}: {card.Summary}").ToArray(),
        }, JsonOptions);

    private static string ParticipantRouteLine(DialogueParticipantCard participant)
    {
        var tags = participant.Tags.Count == 0 ? "" : $" [{string.Join(",", participant.Tags.Take(5))}]";
        var faction = string.IsNullOrWhiteSpace(participant.Faction) ? "" : $" faction={participant.Faction}";
        return $"{participant.Name} ({participant.EntityId}){tags}{faction}";
    }

    private static IEnumerable<string> CompactNearbyLines(DialogueSceneCard scene) =>
        scene.VisibleEntities
            .Concat(scene.NearbyItems)
            .Take(6)
            .Select(line => line.Length > 96 ? line[..96].TrimEnd() : line);

    internal static bool TryParseRouteResult(
        string provider,
        string content,
        out DialogueRouteResult? result,
        out string? error)
    {
        result = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Dialogue route JSON root was not an object.";
                return false;
            }

            var selected = ReadStringArray(root, "selectedCardIds")
                ?? ReadStringArray(root, "selected_context_card_ids")
                ?? ReadStringArray(root, "contextCards")
                ?? ReadStringArray(root, "cards")
                ?? Array.Empty<string>();
            result = new DialogueRouteResult(
                provider,
                content,
                TechnicalFailure: false,
                Error: null,
                SelectedCardIds: selected,
                Reason: ReadString(root, "reason"));
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private DialogueRouteResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, SelectedCardIds: Array.Empty<string>());

    private static IReadOnlyList<string>? ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : null)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!.Trim())
            .ToArray();
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed record ChatResult(
        bool Success,
        string Content,
        string RawText,
        string? Error,
        Sorcerer.Core.Telemetry.ProviderCallStats? Stats = null);

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
