using Sorcerer.Core.Commands;

namespace Sorcerer.Core.Magic;

/// <summary>
/// Renderer-agnostic summary of one Bone-Song response session. The GUI reduces the notes
/// swooping onto three marks of one drum to timing and phrase totals; positions and audio
/// never cross the engine boundary.
/// </summary>
/// <param name="Played">
/// True once the player attempted a drum response. Watching or explicitly skipping is neutral.
/// </param>
/// <param name="ActiveSeconds">
/// Time for which response notes were moving. Used only by the minimum-window rule.
/// </param>
/// <param name="RequiredNotesResolved">Ivory notes hit or allowed to pass.</param>
/// <param name="RequiredNotesHit">Ivory notes struck on the correct drum inside the window.</param>
/// <param name="TimingQualityTotal">
/// Sum of 0..1 timing quality for required hits. Misses contribute through accuracy instead.
/// </param>
/// <param name="BoastsOffered">Optional gold flourishes hit or allowed to pass.</param>
/// <param name="BoastsHit">Optional gold flourishes struck cleanly.</param>
/// <param name="MistimedStrikes">
/// Empty-air attempts and wrong optional flourishes not already represented by a required miss.
/// </param>
/// <param name="CompletedPhrases">Full response phrases completed, retained for audit/feel data.</param>
/// <param name="LongestStreak">Longest run of correct required notes, retained for audit/feel data.</param>
public sealed record BoneSongMetrics(
    bool Played,
    double ActiveSeconds,
    int RequiredNotesResolved,
    int RequiredNotesHit,
    double TimingQualityTotal,
    int BoastsOffered,
    int BoastsHit,
    int MistimedStrikes,
    int CompletedPhrases,
    int LongestStreak);

/// <summary>
/// Maps the three-mark Bone-Song response to <see cref="CastPerformance"/>. Required-note
/// accuracy establishes the groove and therefore power. Timing precision plus accuracy feeds
/// control. Optional gold flourishes can raise power but ignoring them never lowers the baseline;
/// attempting them on the wrong drum still risks a mistimed strike. Poor groove and timing raise
/// wildness. All ratios are note-based, so provider latency never improves the score by itself.
/// </summary>
public static class BoneSongScoring
{
    public const string Source = "bone_song";

    /// <summary>Attempts shorter than this are discarded as neutral, never punished.</summary>
    public const double MinimumScoringWindowSeconds = 2.75;

    /// <summary>Required-note accuracy at or below this is the bottom of the score band.</summary>
    public const double AccuracyFloor = 0.45;

    /// <summary>Required-note accuracy at or above this is the top of the score band.</summary>
    public const double AccuracyCeiling = 0.95;

    /// <summary>Average hit timing at or below this is the bottom of the control band.</summary>
    public const double TimingFloor = 0.35;

    /// <summary>Average hit timing at or above this is the top of the control band.</summary>
    public const double TimingCeiling = 0.90;

    /// <summary>Optional flourishes can supply at most this share of remaining power headroom.</summary>
    public const double BoastHeadroomShare = 0.25;

    public const double PowerSwing = 0.35;

    public const double ControlSwing = 0.30;

    public const double WildnessSwing = 0.45;

    public static CastPerformance ToPerformance(BoneSongMetrics metrics)
    {
        if (!metrics.Played
            || metrics.ActiveSeconds < MinimumScoringWindowSeconds
            || metrics.RequiredNotesResolved <= 0)
        {
            return CastPerformance.Neutral;
        }

        var accuracyDenominator = metrics.RequiredNotesResolved + Math.Max(0, metrics.MistimedStrikes);
        var accuracy = accuracyDenominator > 0
            ? (double)Math.Max(0, metrics.RequiredNotesHit) / accuracyDenominator
            : 0.0;
        var accuracyScore = Normalize(accuracy, AccuracyFloor, AccuracyCeiling);

        var averageTiming = metrics.RequiredNotesHit > 0
            ? metrics.TimingQualityTotal / metrics.RequiredNotesHit
            : 0.0;
        var timingScore = Normalize(averageTiming, TimingFloor, TimingCeiling);

        var boastTake = metrics.BoastsOffered > 0
            ? Math.Clamp((double)metrics.BoastsHit / metrics.BoastsOffered, 0.0, 1.0)
            : 0.0;
        var powerScore = Math.Clamp(
            accuracyScore + (BoastHeadroomShare * boastTake * (1.0 - accuracyScore)),
            0.0,
            1.0);
        var controlScore = Math.Clamp((accuracyScore * 0.45) + (timingScore * 0.55), 0.0, 1.0);
        var quality = (powerScore + controlScore) / 2.0;

        return new CastPerformance(
            Played: true,
            PowerModifier: Round(1.0 + PowerSwing * ((2.0 * powerScore) - 1.0)),
            ControlModifier: Round(1.0 + ControlSwing * ((2.0 * controlScore) - 1.0)),
            WildnessModifier: Round(1.0 + WildnessSwing * (1.0 - (2.0 * quality))),
            Source);
    }

    public static double TimingQuality(double absoluteErrorSeconds)
    {
        var error = Math.Abs(absoluteErrorSeconds);
        if (error <= 0.075)
        {
            return 1.0;
        }

        if (error <= 0.15)
        {
            return Lerp(1.0, 0.65, (error - 0.075) / 0.075);
        }

        if (error <= 0.22)
        {
            return Lerp(0.65, 0.25, (error - 0.15) / 0.07);
        }

        return 0.0;
    }

    private static double Normalize(double value, double floor, double ceiling) =>
        Math.Clamp((value - floor) / (ceiling - floor), 0.0, 1.0);

    private static double Lerp(double from, double to, double amount) =>
        from + ((to - from) * Math.Clamp(amount, 0.0, 1.0));

    private static float Round(double value) => (float)Math.Round(value, 2);
}
