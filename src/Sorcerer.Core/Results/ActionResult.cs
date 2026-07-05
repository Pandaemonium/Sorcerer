using Sorcerer.Core.Dialogue;

namespace Sorcerer.Core.Results;

public sealed record StateDelta(
    string Operation,
    string Target,
    string Summary,
    IReadOnlyDictionary<string, object?> Details);

public static class StateDeltaExtensions
{
    public static IEnumerable<string> PlayerMessages(this IEnumerable<StateDelta> deltas) =>
        deltas.Where(IsPlayerVisible).Select(delta => delta.Summary);

    public static bool IsPlayerVisible(this StateDelta delta) =>
        !IsFalse(delta.Details, "playerVisible")
        && !IsTrue(delta.Details, "auditOnly");

    private static bool IsFalse(IReadOnlyDictionary<string, object?> details, string key) =>
        details.TryGetValue(key, out var value) && value is bool visible && !visible;

    private static bool IsTrue(IReadOnlyDictionary<string, object?> details, string key) =>
        details.TryGetValue(key, out var value) && value is bool flag && flag;

}

public sealed record MagicResolutionRecord(
    string Provider,
    bool Accepted,
    bool TechnicalFailure,
    IReadOnlyList<string> EffectTypes,
    string? Error)
{
    public string? ResolvedMagicJson { get; init; }
}

public sealed record DialogueResolutionRecord(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    DialogueResponse? Response);

public sealed record DialogueRouteMetrics(
    int AvailableCardCount,
    int SelectedCardCount,
    int FallbackCardCount,
    int DeniedCardCount,
    int RouteRequestBytes,
    int AvailableCardBytes,
    int SelectedCardBytes,
    int? GeneratorRequestBytes,
    long RouterElapsedMs,
    IReadOnlyList<string> DeniedCardIds,
    IReadOnlyList<string> UnknownSelectedCardIds,
    IReadOnlyList<string> DeniedSelectedCardIds);

public sealed record DialogueRouteRecord(
    DialogueRouteRequest Request,
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    IReadOnlyList<string> SelectedCardIds,
    IReadOnlyList<string> FallbackCardIds,
    string? Reason,
    bool UsedFallback,
    DialogueRouteMetrics? Metrics = null);

public sealed record DialogueParserRouteMetrics(
    int AvailableCapabilityCount,
    int SelectedCapabilityCount,
    int FallbackCapabilityCount,
    int RouteRequestBytes,
    int SelectedCapabilityBytes,
    int? ParserRequestBytes,
    long RouterElapsedMs,
    long? ParserElapsedMs,
    IReadOnlyList<string> UnknownSelectedCapabilityIds);

public sealed record DialogueParserRouteRecord(
    DialogueParserRouteRequest Request,
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    bool HasMechanics,
    IReadOnlyList<string> SelectedCapabilityIds,
    IReadOnlyList<string> FallbackCapabilityIds,
    string? Reason,
    bool UsedFallback,
    DialogueParserRouteMetrics? Metrics = null);

public sealed record DialogueClaimExtractionRecord(
    DialogueClaimRequest Request,
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    IReadOnlyList<DialogueClaimProposal> Claims,
    bool RequiresSpokenTextSupport);

public sealed record DialogueParseRecord(
    DialogueClaimRequest Request,
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    DialogueProposalSet? Proposals,
    bool RequiresSpokenTextSupport,
    DialogueParserRouteRecord? ParserRoute = null);

public sealed record ActionResult
{
    public required string Action { get; init; }

    public required bool Success { get; init; }

    public required bool ConsumedTurn { get; init; }

    public required int TurnBefore { get; init; }

    public required int TurnAfter { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();

    public bool TechnicalFailure { get; init; }

    public MagicResolutionRecord? Magic { get; init; }

    public DialogueResolutionRecord? Dialogue { get; init; }

    public DialogueRouteRecord? DialogueRoute { get; init; }

    public IReadOnlyList<DialogueClaimExtractionRecord> DialogueClaimExtractions { get; init; } = Array.Empty<DialogueClaimExtractionRecord>();

    public IReadOnlyList<DialogueParseRecord> DialogueParses { get; init; } = Array.Empty<DialogueParseRecord>();

    public IReadOnlyList<StateDelta> Deltas { get; init; } = Array.Empty<StateDelta>();

    public bool ShouldQuit { get; init; }

    public static ActionResult Simple(
        string action,
        bool success,
        bool consumedTurn,
        int turnBefore,
        int turnAfter,
        params string[] messages) =>
        new()
        {
            Action = action,
            Success = success,
            ConsumedTurn = consumedTurn,
            TurnBefore = turnBefore,
            TurnAfter = turnAfter,
            Messages = messages,
        };
}
