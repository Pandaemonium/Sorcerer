using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Core.Dialogue;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Llm;

public static class DialogueClaimExtractorFactory
{
    public static IDialogueClaimExtractor Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static IDialogueClaimExtractor Create(LlmPurposeSettings settings)
    {
        if (!settings.Enabled)
        {
            return NullDialogueClaimExtractor.Instance;
        }

        return settings.Provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockDialogueClaimExtractor(),
            "ollama" or "local" => new OllamaDialogueClaimExtractor(
                settings.Host ?? "http://127.0.0.1:11434",
                settings.Model ?? "qwen3.5:9b",
                numGpu: settings.OllamaNumGpu,
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleDialogueClaimExtractor(
                settings.Host ?? "https://api.openai.com/v1",
                settings.Model ?? "default",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                apiKey: settings.ApiKey),
            _ => new MockDialogueClaimExtractor(),
        };
    }
}

public static class DialogueParserFactory
{
    public static IDialogueParser Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static IDialogueParser Create(LlmPurposeSettings settings)
    {
        if (!settings.Enabled)
        {
            return NullDialogueClaimExtractor.Instance;
        }

        return settings.Provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockDialogueClaimExtractor(),
            "ollama" or "local" => new OllamaDialogueClaimExtractor(
                settings.Host ?? "http://127.0.0.1:11434",
                settings.Model ?? "qwen3.5:9b",
                numGpu: settings.OllamaNumGpu,
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleDialogueClaimExtractor(
                settings.Host ?? "https://api.openai.com/v1",
                settings.Model ?? "default",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                apiKey: settings.ApiKey),
            _ => new MockDialogueClaimExtractor(),
        };
    }
}

public static class DialogueParserRouterFactory
{
    public static IDialogueParserRouter Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static IDialogueParserRouter Create(LlmPurposeSettings settings)
    {
        if (!settings.Enabled)
        {
            return DeterministicDialogueParserRouter.Instance;
        }

        return settings.Provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => DeterministicDialogueParserRouter.Instance,
            "ollama" or "local" => new OllamaDialogueParserRouter(
                settings.Host ?? "http://127.0.0.1:11434",
                settings.Model ?? "qwen3.5:9b",
                numGpu: settings.OllamaNumGpu,
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleDialogueParserRouter(
                settings.Host ?? "https://api.openai.com/v1",
                settings.Model ?? "default",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                apiKey: settings.ApiKey),
            _ => DeterministicDialogueParserRouter.Instance,
        };
    }
}

public sealed class MockDialogueClaimExtractor : IDialogueClaimExtractor, IDialogueParser
{
    public string Name => "mock-dialogue-claims";

    public Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var proposals = BuildProposals(request);
        return Task.FromResult(new DialogueClaimExtractionResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            Claims: proposals.Claims ?? Array.Empty<DialogueClaimProposal>()));
    }

    public Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var proposals = BuildProposals(request);
        return Task.FromResult(new DialogueParseResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            proposals));
    }

    private static DialogueProposalSet BuildProposals(DialogueClaimRequest request)
    {
        var npcText = string.Join(" ", request.DialogueLines);
        var lower = npcText.ToLowerInvariant();
        var recentGift = request.RecentMemories.Any(memory =>
            memory.SubjectId.Equals(request.SpeakerId, StringComparison.OrdinalIgnoreCase)
            && memory.Provenance.StartsWith("gift", StringComparison.OrdinalIgnoreCase));
        var claims = new List<DialogueClaimProposal>();
        if (lower.Contains("hollowmere", StringComparison.Ordinal)
            && lower.Contains("remember", StringComparison.Ordinal))
        {
            claims.Add(new DialogueClaimProposal(
                "Hollowmere will remember the color of the sorcerer's magic.",
                "town",
                "Hollowmere",
                Salience: 3,
                Confidence: 70,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "quest",
                RealizationKind: "site",
                TriggerHint: "travel",
                ClaimedPlace: "Hollowmere",
                Tags: new[] { "hollowmere", "town", "memory" },
                UpdateBond: recentGift,
                LoyaltyDelta: recentGift ? 2 : 0,
                AdmirationDelta: recentGift ? 1 : 0,
                BondPosture: recentGift ? "grateful" : null));
        }

        if (lower.Contains("fine blade", StringComparison.Ordinal)
            || lower.Contains("sell you a blade", StringComparison.Ordinal)
            || lower.Contains("blade for sale", StringComparison.Ordinal))
        {
            claims.Add(new DialogueClaimProposal(
                $"{request.SpeakerName} can sell you a fine blade.",
                "merchant_stock",
                "fine blade",
                Salience: 3,
                Confidence: 80,
                PlayerVisible: true,
                MerchantId: request.SpeakerId,
                ItemName: "fine blade",
                Tags: new[] { "merchant", "stock", "blade" }));
        }

        if (lower.Contains("nannerl", StringComparison.Ordinal))
        {
            claims.Add(new DialogueClaimProposal(
                "Old Maren has a niece named Nannerl.",
                "person",
                "Nannerl",
                Salience: 3,
                Confidence: 65,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "person",
                TriggerHint: "travel",
                Tags: new[] { "person", "family", "nannerl" }));
        }

        if (lower.Contains("town just south", StringComparison.Ordinal)
            || lower.Contains("town south", StringComparison.Ordinal)
            || lower.Contains("south of here", StringComparison.Ordinal))
        {
            claims.Add(new DialogueClaimProposal(
                "There is a town south of here.",
                "town",
                "southward town",
                Salience: 3,
                Confidence: 70,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "site",
                TriggerHint: "travel",
                ClaimedPlace: "southward town",
                Tags: new[] { "town", "south", "place" }));
        }

        if ((lower.Contains("folk-magic", StringComparison.Ordinal)
                || lower.Contains("folk magic", StringComparison.Ordinal))
            && lower.Contains("execution", StringComparison.Ordinal))
        {
            claims.Add(new DialogueClaimProposal(
                "Folk-magic practice is punishable by execution here.",
                "local_law",
                "folk magic law",
                Salience: 2,
                Confidence: 80,
                BindAsCanon: true,
                CanonKind: "local_law",
                CanonSummary: "Folk magic is a capital crime",
                Tags: new[] { "folk_magic", "vigovia", "law" }));
        }

        return new DialogueProposalSet(Claims: claims);
    }
}

