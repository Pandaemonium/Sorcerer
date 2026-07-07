using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class BoneSongScoringTests
{
    private const float Epsilon = 0.006f;

    [Fact]
    public void NeverStrikingTheDrumIsAPassiveSkipAndScoresNeutral()
    {
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: false,
            ActiveSeconds: 30,
            CleanHits: 0,
            Misses: 0,
            AccentsOffered: 0,
            AccentHits: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void AttemptsShorterThanTheMinimumWindowAreDiscardedAsNeutral()
    {
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: BoneSongScoring.MinimumScoringWindowSeconds - 0.5,
            CleanHits: 4,
            Misses: 0,
            AccentsOffered: 2,
            AccentHits: 2));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void ZeroResolvedNotchesScoresNeutral()
    {
        // Struck only empty air before the provider returned: too little signal to score.
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: 10,
            CleanHits: 0,
            Misses: 0,
            AccentsOffered: 0,
            AccentHits: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void MidBandAccuracyTakingHalfTheKnotsLandsOnTheNeutralCenter()
    {
        // The EV-neutral calibration contract: 0.70 accuracy (mid of the 0.45-0.95 band)
        // while cleanly taking half the accents offered equals skipping.
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: 20,
            CleanHits: 14,
            Misses: 6,
            AccentsOffered: 4,
            AccentHits: 2));

        Assert.True(performance.Played);
        Assert.Equal(BoneSongScoring.Source, performance.Source);
        Assert.Equal(1.0f, performance.PowerModifier, Epsilon);
        Assert.Equal(1.0f, performance.ControlModifier, Epsilon);
        Assert.Equal(1.0f, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void ABarWithNoKnotsOfferedJudgesPowerAsNeutral()
    {
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: 10,
            CleanHits: 7,
            Misses: 3,
            AccentsOffered: 0,
            AccentHits: 0));

        Assert.Equal(1.0f, performance.PowerModifier, Epsilon);
    }

    [Fact]
    public void TheBoastfulDrummerBuysPowerAndControlAndCalmsWildness()
    {
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: 25,
            CleanHits: 19,
            Misses: 1,
            AccentsOffered: 6,
            AccentHits: 6));

        Assert.True(performance.PowerModifier > 1f);
        Assert.True(performance.ControlModifier > 1f);
        Assert.True(performance.WildnessModifier < 1f);
        Assert.Equal(1f + (float)BoneSongScoring.PowerSwing, performance.PowerModifier, Epsilon);
        Assert.Equal(1f - (float)BoneSongScoring.WildnessSwing, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void TheTimidDrummerTradesPowerForControl()
    {
        // Every plain carving struck clean, every knot declined: high control, low power.
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: 20,
            CleanHits: 12,
            Misses: 0,
            AccentsOffered: 5,
            AccentHits: 0));

        Assert.True(performance.ControlModifier > 1f);
        Assert.True(performance.PowerModifier < 1f);
        Assert.Equal(1f - (float)BoneSongScoring.PowerSwing, performance.PowerModifier, Epsilon);
    }

    [Fact]
    public void SloppyDrummingGoesWild()
    {
        var performance = BoneSongScoring.ToPerformance(new BoneSongMetrics(
            Struck: true,
            ActiveSeconds: 15,
            CleanHits: 3,
            Misses: 9,
            AccentsOffered: 4,
            AccentHits: 0));

        Assert.True(performance.PowerModifier < 1f);
        Assert.True(performance.ControlModifier < 1f);
        Assert.True(performance.WildnessModifier > 1f);
    }

    [Theory]
    [InlineData(10, 2, 6)]
    [InlineData(12, 0, 4)]
    [InlineData(8, 3, 8)]
    public void FewerMissesNeverLowersControl(int cleanHits, int fewerMisses, int moreMisses)
    {
        var tight = BoneSongScoring.ToPerformance(new BoneSongMetrics(true, 20, cleanHits, fewerMisses, 2, 1));
        var loose = BoneSongScoring.ToPerformance(new BoneSongMetrics(true, 20, cleanHits, moreMisses, 2, 1));

        Assert.True(tight.ControlModifier >= loose.ControlModifier);
        Assert.True(tight.WildnessModifier <= loose.WildnessModifier);
    }

    [Fact]
    public void ModifiersStayInsideTheirBandsAtExtremes()
    {
        var wild = BoneSongScoring.ToPerformance(new BoneSongMetrics(true, 60, 0, 20, 8, 0));
        var strong = BoneSongScoring.ToPerformance(new BoneSongMetrics(true, 60, 40, 0, 10, 10));

        Assert.InRange(wild.PowerModifier, 1f - (float)BoneSongScoring.PowerSwing - Epsilon, 1f);
        Assert.InRange(wild.WildnessModifier, 1f, 1f + (float)BoneSongScoring.WildnessSwing + Epsilon);
        Assert.InRange(strong.PowerModifier, 1f, 1f + (float)BoneSongScoring.PowerSwing + Epsilon);
        Assert.InRange(strong.WildnessModifier, 1f - (float)BoneSongScoring.WildnessSwing - Epsilon, 1f);
    }
}
