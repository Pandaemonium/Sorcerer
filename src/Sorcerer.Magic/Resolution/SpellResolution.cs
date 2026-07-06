using Sorcerer.Core.Telemetry;
using Sorcerer.Magic.Capabilities;

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
    IReadOnlyList<string> SupportedOperations,
    IReadOnlyList<CapabilityCard>? SelectedCapabilities = null,
    string? CapabilityIndex = null);

public sealed record SpellProviderResult(
    string Provider,
    string RawText,
    SpellResolution? Resolution,
    bool TechnicalFailure,
    string? Error,
    ProviderCallStats? Stats = null,
    // Set when the model answered {"needsCapability":"name"} instead of a resolution (WS1.2). The
    // controller loads that card and re-resolves once; a second such answer is a technical failure.
    string? RequestedCapability = null);

public interface ISpellProvider
{
    string Name { get; }

    Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken);
}
