namespace Sorcerer.Core.Telemetry;

/// <summary>
/// One consolidated per-purpose latency baseline (Phase 1.4). Every provider call already carries a
/// <see cref="ProviderCallStats"/> and a context size through the spell/dialogue/background audit
/// stream; this collects those samples tagged by purpose (wild / dialogue / router / parser /
/// background) and computes p50/p95 summaries so there is one measured baseline instead of several
/// ad-hoc ones. It is audit/diagnostic only -- recording a sample never touches run state, and the
/// summary is a read model for CLI/debug.
/// </summary>
public sealed class ProviderTelemetryLog
{
    private readonly object _gate = new();
    private readonly List<Sample> _samples = new();

    private readonly record struct Sample(string Purpose, int? ContextBytes, ProviderCallStats? Stats);

    /// <summary>Record one provider call's context size and reported timing under its purpose.</summary>
    public void Record(string purpose, int? contextBytes, ProviderCallStats? stats)
    {
        var key = string.IsNullOrWhiteSpace(purpose) ? "unknown" : purpose.Trim().ToLowerInvariant();
        lock (_gate)
        {
            _samples.Add(new Sample(key, contextBytes, stats));
        }
    }

    /// <summary>Total samples recorded across all purposes.</summary>
    public int Count
    {
        get { lock (_gate) { return _samples.Count; } }
    }

    /// <summary>Per-purpose p50/p95 summary of timing, prompt tokens, and context size.</summary>
    public IReadOnlyList<ProviderPurposeSummary> Summarize()
    {
        lock (_gate)
        {
            return _samples
                .GroupBy(sample => sample.Purpose, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group =>
                {
                    var samples = group.ToArray();
                    return new ProviderPurposeSummary(
                        group.Key,
                        samples.Length,
                        PercentileDouble(samples, sample => sample.Stats?.TotalMs, 0.50),
                        PercentileDouble(samples, sample => sample.Stats?.TotalMs, 0.95),
                        PercentileDouble(samples, sample => sample.Stats?.GenerationMs, 0.50),
                        PercentileDouble(samples, sample => sample.Stats?.GenerationMs, 0.95),
                        PercentileInt(samples, sample => sample.Stats?.PromptTokens, 0.50),
                        PercentileInt(samples, sample => sample.Stats?.PromptTokens, 0.95),
                        PercentileInt(samples, sample => sample.ContextBytes, 0.50),
                        PercentileInt(samples, sample => sample.ContextBytes, 0.95));
                })
                .ToArray();
        }
    }

    private static double? PercentileDouble(IEnumerable<Sample> samples, Func<Sample, double?> select, double quantile) =>
        Percentile(samples.Select(select).Where(value => value is not null).Select(value => value!.Value), quantile);

    private static int? PercentileInt(IEnumerable<Sample> samples, Func<Sample, int?> select, double quantile)
    {
        var value = Percentile(samples.Select(select).Where(v => v is not null).Select(v => (double)v!.Value), quantile);
        return value is null ? null : (int)Math.Round(value.Value);
    }

    // Nearest-rank percentile over the present (non-null) values, so a single call still yields a
    // number and mixed-completeness samples (mock providers report nothing) do not skew the result.
    private static double? Percentile(IEnumerable<double> values, double quantile)
    {
        var sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(quantile * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }
}

/// <summary>Read-only p50/p95 latency baseline for one provider purpose. Null fields mean no sample
/// reported that metric (e.g. mock providers report no timing).</summary>
public sealed record ProviderPurposeSummary(
    string Purpose,
    int Count,
    double? TotalMsP50,
    double? TotalMsP95,
    double? GenerationMsP50,
    double? GenerationMsP95,
    int? PromptTokensP50,
    int? PromptTokensP95,
    int? ContextBytesP50,
    int? ContextBytesP95);
