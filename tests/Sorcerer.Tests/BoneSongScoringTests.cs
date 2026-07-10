using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class BoneSongScoringTests
{
    private const float Epsilon = 0.006f;

    [Fact]
    public void WatchingWithoutAnsweringIsAPassiveSkip()
    {
        var performance = BoneSongScoring.ToPerformance(Metrics(
            played: false,
            resolved: 8,
            hits: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void TooShortAResponseIsDiscardedRatherThanPunished()
    {
        var performance = BoneSongScoring.ToPerformance(Metrics(
            activeSeconds: BoneSongScoring.MinimumScoringWindowSeconds - 0.1,
            resolved: 4,
            hits: 4,
            timingTotal: 4));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void NoResolvedRequiredNotesIsNeutral()
    {
        var performance = BoneSongScoring.ToPerformance(Metrics(resolved: 0, hits: 0));

        Assert.Equal(CastPerformance.Neutral, performance);
    }

    [Fact]
    public void ParGrooveAndParTimingLandAtNeutralWithoutTakingAFlourish()
    {
        // Accuracy 0.70 is the middle of 0.45..0.95. Average timing 0.625 is the middle
        // of 0.35..0.90. Gold notes are optional, so declining them does not lower baseline.
        var performance = BoneSongScoring.ToPerformance(Metrics(
            resolved: 20,
            hits: 14,
            timingTotal: 8.75,
            boastsOffered: 4,
            boastsHit: 0));

        Assert.True(performance.Played);
        Assert.Equal(BoneSongScoring.Source, performance.Source);
        Assert.Equal(1f, performance.PowerModifier, Epsilon);
        Assert.Equal(1f, performance.ControlModifier, Epsilon);
        Assert.Equal(1f, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void OptionalFlourishesOnlyAddPowerHeadroom()
    {
        var plain = BoneSongScoring.ToPerformance(Metrics(
            resolved: 20,
            hits: 14,
            timingTotal: 8.75,
            boastsOffered: 4,
            boastsHit: 0));
        var ornate = BoneSongScoring.ToPerformance(Metrics(
            resolved: 20,
            hits: 14,
            timingTotal: 8.75,
            boastsOffered: 4,
            boastsHit: 4));

        Assert.True(ornate.PowerModifier > plain.PowerModifier);
        Assert.Equal(plain.ControlModifier, ornate.ControlModifier);
        Assert.True(ornate.WildnessModifier < plain.WildnessModifier);
    }

    [Fact]
    public void PerfectThreeMarkResponseReachesTheStrongEdge()
    {
        var performance = BoneSongScoring.ToPerformance(Metrics(
            resolved: 24,
            hits: 24,
            timingTotal: 24,
            boastsOffered: 5,
            boastsHit: 5,
            completedPhrases: 3,
            longestStreak: 24));

        Assert.Equal(1f + (float)BoneSongScoring.PowerSwing, performance.PowerModifier, Epsilon);
        Assert.Equal(1f + (float)BoneSongScoring.ControlSwing, performance.ControlModifier, Epsilon);
        Assert.Equal(1f - (float)BoneSongScoring.WildnessSwing, performance.WildnessModifier, Epsilon);
    }

    [Fact]
    public void WrongDrumStrikesBreakGrooveAndRaiseWildness()
    {
        var clean = BoneSongScoring.ToPerformance(Metrics(
            resolved: 12,
            hits: 10,
            timingTotal: 8));
        var wrongDrums = BoneSongScoring.ToPerformance(Metrics(
            resolved: 12,
            hits: 10,
            timingTotal: 8,
            mistimed: 6));

        Assert.True(wrongDrums.PowerModifier < clean.PowerModifier);
        Assert.True(wrongDrums.ControlModifier < clean.ControlModifier);
        Assert.True(wrongDrums.WildnessModifier > clean.WildnessModifier);
    }

    [Theory]
    [InlineData(0.00, 1.00)]
    [InlineData(0.075, 1.00)]
    [InlineData(0.15, 0.65)]
    [InlineData(0.22, 0.25)]
    [InlineData(0.23, 0.00)]
    public void TimingWindowsAreContinuousAndLegible(double error, double expected)
    {
        Assert.Equal(expected, BoneSongScoring.TimingQuality(error), 3);
        Assert.Equal(expected, BoneSongScoring.TimingQuality(-error), 3);
    }

    [Fact]
    public void BetterTimingNeverLowersControl()
    {
        var loose = BoneSongScoring.ToPerformance(Metrics(
            resolved: 10,
            hits: 8,
            timingTotal: 4));
        var tight = BoneSongScoring.ToPerformance(Metrics(
            resolved: 10,
            hits: 8,
            timingTotal: 7));

        Assert.True(tight.ControlModifier >= loose.ControlModifier);
        Assert.True(tight.WildnessModifier <= loose.WildnessModifier);
    }

    [Fact]
    public void DeterministicPhraseBookProducesMusicalBoundedPatterns()
    {
        var first = BoneSongPattern.Create("ask the sea to remember me", 0);
        var repeat = BoneSongPattern.Create("ask the sea to remember me", 0);
        var late = BoneSongPattern.Create("ask the sea to remember me", 20);

        Assert.Equal(first.Index, repeat.Index);
        Assert.Equal(first.Beats, repeat.Beats);
        Assert.Equal(first.BeatsPerMinute, repeat.BeatsPerMinute);
        Assert.Equal(first.Notes, repeat.Notes);
        Assert.Equal(BoneSongPattern.PhraseBeats, first.Beats);
        Assert.InRange(first.BeatsPerMinute, BoneSongPattern.BaseBpm, BoneSongPattern.MaximumBpm);
        Assert.Equal(BoneSongPattern.MaximumBpm, late.BeatsPerMinute);
        Assert.InRange(first.Notes.Count(note => note.Kind == BoneSongNoteKind.Required), 8, 9);
        Assert.InRange(first.Notes.Count(note => note.Kind == BoneSongNoteKind.Boast), 1, 2);
        Assert.All(first.Notes, note =>
        {
            Assert.InRange(note.Beat, 0, first.Beats - 0.5);
            Assert.InRange((int)note.Lane, 0, 2);
        });
        Assert.Equal(first.Notes.Count, first.Notes.Select(note => note.Beat).Distinct().Count());
    }

    [Fact]
    public void StreakBonusAddsOneOptionalFlourishWithoutTouchingRequiredNotes()
    {
        for (var phraseIndex = 0; phraseIndex < 14; phraseIndex++)
        {
            var plain = BoneSongPattern.Create("boast for the hall", phraseIndex);
            var earned = BoneSongPattern.Create("boast for the hall", phraseIndex, bonusBoasts: 1);

            Assert.Equal(
                plain.Notes.Where(note => note.Kind == BoneSongNoteKind.Required),
                earned.Notes.Where(note => note.Kind == BoneSongNoteKind.Required));
            var plainBoasts = plain.Notes.Count(note => note.Kind == BoneSongNoteKind.Boast);
            var earnedBoasts = earned.Notes.Count(note => note.Kind == BoneSongNoteKind.Boast);
            Assert.InRange(earnedBoasts - plainBoasts, 0, 1);
            Assert.Equal(
                earned.Notes.Count,
                earned.Notes.Select(note => note.Beat).Distinct().Count());
        }
    }

    [Fact]
    public void EveryPhraseKeepsEighthNoteSpacingSoHitWindowsStayLegible()
    {
        for (var phraseIndex = 0; phraseIndex < 16; phraseIndex++)
        {
            var phrase = BoneSongPattern.Create("the sea keeps its own count", phraseIndex, bonusBoasts: 1);
            var beats = phrase.Notes.Select(note => note.Beat).OrderBy(beat => beat).ToArray();
            for (var index = 1; index < beats.Length; index++)
            {
                Assert.True(
                    beats[index] - beats[index - 1] >= 0.49,
                    $"phrase {phraseIndex}: beats {beats[index - 1]} and {beats[index]} too close");
            }
        }
    }

    private static BoneSongMetrics Metrics(
        bool played = true,
        double activeSeconds = 20,
        int resolved = 10,
        int hits = 7,
        double timingTotal = 4.375,
        int boastsOffered = 0,
        int boastsHit = 0,
        int mistimed = 0,
        int completedPhrases = 1,
        int longestStreak = 5) =>
        new(
            played,
            activeSeconds,
            resolved,
            hits,
            timingTotal,
            boastsOffered,
            boastsHit,
            mistimed,
            completedPhrases,
            longestStreak);
}
