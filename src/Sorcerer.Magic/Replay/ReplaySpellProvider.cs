using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic.Replay;

public sealed class ReplaySpellProvider : ISpellProvider
{
    private readonly IReadOnlyList<string> _resolvedMagic;
    private readonly OperationRegistry _registry;
    private int _index;

    public ReplaySpellProvider(
        IEnumerable<string> resolvedMagic,
        OperationRegistry? registry = null)
    {
        _resolvedMagic = resolvedMagic
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        _registry = registry ?? OperationRegistry.CreateDefault();
    }

    public string Name => "replay";

    public Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken)
    {
        if (_index >= _resolvedMagic.Count)
        {
            return Task.FromResult(new SpellProviderResult(
                Name,
                string.Empty,
                Resolution: null,
                TechnicalFailure: true,
                Error: $"Replay has no materialized spell resolution for '{request.SpellText}'."));
        }

        var raw = _resolvedMagic[_index++];
        try
        {
            return Task.FromResult(new SpellProviderResult(
                Name,
                raw,
                SpellResolutionJson.Parse(raw, _registry),
                TechnicalFailure: false,
                Error: null));
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
        {
            return Task.FromResult(new SpellProviderResult(
                Name,
                raw,
                Resolution: null,
                TechnicalFailure: true,
                Error: $"Replay spell resolution is invalid: {ex.Message}"));
        }
    }
}
