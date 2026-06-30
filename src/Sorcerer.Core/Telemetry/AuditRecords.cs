namespace Sorcerer.Core.Telemetry;

public sealed record AuditRecord(
    int SchemaVersion,
    DateTimeOffset Timestamp,
    string Purpose,
    string Provider,
    string Model,
    object Request,
    string RawResponse,
    object? ParsedResponse,
    IReadOnlyList<string> ValidationErrors,
    bool TechnicalFailure,
    long ElapsedMilliseconds);
