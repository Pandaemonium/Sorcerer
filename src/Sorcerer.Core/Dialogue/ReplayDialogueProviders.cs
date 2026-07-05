using Sorcerer.Core.Results;

namespace Sorcerer.Core.Dialogue;

public sealed class ReplayDialogueProvider : IDialogueProvider
{
    private readonly Queue<DialogueResolutionRecord> _records;

    public ReplayDialogueProvider(IEnumerable<DialogueResolutionRecord> records)
    {
        _records = new Queue<DialogueResolutionRecord>(records);
    }

    public string Name => "replay-dialogue";

    public Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken)
    {
        if (_records.Count == 0)
        {
            return Task.FromResult(new DialogueProviderResult(
                Name,
                "",
                TechnicalFailure: true,
                Error: "Replay transcript has no materialized dialogue response for this talk command.",
                Response: null));
        }

        var record = _records.Dequeue();
        return Task.FromResult(new DialogueProviderResult(
            string.IsNullOrWhiteSpace(record.Provider) ? Name : record.Provider,
            record.RawText,
            record.TechnicalFailure,
            record.Error,
            record.Response));
    }
}

public sealed class ReplayDialogueRouter : IDialogueRouter
{
    private readonly Queue<DialogueRouteRecord> _records;

    public ReplayDialogueRouter(IEnumerable<DialogueRouteRecord> records)
    {
        _records = new Queue<DialogueRouteRecord>(records);
    }

    public string Name => "replay-dialogue-router";

    public Task<DialogueRouteResult> RouteAsync(
        DialogueRouteRequest request,
        CancellationToken cancellationToken)
    {
        if (_records.Count == 0)
        {
            return Task.FromResult(new DialogueRouteResult(
                Name,
                "",
                TechnicalFailure: true,
                Error: "Replay transcript has no materialized dialogue route for this talk command.",
                SelectedCardIds: Array.Empty<string>()));
        }

        var record = _records.Dequeue();
        return Task.FromResult(new DialogueRouteResult(
            string.IsNullOrWhiteSpace(record.Provider) ? Name : record.Provider,
            record.RawText,
            record.TechnicalFailure,
            record.Error,
            record.SelectedCardIds,
            record.Reason));
    }
}

public sealed class ReplayDialogueClaimExtractor : IDialogueClaimExtractor
{
    private readonly List<DialogueClaimExtractionRecord> _records;
    private readonly bool _requiresSpokenTextSupport;

    public ReplayDialogueClaimExtractor(IEnumerable<DialogueClaimExtractionRecord> records)
    {
        _records = records.ToList();
        _requiresSpokenTextSupport = _records.Count == 0
            || _records.Any(record => record.RequiresSpokenTextSupport);
    }

    public string Name => "replay-claim-extractor";

    public bool RequiresSpokenTextSupport => _requiresSpokenTextSupport;

    public Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var index = _records.FindIndex(record => SameRequest(record.Request, request));
        if (index < 0 && _records.Count > 0)
        {
            index = 0;
        }

        if (index < 0)
        {
            return Task.FromResult(new DialogueClaimExtractionResult(
                Name,
                "",
                TechnicalFailure: false,
                Error: null,
                Claims: Array.Empty<DialogueClaimProposal>()));
        }

        var record = _records[index];
        _records.RemoveAt(index);
        return Task.FromResult(new DialogueClaimExtractionResult(
            string.IsNullOrWhiteSpace(record.Provider) ? Name : record.Provider,
            record.RawText,
            record.TechnicalFailure,
            record.Error,
            record.Claims));
    }

    private static bool SameRequest(DialogueClaimRequest left, DialogueClaimRequest right) =>
        left.SpeakerId.Equals(right.SpeakerId, StringComparison.OrdinalIgnoreCase)
        && left.ListenerSoulId.Equals(right.ListenerSoulId, StringComparison.OrdinalIgnoreCase)
        && left.PlayerText.Equals(right.PlayerText, StringComparison.Ordinal)
        && left.DialogueLines.SequenceEqual(right.DialogueLines, StringComparer.Ordinal);
}

public sealed class ReplayDialogueParserRouter : IDialogueParserRouter
{
    private readonly Queue<DialogueParserRouteRecord> _records;

    public ReplayDialogueParserRouter(IEnumerable<DialogueParserRouteRecord> records)
    {
        _records = new Queue<DialogueParserRouteRecord>(records);
    }

    public string Name => "replay-dialogue-parser-router";

    public Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken)
    {
        if (_records.Count == 0)
        {
            return Task.FromResult(new DialogueParserRouteResult(
                Name,
                "",
                TechnicalFailure: true,
                Error: "Replay transcript has no materialized dialogue parser route for this talk command.",
                HasMechanics: false,
                SelectedCapabilityIds: Array.Empty<string>()));
        }

        var record = _records.Dequeue();
        return Task.FromResult(new DialogueParserRouteResult(
            string.IsNullOrWhiteSpace(record.Provider) ? Name : record.Provider,
            record.RawText,
            record.TechnicalFailure,
            record.Error,
            record.HasMechanics,
            record.SelectedCapabilityIds,
            record.Reason));
    }
}

public sealed class ReplayDialogueParser : IDialogueParser
{
    private readonly List<DialogueParseRecord> _records;
    private readonly bool _requiresSpokenTextSupport;

    public ReplayDialogueParser(IEnumerable<DialogueParseRecord> records)
    {
        _records = records.ToList();
        _requiresSpokenTextSupport = _records.Count == 0
            || _records.Any(record => record.RequiresSpokenTextSupport);
    }

    public string Name => "replay-dialogue-parser";

    public bool RequiresSpokenTextSupport => _requiresSpokenTextSupport;

    public Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var index = _records.FindIndex(record => SameRequest(record.Request, request));
        if (index < 0 && _records.Count > 0)
        {
            index = 0;
        }

        if (index < 0)
        {
            return Task.FromResult(new DialogueParseResult(
                Name,
                "",
                TechnicalFailure: false,
                Error: null,
                Proposals: null));
        }

        var record = _records[index];
        _records.RemoveAt(index);
        return Task.FromResult(new DialogueParseResult(
            string.IsNullOrWhiteSpace(record.Provider) ? Name : record.Provider,
            record.RawText,
            record.TechnicalFailure,
            record.Error,
            record.Proposals));
    }

    private static bool SameRequest(DialogueClaimRequest left, DialogueClaimRequest right) =>
        left.SpeakerId.Equals(right.SpeakerId, StringComparison.OrdinalIgnoreCase)
        && left.ListenerSoulId.Equals(right.ListenerSoulId, StringComparison.OrdinalIgnoreCase)
        && left.PlayerText.Equals(right.PlayerText, StringComparison.Ordinal)
        && left.DialogueLines.SequenceEqual(right.DialogueLines, StringComparer.Ordinal);
}
