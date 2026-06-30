namespace Sorcerer.Core.World;

public sealed record WorldReactionSummary(
    IReadOnlyList<string> StandingChanges,
    IReadOnlyList<string> NewPromises,
    IReadOnlyList<string> NarrationHooks);

public sealed class WorldReactionSystem
{
    public WorldReactionSummary PreviewDailyTick(GameState state)
    {
        var standingChanges = state.Deeds.Records
            .Where(deed => deed.Kind.Contains("imperial", StringComparison.OrdinalIgnoreCase))
            .Select(deed => $"Empire pressure notices {deed.Kind}.")
            .ToArray();

        return new WorldReactionSummary(
            standingChanges,
            Array.Empty<string>(),
            Array.Empty<string>());
    }
}
