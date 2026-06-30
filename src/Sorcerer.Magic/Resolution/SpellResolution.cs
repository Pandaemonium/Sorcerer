namespace Sorcerer.Magic.Resolution;

public sealed record SpellEffect(string Type, IReadOnlyDictionary<string, object?> Fields);

public sealed record SpellCost(string Type, IReadOnlyDictionary<string, object?> Fields);

public sealed record SpellResolution(
    bool Accepted,
    string Severity,
    string OutcomeText,
    IReadOnlyList<SpellEffect> Effects,
    IReadOnlyList<SpellCost> Costs,
    string? RejectedReason);

public sealed record SpellRequest(
    string SpellText,
    object Context,
    IReadOnlyList<string> SupportedOperations);

public sealed record SpellProviderResult(
    string Provider,
    string RawText,
    SpellResolution? Resolution,
    bool TechnicalFailure,
    string? Error);

public interface ISpellProvider
{
    string Name { get; }

    Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken);
}

