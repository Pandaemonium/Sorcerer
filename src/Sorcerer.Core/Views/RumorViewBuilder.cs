using Sorcerer.Core.World;

namespace Sorcerer.Core.Views;

public static class RumorViewBuilder
{
    public static IReadOnlyList<RumorRecord> Visible(GameState state, int limit = 12) =>
        RumorSystem.VisibleToPlayer(state, limit).ToArray();

    public static IReadOnlyList<string> BuildLines(GameState state, int limit = 12)
    {
        var rumors = Visible(state, limit)
            .Select(FormatLine)
            .ToArray();
        return rumors.Length == 0
            ? new[] { "No rumors have reached you yet." }
            : rumors;
    }

    public static IReadOnlyList<string> BuildJournalLines(GameState state, int limit = 8) =>
        Visible(state, limit)
            .Select(FormatJournalLine)
            .ToArray();

    public static string FormatLine(RumorRecord rumor)
    {
        var tags = rumor.Tags.Count == 0 ? "" : $", tags {string.Join(",", rumor.Tags)}";
        var history = rumor.DistortionHistory.Count == 0
            ? ""
            : $" Retelling: {rumor.DistortionHistory.Last()}";
        return $"Rumor: {rumor.Id} [{rumor.SourceKind}:{rumor.SourceId}, status {rumor.Status}, salience {rumor.Salience}, hops {rumor.Hops}, region {rumor.CurrentRegionId}{tags}] {rumor.Text}{history}";
    }

    public static string FormatJournalLine(RumorRecord rumor) =>
        $"Rumor: {rumor.Id} [{rumor.SourceKind}:{rumor.SourceId}, status {rumor.Status}, salience {rumor.Salience}] {rumor.Text}";
}
