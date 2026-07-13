namespace Sorcerer.Core.World;

public sealed record PromiseObjectiveContract(
    string Kind,
    string GiverEntityId,
    string GiverName,
    string? RequiredItem,
    bool ReturnToGiver,
    IReadOnlyList<string> Tags);

public static class PromiseObjectiveContracts
{
    public static PromiseObjectiveContract? For(GameState state, WorldPromise promise)
    {
        if (string.IsNullOrWhiteSpace(promise.SourceClaimId)
            || state.Claims.Records.FirstOrDefault(claim =>
                claim.Id.Equals(promise.SourceClaimId, StringComparison.OrdinalIgnoreCase)) is not { } claim)
        {
            return null;
        }

        var kind = TagValue(claim.Tags, "objective_kind:");
        if (string.IsNullOrWhiteSpace(kind))
        {
            return null;
        }

        return new PromiseObjectiveContract(
            NormalizeToken(kind),
            promise.SourceSpeakerId ?? claim.SpeakerId,
            TagValue(claim.Tags, "objective_giver_name:") ?? promise.SourceSpeakerId ?? claim.SpeakerId,
            TagValue(claim.Tags, "objective_item:"),
            claim.Tags.Contains("objective_return_to_giver", StringComparer.OrdinalIgnoreCase),
            claim.Tags);
    }

    public static bool IsGeneratedObjective(GameState state, WorldPromise promise) =>
        For(state, promise) is not null;

    private static string? TagValue(IEnumerable<string> tags, string prefix) =>
        tags.FirstOrDefault(tag => tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))?[prefix.Length..].Trim();

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }
}
