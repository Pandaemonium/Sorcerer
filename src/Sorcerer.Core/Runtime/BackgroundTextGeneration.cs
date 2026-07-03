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
    string? RawText = null);

public interface IBackgroundTextGenerator
{
    string Name { get; }

    BackgroundTextGenerationResult Generate(BackgroundTextRequest request);
}
