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
