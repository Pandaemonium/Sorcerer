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
/// Deterministic, renderer-independent phrase book for Bone-Song. Each cell is a two-bar groove
/// lifted from a real drumming tradition — Mandé welcome, Afro-Cuban tresillo and clave, kuku,
/// bembé, dundun gallop — with fixed drum roles: the deep center duum is the heartbeat, the rim
/// tick carries the timeline (clave or bell), and the edge doo sings the call-and-response answer.
/// The whole book shares one heartbeat: a duum lands on the "one" of nearly every cell and on the
/// downbeat of the second bar, so however the walk moves from groove to groove it reads as one
/// continuous drum song developing rather than a new pattern every phrase. Bar two answers bar one
/// inside each cell, which is what makes a groove learnable and makes it sing. The spell text
/// chooses the starting groove; later phrases walk the book, add tempo, and gain one syncopated
/// answer, so the same spell stays fresh across casts without any bar repeating forever.
/// </summary>
public static class BoneSongPattern
{
    public const double PhraseBeats = 8.0;
    public const double BaseBpm = 92.0;
    public const double BpmPerPhrase = 4.0;
    public const double MaximumBpm = 124.0;

    // Lane 0 = deep duum (heartbeat, center), lane 1 = edge doo (open tone, the "song"),
    // lane 2 = rim tick (the timeline). Every groove is two bars of four on an eighth-note grid.
    private static readonly (double Beat, int Lane)[][] RequiredCells =
    {
        // Fanga welcome: the anchor heartbeat. Duum on the one with a pushing "and-of-3", a tick
        // on the offbeat, and the doo singing a pickup into each downbeat. Bar two mirrors bar one.
        new[] { (0.0, 0), (1.5, 2), (2.5, 0), (3.5, 1), (4.0, 0), (5.5, 2), (6.5, 0), (7.5, 1) },
        // Tresillo: the driving 3+3+2 bass figure (0, 1.5, 3), a tone answering on the two, the
        // second bar echoing it home with a rim turnaround and a pickup doo.
        new[] { (0.0, 0), (1.5, 0), (2.0, 1), (3.0, 0), (4.0, 0), (5.5, 0), (6.5, 2), (7.0, 1) },
        // Son clave (3-2) carried on the rim as the timeline; the bass grounds the two-side and the
        // doo answers. The clave itself is felt as the one, so no duum crowds beat zero.
        new[] { (0.0, 2), (1.5, 2), (2.0, 1), (3.0, 2), (4.0, 0), (5.0, 2), (6.0, 2), (7.0, 1) },
        // Rumba clave (3-2): the same shape with its third stroke pushed to the "and-of-4" for the
        // rumba lilt, so it lands with a different swing than the son next to it in the book.
        new[] { (0.0, 2), (1.5, 2), (2.0, 1), (3.5, 2), (4.0, 0), (5.0, 2), (6.0, 2), (7.0, 1) },
        // Kuku circle dance: duum on the one, the doo-doo answer twice over, a rim turnaround —
        // the tone pair is the part everyone hums.
        new[] { (0.0, 0), (1.0, 1), (1.5, 1), (3.0, 2), (4.0, 0), (5.0, 1), (5.5, 1), (7.0, 2) },
        // Shiko backbeat: bass on the one, ticks planted on the backbeats, the doo swung late in
        // the pocket between them. A steady, danceable floor.
        new[] { (0.0, 0), (1.0, 2), (2.5, 1), (3.0, 2), (4.0, 0), (5.0, 2), (6.5, 1), (7.0, 2) },
        // Bembé run: a rising tick run over the heartbeat states the call, and bar two answers with
        // two sung doos leaning into the next one.
        new[] { (0.0, 0), (1.5, 2), (2.5, 2), (3.5, 2), (4.0, 0), (5.5, 2), (6.5, 1), (7.5, 1) },
        // Dundun gallop: the duum-duum horse figure, a doo-tick answer, the whole thing twice over.
        new[] { (0.0, 0), (1.0, 0), (2.0, 1), (2.5, 2), (4.0, 0), (5.0, 0), (6.0, 1), (6.5, 2) },
        // Pickup chant: a lone duum, then silence, then the doo-doo-tick run drives back home —
        // the space before the answer is the drama.
        new[] { (0.0, 0), (2.0, 1), (2.5, 1), (3.0, 2), (4.0, 0), (6.0, 1), (6.5, 1), (7.5, 2) },
        // Rolling call and answer: a four-note run states itself in bar one and answers, note for
        // note, in bar two — the plainest call-and-response in the book.
        new[] { (0.0, 0), (0.5, 1), (1.0, 2), (2.0, 1), (4.0, 0), (4.5, 1), (5.0, 2), (6.0, 1) },
        // Double-tick weave: tick pairs orbit the bass like sticks walking the rim, a doo answering
        // off the back of each bar.
        new[] { (0.0, 0), (1.0, 2), (1.5, 2), (2.0, 0), (3.5, 1), (4.0, 0), (5.5, 2), (7.0, 1) },
        // Tumbao ride: a steady offbeat tick ride over the heartbeat, the open doo singing on the
        // two of each bar — the loosest, most rolling groove of the set.
        new[] { (0.0, 0), (1.5, 2), (2.0, 1), (3.5, 2), (4.0, 0), (5.5, 2), (6.0, 1), (7.5, 2) },
    };

    // Boast slots sit in each groove's rests — the show-off answer to the silence. Kept off the
    // 3.5/5.5 beats the density bump can claim, so an earned flourish never gets swallowed.
    private static readonly double[][] BoastCells =
    {
        new[] { 2.0, 4.5, 6.0 },
        new[] { 2.5, 4.5, 7.5 },
        new[] { 2.5, 4.5, 7.5 },
        new[] { 2.5, 4.5, 7.5 },
        new[] { 2.5, 6.0, 7.5 },
        new[] { 2.0, 4.5, 6.0 },
        new[] { 2.0, 5.0, 7.0 },
        new[] { 3.0, 4.5, 7.0 },
        new[] { 1.0, 4.5, 7.0 },
        new[] { 2.5, 6.5, 7.5 },
        new[] { 2.5, 5.0, 6.5 },
        new[] { 2.5, 4.5, 7.0 },
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
