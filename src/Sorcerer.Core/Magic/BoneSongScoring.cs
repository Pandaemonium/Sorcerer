using Sorcerer.Core.Commands;

namespace Sorcerer.Core.Magic;

/// <summary>
/// Renderer-agnostic summary of one Bone-Song minigame session. The GUI reduces its laps
/// (strikes, passes, accents taken or declined, rests violated) to these counts; nothing
/// renderer-specific (angles, tempos, windows in milliseconds) crosses this boundary.
/// </summary>
/// <param name="Struck">
/// True once the player struck the drum at all. Never striking is a passive skip and scores
/// neutral, per the UX rule that skipping is not punished.
/// </param>
/// <param name="ActiveSeconds">
/// Total time the beat lap was running. Used only for the minimum-window rule; scoring
/// itself is per-notch fractions so latency length stays fair.
/// </param>
/// <param name="CleanHits">Notches struck inside their window, plain or accent.</param>
/// <param name="Misses">
/// Plain notches passed unstruck, swings that landed outside a window, and rests struck.
/// Declined accents are deliberately absent: letting a knot pass is safe, it only costs power.
/// </param>
/// <param name="AccentsOffered">Accent knots the lap presented (taken, missed, or declined).</param>
/// <param name="AccentHits">Accent knots struck clean inside their tighter window.</param>
public sealed record BoneSongMetrics(
    bool Struck,
    double ActiveSeconds,
    int CleanHits,
    int Misses,
    int AccentsOffered,
    int AccentHits);

/// <summary>
/// Maps Bone-Song metrics to a <see cref="CastPerformance"/>. This is calibration policy and
/// lives in the core so tests can pin it and the CLI can reuse it.
///
/// The game is Bralli scrimshaw drumming: keep the spell's pulse on carved whalebone.
/// Accuracy on the carvings feeds control; the boldness to take the tight accent knots
/// feeds power; sloppy strikes and struck rests feed wildness. Calibration contract (see
/// CASTING_AND_MINIGAMES.md):
/// - Playing is a fair gamble centered on neutral: mid-band accuracy while taking half the
///   accents offered maps to exactly 1.0/1.0/1.0, the same as skipping. The timid drummer
///   trades power for control; the boastful one risks the opposite.
/// - Scoring is per-notch fractions; a longer provider wait is never an advantage or a
///   penalty.
/// - If the provider returns before <see cref="MinimumScoringWindowSeconds"/> of lap time
///   elapsed, the attempt is discarded as neutral rather than scored on a sliver.
/// </summary>
public static class BoneSongScoring
{
    public const string Source = "bone_song";

    /// <summary>Attempts shorter than this are discarded as neutral, never punished.</summary>
    public const double MinimumScoringWindowSeconds = 2.75;

    /// <summary>Accuracy at or below this scores as fully wild; tune against real drumming.</summary>
    public const double AccuracyFloor = 0.45;

    /// <summary>Accuracy at or above this scores as fully controlled.</summary>
    public const double AccuracyCeiling = 0.95;

    /// <summary>The par accent take: cleanly striking half the knots offered sits at center.</summary>
    public const double ParAccentTake = 0.5;

    /// <summary>Half-width of the power modifier band around 1.0.</summary>
    public const double PowerSwing = 0.35;

    /// <summary>Half-width of the control modifier band around 1.0.</summary>
    public const double ControlSwing = 0.30;

    /// <summary>Half-width of the wildness modifier band around 1.0.</summary>
    public const double WildnessSwing = 0.45;

    public static CastPerformance ToPerformance(BoneSongMetrics metrics)
    {
        if (!metrics.Struck
            || metrics.ActiveSeconds < MinimumScoringWindowSeconds
            || metrics.CleanHits + metrics.Misses == 0)
        {
            return CastPerformance.Neutral;
        }

        // Both scores are normalized so 0.5 is the neutral center of their band.
        var accuracy = (double)metrics.CleanHits / (metrics.CleanHits + metrics.Misses);
        var accuracyScore = Math.Clamp(
            (accuracy - AccuracyFloor) / (AccuracyCeiling - AccuracyFloor),
            0.0,
            1.0);

        // A lap that never offered a knot cannot judge boldness either way.
        var accentRatio = metrics.AccentsOffered > 0
            ? (double)metrics.AccentHits / metrics.AccentsOffered
            : ParAccentTake;
        var accentScore = Math.Clamp(accentRatio / (2.0 * ParAccentTake), 0.0, 1.0);

        var power = 1.0 + PowerSwing * ((2.0 * accentScore) - 1.0);
        var control = 1.0 + ControlSwing * ((2.0 * accuracyScore) - 1.0);
        var quality = (accentScore + accuracyScore) / 2.0;
        var wildness = 1.0 + WildnessSwing * (1.0 - (2.0 * quality));

        return new CastPerformance(
            Played: true,
            PowerModifier: Round(power),
            ControlModifier: Round(control),
            WildnessModifier: Round(wildness),
            Source);
    }

    private static float Round(double value) => (float)Math.Round(value, 2);
}
