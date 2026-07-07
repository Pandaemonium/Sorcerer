using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class TrueSigilScoringTests
{
    private const float Epsilon = 0.006f;

    private static double MidAccuracy =>
        (TrueSigilScoring.AccuracyFloor + TrueSigilScoring.AccuracyCeiling) / 2.0;

    [Fact]
    public void NeverChoosingIsAPassiveSkipAndScoresNeutral()
    {
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: false,
            ActiveSeconds: 30,
            Rounds: 0,
            Accuracy01: 0,
            SpeedRatio: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void AttemptsShorterThanTheMinimumWindowAreDiscardedAsNeutral()
    {
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: true,
            ActiveSeconds: TrueSigilScoring.MinimumScoringWindowSeconds - 0.5,
            Rounds: 1,
            Accuracy01: 1.0,
            SpeedRatio: 2.0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void ZeroResolvedRoundsScoresNeutral()
    {
        // A round abandoned in hand when the provider returned: too little signal to score.
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: true,
            ActiveSeconds: 10,
            Rounds: 0,
            Accuracy01: 0,
            SpeedRatio: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void ParSpeedAndMidAccuracyLandOnTheNeutralCenter()
    {
        // The EV-neutral calibration contract: three of four rounds true, answered at par,
        // equals skipping.
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: true,
            ActiveSeconds: 12,
            Rounds: 4,
            Accuracy01: MidAccuracy,
            SpeedRatio: TrueSigilScoring.SpeedCeilingRatio / 2.0));

        Assert.True(performance.Played);
        Assert.Equal(TrueSigilScoring.Source, performance.Source);
        Assert.Equal(1.0f, performance.PowerModifier, Epsilon);
        Assert.Equal(1.0f, performance.ControlModifier, Epsilon);
        Assert.Equal(1.0f, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void SwiftPerfectRecallBuysPowerAndControlAndCalmsWildness()
    {
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: true,
            ActiveSeconds: 10,
            Rounds: 5,
            Accuracy01: 1.0,
            SpeedRatio: 2.4));

        Assert.True(performance.PowerModifier > 1f);
        Assert.True(performance.ControlModifier > 1f);
        Assert.True(performance.WildnessModifier < 1f);
        Assert.Equal(1f + (float)TrueSigilScoring.PowerSwing, performance.PowerModifier, Epsilon);
        Assert.Equal(1f + (float)TrueSigilScoring.ControlSwing, performance.ControlModifier, Epsilon);
        Assert.Equal(1f - (float)TrueSigilScoring.WildnessSwing, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void RushedGuessingGoesWild()
    {
        // Fast but wrong: chance-level accuracy under the floor buys wildness even at speed.
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: true,
            ActiveSeconds: 10,
            Rounds: 6,
            Accuracy01: 0.33,
            SpeedRatio: 2.0));

        Assert.True(performance.ControlModifier < 1f);
        Assert.True(performance.WildnessModifier >= 1f);
    }

    [Fact]
    public void SlowSureRecallTradesPowerForControl()
    {
        var performance = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
            Answered: true,
            ActiveSeconds: 20,
            Rounds: 3,
            Accuracy01: 1.0,
            SpeedRatio: 0.4));

        Assert.True(performance.PowerModifier < 1f);
        Assert.True(performance.ControlModifier > 1f);
    }

    [Theory]
    [InlineData(0.5, 0.75)]
    [InlineData(0.75, 0.9)]
    [InlineData(0.9, 1.0)]
    public void BetterAccuracyNeverLowersControl(double worse, double better)
    {
        var low = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(true, 10, 4, worse, 1.0));
        var high = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(true, 10, 4, better, 1.0));

        Assert.True(high.ControlModifier >= low.ControlModifier);
        Assert.True(high.WildnessModifier <= low.WildnessModifier);
    }

    [Fact]
    public void ModifiersStayInsideTheirBandsAtExtremes()
    {
        var wild = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(true, 60, 10, 0.0, 0.0));
        var strong = TrueSigilScoring.ToPerformance(new TrueSigilMetrics(true, 60, 10, 1.0, 99.0));

        Assert.InRange(wild.PowerModifier, 1f - (float)TrueSigilScoring.PowerSwing - Epsilon, 1f);
        Assert.InRange(wild.WildnessModifier, 1f, 1f + (float)TrueSigilScoring.WildnessSwing + Epsilon);
        Assert.InRange(strong.PowerModifier, 1f, 1f + (float)TrueSigilScoring.PowerSwing + Epsilon);
        Assert.InRange(strong.WildnessModifier, 1f - (float)TrueSigilScoring.WildnessSwing - Epsilon, 1f);
    }
}