public sealed class OpenAiCompatibleDialogueParserRouter : IDialogueParserRouter
{
    private readonly OpenAiCompatibleChatClient _chat;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleDialogueParserRouter(
        string endpoint = "https://api.openai.com/v1",
        string model = "default",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
    {
        _chat = new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey);
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "openai-compatible-dialogue-parser-router";

    public async Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var result = await _chat.ChatAsync(
            OllamaDialogueParserRouter.SystemPrompt(),
            OllamaDialogueParserRouter.UserPrompt(request),
            temperature: 0.0,
            maxTokens: 240,
            timeout.Token,
            label: "dialogue-parser-router");
        if (!result.Success)
        {
            return Failure(result.RawText, result.Error ?? "OpenAI-compatible dialogue parser router failed.");
        }

        return OllamaDialogueParserRouter.TryParseRouteResult(Name, result.Content, out var route, out var error)
            ? route!
            : Failure(result.Content, error ?? "OpenAI-compatible dialogue parser router returned invalid JSON.");
    }

    private DialogueParserRouteResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, HasMechanics: false, SelectedCapabilityIds: Array.Empty<string>());
}

public sealed class OllamaDialogueParserRouter : IDialogueParserRouter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly int? _numGpu;
    private readonly TimeSpan _timeout;

    public OllamaDialogueParserRouter(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        int? numGpu = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _numGpu = numGpu;
        _httpClient = httpClient ?? CreateHttpClient();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
    }

    public string Name => "ollama-dialogue-parser-router";

    public async Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var result = await ChatAsync(
            SystemPrompt(),
            UserPrompt(request),
            temperature: 0.0,
            maxTokens: 240,
            timeout.Token);
        if (!result.Success)
        {
            return Failure(result.RawText, result.Error ?? "Dialogue parser router failed.");
        }

        return TryParseRouteResult(Name, result.Content, out var route, out var error)
            ? route!
            : Failure(result.Content, error ?? "Dialogue parser router returned invalid JSON.");
    }

    private async Task<ChatResult> ChatAsync(
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var traceId = Diagnostics.LlmTrace.Begin("dialogue-parser-router", _model, system, user);
        var result = await SendAsync(system, user, temperature, maxTokens, cancellationToken);
        Diagnostics.LlmTrace.End(traceId, result.Success ? result.Content : result.RawText, result.Error);
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
            var options = new Dictionary<string, object?>
            {
                ["temperature"] = temperature,
                ["num_ctx"] = 2048,
                ["num_predict"] = maxTokens,
            };
            if (_numGpu.HasValue)
            {
                options["num_gpu"] = _numGpu.Value;
            }

            var payload = new
            {
                model = _model,
                stream = false,
                format = "json",
                think = false,
                options,
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
            var content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(content)
                ? new ChatResult(false, "", raw, "Ollama returned an empty message.")
                : new ChatResult(true, content, raw, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new ChatResult(false, "", raw, ex.Message);
        }
    }

    internal static string SystemPrompt() =>
        "You are Sorcerer's dialogue parser router. Return exactly JSON with shape "
        + "{\"hasMechanics\":true|false,\"selectedCapabilityIds\":[\"claims\"],\"reason\":\"short\"}. "
        + "Choose only from available capability ids. Say hasMechanics true only when the NPC's spoken reply contains something the engine may need to remember or apply. "
        + "Player-spoken assertions are not binding. Do not extract facts or write proposals.";

    internal static string UserPrompt(DialogueParserRouteRequest request) =>
        JsonSerializer.Serialize(new
        {
            request.Turn,
            request.RegionId,
            request.CurrentZoneId,
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.PlayerText,
            npcDialogue = request.DialogueLines,
            availableCapabilities = request.AvailableCapabilities,
        }, JsonOptions);

    internal static bool TryParseRouteResult(
        string provider,
        string content,
        out DialogueParserRouteResult? result,
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
                error = "Dialogue parser route JSON root was not an object.";
                return false;
            }

            var selected = ReadStringArray(root, "selectedCapabilityIds")
                ?? ReadStringArray(root, "selected_capability_ids")
                ?? ReadStringArray(root, "capabilities")
                ?? ReadStringArray(root, "families")
                ?? Array.Empty<string>();
            var hasMechanics = ReadBool(root, "hasMechanics")
                || ReadBool(root, "has_mechanics")
                || selected.Count > 0;
            result = new DialogueParserRouteResult(
                provider,
                content,
                TechnicalFailure: false,
                Error: null,
                hasMechanics,
                selected,
                ReadString(root, "reason"));
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static bool ReadBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private DialogueParserRouteResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, HasMechanics: false, SelectedCapabilityIds: Array.Empty<string>());

    private sealed record ChatResult(bool Success, string Content, string RawText, string? Error);

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}

