using Sorcerer.Core.Commands;

namespace Sorcerer.Core.Magic;

/// <summary>
/// Renderer-agnostic summary of one Thread &amp; Knot minigame session. The GUI reduces its
/// hold/release gestures, banked pulls, and snapped threads to these rate-based metrics;
/// nothing renderer-specific (pixels, node counts, timings per thread) crosses this boundary.
/// </summary>
/// <param name="Pulled">
/// True once the player drew the thread at all. Never pulling is a passive skip and scores
/// neutral, per the UX rule that skipping is not punished.
/// </param>
/// <param name="ActiveSeconds">
/// Total time the thread was drawable. Used only for the minimum-window rule; scoring itself
/// is rate-based so latency length stays fair.
/// </param>
/// <param name="BankRateRatio">
/// Banked draw per second divided by the par banking rate. 1.0 means banking at par; higher
/// means greedier, longer pulls that actually got tied off. Computed from totals so a
/// 5-second and an 80-second wait score on the same scale.
/// </param>
/// <param name="Knots">Threads tied off cleanly, banking their drawn length.</param>
/// <param name="Snaps">Threads pulled past fraying until they snapped, losing the draw.</param>
public sealed record ThreadKnotMetrics(
    bool Pulled,
    double ActiveSeconds,
    double BankRateRatio,
    int Knots,
    int Snaps);

/// <summary>
/// Maps Thread &amp; Knot metrics to a <see cref="CastPerformance"/>. This is calibration
/// policy and lives in the core so tests can pin it and the CLI can reuse it.
///
/// The game is a push-your-luck gamble: holding longer banks power faster but frays the
/// thread toward a snap. Calibration contract (see CASTING_AND_MINIGAMES.md):
/// - Playing is a fair gamble centered on neutral: banking at par with a par share of snaps
///   (about 7 knots to every 3 snaps) maps to exactly 1.0/1.0/1.0, the same as skipping.
///   Greedy-but-clean weaving buys power and control; timid weaving trades power for
///   control; reckless snapping buys wildness.
/// - Scoring is rate-based; a longer provider wait is never an advantage or a penalty.
/// - If the provider returns before <see cref="MinimumScoringWindowSeconds"/> of drawable
///   time elapsed, the attempt is discarded as neutral rather than scored on a sliver.
/// </summary>
public static class ThreadKnotScoring
{
    public const string Source = "thread_knot";

    /// <summary>Attempts shorter than this are discarded as neutral, never punished.</summary>
    public const double MinimumScoringWindowSeconds = 2.75;

    /// <summary>Bank-rate ratio that maps to the top of the power band (par sits at the center).</summary>
    public const double BankCeilingRatio = 2.0;

    /// <summary>Clean-tie fraction at or below this scores as fully wild; tune against real play.</summary>
    public const double CleanFloor = 0.40;

    /// <summary>Clean-tie fraction at or above this scores as fully controlled.</summary>
    public const double CleanCeiling = 1.00;

    /// <summary>Half-width of the power modifier band around 1.0.</summary>
    public const double PowerSwing = 0.35;

    /// <summary>Half-width of the control modifier band around 1.0.</summary>
    public const double ControlSwing = 0.30;

    /// <summary>Half-width of the wildness modifier band around 1.0.</summary>
    public const double WildnessSwing = 0.45;

    public static CastPerformance ToPerformance(ThreadKnotMetrics metrics)
    {
        // Pulling without ever resolving a thread (tie or snap) is too little signal to score.
        if (!metrics.Pulled
            || metrics.ActiveSeconds < MinimumScoringWindowSeconds
            || metrics.Knots + metrics.Snaps == 0)
        {
            return CastPerformance.Neutral;
        }

        // Both scores are normalized so 0.5 is the neutral center of their band.
        var bankScore = Math.Clamp(metrics.BankRateRatio / BankCeilingRatio, 0.0, 1.0);
        var cleanliness = (double)metrics.Knots / (metrics.Knots + metrics.Snaps);
        var cleanScore = Math.Clamp((cleanliness - CleanFloor) / (CleanCeiling - CleanFloor), 0.0, 1.0);

        var power = 1.0 + PowerSwing * ((2.0 * bankScore) - 1.0);
        var control = 1.0 + ControlSwing * ((2.0 * cleanScore) - 1.0);
        var quality = (bankScore + cleanScore) / 2.0;
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
