using Sorcerer.Core.Results;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic.Auditing;

public sealed record SpellAuditEntry(
    DateTimeOffset Timestamp,
    string Provider,
    string SpellText,
    object Context,
    string RawText,
    SpellResolution? ParsedResolution,
    ActionResult Result,
    IReadOnlyList<string> ValidationErrors);

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
