using Sorcerer.Core.Commands;

namespace Sorcerer.Core.Magic;

/// <summary>
/// Renderer-agnostic summary of one True-Sigil minigame session. The GUI reduces its rounds
/// (flash, choose-among-liars, reveal) to these ratio-based metrics; nothing renderer-specific
/// (pixels, decoy layouts, per-round timings) crosses this boundary.
/// </summary>
/// <param name="Answered">
/// True once the player picked a candidate at all. Never choosing is a passive skip and
/// scores neutral, per the UX rule that skipping is not punished.
/// </param>
/// <param name="ActiveSeconds">
/// Total time sigils were flashed or choosable. Used only for the minimum-window rule;
/// scoring itself is per-round ratios so latency length stays fair.
/// </param>
/// <param name="Rounds">Rounds actually answered (an abandoned round in hand never counts).</param>
/// <param name="Accuracy01">Fraction of answered rounds where the true sigil was chosen.</param>
/// <param name="SpeedRatio">
/// Par answer time divided by the mean answer time. 1.0 means answering at par; higher is
/// faster. Computed from totals, so a 5-second and an 80-second wait score on the same scale.
/// </param>
public sealed record TrueSigilMetrics(
    bool Answered,
    double ActiveSeconds,
    int Rounds,
    double Accuracy01,
    double SpeedRatio);

/// <summary>
/// Maps True-Sigil metrics to a <see cref="CastPerformance"/>. This is calibration policy and
/// lives in the core so tests can pin it and the CLI can reuse it.
///
/// The game is recognition memory: the wild shows the spell's true shape once, then asks for
/// it back among near-lies. Calibration contract (see CASTING_AND_MINIGAMES.md):
/// - Playing is a fair gamble centered on neutral: answering at par speed with the mid-band
///   accuracy (three of four rounds true) maps to exactly 1.0/1.0/1.0, the same as skipping.
///   Sure and swift recall buys power and control; guessing buys wildness.
/// - Scoring is ratio-based; a longer provider wait is never an advantage or a penalty.
/// - If the provider returns before <see cref="MinimumScoringWindowSeconds"/> of playable
///   time elapsed, the attempt is discarded as neutral rather than scored on a sliver.
/// </summary>
public static class TrueSigilScoring
{
    public const string Source = "true_sigil";

    /// <summary>Attempts shorter than this are discarded as neutral, never punished.</summary>
    public const double MinimumScoringWindowSeconds = 2.75;

    /// <summary>Accuracy at or below this scores as fully wild; near chance for 3-4 candidates.</summary>
    public const double AccuracyFloor = 0.50;

    /// <summary>Accuracy at or above this scores as fully controlled.</summary>
    public const double AccuracyCeiling = 1.00;

    /// <summary>Speed ratio that maps to the top of the power band (par sits at the center).</summary>
    public const double SpeedCeilingRatio = 2.0;

    /// <summary>Half-width of the power modifier band around 1.0.</summary>
    public const double PowerSwing = 0.35;

    /// <summary>Half-width of the control modifier band around 1.0.</summary>
    public const double ControlSwing = 0.30;

    /// <summary>Half-width of the wildness modifier band around 1.0.</summary>
    public const double WildnessSwing = 0.45;

    public static CastPerformance ToPerformance(TrueSigilMetrics metrics)
    {
        if (!metrics.Answered
            || metrics.ActiveSeconds < MinimumScoringWindowSeconds
            || metrics.Rounds == 0)
        {
            return CastPerformance.Neutral;
        }

        // Both scores are normalized so 0.5 is the neutral center of their band.
        var speedScore = Math.Clamp(metrics.SpeedRatio / SpeedCeilingRatio, 0.0, 1.0);
        var accuracyScore = Math.Clamp(
            (metrics.Accuracy01 - AccuracyFloor) / (AccuracyCeiling - AccuracyFloor),
            0.0,
            1.0);

        var power = 1.0 + PowerSwing * ((2.0 * speedScore) - 1.0);
        var control = 1.0 + ControlSwing * ((2.0 * accuracyScore) - 1.0);
        var quality = (speedScore + accuracyScore) / 2.0;
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
