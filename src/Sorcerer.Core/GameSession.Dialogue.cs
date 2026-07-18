using System.Diagnostics;
using System.Text.Json;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

/// <summary>
/// <see cref="GameSession"/> the NPC dialogue turn: context routing, the provider call, response
/// normalization, exchange memory, and the parser/claim-extraction hand-off. Applying the
/// generated proposal set lives in GameSession.DialogueProposals.cs. Split from the GameSession
/// orchestrator (Phase 0.3); ExecuteAsync, the command switch, View/Observation, and the
/// post-action pipeline stay in GameSession.cs.
/// </summary>
public sealed partial class GameSession
{
    private async Task<ActionResult> TalkAsync(
        TalkCommand command,
        CancellationToken cancellationToken)
    {
        if (_dialogueProvider is NullDialogueProvider)
        {
            return Engine.Talk(command.Text);
        }

        var preparation = Engine.PrepareDialogue(command.Text);
        if (preparation.ImmediateResult is not null || preparation.Turn is null)
        {
            return preparation.ImmediateResult
                ?? ActionResult.Simple(
                    "talk",
                    success: false,
                    consumedTurn: false,
                    Engine.State.Turn,
                    Engine.State.Turn,
                    "No one nearby is ready to talk.");
        }

        var route = await RouteDialogueContextAsync(preparation.Turn, cancellationToken);
        var request = route.Assembly.BuildDialogueRequest(route.Selection) with
        {
            RecentDialogue = RecentDialogueFor(preparation.Turn.SpeakerId),
        };
        route = route with
        {
            Record = AttachGeneratorRequestMetrics(route.Record, request),
        };
        DialogueProviderResult providerResult;
        try
        {
            providerResult = await _dialogueProvider.ResolveAsync(request, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            providerResult = new DialogueProviderResult(
                _dialogueProvider.Name,
                "",
                TechnicalFailure: true,
                Error: ex.Message,
                Response: null);
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                _dialogueProvider.Name,
                "",
                ex.Message);
            RecordDialogueAudit(request, providerResult, failure, new[] { ex.Message }, route.Record);
            return failure with
            {
                Dialogue = ToDialogueResolutionRecord(providerResult),
                DialogueRoute = route.Record,
            };
        }

        if (providerResult.TechnicalFailure || providerResult.Response is null)
        {
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                providerResult.Provider,
                providerResult.RawText,
                providerResult.Error ?? "Dialogue provider failed.");
            RecordDialogueAudit(request, providerResult, failure, new[] { providerResult.Error ?? "Dialogue provider failed." }, route.Record);
            return failure with
            {
                Dialogue = ToDialogueResolutionRecord(providerResult),
                DialogueRoute = route.Record,
            };
        }

        var normalized = NormalizeDialogueResponse(request, providerResult.Response, out var validationError);
        if (normalized is null)
        {
            var failure = DialogueTechnicalFailure(
                preparation.Turn,
                providerResult.Provider,
                providerResult.RawText,
                validationError ?? "Dialogue provider returned invalid speech.");
            RecordDialogueAudit(request, providerResult, failure, new[] { validationError ?? "invalid_dialogue" }, route.Record);
            return failure with
            {
                Dialogue = ToDialogueResolutionRecord(providerResult),
                DialogueRoute = route.Record,
            };
        }

