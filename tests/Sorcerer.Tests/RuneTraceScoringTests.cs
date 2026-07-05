using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class RuneTraceScoringTests
{
    private const float Epsilon = 0.006f;

    private static double MidAccuracy =>
        (RuneTraceScoring.AccuracyFloor + RuneTraceScoring.AccuracyCeiling) / 2.0;

    [Fact]
    public void NeverTouchingTheRuneIsAPassiveSkipAndScoresNeutral()
    {
        var performance = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(
            Traced: false,
            ActiveSeconds: 30,
            Accuracy01: 0,
            SpeedRatio: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void AttemptsShorterThanTheMinimumWindowAreDiscardedAsNeutral()
    {
        var performance = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(
            Traced: true,
            ActiveSeconds: RuneTraceScoring.MinimumScoringWindowSeconds - 0.5,
            Accuracy01: 0.95,
            SpeedRatio: 2.0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void ParSpeedAndMidAccuracyLandOnTheNeutralCenter()
    {
        // The EV-neutral calibration contract: an average performance equals skipping.
        var performance = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(
            Traced: true,
            ActiveSeconds: 10,
            Accuracy01: MidAccuracy,
            SpeedRatio: RuneTraceScoring.SpeedCeilingRatio / 2.0));

        Assert.True(performance.Played);
        Assert.Equal(RuneTraceScoring.Source, performance.Source);
        Assert.Equal(1.0f, performance.PowerModifier, Epsilon);
        Assert.Equal(1.0f, performance.ControlModifier, Epsilon);
        Assert.Equal(1.0f, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void FastAccurateTracingBuysPowerAndControlAndCalmsWildness()
    {
        var performance = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(
            Traced: true,
            ActiveSeconds: 8,
            Accuracy01: 0.95,
            SpeedRatio: 2.2));

        Assert.True(performance.PowerModifier > 1f);
        Assert.True(performance.ControlModifier > 1f);
        Assert.True(performance.WildnessModifier < 1f);
        Assert.Equal(1f + (float)RuneTraceScoring.PowerSwing, performance.PowerModifier, Epsilon);
        Assert.Equal(1f + (float)RuneTraceScoring.ControlSwing, performance.ControlModifier, Epsilon);
        Assert.Equal(1f - (float)RuneTraceScoring.WildnessSwing, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void SlowSloppyTracingGoesWild()
    {
        var performance = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(
            Traced: true,
            ActiveSeconds: 8,
            Accuracy01: 0.2,
            SpeedRatio: 0.2));

        Assert.True(performance.PowerModifier < 1f);
        Assert.True(performance.ControlModifier < 1f);
        Assert.True(performance.WildnessModifier > 1f);
    }

    [Theory]
    [InlineData(0.3, 0.5)]
    [InlineData(0.5, 0.7)]
    [InlineData(0.7, 0.9)]
    public void BetterAccuracyNeverLowersControl(double worse, double better)
    {
        var low = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(true, 10, worse, 1.0));
        var high = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(true, 10, better, 1.0));

        Assert.True(high.ControlModifier >= low.ControlModifier);
        Assert.True(high.WildnessModifier <= low.WildnessModifier);
    }

    [Fact]
    public void ModifiersStayInsideTheirBandsAtExtremes()
    {
        var wild = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(true, 60, 0.0, 0.0));
        var strong = RuneTraceScoring.ToPerformance(new RuneTraceMetrics(true, 60, 1.0, 99.0));

        Assert.InRange(wild.PowerModifier, 1f - (float)RuneTraceScoring.PowerSwing - Epsilon, 1f);
        Assert.InRange(wild.WildnessModifier, 1f, 1f + (float)RuneTraceScoring.WildnessSwing + Epsilon);
        Assert.InRange(strong.PowerModifier, 1f, 1f + (float)RuneTraceScoring.PowerSwing + Epsilon);
        Assert.InRange(strong.WildnessModifier, 1f - (float)RuneTraceScoring.WildnessSwing - Epsilon, 1f);
    }
}