public sealed class OpenAiCompatibleDialogueClaimExtractor : IDialogueClaimExtractor, IDialogueParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly OpenAiCompatibleChatClient _chat;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleDialogueClaimExtractor(
        string endpoint = "https://api.openai.com/v1",
        string model = "default",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
    {
        _chat = new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey);
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "openai-compatible-dialogue-claims";

    public async Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ParseAsync(request, cancellationToken);
        return new DialogueClaimExtractionResult(
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            ClaimsFrom(result.Proposals));
    }

    public async Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        if (request.ParserCapabilityCards is not null)
        {
            if (request.ParserCapabilityCards.Count == 0)
            {
                return ParserSuccess("", new DialogueProposalSet());
            }

            return await ParseDetailAsync(request, timeout.Token);
        }

        var router = await _chat.ChatAsync(
            OllamaDialogueClaimExtractor.RouterSystemPrompt(),
            OllamaDialogueClaimExtractor.RouterUserPrompt(request),
            temperature: 0.0,
            maxTokens: 180,
            timeout.Token,
            label: "dialogue-parser-router");
        if (!router.Success)
        {
            return ParserFailure(router.RawText, router.Error ?? "OpenAI-compatible dialogue parser router failed.");
        }

        if (!OllamaDialogueClaimExtractor.RouterFoundClaim(router.Content))
        {
            return ParserSuccess(router.Content, new DialogueProposalSet());
        }

        return await ParseDetailAsync(request, timeout.Token);
    }

    private async Task<DialogueParseResult> ParseDetailAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await _chat.ChatAsync(
            OllamaDialogueClaimExtractor.DetailSystemPrompt(),
            OllamaDialogueClaimExtractor.DetailUserPrompt(request),
            temperature: 0.1,
            maxTokens: 900,
            cancellationToken,
            label: "dialogue-parser-detail");
        if (!detail.Success)
        {
            return ParserFailure(detail.RawText, detail.Error ?? "OpenAI-compatible dialogue parser detail extraction failed.");
        }

        if (OllamaDialogueClaimExtractor.TryParseProposalEnvelope(
            detail.Content,
            request,
            out var proposals,
            out var parseError))
        {
            return ParserSuccess(detail.Content, proposals);
        }

        return ParserFailure(detail.Content, parseError ?? "OpenAI-compatible dialogue parser returned invalid JSON.");
    }

    private static IReadOnlyList<DialogueClaimProposal> ClaimsFrom(DialogueProposalSet? proposals) =>
        (proposals?.Claims ?? Array.Empty<DialogueClaimProposal>())
            .Where(claim => !claim.PlayerAuthored && !string.IsNullOrWhiteSpace(claim.Text))
            .ToArray();

    private DialogueParseResult ParserSuccess(string raw, DialogueProposalSet? proposals) =>
        new(Name, raw, TechnicalFailure: false, Error: null, Proposals: proposals);

    private DialogueParseResult ParserFailure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, Proposals: null);
}

