using Sorcerer.Core.Commands;
using Sorcerer.Core.Results;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic.Auditing;

/// <summary>
/// Capability-routing metrics for one cast: which capability cards were selected, how many
/// operations the resolver prompt advertised after routing narrowed the registry, and the byte size
/// of the rich engine-side context retained in the audit. The latter is not the compact provider-wire
/// projection; use ProviderStats.PromptTokens for live wire-size comparisons.
/// </summary>
public sealed record SpellRoutingRecord(
    IReadOnlyList<string> SelectedCapabilities,
    int AdvertisedOperationCount,
    int ContextPayloadBytes,
    Sorcerer.Core.Telemetry.ProviderCallStats? ProviderStats = null);

public sealed record SpellAuditEntry(
    DateTimeOffset Timestamp,
    string Provider,
    string SpellText,
    object Context,
    string RawText,
    SpellResolution? ParsedResolution,
    ActionResult Result,
    IReadOnlyList<string> ValidationErrors,
    CastPerformance? Performance = null,
    SpellRoutingRecord? Routing = null);

public interface ISpellAuditSink
{
    void Record(SpellAuditEntry entry);
}

public sealed class NullSpellAuditSink : ISpellAuditSink
{
    public static NullSpellAuditSink Instance { get; } = new();

    private NullSpellAuditSink()
    {
    }

    public void Record(SpellAuditEntry entry)
    {
    }
}
