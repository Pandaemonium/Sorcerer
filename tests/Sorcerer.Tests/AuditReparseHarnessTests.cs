using Sorcerer.Cli;
using Sorcerer.Magic.Operations;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Covers the from-audit reparse core: recorded raw model outputs are re-run through the current
/// parser so a parser change can be validated against real captured responses without a provider.
/// </summary>
public sealed class AuditReparseHarnessTests
{
    [Fact]
    public void RecoversPreviouslyUnparsedRawOutputAndLeavesStableOnesUnchanged()
    {
        var lines = new[]
        {
            // Recorded as unparsed (parsedResolution null), but rawText is a bare effect object the
            // current parser now wraps and parses -> counts as a recovery.
            """{"spellText":"strike the soldier","rawText":"{\"type\":\"damage\",\"target\":\"nearest_enemy\",\"amount\":5}","parsedResolution":null}""",
            // Recorded and parses to the same shape now -> unchanged.
            """{"spellText":"mend me","rawText":"{\"accepted\":true,\"effects\":[{\"type\":\"heal\",\"target\":\"player\",\"amount\":3}]}","parsedResolution":{"accepted":true,"effects":[{"type":"heal"}]}}""",
        };

        var summary = AuditReparseHarness.RunFromLines(lines, OperationRegistry.CreateDefault());

        Assert.Equal(2, summary.Total);
        Assert.Equal(2, summary.Parsed);
        Assert.Equal(0, summary.Failed);
        Assert.Equal(1, summary.Recovered);
        Assert.Equal(0, summary.Changed);
    }

    [Fact]
    public void SkipsBlankMalformedAndNonAuditLines()
    {
        var lines = new[] { string.Empty, "not json at all", """{"note":"no rawText field"}""" };

        var summary = AuditReparseHarness.RunFromLines(lines, OperationRegistry.CreateDefault());

        Assert.Equal(0, summary.Total);
    }
}
