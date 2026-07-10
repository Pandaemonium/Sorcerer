namespace Sorcerer.Magic.Capabilities;

/// <summary>
/// Result of a routing pass: the capability-card names the router judged relevant to the spell.
/// Names are raw model output; the controller unions them with keyword routing and validates them
/// against the registry, so unknown or hallucinated names are harmless (they simply do not resolve
/// to a card).
/// </summary>
public sealed record SpellRouteResult(
    IReadOnlyList<string> CapabilityNames,
    string RawText,
    bool TechnicalFailure,
    string? Error)
{
    public static SpellRouteResult Empty { get; } =
        new(Array.Empty<string>(), string.Empty, TechnicalFailure: false, Error: null);
}

/// <summary>
/// A "which capabilities does this spell need?" fallback for spells deterministic routing cannot
/// classify. Implementations may call an LLM, but a failure or timeout must never block a cast: the controller treats any
/// <see cref="SpellRouteResult.TechnicalFailure"/> (or thrown error) as "no router opinion" and
/// falls back to keyword routing alone.
/// </summary>
public interface ISpellRouter
{
    string Name { get; }

    Task<SpellRouteResult> RouteAsync(
        string spellText,
        string capabilityIndex,
        CancellationToken cancellationToken);
}

/// <summary>The default when no router is wired: contributes nothing, so selection is keyword-only.</summary>
public sealed class NullSpellRouter : ISpellRouter
{
    public static NullSpellRouter Instance { get; } = new();

    private NullSpellRouter()
    {
    }

    public string Name => "none";

    public Task<SpellRouteResult> RouteAsync(
        string spellText,
        string capabilityIndex,
        CancellationToken cancellationToken) =>
        Task.FromResult(SpellRouteResult.Empty);
}
