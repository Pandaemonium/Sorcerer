using Sorcerer.Core.Commands;
using Sorcerer.Core.Results;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic.Auditing;

/// <summary>
/// Capability-routing metrics for one cast: which capability cards were selected, how many
/// operations the resolver prompt advertised after routing narrowed the registry, and the byte size
/// of the serialized magic context. These make "routing trimmed the prompt" measurable (Phase 2 of
/// the WildMagic import) so deterministic routing can be tuned before a live router is trusted.
/// </summary>
public sealed record SpellRoutingRecord(
    IReadOnlyList<string> SelectedCapabilities,
    int AdvertisedOperationCount,
    int ContextPayloadBytes);

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