public sealed class OllamaDialogueClaimExtractor : IDialogueClaimExtractor, IDialogueParser
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly int? _numGpu;
    private readonly TimeSpan _timeout;

    public OllamaDialogueClaimExtractor(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        int? numGpu = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _numGpu = numGpu;
        _httpClient = httpClient ?? CreateHttpClient();
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "ollama-dialogue-claims";

    public async Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var result = await ParseAsync(request, cancellationToken);
        return new DialogueClaimExtractionResult(
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            ClaimsFrom(result.Proposals));
    }

    public async Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        if (request.ParserCapabilityCards is not null)
        {
            if (request.ParserCapabilityCards.Count == 0)
            {
                return ParserSuccess("", new DialogueProposalSet());
            }

            return await ParseDetailAsync(request, timeout.Token);
        }

        var router = await ChatAsync(
            "dialogue-parser-router",
            RouterSystemPrompt(),
            RouterUserPrompt(request),
            temperature: 0.0,
            maxTokens: 180,
            timeout.Token);
        if (!router.Success)
        {
            return ParserFailure(router.RawText, router.Error ?? "Dialogue parser router failed.");
        }

        if (!RouterFoundClaim(router.Content))
        {
            return ParserSuccess(router.Content, new DialogueProposalSet());
        }

        return await ParseDetailAsync(request, timeout.Token);
    }

    private async Task<DialogueParseResult> ParseDetailAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var detail = await ChatAsync(
            "dialogue-parser-detail",
            DetailSystemPrompt(),
            DetailUserPrompt(request),
            temperature: 0.1,
            maxTokens: 900,
            cancellationToken);
        if (!detail.Success)
        {
            return ParserFailure(detail.RawText, detail.Error ?? "Dialogue parser detail extraction failed.");
        }

        if (TryParseProposalEnvelope(
            detail.Content,
            request,
            out var proposals,
            out var parseError))
        {
            return ParserSuccess(detail.Content, proposals);
        }

        return ParserFailure(detail.Content, parseError ?? "Dialogue parser returned invalid JSON.");
    }

    private async Task<ChatResult> ChatAsync(
        string label,
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        var traceId = Diagnostics.LlmTrace.Begin(label, _model, system, user);
        var result = await SendAsync(system, user, temperature, maxTokens, cancellationToken);
        Diagnostics.LlmTrace.End(traceId, result.Success ? result.Content : result.RawText, result.Error);
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
            var options = new Dictionary<string, object?>
            {
                ["temperature"] = temperature,
                ["num_ctx"] = 4096,
                ["num_predict"] = maxTokens,
            };
            if (_numGpu.HasValue)
            {
                options["num_gpu"] = _numGpu.Value;
            }

            var payload = new
            {
                model = _model,
                stream = false,
                format = "json",
                think = false,
                options,
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
            var content = document.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;
            return string.IsNullOrWhiteSpace(content)
                ? new ChatResult(false, "", raw, "Ollama returned an empty message.")
                : new ChatResult(true, content, raw, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new ChatResult(false, "", raw, ex.Message);
        }
    }

    internal static bool RouterFoundClaim(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            return ReadBool(root, "hasMechanics")
                || ReadBool(root, "hasClaim")
                || ReadBool(root, "hasPromise")
                || ReadBool(root, "hasMemory")
                || ReadBool(root, "hasBond")
                || ReadBool(root, "hasWant")
                || ReadBool(root, "hasAction")
                || ReadBool(root, "hasService")
                || ReadBool(root, "hasConsequence")
                || HasNonEmptyArray(root, "families")
                || (root.TryGetProperty("capabilities", out var capabilities)
                    && capabilities.ValueKind == JsonValueKind.Array
                    && capabilities.GetArrayLength() > 0);
        }
        catch (JsonException)
        {
            return content.Contains("true", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool ReadBool(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.True;

    private static bool HasNonEmptyArray(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.Array
        && value.GetArrayLength() > 0;

    internal static string RouterSystemPrompt() =>
        "You are Sorcerer's dialogue mechanics router. Return exactly JSON: "
        + "{\"hasMechanics\":true|false,\"families\":[\"claims|memories|bond|want|actions|services|trade|consequence\"],\"reason\":\"short\"}. "
        + "Say true only when the NPC's spoken reply contains something the engine may need to remember or apply: an NPC-authored/reported claim, promise, canon fact, durable memory, material bond or want shift, immediate local action, service/trade offer, or typed world consequence. "
        + "Player-spoken assertions are not binding. Report false for greetings, pure mood, refusals without side effects, or answers that add no durable/local mechanic.";

    internal static string RouterUserPrompt(DialogueClaimRequest request) =>
        JsonSerializer.Serialize(new
        {
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.PlayerText,
            npcDialogue = request.DialogueLines,
        }, JsonOptions);

    internal static string DetailSystemPrompt() =>
        "You are Sorcerer's post-speech dialogue parser. Return exactly JSON with shape {\"proposals\":{\"claims\":[],\"memories\":[],\"bond\":null,\"want\":null,\"actions\":[]}}. "
        + "Parse only mechanics plainly supported by the NPC's spoken reply and provided context; do not add new facts for flavor. Extract only NPC-authored or NPC-reported claims, never player-invented claims. Reported claims may be uncertain; use confidence. "
        + "Use only the selected parser capability cards in the user payload for schema/detail guidance. If no selected card supports a proposal family, omit that family. "
        + "Use salience 1-5; reserve salience 3+ for claims that open a route, stock, person, secret, place, threat, or tactical opportunity. "
        + "Keep all text concise and concrete.";

    internal static string DetailUserPrompt(DialogueClaimRequest request) =>
        JsonSerializer.Serialize(new
        {
            request.Turn,
            request.RegionId,
            request.CurrentZoneId,
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.ListenerSoulId,
            request.PlayerText,
            npcDialogue = request.DialogueLines,
            selectedParserCapabilityIds = request.SelectedParserCapabilityIds ?? Array.Empty<string>(),
            parserCapabilityCards = request.ParserCapabilityCards ?? DialogueParserCapabilityCatalog.All,
            recentMemories = request.RecentMemories.Select(memory => new
            {
                memory.SubjectId,
                memory.Text,
                memory.Provenance,
                memory.Salience,
            }),
            recentClaims = request.RecentClaims.Select(claim => new
            {
                claim.Text,
                claim.Category,
                claim.Subject,
                claim.Salience,
                claim.Status,
            }),
        }, JsonOptions);

    internal static bool TryParseProposalEnvelope(
        string content,
        DialogueClaimRequest request,
        out DialogueProposalSet? proposals,
        out string? error)
    {
        var spokenText = string.Join(" ", request.DialogueLines);
        return OllamaDialogueProvider.TryParseProposalEnvelope(
            content,
            ParserDialogueRequest(request),
            spokenText,
            out proposals,
            out error);
    }

    private static DialogueRequest ParserDialogueRequest(DialogueClaimRequest request)
    {
        var listenerId = string.IsNullOrWhiteSpace(request.ListenerEntityId)
            ? request.ListenerSoulId
            : request.ListenerEntityId!;
        return new DialogueRequest(
            request.Turn,
            request.PlayerText,
            new DialogueParticipantCard(
                request.SpeakerId,
                request.SpeakerName,
                request.SpeakerTags),
            new DialogueParticipantCard(
                listenerId,
                "listener",
                Array.Empty<string>()),
            new DialogueSceneCard(
                request.RegionId,
                request.CurrentZoneId,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>()),
            request.RecentMemories
                .Select(memory => $"{memory.SubjectId} [{memory.Provenance}, salience {memory.Salience}]: {memory.Text}")
                .ToArray(),
            request.RecentClaims
                .Select(claim => $"{claim.Category}:{claim.Subject} [{claim.Status}, salience {claim.Salience}]: {claim.Text}")
                .ToArray(),
            Array.Empty<string>());
    }

    private static IReadOnlyList<DialogueClaimProposal> ClaimsFrom(DialogueProposalSet? proposals) =>
        (proposals?.Claims ?? Array.Empty<DialogueClaimProposal>())
            .Where(claim => !claim.PlayerAuthored && !string.IsNullOrWhiteSpace(claim.Text))
            .ToArray();

    private DialogueParseResult ParserSuccess(string raw, DialogueProposalSet? proposals) =>
        new(Name, raw, TechnicalFailure: false, Error: null, Proposals: proposals);

    private DialogueParseResult ParserFailure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, Proposals: null);

    private sealed record ChatResult(bool Success, string Content, string RawText, string? Error);

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
