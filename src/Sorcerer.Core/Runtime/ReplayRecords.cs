namespace Sorcerer.Core.Runtime;

public sealed record ReplayCommandRecord(
    int Step,
    int TurnBefore,
    string CommandText,
    string? ResolvedMagicJson);

public sealed record ReplayRecord(
    int Version,
    int Seed,
    string Scenario,
    IReadOnlyList<ReplayCommandRecord> Commands,
    object? FinalSummary);
