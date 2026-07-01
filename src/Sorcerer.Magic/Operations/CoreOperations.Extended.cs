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
            .Select(entity => context.Engine.ApplyStatus(entity, status, duration, displayName))
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

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        string.IsNullOrWhiteSpace(Text(effect, "item", ""))
            ? ValidationOutcome.Reject("modifyInventory needs an item.")
            : ValidationOutcome.Pass;

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var target = ResolveTargets(context, effect, "self").FirstOrDefault() ?? context.Caster;
        var item = NormalizeToken(Text(effect, "item", ""), fallback: "trinket");
        var op = Text(effect, "op", "add").Trim().ToLowerInvariant();
        var amount = Int(effect, "amount", 1, min: 0, max: 99);
        var inventory = target.TryGet<InventoryComponent>(out var existing) ? existing : InventoryComponent.Empty();
        var current = inventory.Items.TryGetValue(item, out var count) ? count : 0;
        var updated = op switch
        {
            "remove" or "subtract" => Math.Max(0, current - amount),
            "set" => Math.Max(0, amount),
            _ => current + Math.Max(1, amount),
        };

        if (updated <= 0)
        {
            inventory.Items.Remove(item);
            inventory.TreasuredItems.Remove(item);
        }
        else
        {
            inventory.Items[item] = updated;
        }

        target.Set(inventory);
        var summary = $"{item.Replace('_', ' ')} count becomes {updated}.";
        context.Engine.AddMessage(summary);
        return new[]
        {
            new StateDelta(
                "modifyInventory",
                target.Id.Value,
                summary,
                new Dictionary<string, object?> { ["item"] = item, ["count"] = updated }),
        };
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
            .Select(target =>
            {
                AddTags(target, tags);
                var summary = $"{Subject(context, target)} {Verb(context, target, "are", "is")} tagged {string.Join(", ", tags)}.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "addTag",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["tags"] = tags });
            })
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
            .Select(target =>
            {
                var current = target.TryGet<TagsComponent>(out var existing)
                    ? existing.Tags.ToList()
                    : new List<string>();
                current.RemoveAll(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
                target.Set(new TagsComponent(current));
                var summary = tags.Count > 0
                    ? $"{Subject(context, target)} {Verb(context, target, "lose", "loses")} {string.Join(", ", tags)}."
                    : $"{Possessive(context, target)} tags are unchanged.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "removeTag",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["tags"] = tags });
            })
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
            .Select(target => Accelerate(context, target, statusId))
            .Where(delta => delta is not null)
            .Select(delta => delta!)
            .ToArray();
    }

    private static StateDelta? Accelerate(EffectContext context, Entity target, string statusId)
    {
        if (!target.TryGet<StatusContainerComponent>(out var container) || container.Statuses.Count == 0)
        {
            return null;
        }

        var canonical = string.IsNullOrWhiteSpace(statusId) ? "" : context.Engine.Statuses.Canonicalize(statusId);
        var instance = container.Statuses.FirstOrDefault(status =>
            string.IsNullOrWhiteSpace(canonical)
                ? context.Engine.Statuses.DamagePerTurn(status.Id) != 0 || context.Engine.Statuses.HealPerTurn(status.Id) != 0
                : status.Id.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            return null;
        }

        var remainingTurns = Math.Max(1, (instance.ExpiresTurn ?? context.Engine.State.Turn + 1) - context.Engine.State.Turn);
        var damagePerTurn = context.Engine.Statuses.DamagePerTurn(instance.Id);
        var healPerTurn = context.Engine.Statuses.HealPerTurn(instance.Id);
        var remaining = container.Statuses.Where(status => !ReferenceEquals(status, instance)).ToList();
        target.Set(new StatusContainerComponent(remaining));

        if (damagePerTurn > 0)
        {
            return context.Engine.DamageEntity(target, damagePerTurn * remainingTurns, instance.Id);
        }

        if (healPerTurn > 0)
        {
            return context.Engine.HealEntity(target, healPerTurn * remainingTurns);
        }

        var summary = $"{Possessive(context, target)} {instance.DisplayName.Replace('_', ' ')} rushes to its conclusion.";
        context.Engine.AddMessage(summary);
        return new StateDelta(
            "accelerateStatus",
            target.Id.Value,
            summary,
            new Dictionary<string, object?> { ["status"] = instance.Id });
    }
}
