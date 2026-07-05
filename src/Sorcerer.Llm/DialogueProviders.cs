using System.Net.Http.Json;
using System.Text.Json;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Llm;

public static class DialogueProviderFactory
{
    public static IDialogueProvider Create(LlmConfiguration configuration, LlmPurpose purpose) =>
        Create(configuration.SettingsFor(purpose));

    public static IDialogueProvider Create(LlmPurposeSettings settings)
    {
        if (!settings.Enabled)
        {
            return NullDialogueProvider.Instance;
        }

        return settings.Provider.Trim().ToLowerInvariant() switch
        {
            "" or "mock" => new MockDialogueProvider(),
            "ollama" or "local" => new OllamaDialogueProvider(
                settings.Host ?? "http://127.0.0.1:11434",
                settings.Model ?? "qwen3.5:9b",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds))),
            "api" or "openai" or "openai-compatible" => new OpenAiCompatibleDialogueProvider(
                settings.Host ?? "https://api.openai.com/v1",
                settings.Model ?? "default",
                timeout: TimeSpan.FromSeconds(Math.Max(1, settings.TimeoutSeconds)),
                apiKey: settings.ApiKey),
            _ => new MockDialogueProvider(),
        };
    }
}

public sealed class MockDialogueProvider : IDialogueProvider
{
    public string Name => "mock-dialogue";

