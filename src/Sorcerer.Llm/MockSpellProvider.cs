using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

public sealed class MockSpellProvider : ISpellProvider
{
    public string Name => "mock";

    public Task<SpellProviderResult> ResolveAsync(
        SpellRequest request,
        CancellationToken cancellationToken)
    {
        var spell = request.SpellText.ToLowerInvariant();
        SpellResolution resolution;

        if (spell.Contains("promise", StringComparison.Ordinal)
            || spell.Contains("prophecy", StringComparison.Ordinal)
            || spell.Contains("omen", StringComparison.Ordinal))
        {
            resolution = new SpellResolution(
                Accepted: true,
                Severity: "moderate",
                OutcomeText: "The spell leaves a bright hook in tomorrow.",
                Effects: new[]
                {
                    Effect("createPromise", new Dictionary<string, object?>
                    {
                        ["kind"] = "prophecy",
                        ["text"] = request.SpellText,
                    }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);
        }
        else if (spell.Contains("heal", StringComparison.Ordinal)
            || spell.Contains("mend", StringComparison.Ordinal))
        {
            resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "Green light stitches itself through the wound.",
                Effects: new[]
                {
                    Effect("heal", new Dictionary<string, object?>
                    {
                        ["target"] = "player",
                        ["amount"] = 5,
                    }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);
        }
        else
        {
            resolution = new SpellResolution(
                Accepted: true,
                Severity: "moderate",
                OutcomeText: "Blue fire snaps from your fingers in a crooked line.",
                Effects: new[]
                {
                    Effect("damage", new Dictionary<string, object?>
                    {
                        ["target"] = "nearest_enemy",
                        ["amount"] = 6,
                        ["damageType"] = "arcane",
                    }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);
        }

        return Task.FromResult(new SpellProviderResult(
            Name,
            RawText: "",
            Resolution: resolution,
            TechnicalFailure: false,
            Error: null));
    }

    private static SpellEffect Effect(
        string type,
        IReadOnlyDictionary<string, object?> fields) =>
        new(type, fields);
}