        var result = Engine.ApplyGeneratedDialogue(
            preparation.Turn,
            normalized.SpokenText,
            providerResult.Provider,
            providerResult.RawText,
            normalized.Delivery,
            normalized.Intent);
        result = ApplyGeneratedDialogueProposals(
            result,
            preparation.Turn,
            request,
            providerResult.Provider,
            normalized);
        result = result with
        {
            Dialogue = ToDialogueResolutionRecord(providerResult with { Response = normalized }),
            DialogueRoute = route.Record,
        };
        RememberDialogueExchange(preparation.Turn, request.PlayerText, normalized.SpokenText);
        RecordDialogueAudit(request, providerResult, result, Array.Empty<string>(), route.Record);
        return result;
    }

    private IReadOnlyList<string>? RecentDialogueFor(string speakerId) =>
        _recentDialogueBySpeaker.TryGetValue(speakerId, out var lines) && lines.Count > 0
            ? lines.ToArray()
            : null;

    /// <summary>
    /// Appends this turn's player line and the NPC's reply to the speaker's rolling history, so the
    /// next turn with the same NPC can reference what was just said. Trims each line and keeps only
    /// the last <see cref="RecentDialogueLineCap"/> lines (docs/OPTIMIZATION_PLAN.md WS3.4).
    /// </summary>
    private void RememberDialogueExchange(PreparedDialogueTurn turn, string playerText, string spokenText)
    {
        if (!_recentDialogueBySpeaker.TryGetValue(turn.SpeakerId, out var lines))
        {
            lines = new List<string>();
            _recentDialogueBySpeaker[turn.SpeakerId] = lines;
        }

        lines.Add($"player: {TrimDialogueLine(playerText)}");
        lines.Add($"{turn.SpeakerName}: {TrimDialogueLine(spokenText)}");
        if (lines.Count > RecentDialogueLineCap)
        {
            lines.RemoveRange(0, lines.Count - RecentDialogueLineCap);
        }
    }

    private static string TrimDialogueLine(string text)
    {
        var collapsed = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length > RecentDialogueLineLength
            ? collapsed[..RecentDialogueLineLength].TrimEnd() + "…"
            : collapsed;
    }

    private static DialogueResolutionRecord ToDialogueResolutionRecord(DialogueProviderResult providerResult) =>
        new(
            providerResult.Provider,
            providerResult.RawText,
            providerResult.TechnicalFailure,
            providerResult.Error,
            providerResult.Response);

    private static bool ShouldQueueDialogueClaimExtraction(ActionResult result) =>
        result.Dialogue?.Response?.Proposals is null;

    private async Task<PreparedDialogueRoute> RouteDialogueContextAsync(
        PreparedDialogueTurn turn,
        CancellationToken cancellationToken)
    {
        var assembly = DialogueContextAssembler.Build(Engine, turn);
        var stopwatch = Stopwatch.StartNew();
        DialogueRouteResult result;
        try
        {
            result = await _dialogueRouter.RouteAsync(assembly.RouteRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            result = new DialogueRouteResult(
                _dialogueRouter.Name,
                "",
                TechnicalFailure: true,
                Error: ex.Message,
                SelectedCardIds: Array.Empty<string>());
        }

        stopwatch.Stop();
        var selection = assembly.Select(result);
        var record = new DialogueRouteRecord(
            assembly.RouteRequest,
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            selection.SelectedCardIds,
            selection.FallbackCardIds,
            result.Reason,
            selection.UsedFallback,
            assembly.CreateMetrics(selection, stopwatch.ElapsedMilliseconds));
        return new PreparedDialogueRoute(record, assembly, selection);
    }

    private static DialogueRouteRecord AttachGeneratorRequestMetrics(
        DialogueRouteRecord record,
        DialogueRequest request)
    {
        var generatorRequestBytes = DialoguePayloadSizer.JsonUtf8Bytes(request);
        if (record.Metrics is null)
        {
            return record;
        }

        return record with
        {
            Metrics = record.Metrics with { GeneratorRequestBytes = generatorRequestBytes },
        };
    }

    private IReadOnlyList<StateDelta> AddDialogueExchangeMemory(PreparedDialogueTurn turn, string spokenText)
    {
        var speaker = Engine.EntityById(turn.SpeakerId);
        if (speaker is null)
        {
            return Array.Empty<StateDelta>();
        }

        var text = $"{turn.SpeakerName} spoke with the sorcerer. Player: {turn.PlayerText} Reply: {spokenText}";
        return Engine.ApplyConsequence(WorldConsequence.RecordMemory(
            "dialogue_exchange",
            speaker.Id.Value,
            text,
            "conversation",
            2,
            shareable: false,
            sourceEntityId: Engine.State.ControlledEntityId.Value,
            evidence: turn.PlayerText,
            reason: "Generated dialogue exchange.",
            operation: "dialogueExchangeMemory",
            details: new Dictionary<string, object?>
            {
                ["speakerId"] = speaker.Id.Value,
                ["playerText"] = turn.PlayerText,
                ["spokenText"] = spokenText,
            })).Deltas;
    }

    private DialogueResponse? NormalizeDialogueResponse(
        DialogueRequest request,
        DialogueResponse response,
        out string? error)
    {
        error = null;
        var spoken = (response.SpokenText ?? "").Trim();
        if (string.IsNullOrWhiteSpace(spoken))
        {
            error = "Dialogue provider returned empty spokenText.";
            return null;
        }

        if (spoken.Length > 700)
        {
            spoken = spoken[..700].TrimEnd();
        }

        if (LooksLikeJson(spoken))
        {
            error = "Dialogue provider leaked JSON into spokenText.";
            return null;
        }

        if (IsDegenerateEcho(spoken, request.PlayerText))
        {
            error = "Dialogue provider echoed the player instead of answering.";
            return null;
        }

        return response with
        {
            SpokenText = spoken,
            Delivery = string.IsNullOrWhiteSpace(response.Delivery) ? null : response.Delivery.Trim(),
            Intent = string.IsNullOrWhiteSpace(response.Intent) ? null : response.Intent.Trim(),
        };
    }

    private void RecordDialogueAudit(
        DialogueRequest request,
        DialogueProviderResult providerResult,
        ActionResult result,
        IReadOnlyList<string> validationIssues,
        DialogueRouteRecord? route = null)
    {
        _dialogueAudit.Record(new DialogueAuditEntry(
            DateTimeOffset.UtcNow,
            providerResult.Provider,
            request.Speaker.EntityId,
            request.Speaker.Name,
            request.PlayerText,
            request,
            providerResult.RawText,
            providerResult.Response,
            providerResult.TechnicalFailure || result.TechnicalFailure,
            providerResult.Error,
            result.Action,
            result.Success,
            result.ConsumedTurn,
            validationIssues,
            result.Deltas.Select(delta => delta.Operation).ToArray(),
            route,
            providerResult.Stats));
    }

    private ActionResult DialogueTechnicalFailure(
        PreparedDialogueTurn turn,
        string provider,
        string rawText,
        string error)
    {
        var message = $"Dialogue provider failed: {error}";
        return new ActionResult
        {
            Action = "talk",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = turn.TurnBefore,
            TurnAfter = Engine.State.Turn,
            TechnicalFailure = true,
            FailureCode = Sorcerer.Core.Results.FailureCode.ProviderFailure,
            Messages = new[] { message },
            Deltas = new[]
            {
                new StateDelta(
                    "dialogueProviderFailed",
                    turn.SpeakerId,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["provider"] = provider,
                        ["error"] = error,
                        ["rawText"] = rawText,
                    }),
            },
        };
    }

    private static bool LooksLikeJson(string text) =>
        text.StartsWith("{", StringComparison.Ordinal) || text.StartsWith("[", StringComparison.Ordinal);

    private static bool IsDegenerateEcho(string reply, string playerText)
    {
        var normalizedReply = NormalizeDialogueComparison(reply);
        var normalizedPlayer = NormalizeDialogueComparison(playerText);
        return normalizedReply.Length > 0
            && normalizedPlayer.Length > 0
            && (normalizedReply.Equals(normalizedPlayer, StringComparison.OrdinalIgnoreCase)
                || (normalizedReply.Length >= 24
                    && normalizedPlayer.Contains(normalizedReply, StringComparison.OrdinalIgnoreCase)));
    }

    private static string NormalizeDialogueComparison(string text) =>
        new(text
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private void QueueDialogueClaimExtraction(TalkCommand command, ActionResult result)
    {
        if (_dialogueParser is NullDialogueClaimExtractor)
        {
            return;
        }

        var dialogue = result.Deltas.LastOrDefault(delta =>
            delta.Operation.Equals("dialogue", StringComparison.OrdinalIgnoreCase));
        if (dialogue is null)
        {
            return;
        }

        var speaker = Engine.EntityById(dialogue.Target);
        var request = new DialogueClaimRequest(
            Engine.State.Turn,
            Engine.State.RegionId,
            Engine.State.CurrentZoneId,
            ReadString(dialogue.Details, "speakerId") ?? dialogue.Target,
            ReadString(dialogue.Details, "speakerName") ?? speaker?.Name ?? dialogue.Target,
            ReadStringList(dialogue.Details, "speakerTags")
                ?? (speaker is null ? Array.Empty<string>() : TagsFor(speaker)),
            ReadString(dialogue.Details, "listenerSoulId") ?? SoulIdFor(Engine.State.ControlledEntity),
            ReadString(dialogue.Details, "playerText") ?? command.Text,
            ReadStringList(dialogue.Details, "lines") ?? result.Messages.ToArray(),
            RecentMemoriesFor(dialogue.Target),
            Engine.State.Claims.Records.TakeLast(DialogueRecentClaimLimit).ToArray(),
            Engine.State.ControlledEntityId.Value,
            SpeakerWant: speaker?.TryGet<WantComponent>(out var want) == true
                ? $"Want id {want.Id}: {want.Text} [{want.Status}]"
                : null,
            ListenerInventory: Engine.State.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
                ? inventory.Items
                    .Where(pair => pair.Value > 0)
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => $"{pair.Key} x{pair.Value}"
                        + (inventory.TreasuredItems.Contains(pair.Key) ? " [protected]" : ""))
                    .ToArray()
                : Array.Empty<string>());

        var task = RunDialogueParserFlowAsync(request, CancellationToken.None);
        _pendingClaimExtractions.Add(new PendingClaimExtraction(
            request,
            task,
            _dialogueParser.RequiresSpokenTextSupport));
    }

    private async Task<DialogueParseMaterialization> RunDialogueParserFlowAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var routeRequest = DialogueParserCapabilityCatalog.BuildRouteRequest(request);
        var routeStopwatch = Stopwatch.StartNew();
        DialogueParserRouteResult routeResult;
        try
        {
            routeResult = await _dialogueParserRouter.RouteAsync(routeRequest, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            routeResult = new DialogueParserRouteResult(
                _dialogueParserRouter.Name,
                "",
                TechnicalFailure: true,
                Error: ex.Message,
                HasMechanics: false,
                SelectedCapabilityIds: Array.Empty<string>());
        }

        routeStopwatch.Stop();
        var selection = DialogueParserCapabilityCatalog.Select(routeResult, routeRequest);
        var parserRequest = request with
        {
            SelectedParserCapabilityIds = selection.SelectedCapabilityIds,
            ParserCapabilityCards = selection.SelectedCards,
        };
        var routeRecord = CreateDialogueParserRouteRecord(
            routeRequest,
            routeResult,
            selection,
            routeStopwatch.ElapsedMilliseconds,
            parserRequestBytes: DialoguePayloadSizer.JsonUtf8Bytes(parserRequest),
            parserElapsedMs: null);
        if (!selection.HasMechanics || selection.SelectedCapabilityIds.Count == 0)
        {
            return new DialogueParseMaterialization(
                parserRequest,
                new DialogueParseResult(
                    _dialogueParser.Name,
                    routeResult.RawText,
                    TechnicalFailure: routeResult.TechnicalFailure,
                    Error: routeResult.TechnicalFailure ? routeResult.Error : null,
                    Proposals: routeResult.TechnicalFailure ? null : new DialogueProposalSet()),
                routeRecord);
        }

        var parserStopwatch = Stopwatch.StartNew();
        var parse = await _dialogueParser.ParseAsync(parserRequest, cancellationToken);
        if (selection.SelectedCapabilityIds.Contains("bargains", StringComparer.OrdinalIgnoreCase)
            && parse.Proposals?.Bargain is null
            && RepairGroundedBargain(parserRequest) is { } repairedBargain)
        {
            parse = parse with
            {
                Provider = $"{parse.Provider}:grounded-bargain-repair",
                TechnicalFailure = false,
                Error = string.IsNullOrWhiteSpace(parse.Error)
                    ? "The live parser omitted a routed bargain; exact grounded terms were repaired from its speech."
                    : $"{parse.Error} Exact grounded terms were repaired from its speech.",
                Proposals = (parse.Proposals ?? new DialogueProposalSet()) with { Bargain = repairedBargain },
            };
        }
        parserStopwatch.Stop();
        routeRecord = routeRecord with
        {
            Metrics = routeRecord.Metrics is null
                ? null
                : routeRecord.Metrics with { ParserElapsedMs = parserStopwatch.ElapsedMilliseconds },
        };
        return new DialogueParseMaterialization(parserRequest, parse, routeRecord);
    }

    /// <summary>
    /// Narrow schema repair for a provider response whose live router already classified the NPC's
    /// speech as a bargain. It can recover only immediate gold/item terms named verbatim in the
    /// authoritative listener inventory; services, standings, concessions, and deadlines still
    /// require a valid typed provider packet. Protected items are never eligible.
    /// </summary>
    internal static BargainOffer? RepairGroundedBargain(DialogueClaimRequest request)
    {
        if (request.SelectedParserCapabilityIds?.Contains("bargains", StringComparer.OrdinalIgnoreCase) != true)
        {
            return null;
        }

        var speech = string.Join(" ", request.DialogueLines);
        var speechWords = DialogueWords(speech);
        if (!speechWords.Any(word => word is "give" or "hand" or "pay" or "cost" or "toss" or "drop")
            || !speechWords.Any(word => word is "exchange" or "deal" or "stand" or "turn" or "leave" or "let" or "allow" or "unpin" or "unlock"))
        {
            return null;
        }

        var terms = new List<BargainTerm>();
        foreach (var entry in request.ListenerInventory ?? Array.Empty<string>())
        {
            if (entry.Contains("[protected]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var marker = entry.LastIndexOf(" x", StringComparison.OrdinalIgnoreCase);
            var resource = (marker > 0 ? entry[..marker] : entry).Trim();
            var availableText = marker > 0
                ? entry[(marker + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                : null;
            var available = int.TryParse(availableText, out var parsedAvailable) ? Math.Max(1, parsedAvailable) : 1;
            var resourceWords = DialogueWords(resource);
            var mention = FindWordSequence(speechWords, resourceWords);
            if (mention < 0)
            {
                continue;
            }

            var quantity = MentionedQuantity(speechWords, mention, resourceWords.Length, available);
            var kind = resource.Equals("gold", StringComparison.OrdinalIgnoreCase)
                ? BargainTermKinds.Currency
                : BargainTermKinds.Item;
            var id = NormalizeToken(resource, kind);
            terms.Add(new BargainTerm(
                id,
                kind,
                kind == BargainTermKinds.Currency
                    ? $"Pay {quantity} gold."
                    : $"Give {quantity} {resource}.",
                quantity,
                resource));
        }

        if (terms.Count == 0)
        {
            return null;
        }

        var label = string.Join(" and ", terms.Select(term => term.Text.TrimEnd('.').ToLowerInvariant()));
        return new BargainOffer(
            request.SpeakerId,
            $"{request.SpeakerName} states grounded terms: {label}.",
            new[] { new BargainOption("spoken_terms", label, terms) },
            request.Turn,
            ExpiresTurn: request.Turn + 12);
    }

    private static string[] DialogueWords(string text) =>
        text.ToLowerInvariant()
            .Split(text.Select(character => char.IsLetterOrDigit(character) ? '\0' : character)
                .Where(character => character != '\0')
                .Distinct()
                .ToArray(), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static int FindWordSequence(IReadOnlyList<string> words, IReadOnlyList<string> sequence)
    {
        if (sequence.Count == 0 || sequence.Count > words.Count)
        {
            return -1;
        }

        for (var start = 0; start <= words.Count - sequence.Count; start++)
        {
            if (sequence.Select((word, offset) => words[start + offset].Equals(word, StringComparison.OrdinalIgnoreCase)).All(match => match))
            {
                return start;
            }
        }

        return -1;
    }

    private static int MentionedQuantity(IReadOnlyList<string> words, int mention, int wordCount, int available)
    {
        var numberWords = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["one"] = 1,
            ["two"] = 2,
            ["three"] = 3,
            ["four"] = 4,
            ["five"] = 5,
            ["six"] = 6,
            ["seven"] = 7,
            ["eight"] = 8,
            ["nine"] = 9,
            ["ten"] = 10,
            ["eleven"] = 11,
            ["twelve"] = 12,
            ["thirteen"] = 13,
            ["fourteen"] = 14,
            ["fifteen"] = 15,
            ["sixteen"] = 16,
            ["seventeen"] = 17,
            ["eighteen"] = 18,
            ["nineteen"] = 19,
            ["twenty"] = 20,
        };
        var nearby = Enumerable.Range(Math.Max(0, mention - 5), Math.Min(words.Count, mention + wordCount + 3) - Math.Max(0, mention - 5))
            .Select(index => words[index]);
        foreach (var word in nearby)
        {
            var numeric = word.StartsWith('x') ? word[1..] : word;
            if (int.TryParse(numeric, out var parsed))
            {
                return Math.Clamp(parsed, 1, available);
            }

            if (numberWords.TryGetValue(word, out var named))
            {
                return Math.Clamp(named, 1, available);
            }
        }

        return 1;
    }

    private static DialogueParserRouteRecord CreateDialogueParserRouteRecord(
        DialogueParserRouteRequest request,
        DialogueParserRouteResult result,
        DialogueParserCapabilitySelection selection,
        long routerElapsedMs,
        int? parserRequestBytes,
        long? parserElapsedMs) =>
        new(
            request,
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            selection.HasMechanics,
            selection.SelectedCapabilityIds,
            selection.FallbackCapabilityIds,
            result.Reason,
            selection.UsedFallback,
            new DialogueParserRouteMetrics(
                request.AvailableCapabilities.Count,
                selection.SelectedCapabilityIds.Count,
                selection.FallbackCapabilityIds.Count,
                DialoguePayloadSizer.JsonUtf8Bytes(request),
                DialoguePayloadSizer.JsonUtf8Bytes(selection.SelectedCards),
                parserRequestBytes,
                Math.Max(0, routerElapsedMs),
                parserElapsedMs,
                selection.UnknownSelectedCapabilityIds));
}
