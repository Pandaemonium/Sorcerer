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
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "api" or "openai" or "openai-compatible" => new TechnicalFailureDialogueClaimExtractor(
                "openai-compatible-dialogue-claims",
                "OpenAI-compatible dialogue claim extraction is not implemented yet."),
            _ => new MockDialogueClaimExtractor(),
        };
    }
}

public sealed class TechnicalFailureDialogueClaimExtractor : IDialogueClaimExtractor
{
    private readonly string _error;

    public TechnicalFailureDialogueClaimExtractor(string name, string error)
    {
        Name = name;
        _error = error;
    }

    public string Name { get; }

    public Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueClaimExtractionResult(
            Name,
            "",
            TechnicalFailure: true,
            Error: _error,
            Claims: Array.Empty<DialogueClaimProposal>()));
}

public sealed class MockDialogueClaimExtractor : IDialogueClaimExtractor
{
    public string Name => "mock-dialogue-claims";

    public Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
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

        return Task.FromResult(new DialogueClaimExtractionResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            Claims: claims));
    }
}

public sealed class OllamaDialogueClaimExtractor : IDialogueClaimExtractor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public OllamaDialogueClaimExtractor(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _httpClient = httpClient ?? new HttpClient();
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "ollama-dialogue-claims";

    public async Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        var router = await ChatAsync(
            RouterSystemPrompt(),
            RouterUserPrompt(request),
            temperature: 0.0,
            maxTokens: 180,
            timeout.Token);
        if (!router.Success)
        {
            return Failure(router.RawText, router.Error ?? "Dialogue claim router failed.");
        }

        if (!RouterFoundClaim(router.Content))
        {
            return Success(router.Content, Array.Empty<DialogueClaimProposal>());
        }

        var detail = await ChatAsync(
            DetailSystemPrompt(),
            DetailUserPrompt(request),
            temperature: 0.1,
            maxTokens: 900,
            timeout.Token);
        if (!detail.Success)
        {
            return Failure(detail.RawText, detail.Error ?? "Dialogue claim detail extraction failed.");
        }

        try
        {
            var envelope = JsonSerializer.Deserialize<ClaimEnvelope>(detail.Content, JsonOptions);
            var claims = (envelope?.Claims ?? Array.Empty<DialogueClaimProposal>())
                .Where(claim => !claim.PlayerAuthored && !string.IsNullOrWhiteSpace(claim.Text))
                .ToArray();
            return Success(detail.Content, claims);
        }
        catch (JsonException ex)
        {
            return Failure(detail.Content, ex.Message);
        }
    }

    private async Task<ChatResult> ChatAsync(
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
                    num_ctx = 4096,
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

    private static bool RouterFoundClaim(string content)
    {
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            return ReadBool(root, "hasClaim")
                || ReadBool(root, "hasPromise")
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

    private static string RouterSystemPrompt() =>
        "You are Sorcerer's dialogue claim router. Return exactly JSON: "
        + "{\"hasClaim\":true|false,\"capabilities\":[\"memory|promise|merchant_stock|person|item|place\"],\"reason\":\"short\"}. "
        + "A claim is an NPC-authored or NPC-reported assertion that could matter later: a place exists, a person exists, a merchant has stock, a secret is true, a danger is coming, or a relationship/fact should be remembered. "
        + "Player-spoken assertions are not binding. Report false if only the player claimed it. Err toward yes for plausible NPC claims, but ignore jokes, greetings, and pure mood.";

    private static string RouterUserPrompt(DialogueClaimRequest request) =>
        JsonSerializer.Serialize(new
        {
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.PlayerText,
            npcDialogue = request.DialogueLines,
        }, JsonOptions);

    private static string DetailSystemPrompt() =>
        "You are Sorcerer's dialogue claim extractor. Return exactly JSON with shape {\"claims\":[...]}. "
        + "Extract only NPC-authored or NPC-reported claims, never player-invented claims. Reported claims may be uncertain; use confidence. "
        + "Use salience 1-5. Only salience 3+ will be shown to the player, so reserve 3+ for claims that open a route, stock, person, secret, place, threat, or tactical opportunity. "
        + "Set bindAsPromise true when the world should later deliver the claim organically through ordinary systems, especially places, landmarks, towns, items, people, or threats not already present. "
        + "Use category values such as memory, town, landmark, person, item, merchant_stock, threat, rumor. "
        + "If an existing merchant says they can sell something, use category merchant_stock, merchantId as speakerId, itemName, and bindAsPromise false. "
        + "Each claim object may include text, category, subject, salience, confidence, playerVisible, bindAsPromise, promiseKind, realizationKind, triggerHint, claimedPlace, targetEntityId, merchantId, itemName, playerAuthored, tags. "
        + "When the NPC's dialogue implies their personal feeling toward the listener changed, include updateBond true plus loyaltyDelta, fearDelta, admirationDelta, resentmentDelta, and bondPosture. Recent gift memories are relevant context, but the bond shift must come from the NPC's dialogue rather than the gift action alone. "
        + "For travel-delivered promises, prefer triggerHint travel and realizationKind site, item, person, or threat. Keep text concise and concrete.";

    private static string DetailUserPrompt(DialogueClaimRequest request) =>
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

    private DialogueClaimExtractionResult Success(
        string raw,
        IReadOnlyList<DialogueClaimProposal> claims) =>
        new(Name, raw, TechnicalFailure: false, Error: null, Claims: claims);

    private DialogueClaimExtractionResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, Claims: Array.Empty<DialogueClaimProposal>());

    private sealed record ClaimEnvelope(IReadOnlyList<DialogueClaimProposal>? Claims);

    private sealed record ChatResult(bool Success, string Content, string RawText, string? Error);
}
