namespace Sorcerer.Core.Magic;

public enum BoneSongLane
{
    Deep,
    Heart,
    Bright,
}

public enum BoneSongNoteKind
{
    Required,
    Boast,
}

public sealed record BoneSongNote(double Beat, BoneSongLane Lane, BoneSongNoteKind Kind);

public sealed record BoneSongPhrase(
    int Index,
    double Beats,
    double BeatsPerMinute,
    IReadOnlyList<BoneSongNote> Notes)
{
    public double BeatSeconds => 60.0 / BeatsPerMinute;

    public double DurationSeconds => Beats * BeatSeconds;
}

/// <summary>
/// Deterministic, renderer-independent phrase book for Bone-Song. Each cell is a two-bar tribal
/// groove with fixed drum roles — the deep center duum anchors the downbeats, the rim tick rides
/// the offbeats, and the edge doo answers in call-and-response — so a phrase loops like a real
/// drum circle part instead of random note soup. The spell text chooses a starting groove and
/// later phrases walk the groove book, add tempo, and gain one syncopated answer. The same spell
/// therefore remains learnable across casts without every provider wait repeating one bar forever.
/// </summary>
public static class BoneSongPattern
{
    public const double PhraseBeats = 8.0;
    public const double BaseBpm = 92.0;
    public const double BpmPerPhrase = 4.0;
    public const double MaximumBpm = 124.0;

    // Lane 0 = deep duum (center), lane 1 = edge doo (open tone), lane 2 = rim tick.
    private static readonly (double Beat, int Lane)[][] RequiredCells =
    {
        // Heartbeat: duum on every strong pulse, doo and tick pickups leaning into it.
        new[] { (0.0, 0), (1.5, 1), (2.0, 0), (3.5, 2), (4.0, 0), (5.5, 1), (6.0, 0), (7.5, 2) },
        // Tresillo: the 3+3+2 bass figure in bar one, answered by tones in bar two.
        new[] { (0.0, 0), (1.5, 0), (2.5, 2), (3.0, 0), (4.0, 1), (5.5, 1), (6.5, 2), (7.0, 0) },
        // Son clave carried on the rim, the bass grounding the two-side.
        new[] { (0.0, 2), (1.5, 2), (2.0, 1), (3.0, 2), (4.0, 0), (5.0, 2), (6.0, 2), (7.0, 0) },
        // Kuku march: duum, then the double-doo answer, twice, with a tick turnaround.
        new[] { (0.0, 0), (1.0, 1), (1.5, 1), (3.0, 2), (4.0, 0), (5.0, 1), (5.5, 1), (7.0, 2) },
        // Shuffle: bass anchors, ticks on the backbeats, tones swung late between them.
        new[] { (0.0, 0), (1.0, 2), (2.5, 1), (3.5, 2), (4.0, 0), (5.0, 2), (6.5, 1), (7.5, 2) },
        // Rolling call and answer: a four-note run states itself, rests, and answers.
        new[] { (0.0, 0), (0.5, 1), (1.0, 2), (2.0, 1), (4.0, 0), (4.5, 1), (5.0, 2), (6.0, 1) },
        // Offbeat ride: ticks live on the ands over a steady duum floor.
        new[] { (0.0, 0), (1.5, 2), (2.5, 2), (3.5, 2), (4.0, 0), (5.5, 1), (6.5, 1), (7.0, 0) },
        // Rumba clave on the rim, bass and doo answering the back half.
        new[] { (0.0, 2), (1.5, 2), (2.0, 0), (3.5, 2), (4.0, 0), (5.0, 2), (6.0, 2), (7.0, 1) },
        // Gallop: duum-duum doo-tick, the dunun horse figure twice over.
        new[] { (0.0, 0), (1.0, 0), (2.0, 1), (2.5, 2), (4.0, 0), (5.0, 0), (6.0, 1), (6.5, 2) },
        // Displaced bass: the second duum lands late and the tones lean into the gap.
        new[] { (0.0, 0), (1.5, 1), (3.0, 0), (3.5, 2), (4.5, 1), (5.5, 0), (6.5, 2), (7.0, 1) },
        // Pickup chant: silence after the duum, then doo-doo-tick driving back home.
        new[] { (0.0, 0), (2.0, 1), (2.5, 1), (3.0, 2), (4.0, 0), (6.0, 1), (6.5, 1), (7.5, 2) },
        // Double-tick weave: tick pairs orbit the bass like sticks walking the rim.
        new[] { (0.0, 0), (1.0, 2), (1.5, 2), (2.0, 0), (3.5, 1), (4.0, 0), (5.5, 2), (7.0, 1) },
    };

