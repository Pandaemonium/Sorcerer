using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ThreadKnotScoringTests
{
    private const float Epsilon = 0.006f;

    [Fact]
    public void NeverPullingTheThreadIsAPassiveSkipAndScoresNeutral()
    {
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: false,
            ActiveSeconds: 30,
            BankRateRatio: 0,
            Knots: 0,
            Snaps: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void AttemptsShorterThanTheMinimumWindowAreDiscardedAsNeutral()
    {
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: true,
            ActiveSeconds: ThreadKnotScoring.MinimumScoringWindowSeconds - 0.5,
            BankRateRatio: 2.0,
            Knots: 3,
            Snaps: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void PullingWithoutEverResolvingAThreadScoresNeutral()
    {
        // A pull in hand when the provider returned, never tied nor snapped: too little
        // signal to score, and never a punishment.
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: true,
            ActiveSeconds: 10,
            BankRateRatio: 0.4,
            Knots: 0,
            Snaps: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void ParBankingWithAParShareOfSnapsLandsOnTheNeutralCenter()
    {
        // The EV-neutral calibration contract: banking at par while tying 7 of every 10
        // threads (the mid-band cleanliness) equals skipping.
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: true,
            ActiveSeconds: 20,
            BankRateRatio: ThreadKnotScoring.BankCeilingRatio / 2.0,
            Knots: 7,
            Snaps: 3));

        Assert.True(performance.Played);
        Assert.Equal(ThreadKnotScoring.Source, performance.Source);
        Assert.Equal(1.0f, performance.PowerModifier, Epsilon);
        Assert.Equal(1.0f, performance.ControlModifier, Epsilon);
        Assert.Equal(1.0f, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void GreedyCleanWeavingBuysPowerAndControlAndCalmsWildness()
    {
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: true,
            ActiveSeconds: 15,
            BankRateRatio: 2.4,
            Knots: 8,
            Snaps: 0));

        Assert.True(performance.PowerModifier > 1f);
        Assert.True(performance.ControlModifier > 1f);
        Assert.True(performance.WildnessModifier < 1f);
        Assert.Equal(1f + (float)ThreadKnotScoring.PowerSwing, performance.PowerModifier, Epsilon);
        Assert.Equal(1f + (float)ThreadKnotScoring.ControlSwing, performance.ControlModifier, Epsilon);
        Assert.Equal(1f - (float)ThreadKnotScoring.WildnessSwing, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void RecklessSnappingGoesWild()
    {
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: true,
            ActiveSeconds: 15,
            BankRateRatio: 0.3,
            Knots: 1,
            Snaps: 6));

        Assert.True(performance.PowerModifier < 1f);
        Assert.True(performance.ControlModifier < 1f);
        Assert.True(performance.WildnessModifier > 1f);
    }

    [Fact]
    public void TimidTinyKnotsTradePowerForControl()
    {
        // The "precise but weak" playstyle: many small clean ties, low bank rate. A
        // legitimate corner of the gamble, not a degenerate one.
        var performance = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
            Pulled: true,
            ActiveSeconds: 20,
            BankRateRatio: 0.35,
            Knots: 12,
            Snaps: 0));

        Assert.True(performance.PowerModifier < 1f);
        Assert.True(performance.ControlModifier > 1f);
    }

    [Theory]
    [InlineData(4, 0, 6)]
    [InlineData(7, 1, 3)]
    [InlineData(9, 3, 5)]
    public void FewerSnapsNeverLowersControl(int knots, int fewerSnaps, int moreSnaps)
    {
        var clean = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(true, 20, 1.0, knots, fewerSnaps));
        var sloppy = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(true, 20, 1.0, knots, moreSnaps));

        Assert.True(clean.ControlModifier >= sloppy.ControlModifier);
        Assert.True(clean.WildnessModifier <= sloppy.WildnessModifier);
    }

    [Fact]
    public void ModifiersStayInsideTheirBandsAtExtremes()
    {
        var wild = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(true, 60, 0.0, 0, 20));
        var strong = ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(true, 60, 99.0, 40, 0));

        Assert.InRange(wild.PowerModifier, 1f - (float)ThreadKnotScoring.PowerSwing - Epsilon, 1f);
        Assert.InRange(wild.WildnessModifier, 1f, 1f + (float)ThreadKnotScoring.WildnessSwing + Epsilon);
        Assert.InRange(strong.PowerModifier, 1f, 1f + (float)ThreadKnotScoring.PowerSwing + Epsilon);
        Assert.InRange(strong.WildnessModifier, 1f - (float)ThreadKnotScoring.WildnessSwing - Epsilon, 1f);
    }
}
