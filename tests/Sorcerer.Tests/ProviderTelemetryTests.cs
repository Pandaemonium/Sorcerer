using System;
using System.Linq;
using Sorcerer.Core.Results;
using Sorcerer.Core.Telemetry;
using Sorcerer.Magic.Auditing;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 1.4 — one consolidated latency baseline. <see cref="ProviderTelemetryLog"/> aggregates the
/// per-call context size and provider timing that already flow through the audit stream into one
/// per-purpose p50/p95 summary, and <see cref="TelemetryAuditSink"/> feeds it off the existing
/// spell audit without a second telemetry model. Audit-only.
/// </summary>
public sealed class ProviderTelemetryTests
{
    [Fact]
    public void SummarizesEachPurposeWithNearestRankPercentiles()
    {
        var log = new ProviderTelemetryLog();
        foreach (var totalMs in new[] { 100.0, 200.0, 300.0, 400.0 })
        {
            log.Record("wild", contextBytes: 4200, new ProviderCallStats(PromptTokens: 4200, TotalMs: totalMs));
        }

        log.Record("dialogue", contextBytes: 1500, new ProviderCallStats(PromptTokens: 1500, TotalMs: 50.0));

        var summaries = log.Summarize();
        Assert.Equal(new[] { "dialogue", "wild" }, summaries.Select(summary => summary.Purpose).ToArray());

        var wild = summaries.Single(summary => summary.Purpose == "wild");
        Assert.Equal(4, wild.Count);
        Assert.Equal(200.0, wild.TotalMsP50); // nearest-rank over [100,200,300,400]
        Assert.Equal(400.0, wild.TotalMsP95);
        Assert.Equal(4200, wild.ContextBytesP50);

        var dialogue = summaries.Single(summary => summary.Purpose == "dialogue");
        Assert.Equal(1, dialogue.Count);
        Assert.Equal(50.0, dialogue.TotalMsP95);
    }

    [Fact]
    public void MissingMetricsAreOmittedNotZeroed()
    {
        var log = new ProviderTelemetryLog();
        // A mock provider reports no timing at all.
        log.Record("wild", contextBytes: null, new ProviderCallStats());

        var summary = Assert.Single(log.Summarize());
        Assert.Equal(1, summary.Count);
        Assert.Null(summary.TotalMsP50);
        Assert.Null(summary.ContextBytesP50);
    }

    [Fact]
    public void AuditSinkFeedsTheLogFromTheSpellStreamAndChainsToInner()
    {
        var log = new ProviderTelemetryLog();
        var inner = new CountingSpellAuditSink();
        var sink = new TelemetryAuditSink(log, inner);

        var entry = new SpellAuditEntry(
            DateTimeOffset.UtcNow,
            "ollama",
            "let the reeds remember the river",
            new object(),
            "{}",
            ParsedResolution: null,
            ActionResult.Simple("cast", success: true, consumedTurn: true, 0, 1),
            Array.Empty<string>(),
            Performance: null,
            new SpellRoutingRecord(Array.Empty<string>(), AdvertisedOperationCount: 0, ContextPayloadBytes: 4300,
                new ProviderCallStats(PromptTokens: 4300, TotalMs: 22000)));

        sink.Record(entry);

        var summary = Assert.Single(log.Summarize());
        Assert.Equal("wild", summary.Purpose);
        Assert.Equal(22000.0, summary.TotalMsP50);
        Assert.Equal(4300, summary.ContextBytesP50);
        Assert.Equal(1, inner.Count); // chained through to the wrapped sink
    }

    private sealed class CountingSpellAuditSink : ISpellAuditSink
    {
        public int Count { get; private set; }

        public void Record(SpellAuditEntry entry) => Count++;
    }
}
