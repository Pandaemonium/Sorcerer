using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.References;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;
using Sorcerer.Magic.Resolution;
using static Sorcerer.Magic.Operations.OperationHelpers;

namespace Sorcerer.Magic.Operations;

/// <summary>
/// Phase B, group 3 of the Wild Magic resolver port: the subsystem-heavy operations
/// (createPersistentEffect/sympathetic links, setBehavior, createFlow). See
/// docs/MAGIC_RESOLVER_ARCHITECTURE.md.
/// </summary>
public sealed class CreatePersistentEffectOperation : OperationBase
{
    private static readonly string[] SupportedEffectTypes =
    {
        "damage", "harm", "heal", "restorehealth", "restoreHealth", "addStatus", "status", "applyStatus", "message",
        "consequence", "worldconsequence", "world_consequence",
    };

    public CreatePersistentEffectOperation()
        : base(
            "createPersistentEffect",
            new[] { "persistentEffect", "create_persistent_effect" },
            "Anchor an effect that fires when the anchor hits or is hit.",
            "Fields: target (anchor), hook (on_hit fires when the anchor is struck, on_strike "
                + "fires when the anchor lands a hit), effect (a nested effect object) or "
                + "effectType/amount/damageType/status/duration/text at top level, uses. For a "
                + "sympathetic link, set kind:'sympathetic_link' and linkTarget to mirror a "
                + "fraction of incoming damage onto the partner.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var targets = RequireTargets(context, effect, "self");
        if (!targets.Ok)
        {
            return targets;
        }

        if (IsSympatheticLink(effect))
        {
            var anchor = ResolveTargets(context, effect, "self").FirstOrDefault();
            var linkTargetRaw = effect.Fields.TryGetValue("linkTarget", out var rawLink) ? rawLink : "nearest_enemy";
            var resolved = context.Refs.Resolve(ReferenceBinder.NormalizeEntityRef(linkTargetRaw));
            return anchor is not null && resolved.Entities.Any(entity => entity.Id != anchor.Id)
                ? ValidationOutcome.Pass
                : ValidationOutcome.Reject("sympathetic links need a second actor to bind.");
        }

        var effectType = EffectType(effect);
        if (string.IsNullOrWhiteSpace(effectType))
        {
            return ValidationOutcome.Reject("createPersistentEffect needs effectType or a nested effect.type.");
        }

        if (!SupportedEffectTypes.Contains(effectType, StringComparer.OrdinalIgnoreCase))
        {
            return ValidationOutcome.Reject($"createPersistentEffect does not support embedded effect '{effectType}'.");
        }

        return effectType.Trim().ToLowerInvariant() is "consequence" or "worldconsequence" or "world_consequence"
            && string.IsNullOrWhiteSpace(NestedOrTopLevelText(effect, "consequenceType", NestedOrTopLevelText(effect, "consequence_type", "")))
            ? ValidationOutcome.Reject("Persistent consequence effects need consequenceType.")
            : ValidationOutcome.Pass;
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var anchor = ResolveTargets(context, effect, "self").FirstOrDefault() ?? context.Caster;
        var hook = Text(effect, "hook", "on_hit").Trim().ToLowerInvariant();
        if (hook != "on_hit" && hook != "on_strike")
        {
            hook = "on_hit";
        }

        var sympathetic = IsSympatheticLink(effect);
        var uses = Int(effect, "uses", sympathetic ? 999 : 3, min: 1, max: 999);
        string? linkPartnerId = null;
        if (sympathetic)
        {
            var linkTargetRaw = effect.Fields.TryGetValue("linkTarget", out var rawLink) ? rawLink : "nearest_enemy";
            var resolved = context.Refs.Resolve(ReferenceBinder.NormalizeEntityRef(linkTargetRaw));
            linkPartnerId = resolved.Entities.FirstOrDefault(entity => entity.Id != anchor.Id)?.Id.Value;
        }

        var effectType = sympathetic ? "damage" : EffectType(effect);
        var effectFields = sympathetic
            ? new Dictionary<string, object?> { ["target"] = "other" }
            : ExtractEffectFields(effect);
        return context.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "wild_magic",
            anchor.Id.Value,
            hook,
            effectType,
            effectFields,
            uses,
            linkPartnerId,
            playerVisible: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: context.Caster.Id.Value)).Deltas;
    }

    private static bool IsSympatheticLink(SpellEffect effect) =>
        Text(effect, "kind", "").Equals("sympathetic_link", StringComparison.OrdinalIgnoreCase);

    private static string EffectType(SpellEffect effect) =>
        TryNestedEffect(effect, out var nested) && nested.TryGetValue("type", out var nestedType)
            ? Convert.ToString(nestedType) ?? ""
            : Text(effect, "effectType", "");

    private static Dictionary<string, object?> ExtractEffectFields(SpellEffect effect)
    {
        if (TryNestedEffect(effect, out var nested))
        {
            return new Dictionary<string, object?>(nested, StringComparer.OrdinalIgnoreCase);
        }

        var fields = new Dictionary<string, object?>(effect.Fields, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "target", "hook", "kind", "uses", "linkTarget", "effectType", "effect" })
        {
            fields.Remove(key);
        }

        return fields;
    }

    private static string NestedOrTopLevelText(SpellEffect effect, string key, string fallback) =>
        TryNestedEffect(effect, out var nested) && nested.TryGetValue(key, out var raw)
            ? Convert.ToString(raw) ?? fallback
            : Text(effect, key, fallback);

    private static bool TryNestedEffect(SpellEffect effect, out IReadOnlyDictionary<string, object?> nested)
    {
        if (effect.Fields.TryGetValue("effect", out var raw) && raw is IReadOnlyDictionary<string, object?> dictionary)
        {
            nested = dictionary;
            return true;
        }

        nested = new Dictionary<string, object?>();
        return false;
    }
}

