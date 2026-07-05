using Sorcerer.Core.Commands;

namespace Sorcerer.Core.Magic;

/// <summary>
/// Renderer-agnostic summary of one rune-trace minigame session. The GUI reduces raw gesture
/// data to these rate-based metrics; nothing renderer-specific (points, timings per rune,
/// screen sizes) crosses this boundary.
/// </summary>
/// <param name="Traced">
/// True once the player engaged the trace at all. Never touching the rune is a passive skip
/// and scores neutral, per the UX rule that skipping is not punished.
/// </param>
/// <param name="ActiveSeconds">
/// Total time runes were traceable (burn-in and celebration excluded). Used only for the
/// minimum-window rule; scoring itself is rate-based so latency length stays fair.
/// </param>
/// <param name="Accuracy01">
/// Length-weighted mean trace accuracy in [0,1]: 1 - clamp(perpendicularDistance / tolerance)
/// sampled per unit of traced arclength.
/// </param>
/// <param name="SpeedRatio">
/// Achieved trace speed divided by par speed. 1.0 means tracing at par; higher is faster.
/// Computed from totals (par seconds of all traced length / active seconds), so a 5-second
/// and a 25-second wait score on the same scale.
/// </param>
public sealed record RuneTraceMetrics(
    bool Traced,
    double ActiveSeconds,
    double Accuracy01,
    double SpeedRatio);

/// <summary>
/// Maps rune-trace metrics to a <see cref="CastPerformance"/>. This is calibration policy and
/// lives in the core so tests can pin it and the CLI can reuse it.
///
/// Calibration contract (see CASTING_AND_MINIGAMES.md):
/// - Playing is a fair gamble centered on neutral: par speed plus mid-band accuracy maps to
///   1.0/1.0/1.0, the same as skipping. Good play buys power and control; poor play buys
///   wildness. On average, playing and skipping produce the same cast.
/// - Scoring is rate-based; a longer provider wait is never an advantage or a penalty.
/// - If the provider returns before <see cref="MinimumScoringWindowSeconds"/> of traceable
///   time elapsed, the attempt is discarded as neutral rather than scored on a sliver.
/// </summary>
public static class RuneTraceScoring
{
    public const string Source = "rune_trace";

    /// <summary>Attempts shorter than this are discarded as neutral, never punished.</summary>
    public const double MinimumScoringWindowSeconds = 2.75;

    /// <summary>Accuracy at or below this scores as fully wild; tune against real traces.</summary>
    public const double AccuracyFloor = 0.35;

    /// <summary>Accuracy at or above this scores as fully controlled.</summary>
    public const double AccuracyCeiling = 0.90;

    /// <summary>Speed ratio that maps to the top of the power band (par sits at the center).</summary>
    public const double SpeedCeilingRatio = 2.0;

    /// <summary>Half-width of the power modifier band around 1.0.</summary>
    public const double PowerSwing = 0.35;

    /// <summary>Half-width of the control modifier band around 1.0.</summary>
    public const double ControlSwing = 0.30;

    /// <summary>Half-width of the wildness modifier band around 1.0.</summary>
    public const double WildnessSwing = 0.45;

    public static CastPerformance ToPerformance(RuneTraceMetrics metrics)
    {
        if (!metrics.Traced || metrics.ActiveSeconds < MinimumScoringWindowSeconds)
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
