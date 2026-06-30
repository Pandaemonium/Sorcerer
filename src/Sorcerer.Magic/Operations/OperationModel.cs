using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.References;
using Sorcerer.Core.Results;
using Sorcerer.Core.Views;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic.Operations;

public sealed record ValidationOutcome(bool Ok, string? RejectReason = null, bool Fatal = false)
{
    public static ValidationOutcome Pass { get; } = new(true);

    public static ValidationOutcome Reject(string reason) => new(false, reason);
}

public sealed record OperationCard(
    string Name,
    IReadOnlyList<string> Aliases,
    string Summary,
    string PromptGuidance,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<object> Examples)
{
    public OperationCardView ToView() =>
        new(Name, Aliases, Summary, PromptGuidance, Fields, Examples);
}

public sealed record EffectContext(
    GameEngine Engine,
    Entity Caster,
    IReferenceResolver Refs,
    int GroupTargetCap = 8);

public interface IOperation
{
    string Name { get; }

    IReadOnlyList<string> Aliases { get; }

    OperationCard Card { get; }

    ValidationOutcome Validate(EffectContext context, SpellEffect effect);

    IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect);
}

public abstract class OperationBase : IOperation
{
    protected OperationBase(
        string name,
        IReadOnlyList<string> aliases,
        string summary,
        string promptGuidance,
        IReadOnlyDictionary<string, string>? fields = null)
    {
        Name = name;
        Aliases = aliases;
        Card = new OperationCard(
            name,
            aliases,
            summary,
            promptGuidance,
            fields ?? new Dictionary<string, string>(),
            Array.Empty<object>());
    }

    public string Name { get; }

    public IReadOnlyList<string> Aliases { get; }

    public OperationCard Card { get; private set; }

    public void AttachCard(OperationCard card) => Card = card;

    public abstract ValidationOutcome Validate(EffectContext context, SpellEffect effect);

    public abstract IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect);

    protected static string Text(SpellEffect effect, string key, string fallback = "") =>
        effect.Fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    protected static int Int(SpellEffect effect, string key, int fallback = 0, int min = int.MinValue, int max = int.MaxValue)
    {
        var value = effect.Fields.TryGetValue(key, out var raw)
            && int.TryParse(Convert.ToString(raw), out var parsed)
                ? parsed
                : fallback;
        return Math.Clamp(value, min, max);
    }

    protected static bool Bool(SpellEffect effect, string key, bool fallback = false) =>
        effect.Fields.TryGetValue(key, out var raw)
            && bool.TryParse(Convert.ToString(raw), out var parsed)
                ? parsed
                : fallback;

    protected static EntityRef TargetRef(SpellEffect effect, string fallback = "self")
    {
        var radius = effect.Fields.TryGetValue("radius", out var rawRadius)
            && int.TryParse(Convert.ToString(rawRadius), out var parsedRadius)
                ? parsedRadius
                : (int?)null;
        var target = effect.Fields.TryGetValue("target", out var value) ? value : fallback;
        return ReferenceBinder.NormalizeEntityRef(target, radius);
    }

    protected static ValidationOutcome RequireTargets(EffectContext context, SpellEffect effect, string fallback = "self")
    {
        var resolved = context.Refs.Resolve(TargetRef(effect, fallback));
        return resolved.Success && (resolved.Entities.Count > 0 || resolved.Position is not null)
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject(resolved.Error ?? "Target could not be resolved.");
    }
}
