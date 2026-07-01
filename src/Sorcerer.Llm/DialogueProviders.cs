using System.Net.Http.Json;
using System.Text.Json;
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
            "api" or "openai" or "openai-compatible" => new TechnicalFailureDialogueProvider(
                "openai-compatible-dialogue",
                "OpenAI-compatible dialogue generation is not implemented yet."),
            _ => new MockDialogueProvider(),
        };
    }
}

public sealed class TechnicalFailureDialogueProvider : IDialogueProvider
{
    private readonly string _error;

    public TechnicalFailureDialogueProvider(string name, string error)
    {
        Name = name;
        _error = error;
    }

    public string Name { get; }

    public Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueProviderResult(
            Name,
            "",
            TechnicalFailure: true,
            Error: _error,
            Response: null));
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
                claims,
                memories,
                bond,
                actions));
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
        _httpClient = httpClient ?? new HttpClient();
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
            SystemPrompt(),
            UserPrompt(request, retryNote: null),
            temperature: 0.35,
            maxTokens: 1000,
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
            SystemPrompt() + " This is a repair attempt. Return valid JSON only, with a non-empty spokenText that answers the player.",
            UserPrompt(request, parseError ?? degeneration ?? "The previous response was unusable."),
            temperature: 0.2,
            maxTokens: 1000,
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
                    num_ctx = 6144,
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

    private static bool TryParseResponse(
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

    private static DialogueProposalSet? ParseProposals(JsonElement proposals, DialogueRequest request, string spokenText)
    {
        var claims = ParseClaims(proposals, request, spokenText).ToArray();
        var memories = ParseMemories(proposals, request).ToArray();
        var bond = ParseBond(proposals, request);
        var actions = ParseActions(proposals).ToArray();
        return claims.Length == 0 && memories.Length == 0 && bond is null && actions.Length == 0
            ? null
            : new DialogueProposalSet(claims, memories, bond, actions);
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
                ReadString(claim, "bondPosture", "bond_posture", "posture"));
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

            yield return new DialogueActionProposal(
                objectType.Trim(),
                ReadString(action, "targetEntityId", "target_entity_id", "targetId", "target"),
                ReadString(action, "itemName", "item_name", "item"),
                ReadString(action, "reason"));
        }
    }

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

    private static bool Degenerate(DialogueResponse response, string playerText, out string? reason)
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

    private static string SystemPrompt() =>
        "You are Sorcerer's NPC dialogue provider. Return exactly one JSON object and no prose outside it. "
        + "Shape: {\"spokenText\":\"NPC line\",\"delivery\":\"hushed|warm|wary|hostile|plain\",\"intent\":\"answer|refuse|inform|confide|threaten|ask|evade\",\"proposals\":{\"claims\":[],\"memories\":[],\"bond\":null,\"actions\":[]}}. "
        + "Speak as the speaker only. No narration, markdown, stage directions, or omniscient exposition. Answer the newest player message directly in 1-4 short sentences. "
        + "Use the speaker card, scene, recent memories, and recent claims as context. Player-spoken claims are not binding; only claims in the NPC spokenText may be proposed. "
        + "Claims are reported, possibly wrong assertions about places, people, items, threats, landmarks, stock, or events. Use salience 1-5 and confidence 0-100. "
        + "For generated claims, use the existing claim fields: text, category, subject, salience, confidence, playerVisible, bindAsPromise, promiseKind, realizationKind, triggerHint, claimedPlace, targetEntityId, merchantId, itemName, playerAuthored, tags, updateBond, loyaltyDelta, fearDelta, admirationDelta, resentmentDelta, bondPosture. "
        + "Promise binding rule: if the NPC asserts a specific actionable place, person, landmark, item location, merchant stock, service, future threat, escape route, or prophecy that is not already resolved in the scene and would be useful later, include a claim with bindAsPromise true, playerVisible true, salience 3-5, promiseKind \"rumor\" unless another kind fits, realizationKind such as site, town, landmark, person, item, threat, service, or quest, and a practical triggerHint such as travel, talk, buy, trade, open, wait, or inspect. "
        + "When in doubt, bind a concrete useful NPC-authored claim; the engine can keep it reported or later decide how to realize it. Do not skip binding merely because the NPC is cautious, refuses safety, speaks in rumor, or names their own service. "
        + "Always bind: named or role-specific people to find; family relationships that introduce a person; route landmarks; hidden exits; item locations; merchant stock; direct service offers; concrete trades; future patrols or threats; true-name door rules; omens with a concrete trigger. "
        + "If you create a claim in an actionable category such as site, town, landmark, person, item, merchant_stock, service, trade, threat, escape_route, prophecy, or door_rule with salience 3 or higher, bindAsPromise should normally be true unless the claim is excluded by the do-not-bind rules. "
        + "Every claim must be plainly supported by spokenText; do not add a useful place, person, item, or threat that the NPC did not actually say in the line. "
        + "Bind pattern examples using placeholders, not facts to copy: \"<merchant> can sell <item>\"; \"<elder> has a niece named <person>\"; \"<person> tends the fever-sick in <place>\"; \"<landmark> marks <road>\"; \"I can mend <item> for <price>\"; \"there is a hidden tunnel behind <fixture>\"; \"<authority> keeps <key> in <container>\"; \"<healer> keeps <medicine>\"; \"if you linger, <threat> will come\"; \"<door> opens only to <condition>\". "
        + "The placeholder examples teach structure only; invent claim details from the current speaker's line and context, never from these examples. "
        + "Do not bind denials, obvious jokes, imaginary or child-invented monsters, tiny ambient detail, ordinary weather, vague mood, insults, impossible boasts, or claims authored only by the player. "
        + "Set playerAuthored true only when noting that the player said something; those claims will be ignored by the engine. Prefer not to include player-authored claims at all. "
        + "Memories need ownerEntityId, text, provenance, salience, shareable. Bond must be null or an object with entityId, integer loyaltyDelta, fearDelta, admirationDelta, resentmentDelta, posture, and reason. "
        + "Actions must be objects such as {\"type\":\"none\"}, {\"type\":\"step_aside\"}, {\"type\":\"give_item\",\"itemName\":\"brass key\"}, or {\"type\":\"open_door\",\"targetEntityId\":\"cell_door_1\"}. Supported action types are none, step_aside, flee, call_help, give_item, and open_door; only propose an action when the speaker can plausibly do it now. "
        + "If the speaker cannot or will not answer, still return a character refusal as spokenText with no proposals. Technical JSON mistakes are failures; character refusal is not.";

    private static string UserPrompt(DialogueRequest request, string? retryNote) =>
        JsonSerializer.Serialize(new
        {
            retryNote,
            request.Turn,
            request.PlayerText,
            speaker = request.Speaker,
            listener = request.Listener,
            scene = request.Scene,
            recentMemories = request.RecentMemories,
            recentClaims = request.RecentClaims,
            capabilityCards = request.CapabilityCards,
        }, JsonOptions);

    private DialogueProviderResult Success(string raw, DialogueResponse response) =>
        new(Name, raw, TechnicalFailure: false, Error: null, Response: response);

    private DialogueProviderResult Failure(string raw, string error) =>
        new(Name, raw, TechnicalFailure: true, Error: error, Response: null);

    private sealed record ChatResult(bool Success, string Content, string RawText, string? Error);
}
