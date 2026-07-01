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

        return SupportedEffectTypes.Contains(effectType, StringComparer.OrdinalIgnoreCase)
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject($"createPersistentEffect does not support embedded effect '{effectType}'.");
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
        var record = context.Engine.State.PersistentEffects.Add(
            anchor.Id.Value,
            hook,
            effectType,
            effectFields,
            uses,
            linkPartnerId,
            playerVisible: true);
        var anchorName = anchor.Id == context.Engine.State.ControlledEntityId ? "you" : anchor.Name;
        var summary = sympathetic
            ? $"A sympathetic link binds {anchorName} to another's wounds."
            : $"A lasting mark settles onto {anchorName}, waiting to answer when {(hook == "on_hit" ? "it is struck" : "it strikes")}.";
        context.Engine.AddMessage(summary);
        return new[]
        {
            new StateDelta(
                "createPersistentEffect",
                record.Id,
                summary,
                new Dictionary<string, object?> { ["hook"] = hook, ["effectType"] = effectType }),
        };
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
            .Select(target =>
            {
                var behaviors = target.TryGet<BehaviorTagsComponent>(out var existing) ? existing : BehaviorTagsComponent.Empty();
                behaviors.Tags[tag] = duration > 0 ? context.Engine.State.Turn + duration : null;
                target.Set(behaviors);
                var summary = $"{Subject(context, target)} {Verb(context, target, "fall", "falls")} under a {tag.Replace('_', ' ')} compulsion.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "setBehavior",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["tag"] = tag, ["duration"] = duration });
            })
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

        var radius = Int(effect, "radius", 1, min: 0, max: 5);
        var dx = Math.Clamp(Int(effect, "dx", 1), -1, 1);
        var dy = Math.Clamp(Int(effect, "dy", 0), -1, 1);
        var duration = Int(effect, "duration", 5, min: 1, max: 99);
        var expiresTurn = context.Engine.State.Turn + duration;
        for (var y = origin.Value.Y - radius; y <= origin.Value.Y + radius; y++)
        {
            for (var x = origin.Value.X - radius; x <= origin.Value.X + radius; x++)
            {
                var point = new GridPoint(x, y);
                if (context.Engine.InBounds(point)
                    && GameEngineDistance(origin.Value, point) <= radius
                    && !context.Engine.State.BlockingTerrain.Contains(point))
                {
                    context.Engine.State.TileFlows[point] = new TileFlow(dx, dy, expiresTurn);
                }
            }
        }

        var summary = $"The ground begins to flow near {origin.Value.X},{origin.Value.Y}.";
        context.Engine.AddMessage(summary);
        return new[]
        {
            new StateDelta(
                "createFlow",
                $"tile:{origin.Value.X},{origin.Value.Y}",
                summary,
                new Dictionary<string, object?> { ["dx"] = dx, ["dy"] = dy, ["radius"] = radius }),
        };
    }
}