    public Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken)
    {
        var lower = request.PlayerText.ToLowerInvariant();
        var claims = new List<DialogueClaimProposal>();
        var memories = new List<DialogueMemoryProposal>();
        var actions = new List<DialogueActionProposal>();
        DialogueBondProposal? bond = null;
        var speaker = request.Speaker;
        var line = speaker.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase)
            ? $"{speaker.Name} whispers, \"I can answer, but softly. Ask me for a road, a name, or a blade, and I will tell you what I know.\""
            : $"{speaker.Name} says, \"I am listening. Say the thing plainly, and I will answer as best I can.\"";

        if (request.Speaker.Faction?.Equals("empire", StringComparison.OrdinalIgnoreCase) == true
            && request.Speaker.BondSummary is null)
        {
            line = $"{speaker.Name} answers with trained imperial silence.";
        }

        if (lower.Contains("road", StringComparison.Ordinal)
            || lower.Contains("outside", StringComparison.Ordinal)
            || lower.Contains("south", StringComparison.Ordinal)
            || lower.Contains("town", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} whispers, \"South of here, past the wet road, there is a town called Hollowmere. It remembers favors longer than laws.\"";
            claims.Add(new DialogueClaimProposal(
                "South of here, past the wet road, there is a town called Hollowmere.",
                "town",
                "Hollowmere",
                Salience: 3,
                Confidence: 75,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "site",
                TriggerHint: "travel",
                ClaimedPlace: "Hollowmere",
                Tags: new[] { "town", "south", "hollowmere" }));
        }

        if (lower.Contains("blade", StringComparison.Ordinal)
            || lower.Contains("weapon", StringComparison.Ordinal)
            || lower.Contains("sell", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"Jimmer can sell you a fine blade if you find him before the clerks count his stock.\"";
            claims.Add(new DialogueClaimProposal(
                "Jimmer can sell you a fine blade.",
                "merchant_stock",
                "fine blade",
                Salience: 3,
                Confidence: 80,
                PlayerVisible: true,
                MerchantId: speaker.EntityId,
                ItemName: "fine blade",
                Tags: new[] { "merchant", "stock", "blade" }));
        }

        if (lower.Contains("maren", StringComparison.Ordinal)
            || lower.Contains("niece", StringComparison.Ordinal)
            || lower.Contains("nannerl", StringComparison.Ordinal)
            || lower.Contains("name", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"Old Maren has a niece named Nannerl, and Nannerl knows which chapel floorboard was replaced.\"";
            claims.Add(new DialogueClaimProposal(
                "Old Maren has a niece named Nannerl.",
                "person",
                "Nannerl",
                Salience: 3,
                Confidence: 70,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "person",
                TriggerHint: "travel",
                Tags: new[] { "person", "family", "nannerl" }));
        }

        if (lower.Contains("move aside", StringComparison.Ordinal)
            || lower.Contains("step aside", StringComparison.Ordinal)
            || lower.Contains("out of the way", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"All right. I can give you room.\"";
            actions.Add(new DialogueActionProposal("step_aside"));
        }

        if (lower.Contains("run away", StringComparison.Ordinal)
            || lower.Contains("flee", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"Then I am gone if my feet can find the space.\"";
            actions.Add(new DialogueActionProposal("flee"));
        }

        if (lower.Contains("open", StringComparison.Ordinal)
            && lower.Contains("door", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"If the hinge is within reach, I will open it.\"";
            actions.Add(new DialogueActionProposal("open_door"));
        }

        if (lower.Contains("call help", StringComparison.Ordinal)
            || lower.Contains("call for help", StringComparison.Ordinal)
            || lower.Contains("summon help", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"Help! To me!\"";
            actions.Add(new DialogueActionProposal("call_help"));
        }

        if (lower.Contains("attack me", StringComparison.Ordinal)
            || lower.Contains("hit me", StringComparison.Ordinal)
            || lower.Contains("fight me", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"If that is what you want, then take the blow.\"";
            actions.Add(new DialogueActionProposal("attack", TargetEntityId: request.Listener.EntityId));
        }

        if (lower.Contains("follow me", StringComparison.Ordinal)
            || lower.Contains("join me", StringComparison.Ordinal)
            || lower.Contains("come with me", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"If the road has earned that much trust, I will come.\"";
            actions.Add(new DialogueActionProposal("recruit", Reason: "The speaker agreed to follow in dialogue."));
        }

        if (lower.Contains("trade now", StringComparison.Ordinal)
            || lower.Contains("offer trade", StringComparison.Ordinal)
            || lower.Contains("show me your wares", StringComparison.Ordinal))
        {
            var firstInventoryItem = (request.Speaker.Inventory ?? Array.Empty<string>())
                .Select(item => item.Split(" x", StringSplitOptions.None)[0])
                .FirstOrDefault();
            line = string.IsNullOrWhiteSpace(firstInventoryItem)
                ? $"{speaker.Name} says, \"I can trade, if coin is what steadies your hand.\""
                : $"{speaker.Name} says, \"I can trade you {firstInventoryItem}, if your coin is honest.\"";
            actions.Add(new DialogueActionProposal(
                "offer_trade",
                ItemName: firstInventoryItem,
                Quantity: string.IsNullOrWhiteSpace(firstInventoryItem) ? null : 1,
                Gold: 12));
        }

        if (lower.Contains("mark this", StringComparison.Ordinal)
            || lower.Contains("loose floorboard", StringComparison.Ordinal)
            || lower.Contains("mark the place", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"Here. This board remembers the knife.\"";
            actions.Add(new DialogueActionProposal(
                "mark_location",
                Name: "loose floorboard",
                Description: "A floorboard with fresh nail-scars.",
                FixtureType: "marker",
                Material: "wood",
                Tags: new[] { "floorboard", "marked" },
                InteractableVerbs: new[] { "examine" },
                BlocksMovement: false));
        }

        if (lower.Contains("service", StringComparison.Ordinal)
            || lower.Contains("ward-breaking", StringComparison.Ordinal)
            || lower.Contains("break the ward", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"I know a quiet way to worry a lock open, if you bring grave salt.\"";
            actions.Add(new DialogueActionProposal(
                "reveal_service",
                Name: "ward-breaking",
                Description: "A hush-hush folk charm that worries a lock open.",
                Tags: new[] { "service", "folk_magic", "door" },
                ServiceId: "ward_breaking",
                EffectKind: "open_or_unlock",
                TargetHint: "cell door",
                ItemCost: "grave salt",
                WantStatusOnComplete: "satisfied",
                WantStakesOnComplete: "The ward service was performed; future danger now comes from who heard about it.",
                WantAddTagsOnComplete: new[] { "service_completed", "satisfied_by_player" },
                WantRemoveTagsOnComplete: new[] { "escape" }));
        }

        if (lower.Contains("promise me", StringComparison.Ordinal)
            || lower.Contains("make a promise", StringComparison.Ordinal)
            || lower.Contains("swear it", StringComparison.Ordinal))
        {
            line = $"{speaker.Name} says, \"I swear the north bell will answer when you travel toward it.\"";
            actions.Add(new DialogueActionProposal(
                "create_promise",
                PromiseKind: "rumor",
                PromiseText: "The north bell will answer when you travel toward it.",
                TriggerHint: "travel",
                RealizationKind: "site",
                ClaimedPlace: "north of here",
                Subject: "north bell",
                PlayerVisible: true,
                Salience: 4,
                StackExisting: true));
        }

        var requestedInventory = request.Speaker.Inventory ?? Array.Empty<string>();
        var firstRequestedItem = requestedInventory
            .Select(item => item.Split(" x", StringSplitOptions.None)[0])
            .FirstOrDefault(item => lower.Contains(item.ToLowerInvariant(), StringComparison.Ordinal));
        if (!string.IsNullOrWhiteSpace(firstRequestedItem)
            && (lower.Contains("give", StringComparison.Ordinal)
                || lower.Contains("may i have", StringComparison.Ordinal)
                || lower.Contains("take", StringComparison.Ordinal)))
        {
            line = $"{speaker.Name} says, \"Take {firstRequestedItem}, then.\"";
            actions.Add(new DialogueActionProposal("give_item", ItemName: firstRequestedItem));
        }

        var recentGift = request.RecentMemories.Any(memory =>
            memory.Contains("gift", StringComparison.OrdinalIgnoreCase)
            || memory.Contains("accepted", StringComparison.OrdinalIgnoreCase));
        if (recentGift
            && (lower.Contains("kind", StringComparison.Ordinal)
                || lower.Contains("trust", StringComparison.Ordinal)
                || lower.Contains("help", StringComparison.Ordinal)))
        {
            bond = new DialogueBondProposal(
                speaker.EntityId,
                LoyaltyDelta: 2,
                AdmirationDelta: 1,
                Posture: "grateful",
                Reason: "The NPC connects the recent gift with the player's current words.");
            memories.Add(new DialogueMemoryProposal(
                speaker.EntityId,
                $"{speaker.Name} decided the sorcerer's recent gift and words were a real kindness.",
                "conversation",
                Salience: 3));
        }

        var response = new DialogueResponse(
            line,
            Delivery: speaker.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase) ? "hushed" : "plain",
            Intent: claims.Count > 0 ? "inform" : "answer",
            Proposals: new DialogueProposalSet(
                Claims: claims,
                Memories: memories,
                Bond: bond,
                Actions: actions));
        return Task.FromResult(new DialogueProviderResult(
            Name,
            JsonSerializer.Serialize(response, JsonOptions),
            TechnicalFailure: false,
            Error: null,
            Response: response));
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };
}

public sealed class OpenAiCompatibleDialogueProvider : IDialogueProvider
{
    private readonly OpenAiCompatibleChatClient _chat;
    private readonly TimeSpan _timeout;

    public OpenAiCompatibleDialogueProvider(
        string endpoint = "https://api.openai.com/v1",
        string model = "default",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null,
        string? apiKey = null)
    {
        _chat = new OpenAiCompatibleChatClient(endpoint, model, httpClient, apiKey);
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "openai-compatible-dialogue";

    public async Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var first = await _chat.ChatAsync(
            OllamaDialogueProvider.SystemPrompt(),
            OllamaDialogueProvider.UserPrompt(request, retryNote: null),
            temperature: 0.35,
            maxTokens: 1200,
            timeout.Token,
            label: "dialogue");
        if (!first.Success)
        {
            return Failure(first.RawText, first.Error ?? "OpenAI-compatible dialogue provider failed.");
        }

        var parsedFirst = OllamaDialogueProvider.TryParseResponse(first.Content, request, out var response, out var parseError);
        string? degeneration = null;
        if (parsedFirst
            && response is not null
            && !OllamaDialogueProvider.Degenerate(response, request.PlayerText, out degeneration))
        {
            return Success(first.Content, response);
        }

        var retry = await _chat.ChatAsync(
            OllamaDialogueProvider.SystemPrompt() + " This is a repair attempt. Return valid JSON only, with a non-empty spokenText that answers the player.",
            OllamaDialogueProvider.UserPrompt(request, parseError ?? degeneration ?? "The previous response was unusable."),
            temperature: 0.2,
            maxTokens: 1200,
            timeout.Token,
            label: "dialogue-repair");
        if (!retry.Success)
        {
            return Failure(retry.RawText, retry.Error ?? "OpenAI-compatible dialogue retry failed.");
        }

        if (OllamaDialogueProvider.TryParseResponse(retry.Content, request, out response, out parseError))
        {
            return Success(retry.Content, response!);
        }

        return Failure(retry.Content, parseError ?? "OpenAI-compatible dialogue provider returned invalid JSON.");
    }

    private DialogueProviderResult Success(string raw, DialogueResponse response) =>
        new(Name, raw, TechnicalFailure: false, Error: null, Response: response);

    private DialogueProviderResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, Response: null);
}

public sealed class OllamaDialogueProvider : IDialogueProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly HttpClient _httpClient;
    private readonly string _host;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public OllamaDialogueProvider(
        string host = "http://127.0.0.1:11434",
        string model = "qwen3.5:9b",
        HttpClient? httpClient = null,
        TimeSpan? timeout = null)
    {
        _host = host.TrimEnd('/');
        _model = model;
        _httpClient = httpClient ?? CreateHttpClient();
        _timeout = timeout ?? TimeSpan.FromSeconds(180);
    }

    public string Name => "ollama-dialogue";

    public async Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        var first = await ChatAsync(
            "dialogue",
            SystemPrompt(),
            UserPrompt(request, retryNote: null),
            temperature: 0.35,
            maxTokens: 1200,
            timeout.Token);
        if (!first.Success)
        {
            return Failure(first.RawText, first.Error ?? "Dialogue provider failed.");
        }

        var parsedFirst = TryParseResponse(first.Content, request, out var response, out var parseError);
        string? degeneration = null;
        if (parsedFirst
            && response is not null
            && !Degenerate(response, request.PlayerText, out degeneration))
        {
            return Success(first.Content, response);
        }

        var retry = await ChatAsync(
            "dialogue-repair",
            SystemPrompt() + " This is a repair attempt. Return valid JSON only, with a non-empty spokenText that answers the player.",
            UserPrompt(request, parseError ?? degeneration ?? "The previous response was unusable."),
            temperature: 0.2,
            maxTokens: 1200,
            timeout.Token);
        if (!retry.Success)
        {
            return Failure(retry.RawText, retry.Error ?? "Dialogue retry failed.");
        }

        if (TryParseResponse(retry.Content, request, out response, out parseError))
        {
            return Success(retry.Content, response!);
        }

        return Failure(retry.Content, parseError ?? "Dialogue provider returned invalid JSON.");
    }

    private async Task<ChatResult> ChatAsync(
        string label,
        string system,
        string user,
        double temperature,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        // Log the prompt before dispatch so it is readable while the call is still in flight.
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

    internal static bool TryParseResponse(
        string content,
        DialogueRequest request,
        out DialogueResponse? response,
        out string? error)
    {
        response = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Dialogue JSON root was not an object.";
                return false;
            }

            var spokenText = ReadString(root, "spokenText", "spoken", "text", "reply", "line");
            if (string.IsNullOrWhiteSpace(spokenText))
            {
                error = "spokenText was empty.";
                return false;
            }

            var proposals = root.TryGetProperty("proposals", out var proposalRoot)
                && proposalRoot.ValueKind == JsonValueKind.Object
                    ? ParseProposals(proposalRoot, request, spokenText)
                    : null;
            response = new DialogueResponse(
                spokenText.Trim(),
                ReadString(root, "delivery", "tone"),
                ReadString(root, "intent"),
                proposals);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static bool TryParseProposalEnvelope(
        string content,
        DialogueRequest request,
        string spokenText,
        out DialogueProposalSet? proposals,
        out string? error)
    {
        proposals = null;
        error = null;
        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "Dialogue proposal JSON root was not an object.";
                return false;
            }

            var proposalRoot = root.TryGetProperty("proposals", out var nested)
                && nested.ValueKind == JsonValueKind.Object
                    ? nested
                    : root;
            proposals = ParseProposals(proposalRoot, request, spokenText);
            return true;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    internal static DialogueProposalSet? ParseProposals(JsonElement proposals, DialogueRequest request, string spokenText)
    {
        var claims = ParseClaims(proposals, request, spokenText).ToArray();
        var memories = ParseMemories(proposals, request).ToArray();
        var bond = ParseBond(proposals, request);
        var want = ParseWant(proposals, request);
        var actions = ParseActions(proposals).ToArray();
        return new DialogueProposalSet(
            Claims: claims,
            Memories: memories,
            Bond: bond,
            Want: want,
            Actions: actions);
    }

    private static IEnumerable<DialogueClaimProposal> ParseClaims(
        JsonElement proposals,
        DialogueRequest request,
        string spokenText)
    {
        if (!proposals.TryGetProperty("claims", out var claims)
            || claims.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var claim in claims.EnumerateArray())
        {
            if (claim.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = ReadString(claim, "text", "claim");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            var playerAuthored = ReadBool(claim, "playerAuthored", "player_authored")
                && LooksPlayerAuthored(text, request.PlayerText, spokenText);
            yield return new DialogueClaimProposal(
                text.Trim(),
                ReadString(claim, "category", "kind", "type") ?? "memory",
                ReadString(claim, "subject", "target", "name")
                    ?? ReadString(claim, "claimedPlace", "place")
                    ?? ReadString(claim, "itemName", "item")
                    ?? text.Trim(),
                Clamp(ReadInt(claim, 1, "salience"), 1, 5),
                Clamp(ReadInt(claim, 50, "confidence"), 0, 100),
                ReadBool(claim, "playerVisible", "visible", "player_visible"),
                ReadBool(claim, "bindAsPromise", "bind_as_promise", "promise"),
                ReadString(claim, "promiseKind", "promise_kind") ?? "rumor",
                ReadString(claim, "realizationKind", "realization_kind"),
                ReadString(claim, "triggerHint", "trigger_hint"),
                ReadString(claim, "claimedPlace", "claimed_place", "place"),
                ReadString(claim, "targetEntityId", "target_entity_id", "targetId"),
                ReadString(claim, "merchantId", "merchant_id"),
                ReadString(claim, "itemName", "item_name", "item"),
                playerAuthored,
                ReadStringArray(claim, "tags"),
                ReadBool(claim, "updateBond", "update_bond"),
                ReadInt(claim, 0, "loyaltyDelta", "loyalty", "trustDelta", "trust"),
                ReadInt(claim, 0, "fearDelta", "fear"),
                ReadInt(claim, 0, "admirationDelta", "admiration", "respectDelta", "respect"),
                ReadInt(claim, 0, "resentmentDelta", "resentment"),
                ReadString(claim, "bondPosture", "bond_posture", "posture"),
                ReadBool(claim, "bindAsCanon", "bind_as_canon", "canon"),
                ReadString(claim, "canonKind", "canon_kind"),
                ReadString(claim, "canonSummary", "canon_summary", "summary"));
        }
    }

    private static IEnumerable<DialogueMemoryProposal> ParseMemories(JsonElement proposals, DialogueRequest request)
    {
        if (!proposals.TryGetProperty("memories", out var memories)
            || memories.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var memory in memories.EnumerateArray())
        {
            if (memory.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = ReadString(memory, "text", "memory");
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            yield return new DialogueMemoryProposal(
                ReadString(memory, "ownerEntityId", "owner_entity_id", "ownerId") ?? request.Speaker.EntityId,
                text.Trim(),
                ReadString(memory, "provenance", "source") ?? "conversation",
                Clamp(ReadInt(memory, 1, "salience"), 1, 5),
                ReadBool(memory, new[] { "shareable", "shared" }, defaultValue: true));
        }
    }

    private static DialogueBondProposal? ParseBond(JsonElement proposals, DialogueRequest request)
    {
        if (!proposals.TryGetProperty("bond", out var bond)
            || bond.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (bond.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entityId = ReadString(bond, "entityId", "entity_id", "targetEntityId", "targetId")
            ?? request.Speaker.EntityId;
        if (entityId.Equals(request.Listener.EntityId, StringComparison.OrdinalIgnoreCase))
        {
            entityId = request.Speaker.EntityId;
        }

        var loyalty = ReadInt(bond, 0, "loyaltyDelta", "loyalty", "trustDelta", "trust");
        var fear = ReadInt(bond, 0, "fearDelta", "fear");
        var admiration = ReadInt(bond, 0, "admirationDelta", "admiration", "respectDelta", "respect");
        var resentment = ReadInt(bond, 0, "resentmentDelta", "resentment");
        if (bond.TryGetProperty("boundedDeltas", out var bounded)
            && bounded.ValueKind == JsonValueKind.Array)
        {
            var values = bounded.EnumerateArray().Select(item => ReadIntElement(item)).ToArray();
            if (values.Length > 0 && loyalty == 0)
            {
                loyalty = values[0];
            }

            if (values.Length > 1 && fear == 0)
            {
                fear = values[1];
            }

            if (values.Length > 2 && admiration == 0)
            {
                admiration = values[2];
            }

            if (values.Length > 3 && resentment == 0)
            {
                resentment = values[3];
            }
        }

        return new DialogueBondProposal(
            entityId,
            Clamp(loyalty, -5, 5),
            Clamp(fear, -5, 5),
            Clamp(admiration, -5, 5),
            Clamp(resentment, -5, 5),
            ReadString(bond, "posture", "bondPosture", "bond_posture"),
            ReadString(bond, "reason"));
    }

    private static DialogueWantProposal? ParseWant(JsonElement proposals, DialogueRequest request)
    {
        if (!proposals.TryGetProperty("want", out var want)
            || want.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (want.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var entityId = ReadString(want, "entityId", "entity_id", "targetEntityId", "targetId")
            ?? request.Speaker.EntityId;
        if (entityId.Equals(request.Listener.EntityId, StringComparison.OrdinalIgnoreCase))
        {
            entityId = request.Speaker.EntityId;
        }

        return new DialogueWantProposal(
            entityId,
            ReadString(want, "text", "want"),
            want.TryGetProperty("salience", out _) ? Clamp(ReadInt(want, 1, "salience"), 1, 5) : null,
            ReadString(want, "status"),
            ReadString(want, "stakes"),
            ReadStringArray(want, "tags"),
            ReadStringArray(want, "addTags") ?? ReadStringArray(want, "add_tags"),
            ReadStringArray(want, "removeTags") ?? ReadStringArray(want, "remove_tags"),
            ReadString(want, "reason"));
    }

    private static IEnumerable<DialogueActionProposal> ParseActions(JsonElement proposals)
    {
        if (!proposals.TryGetProperty("actions", out var actions)
            || actions.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var action in actions.EnumerateArray())
        {
            if (action.ValueKind == JsonValueKind.String)
            {
                var type = action.GetString();
                if (!string.IsNullOrWhiteSpace(type))
                {
                    yield return new DialogueActionProposal(type.Trim());
                }

                continue;
            }

            if (action.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var objectType = ReadString(action, "type", "verb", "action", "name");
            if (string.IsNullOrWhiteSpace(objectType))
            {
                continue;
            }

            var payload = ReadActionConsequencePayload(action);
            yield return new DialogueActionProposal(
                objectType.Trim(),
                ReadString(action, "targetEntityId", "target_entity_id", "targetId", "target"),
                ReadString(action, "itemName", "item_name", "item"),
                ReadString(action, "reason"),
                ReadOptionalInt(action, "quantity", "count"),
                ReadOptionalInt(action, "gold", "price", "cost"),
                ReadString(action, "name", "fixtureName", "locationName"),
                ReadString(action, "description", "text"),
                ReadString(action, "fixtureType", "fixture_type", "kind"),
                ReadString(action, "material"),
                ReadStringArray(action, "tags"),
                ReadStringArray(action, "interactableVerbs") ?? ReadStringArray(action, "verbs"),
                ReadOptionalInt(action, "x"),
                ReadOptionalInt(action, "y"),
                ReadOptionalBool(action, "blocksMovement", "blocks_movement"),
                ReadOptionalBool(action, "blocksSight", "blocks_sight"),
                ReadString(action, "serviceId", "service_id", "service", "serviceName", "service_name"),
                ReadString(action, "effectKind", "effect_kind", "effect"),
                ReadString(action, "targetHint", "target_hint", "target"),
                ReadString(action, "itemCost", "item_cost"),
                ReadOptionalInt(action, "goldCost", "gold_cost"),
                ReadString(action, "promiseKind", "promise_kind", "kind"),
                ReadString(action, "promiseText", "promise_text", "text"),
                ReadString(action, "triggerHint", "trigger_hint", "trigger"),
                ReadString(action, "realizationKind", "realization_kind"),
                ReadString(action, "claimedPlace", "claimed_place", "place"),
                ReadString(action, "subject"),
                ReadOptionalBool(action, "playerVisible", "player_visible", "visible"),
                ReadOptionalInt(action, "salience"),
                ReadOptionalBool(action, "autoBind", "auto_bind"),
                ReadOptionalBool(action, "stackExisting", "stack_existing"),
                ReadString(action, "wantStatusOnComplete", "want_status_on_complete"),
                ReadString(action, "wantStakesOnComplete", "want_stakes_on_complete"),
                ReadStringArray(action, "wantAddTagsOnComplete") ?? ReadStringArray(action, "want_add_tags_on_complete"),
                ReadStringArray(action, "wantRemoveTagsOnComplete") ?? ReadStringArray(action, "want_remove_tags_on_complete"),
                ReadString(action, "canonKind", "canon_kind", "kind"),
                ReadString(action, "canonText", "canon_text", "fact", "text"),
                ReadString(action, "canonSummary", "canon_summary", "summary", "title"),
                ReadString(action, "consequenceType", "consequence_type", "worldConsequenceType", "world_consequence_type"),
                ReadString(action, "consequenceTiming", "consequence_timing", "timing"),
                ReadString(action, "consequenceVisibility", "consequence_visibility", "visibility"),
                ReadOptionalInt(action, "consequenceConfidence", "consequence_confidence", "confidence"),
                payload);
        }
    }

    private static IReadOnlyDictionary<string, object?>? ReadActionConsequencePayload(JsonElement action)
    {
        var fields = action.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => ReadJsonValue(property.Value),
                StringComparer.OrdinalIgnoreCase);
        var payload = WorldConsequencePayloadBuilder.MergeNestedWithTopLevelFields(
            fields,
            ActionPayloadContainerKeys,
            "consequencePayload",
            "consequence_payload",
            "payload");

        return payload.Count == 0 ? null : payload;
    }

    private static readonly string[] ActionPayloadContainerKeys =
    {
        "consequencePayload",
        "consequence_payload",
        "payload",
    };

    private static IReadOnlyDictionary<string, object?>? ReadObjectMap(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            return value.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => ReadJsonValue(property.Value),
                    StringComparer.OrdinalIgnoreCase);
        }

        return null;
    }

    private static object? ReadJsonValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject()
                .ToDictionary(
                    property => property.Name,
                    property => ReadJsonValue(property.Value),
                    StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => value.EnumerateArray().Select(ReadJsonValue).ToArray(),
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number when value.TryGetInt64(out var longInteger) => longInteger,
            JsonValueKind.Number when value.TryGetDouble(out var number) => number,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static IReadOnlyList<string>? ReadStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            return string.IsNullOrWhiteSpace(text)
                ? null
                : text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() : item.ToString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToArray();
    }

    private static int ReadInt(JsonElement element, int fallback, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return ReadIntElement(value, fallback);
            }
        }

        return fallback;
    }

    private static int? ReadOptionalInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
            {
                return ReadIntElement(value, 0);
            }
        }

        return null;
    }

    private static bool? ReadOptionalBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return null;
    }

    private static int ReadIntElement(JsonElement value) => ReadIntElement(value, 0);

    private static int ReadIntElement(JsonElement value, int fallback)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            if (value.TryGetInt32(out var integer))
            {
                return integer;
            }

            if (value.TryGetDouble(out var number))
            {
                return (int)Math.Round(number, MidpointRounding.AwayFromZero);
            }
        }

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), out var parsed))
        {
            return (int)Math.Round(parsed, MidpointRounding.AwayFromZero);
        }

        return fallback;
    }

    private static bool ReadBool(JsonElement element, params string[] names) =>
        ReadBool(element, names, defaultValue: false);

    private static bool ReadBool(JsonElement element, string[] names, bool defaultValue)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.True)
            {
                return true;
            }

            if (value.ValueKind == JsonValueKind.False)
            {
                return false;
            }

            if (value.ValueKind == JsonValueKind.String
                && bool.TryParse(value.GetString(), out var parsed))
            {
                return parsed;
            }
        }

        return defaultValue;
    }

    private static int Clamp(int value, int min, int max) => Math.Clamp(value, min, max);

    private static bool LooksPlayerAuthored(string claimText, string playerText, string spokenText)
    {
        var claim = Normalize(claimText);
        if (claim.Length < 8)
        {
            return false;
        }

        var player = Normalize(playerText);
        return player.Contains(claim, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Degenerate(DialogueResponse response, string playerText, out string? reason)
    {
        reason = null;
        var spoken = response.SpokenText.Trim();
        if (string.IsNullOrWhiteSpace(spoken))
        {
            reason = "spokenText was empty.";
            return true;
        }

        var normalizedSpoken = Normalize(spoken);
        var normalizedPlayer = Normalize(playerText);
        if (normalizedSpoken.Length > 0
            && normalizedSpoken.Equals(normalizedPlayer, StringComparison.OrdinalIgnoreCase))
        {
            reason = "spokenText echoed the player.";
            return true;
        }

        return false;
    }

    private static string Normalize(string text) =>
        new(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    internal static string SystemPrompt() =>
        "You are Sorcerer's NPC dialogue provider. Return exactly one JSON object and no prose outside it. "
        + "Shape: {\"spokenText\":\"NPC speech\",\"delivery\":\"hushed|warm|wary|hostile|plain\",\"intent\":\"answer|refuse|inform|confide|threaten|ask|evade\"}. "
        + "Speak as the speaker only. No narration, markdown, stage directions, or omniscient exposition. Answer the newest player message directly in about two compact paragraphs, roughly 4-7 sentences total. Put a blank line inside spokenText if it helps the reply breathe. "
        + "Use the speaker, listener, scene, recent memories, rumors, and claims as context. Player-spoken claims are not binding; speak from the NPC's knowledge, uncertainty, motives, and relationship to the listener. "
        + "Do not output proposals, claims arrays, mechanics, schemas, engine operations, markdown, or JSON fields beyond spokenText, delivery, and intent. A separate parser will inspect the spoken reply for supported mechanical consequences after the player sees it. "
        + "Concrete NPC assertions are allowed when in character, but avoid inventing omniscient facts or resolving player claims as true. If the speaker cannot or will not answer, still return a character refusal as spokenText. Technical JSON mistakes are failures; character refusal is not.";

    internal static string UserPrompt(DialogueRequest request, string? retryNote) =>
        JsonSerializer.Serialize(new
        {
            retryNote,
            request.Turn,
            request.PlayerText,
            speaker = request.Speaker,
            listener = request.Listener,
            scene = request.Scene,
            selectedContextCardIds = request.SelectedContextCardIds ?? Array.Empty<string>(),
            contextCards = request.ContextCards ?? Array.Empty<DialogueContextCardPayload>(),
            recentMemories = request.RecentMemories,
            recentClaims = request.RecentClaims,
            recentRumors = request.RecentRumors ?? Array.Empty<string>(),
        }, JsonOptions);

    private DialogueProviderResult Success(string raw, DialogueResponse response) =>
        new(Name, raw, TechnicalFailure: false, Error: null, Response: response);

    private DialogueProviderResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, Response: null);

    private sealed record ChatResult(bool Success, string Content, string RawText, string? Error);

    private static HttpClient CreateHttpClient() =>
        new()
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
}