public sealed class SetBehaviorOperation : OperationBase
{
    private static readonly string[] SupportedTags = { "coward", "dance", "freeze_dread", "mimic" };

    public SetBehaviorOperation()
        : base(
            "setBehavior",
            new[] { "behaviorTag", "set_behavior" },
            "Reshape how a creature decides its turn.",
            "Fields: target, tag (coward flees the player instead of closing; dance/freeze_dread "
                + "skip the actor's turn; mimic copies the player's last movement), duration "
                + "(turns; omit for permanent).",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var targets = RequireTargets(context, effect, "nearest_enemy");
        if (!targets.Ok)
        {
            return targets;
        }

        var tag = Text(effect, "tag", "").Trim().ToLowerInvariant();
        return SupportedTags.Contains(tag)
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject($"setBehavior does not support tag '{tag}'.");
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var tag = Text(effect, "tag", "").Trim().ToLowerInvariant();
        var duration = Int(effect, "duration", 0, min: 0, max: 999);
        return ResolveTargets(context, effect, "nearest_enemy")
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.SetBehavior(
                "wild_magic",
                target.Id.Value,
                tag,
                duration,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
            .ToArray();
    }
}

public sealed class CreateFlowOperation : OperationBase
{
    public CreateFlowOperation()
        : base(
            "createFlow",
            new[] { "tileFlow", "create_flow" },
            "Create a standing tile field that moves whoever stands on it each turn.",
            "Fields: target/x/y, radius, dx, dy (each -1, 0, or 1), duration.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireOrigin(context, effect, "selected_target", "createFlow needs a tile target.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var origin = ResolveOrigin(context, effect, "selected_target");
        if (origin is null)
        {
            return Array.Empty<StateDelta>();
        }

        return context.Engine.ApplyConsequence(WorldConsequence.CreateFlow(
            "wild_magic",
            origin.Value.X,
            origin.Value.Y,
            Int(effect, "radius", 1, min: 0, max: 5),
            Math.Clamp(Int(effect, "dx", 1), -1, 1),
            Math.Clamp(Int(effect, "dy", 0), -1, 1),
            Int(effect, "duration", 5, min: 1, max: 99),
            WorldConsequenceVisibility.Message,
            context.Caster.Id.Value)).Deltas;
    }
}
