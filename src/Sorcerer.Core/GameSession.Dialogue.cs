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
            Engine.State.ControlledEntityId.Value);

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
        parserStopwatch.Stop();
        routeRecord = routeRecord with
        {
            Metrics = routeRecord.Metrics is null
                ? null
                : routeRecord.Metrics with { ParserElapsedMs = parserStopwatch.ElapsedMilliseconds },
        };
        return new DialogueParseMaterialization(parserRequest, parse, routeRecord);
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
