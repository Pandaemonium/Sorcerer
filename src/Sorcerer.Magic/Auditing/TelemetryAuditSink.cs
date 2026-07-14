using Sorcerer.Core.Telemetry;

namespace Sorcerer.Magic.Auditing;

/// <summary>
/// Feeds the one consolidated latency baseline (<see cref="ProviderTelemetryLog"/>) off the existing
/// spell-audit stream (Phase 1.4): each resolved wild cast already reports its context size and
/// provider timing through the routing record, so this records them under the "wild" purpose without
/// a second telemetry model or any new provider plumbing. Audit-only; it never touches run state.
/// Optionally chains to an inner sink so it can wrap an existing audit destination.
/// </summary>
public sealed class TelemetryAuditSink : ISpellAuditSink
{
    private readonly ProviderTelemetryLog _telemetry;
    private readonly ISpellAuditSink? _inner;
    private readonly string _purpose;

    public TelemetryAuditSink(ProviderTelemetryLog telemetry, ISpellAuditSink? inner = null, string purpose = "wild")
    {
        _telemetry = telemetry;
        _inner = inner;
        _purpose = purpose;
    }

    public void Record(SpellAuditEntry entry)
    {
        _telemetry.Record(_purpose, entry.Routing?.ContextPayloadBytes, entry.Routing?.ProviderStats);
        _inner?.Record(entry);
    }
}
