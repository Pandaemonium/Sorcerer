namespace Sorcerer.Core.Runtime;

public sealed record BackgroundTextRequest(
    string JobId,
    string Purpose,
    string TargetId,
    int Priority,
    int Turn,
    string RegionId,
    string TargetKind,
    string? TargetName = null,
    string? TargetMaterial = null,
    IReadOnlyList<string>? TargetTags = null,
    string? OriginalText = null,
    string? RoutedLore = null)
{
    public IReadOnlyList<string> Tags => TargetTags ?? Array.Empty<string>();
}

public sealed record BackgroundTextGenerationResult(
    string? Text,
    bool TechnicalFailure = false,
    string? Error = null,
    string Provider = "deterministic",
    string? Model = null,
    string? RawText = null,
    // Set by generators (e.g. replay) that are feeding back an already-recorded, already-final
    // result rather than freshly generating prose. Skips post-generation normalization so replay
    // reproduces the exact materialized text instead of re-truncating it.
    bool AlreadyMaterialized = false);

public interface IBackgroundTextGenerator
{
    string Name { get; }

    BackgroundTextGenerationResult Generate(BackgroundTextRequest request);
}