    // Boast slots sit in each groove's rests — the show-off answer to the silence.
    private static readonly double[][] BoastCells =
    {
        new[] { 2.5, 4.5, 6.5 },
        new[] { 1.0, 4.5, 6.0 },
        new[] { 2.5, 4.5, 7.5 },
        new[] { 2.5, 6.0, 7.5 },
        new[] { 2.0, 4.5, 6.0 },
        new[] { 3.0, 6.5, 7.0 },
        new[] { 1.0, 5.0, 7.5 },
        new[] { 2.5, 4.5, 6.5 },
        new[] { 3.0, 4.5, 7.5 },
        new[] { 2.0, 5.0, 7.5 },
        new[] { 1.0, 5.0, 7.0 },
        new[] { 3.0, 5.0, 6.5 },
    };

    /// <param name="bonusBoasts">
    /// Extra gold flourishes earned by a hot streak in an earlier phrase. Rewards clean play
    /// with more optional upside rather than direct score, keeping the EV-neutral contract.
    /// </param>
    public static BoneSongPhrase Create(string spellText, int phraseIndex, int bonusBoasts = 0)
    {
        var safeIndex = Math.Max(0, phraseIndex);
        var seed = StableSeed(spellText ?? string.Empty);
        var cellIndex = (seed + safeIndex) % RequiredCells.Length;

        // Grooves keep their voicing — rotating lanes would put the bass line on the rim tick
        // and dissolve the drum-circle feel the cells are written around.
        var required = RequiredCells[cellIndex]
            .Select(note => new BoneSongNote(
                note.Beat,
                (BoneSongLane)note.Lane,
                BoneSongNoteKind.Required))
            .ToList();

        // Long waits grow denser by one answer note, but never faster than eighth-note spacing.
        // The extra answer is a syncopated offbeat, so it belongs to the doo or the tick.
        if (safeIndex >= 2)
        {
            var candidateBeat = safeIndex % 2 == 0 ? 3.5 : 5.5;
            if (required.All(note => Math.Abs(note.Beat - candidateBeat) >= 0.49))
            {
                required.Add(new BoneSongNote(
                    candidateBeat,
                    (seed + safeIndex) % 2 == 0 ? BoneSongLane.Heart : BoneSongLane.Bright,
                    BoneSongNoteKind.Required));
            }
        }

        var occupied = required.Select(note => note.Beat).ToHashSet();
        var boastCount = Math.Min(3, (safeIndex >= 3 ? 2 : 1) + Math.Clamp(bonusBoasts, 0, 1));
        var boasts = BoastCells[cellIndex]
            .Where(beat => !occupied.Contains(beat))
            .Take(boastCount)
            .Select((beat, index) => new BoneSongNote(
                beat,
                (BoneSongLane)((seed + safeIndex + index + 1) % 3),
                BoneSongNoteKind.Boast));
        var notes = required
            .Concat(boasts)
            .OrderBy(note => note.Beat)
            .ThenBy(note => note.Kind)
            .ToArray();

        return new BoneSongPhrase(
            safeIndex,
            PhraseBeats,
            Math.Min(BaseBpm + (safeIndex * BpmPerPhrase), MaximumBpm),
            notes);
    }

    private static int StableSeed(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var character in text.Trim().ToLowerInvariant())
            {
                hash ^= character;
                hash *= 16777619u;
            }

            return (int)(hash & 0x7fffffff);
        }
    }
}
