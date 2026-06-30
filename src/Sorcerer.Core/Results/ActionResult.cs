namespace Sorcerer.Core.Results;

public sealed record StateDelta(
    string Operation,
    string Target,
    string Summary,
    IReadOnlyDictionary<string, object?> Details);

public sealed record MagicResolutionRecord(
    string Provider,
    bool Accepted,
    bool TechnicalFailure,
    IReadOnlyList<string> EffectTypes,
    string? Error);

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

