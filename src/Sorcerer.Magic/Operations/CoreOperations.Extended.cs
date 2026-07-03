using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Magic.Resolution;
using static Sorcerer.Magic.Operations.OperationHelpers;

namespace Sorcerer.Magic.Operations;

/// <summary>
/// Phase B, group 1 of the Wild Magic resolver port: cheap operations that reuse existing
/// engine primitives without new components or ledgers. See docs/MAGIC_RESOLVER_ARCHITECTURE.md.
/// </summary>
public sealed class AreaStatusOperation : OperationBase
{
    public AreaStatusOperation()
        : base(
            "areaStatus",
            new[] { "statusArea", "area_status" },
            "Apply a status to actors around a point.",
            "Use for spreading conditions such as fear, slowing, or confusion. Fields: target, radius, status, displayName, duration, affects.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireOrigin(context, effect, "selected_target", "Area status magic needs a point or target.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var origin = ResolveOrigin(context, effect, "selected_target")
            ?? context.Caster.Get<PositionComponent>().Position;
        var radius = Int(effect, "radius", 2, min: 0, max: 5);
        var status = NormalizeToken(Text(effect, "status", Text(effect, "trait", Text(effect, "name", "marked"))));
        var displayName = Text(effect, "displayName", Text(effect, "display_name", status));
        var duration = Int(effect, "duration", 0, min: 0, max: 99);
        var affects = Text(effect, "affects", "enemies");
        return context.Engine.State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive
                && GameEngineDistance(position.Position, origin) <= radius)
            .Where(entity => AffectsTarget(context, entity, affects))
            .Take(context.GroupTargetCap)
            .SelectMany(entity => context.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
                "wild_magic",
                entity.Id.Value,
                status,
                duration,
                displayName,
                sourceEntityId: context.Caster.Id.Value,
                operation: "areaStatus")).Deltas)
            .ToArray();
    }
}

public sealed class ModifyInventoryOperation : OperationBase
{
    public ModifyInventoryOperation()
        : base(
            "modifyInventory",
            new[] { "inventoryChange", "modify_inventory" },
            "Add, remove, or set a carried item count.",
            "Use for direct inventory bookkeeping magic. Fields: target, item, op (add/remove/set), amount.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var item = Text(effect, "item", "");
        if (string.IsNullOrWhiteSpace(item))
        {
            return ValidationOutcome.Reject("modifyInventory needs an item.");
        }

        var op = NormalizeToken(Text(effect, "op", "add"), "add");
        if (op is not ("remove" or "subtract" or "consume"))
        {
            return ValidationOutcome.Pass;
        }

        var target = ResolveTargets(context, effect, "self").FirstOrDefault() ?? context.Caster;
        var amount = Int(effect, "amount", 1, min: 0, max: 99);
        if (amount <= 0)
        {
            return ValidationOutcome.Pass;
        }

        if (!target.TryGet<InventoryComponent>(out var inventory))
        {
            return ValidationOutcome.Reject($"{target.Name} is not carrying {item}.");
        }

        var key = FindInventoryKey(inventory, item) ?? FindInventoryKey(inventory, NormalizeToken(item, item));
        return key is not null && inventory.Items.TryGetValue(key, out var count) && count >= amount
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject($"{target.Name} is not carrying enough {item}.");
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var target = ResolveTargets(context, effect, "self").FirstOrDefault() ?? context.Caster;
        var item = NormalizeToken(Text(effect, "item", ""), fallback: "trinket");
        var op = Text(effect, "op", "add").Trim().ToLowerInvariant();
        var amount = Int(effect, "amount", 1, min: 0, max: 99);
        return context.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "wild_magic",
            target.Id.Value,
            item,
            op,
            amount,
            WorldConsequenceVisibility.Message,
            context.Caster.Id.Value)).Deltas;
    }

    private static string? FindInventoryKey(InventoryComponent inventory, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (inventory.Items.ContainsKey(item))
        {
            return item;
        }

        var normalized = NormalizeToken(item, item);
        return inventory.Items.Keys.FirstOrDefault(key =>
            NormalizeToken(key, key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class AddTagOperation : OperationBase
{
    public AddTagOperation()
        : base(
            "addTag",
            new[] { "tagEntity", "add_tag" },
            "Add one or more discrete bookkeeping tags to an entity.",
            "Use for quiet flags distinct from addTrait's narrative flavor. Fields: target, tag, tags.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var tags = Tags(effect, "tags", Tags(effect, "tag", Array.Empty<string>()));
        if (tags.Count == 0)
        {
            return Array.Empty<StateDelta>();
        }

        return ResolveTargets(context, effect, "nearest_enemy")
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.AddTags(
                "wild_magic",
                target.Id.Value,
                tags,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
            .ToArray();
    }
}

public sealed class RemoveTagOperation : OperationBase
{
    public RemoveTagOperation()
        : base(
            "removeTag",
            new[] { "untagEntity", "remove_tag" },
            "Remove one or more bookkeeping tags from an entity.",
            "Fields: target, tag, tags.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var tags = Tags(effect, "tags", Tags(effect, "tag", Array.Empty<string>()));
        return ResolveTargets(context, effect, "nearest_enemy")
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.RemoveTags(
                "wild_magic",
                target.Id.Value,
                tags,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
            .ToArray();
    }
}

public sealed class AccelerateStatusOperation : OperationBase
{
    public AccelerateStatusOperation()
        : base(
            "accelerateStatus",
            new[] { "rushStatus", "accelerate_status" },
            "Apply all remaining ticks of an ongoing status at once, then clear it.",
            "Use for compressing a burn, poison, or regeneration into an instant. Fields: target, status.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var statusId = NormalizeToken(Text(effect, "status", ""), fallback: "");
        return ResolveTargets(context, effect, "nearest_enemy")
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.AccelerateStatus(
                "wild_magic",
                target.Id.Value,
                statusId,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
            .ToArray();
    }
}
