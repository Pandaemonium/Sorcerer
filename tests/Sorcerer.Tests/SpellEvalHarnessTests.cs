using System.Linq;
using System.Threading.Tasks;
using Sorcerer.Cli;
using Sorcerer.Llm;
using Sorcerer.Magic.Auditing;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 9 of the WildMagic import: eval reports are inspectable and include latency and the
/// capability cards routing selected, so agents can run many prompts and report reproducible
/// findings without scraping console output.
/// </summary>
public sealed class SpellEvalHarnessTests
{
    [Fact]
    public async Task EvalSummaryRecordsLatencyAndSelectedCards()
    {
        var summary = await SpellEvalHarness.RunToSummaryAsync(
            new MockSpellProvider(),
            NullSpellAuditSink.Instance);

        Assert.True(summary.Total > 0);
        Assert.All(summary.Rows, row =>
        {
            Assert.True(row.LatencyMs >= 0);
            Assert.NotNull(row.SelectedCapabilities);
        });
        Assert.Contains(summary.Rows, row => row.SelectedCapabilities.Count > 0);
    }
}
