using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.References;
using Sorcerer.Core.Results;
using Sorcerer.Magic.Resolution;
using static Sorcerer.Magic.Operations.OperationHelpers;

namespace Sorcerer.Magic.Operations;

public sealed class DamageOperation : OperationBase
{
    public DamageOperation()
        : base(
            "damage",
            new[] { "harm", "attack", "wound" },
            "Damage one or more actor targets.",
            "Use for direct harm. Fields: target, amount, damageType.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireActorTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        ResolveTargets(context, effect, "nearest_enemy")
            .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Select(target => context.Engine.DamageEntity(
                target,
                Int(effect, "amount", 4, min: 1, max: 40),
                Text(effect, "damageType", Text(effect, "damage_type", "arcane"))))
            .ToArray();
}

public sealed class AreaDamageOperation : OperationBase
{
    public AreaDamageOperation()
        : base(
            "areaDamage",
            new[] { "blast", "burst", "area_damage" },
            "Damage actors around a point.",
            "Use for explosions, storms, swarms, or spreading harm. Fields: target, radius, amount, affects.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "selected_target");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var origin = ResolveOrigin(context, effect, "selected_target")
            ?? context.Caster.Get<PositionComponent>().Position;
        var radius = Int(effect, "radius", 2, min: 0, max: 5);
        var amount = Int(effect, "amount", 3, min: 1, max: 25);
        var affects = Text(effect, "affects", "enemies");
        var damageType = Text(effect, "damageType", Text(effect, "damage_type", "arcane"));
        return context.Engine.State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive
                && GameEngineDistance(position.Position, origin) <= radius)
            .Where(entity => Affects(context, entity, affects))
            .Take(context.GroupTargetCap)
            .Select(entity => context.Engine.DamageEntity(entity, amount, damageType))
            .ToArray();
    }

    private static bool Affects(EffectContext context, Entity entity, string affects)
    {
        if (entity.Id == context.Caster.Id
            && affects.Equals("enemies", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return affects.Trim().ToLowerInvariant() switch
        {
            "all" or "everyone" => true,
            "allies" or "friendly" => !context.Engine.IsHostile(context.Caster, entity),
            _ => context.Engine.IsHostile(context.Caster, entity),
        };
    }
}

public sealed class HealOperation : OperationBase
{
    public HealOperation()
        : base(
            "heal",
            new[] { "restoreHealth", "mend" },
            "Restore HP to an actor.",
            "Use for mending wounds. Fields: target, amount.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireActorTargets(context, effect, "self");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        ResolveTargets(context, effect, "self")
            .Where(target => target.TryGet<ActorComponent>(out _))
            .Select(target => context.Engine.HealEntity(target, Int(effect, "amount", 4, min: 1, max: 30)))
            .ToArray();
}

public sealed class RestoreManaOperation : OperationBase
{
    public RestoreManaOperation()
        : base(
            "restoreMana",
            new[] { "restore_mana", "mana" },
            "Restore mana to an actor.",
            "Use when magic returns power to a body. Fields: target, amount.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireActorTargets(context, effect, "self");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        ResolveTargets(context, effect, "self")
            .Where(target => target.TryGet<ActorComponent>(out _))
            .Select(target => context.Engine.RestoreMana(target, Int(effect, "amount", 4, min: 1, max: 30)))
            .ToArray();
}

public sealed class PushOperation : OperationBase
{
    public PushOperation()
        : base(
            "push",
            new[] { "shove", "repel" },
            "Move a target away from an origin.",
            "Use for force, wind, fear, repulsion. Fields: target, distance, origin.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequirePositionedTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        MoveLinearly(context, effect, away: true);
}

public sealed class PullOperation : OperationBase
{
    public PullOperation()
        : base(
            "pull",
            new[] { "drag", "draw" },
            "Move a target toward an origin.",
            "Use for gravity, hooks, beckoning, suction. Fields: target, distance, origin.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequirePositionedTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        MoveLinearly(context, effect, away: false);
}

public sealed class TeleportOperation : OperationBase
{
    public TeleportOperation()
        : base(
            "teleport",
            new[] { "blink", "relocate" },
            "Move an entity to a chosen tile.",
            "Use for spatial relocation. Fields: target, x, y; or target with selected target.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequirePositionedTargets(context, effect, "self");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var destination = Destination(context, effect);
        if (destination is null)
        {
            return new[] { Message(context, "teleport", "", "The teleport curls up without a destination.") };
        }

        return ResolveTargets(context, effect, "self")
            .Where(target => target.TryGet<PositionComponent>(out _))
            .Select(target => context.Engine.MoveEntity(target, destination.Value, "teleport"))
            .ToArray();
    }
}

public sealed class CreateTileOperation : OperationBase
{
    public CreateTileOperation()
        : base(
            "createTile",
            new[] { "terrain", "alterTile" },
            "Change one tile of terrain.",
            "Use for walls, ice, fire, water, vines, rubble, bridges. Fields: target/x/y, terrain, duration.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireOrigin(context, effect, "selected_target", "Terrain magic needs a tile target.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var point = ResolveOrigin(context, effect, "selected_target");
        return point is null
            ? Array.Empty<StateDelta>()
            : new[]
            {
                context.Engine.SetTerrain(
                    point.Value,
                    NormalizeTerrain(Text(effect, "terrain", Text(effect, "tile", "wild_growth"))),
                    Int(effect, "duration", 0, min: 0, max: 99) is var duration && duration > 0 ? duration : null),
            };
    }
}

public sealed class CreateTilesOperation : OperationBase
{
    public CreateTilesOperation()
        : base(
            "createTiles",
            new[] { "terrainArea", "alterTiles" },
            "Change a cluster of terrain tiles.",
            "Use for spreading terrain effects. Fields: target/x/y, radius, terrain, duration.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireOrigin(context, effect, "selected_target", "Terrain magic needs a tile target.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var origin = ResolveOrigin(context, effect, "selected_target");
        if (origin is null)
        {
            return Array.Empty<StateDelta>();
        }

        var radius = Int(effect, "radius", 1, min: 0, max: 4);
        var terrain = NormalizeTerrain(Text(effect, "terrain", Text(effect, "tile", "wild_growth")));
        var duration = Int(effect, "duration", 0, min: 0, max: 99);
        var deltas = new List<StateDelta>();
        for (var y = origin.Value.Y - radius; y <= origin.Value.Y + radius; y++)
        {
            for (var x = origin.Value.X - radius; x <= origin.Value.X + radius; x++)
            {
                var point = new GridPoint(x, y);
                if (context.Engine.InBounds(point)
                    && GameEngineDistance(origin.Value, point) <= radius
                    && !context.Engine.State.BlockingTerrain.Contains(point))
                {
                    deltas.Add(context.Engine.SetTerrain(point, terrain, duration > 0 ? duration : null));
                }
            }
        }

        return deltas;
    }
}

public sealed class AddStatusOperation : OperationBase
{
    public AddStatusOperation()
        : base(
            "addStatus",
            new[] { "status", "applyStatus" },
            "Apply a timed status to actors or objects.",
            "Use status ids as reusable mechanics with vivid names. Fields: target, status, displayName, duration.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var status = NormalizeToken(Text(effect, "status", Text(effect, "trait", Text(effect, "name", "marked"))));
        var displayName = Text(effect, "displayName", Text(effect, "display_name", status));
        var duration = Int(effect, "duration", 3, min: 1, max: 99);
        return ResolveTargets(context, effect, "nearest_enemy")
            .Select(target => context.Engine.ApplyStatus(target, status, duration, displayName))
            .ToArray();
    }
}

public sealed class RemoveStatusOperation : OperationBase
{
    public RemoveStatusOperation()
        : base(
            "removeStatus",
            new[] { "clearStatus", "remove_status" },
            "Remove a status from a target.",
            "Use for cleansing, thawing, releasing bindings. Fields: target, status.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "self");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var status = NormalizeToken(Text(effect, "status", "marked"));
        return ResolveTargets(context, effect, "self")
            .Select(target => context.Engine.RemoveStatus(target, status))
            .ToArray();
    }
}

public sealed class SummonOperation : OperationBase
{
    public SummonOperation()
        : base(
            "summon",
            new[] { "createEntity", "manifest" },
            "Create a bounded creature or construct.",
            "Use for allies, hazards, distractions, and small creatures. Fields: name, faction, hp, attack, glyph, target, placement, tags.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        !string.IsNullOrWhiteSpace(SummonName(effect, ""))
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("Summoning needs a name.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var origin = ResolveOrigin(context, effect, "self")
            ?? context.Caster.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(context, origin);
        if (position is null)
        {
            return new[] { Message(context, "summon", "", "The summoning has nowhere to stand.") };
        }

        var name = SummonName(effect, "summoned wonder");
        var faction = Text(effect, "faction", "player");
        var glyphText = Text(effect, "glyph", "*");
        var tags = Tags(effect, "tags", new[] { "summoned", "wild_magic" });
        var entity = context.Engine.SpawnEntity(
            NormalizeToken(name, "summon"),
            name,
            string.IsNullOrEmpty(glyphText) ? '*' : glyphText[0],
            position.Value,
            faction,
            Int(effect, "hp", 5, min: 1, max: 20),
            Int(effect, "attack", 2, min: 0, max: 10),
            tags);
        entity.Set(new SummonedComponent(context.Caster.Id.Value));
        var summary = $"{name} appears at {position.Value.X},{position.Value.Y}.";
        context.Engine.AddMessage(summary);
        return new[]
        {
            new StateDelta(
                "summon",
                entity.Id.Value,
                summary,
                new Dictionary<string, object?>
                {
                    ["x"] = position.Value.X,
                    ["y"] = position.Value.Y,
                    ["faction"] = faction,
                    ["tags"] = tags,
            }),
        };
    }

    private static string SummonName(SpellEffect effect, string fallback) =>
        Text(effect, "name", Text(effect, "entityName", Text(effect, "entity_name", fallback)));
}

public sealed class TransformEntityOperation : OperationBase
{
    public TransformEntityOperation()
        : base(
            "transformEntity",
            new[] { "transfigure", "alterEntity" },
            "Change an entity's name, material, tags, or description.",
            "Use for open-ended physical transformations. Fields: target, name, material, addTags, description.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        ResolveTargets(context, effect, "nearest_enemy")
            .Select(target =>
            {
                var before = target.Name;
                var newName = Text(effect, "name", Text(effect, "newName", ""));
                if (!string.IsNullOrWhiteSpace(newName))
                {
                    target.Name = newName;
                }

                if (!string.IsNullOrWhiteSpace(Text(effect, "material", "")))
                {
                    var physical = target.TryGet<PhysicalComponent>(out var existing)
                        ? existing
                        : new PhysicalComponent();
                    target.Set(physical with { Material = NormalizeToken(Text(effect, "material", "changed")) });
                }

                AddTags(target, Tags(effect, "addTags", Tags(effect, "tags", Array.Empty<string>())));

                var description = Text(effect, "description", Text(effect, "detail", ""));
                if (!string.IsNullOrWhiteSpace(description))
                {
                    target.Set(new DescriptionComponent(description));
                }

                var summary = $"{before} becomes {target.Name}.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "transformEntity",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?>
                    {
                        ["before"] = before,
                        ["after"] = target.Name,
                        ["material"] = target.TryGet<PhysicalComponent>(out var phys) ? phys.Material : null,
                    });
            })
            .ToArray();
}

public sealed class TransformItemOperation : OperationBase
{
    public TransformItemOperation()
        : base(
            "transformItem",
            new[] { "transformFixture", "alterItem" },
            "Change an item, fixture, or object-like entity.",
            "Use for magic acting on reagents, fixtures, doors, signs, props. Fields: target, material, addTags, name.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "selected_target");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        ResolveTargets(context, effect, "selected_target")
            .Select(target =>
            {
                var before = target.Name;
                var name = Text(effect, "name", Text(effect, "newName", ""));
                if (!string.IsNullOrWhiteSpace(name))
                {
                    target.Name = name;
                }

                var materialText = Text(effect, "material", "");
                var material = string.IsNullOrWhiteSpace(materialText) ? "" : NormalizeToken(materialText);
                if (!string.IsNullOrWhiteSpace(material))
                {
                    if (target.TryGet<PhysicalComponent>(out var physical))
                    {
                        target.Set(physical with { Material = material });
                    }

                    if (target.TryGet<ItemComponent>(out var item))
                    {
                        target.Set(item with { Material = material });
                    }
                }

                AddTags(target, Tags(effect, "addTags", Tags(effect, "tags", Array.Empty<string>())));
                var summary = $"{before} changes into {target.Name}.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "transformItem",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?>
                    {
                        ["before"] = before,
                        ["after"] = target.Name,
                        ["material"] = material,
                    });
            })
            .ToArray();
}

public sealed class PossessOperation : OperationBase
{
    public PossessOperation()
        : base(
            "possess",
            new[] { "bodySwap", "soulSwap", "takeBody" },
            "Move the caster's soul into a nearby vulnerable body.",
            "Use for rare possession/body-swap magic. Fields: target. Alert hostile bodies resist; incapacitated bodies can be taken.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect)
    {
        var resolved = ResolveTargetSet(context, effect, "nearest_enemy");
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        var targets = resolved.Entities
            .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .ToArray();
        if (targets.Length == 0)
        {
            return ValidationOutcome.Reject("Possession needs a living target body.");
        }

        if (!context.Engine.CanPossess(targets[0], out var reason))
        {
            return ValidationOutcome.Reject(reason ?? "The target body resists possession.");
        }

        return ValidationOutcome.Pass;
    }

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var target = ResolveTargets(context, effect, "nearest_enemy")
            .FirstOrDefault(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive);
        return target is null
            ? Array.Empty<StateDelta>()
            : context.Engine.PossessEntity(target);
    }
}

public sealed class ChangeFactionOperation : OperationBase
{
    public ChangeFactionOperation()
        : base(
            "changeFaction",
            new[] { "befriend", "turnFaction", "change_faction" },
            "Change an actor's faction allegiance.",
            "Use for charm, command, diplomacy, betrayal. Fields: target, faction.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireActorTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var faction = Text(effect, "faction", "player");
        return ResolveTargets(context, effect, "nearest_enemy")
            .Where(target => target.TryGet<ActorComponent>(out _))
            .Select(target =>
            {
                var actor = target.Get<ActorComponent>();
                target.Set(actor with { Faction = faction });
                target.Set(new FactionComponent(faction, new[] { faction }));
                var summary = $"{target.Name} now answers to {faction}.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "changeFaction",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["faction"] = faction });
            })
            .ToArray();
    }

}

public sealed class AddTraitOperation : OperationBase
{
    public AddTraitOperation()
        : base(
            "addTrait",
            new[] { "tag", "markTrait" },
            "Add one or more descriptive tags to an entity.",
            "Use when magic changes affordances without a bespoke mechanic. Fields: target, trait, traits.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var traits = Tags(effect, "traits", Tags(effect, "tags", Tags(effect, "trait", Array.Empty<string>()))).ToList();

        if (traits.Count == 0)
        {
            traits.Add("marked");
        }

        return ResolveTargets(context, effect, "nearest_enemy")
            .Select(target =>
            {
                AddTags(target, traits);
                var summary = $"{target.Name} gains {string.Join(", ", traits)}.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "addTrait",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["traits"] = traits.ToArray() });
            })
            .ToArray();
    }
}

public sealed class ScheduleEventOperation : OperationBase
{
    public ScheduleEventOperation()
        : base(
            "scheduleEvent",
            new[] { "delay", "futureEvent", "schedule_event" },
            "Schedule a future world event.",
            "Use for delayed consequences, debts, arrivals, curses, or promises. Fields: turns, eventType, text.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        Int(effect, "turns", 3, min: 1, max: 99) >= 1
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("Scheduled events need a positive delay.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var turns = Int(effect, "turns", 3, min: 1, max: 99);
        var eventType = Text(effect, "eventType", Text(effect, "event_type", "wild_magic"));
        var scheduled = context.Engine.State.ScheduledEvents.Schedule(
            context.Engine.State.Turn + turns,
            eventType,
            context.Caster.Id,
            effect.Fields);
        var summary = $"Something is scheduled for turn {scheduled.DueTurn}: {eventType}.";
        context.Engine.AddMessage(summary);
        return new[]
        {
            new StateDelta(
                "scheduleEvent",
                scheduled.Id,
                summary,
                effect.Fields),
        };
    }
}

public sealed class AddCurseOperation : OperationBase
{
    public AddCurseOperation()
        : base(
            "addCurse",
            new[] { "curse", "debt" },
            "Add a visible debt or curse promise.",
            "Use when magic creates an enduring obligation. Fields: name, description.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        ValidationOutcome.Pass;

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var text = Text(effect, "description", Text(effect, "name", "Wild Debt"));
        var anchor = ResolveTargets(context, effect, "selected_target").FirstOrDefault();
        var triggerHint = Text(effect, "trigger", Text(effect, "triggerHint", Text(effect, "trigger_hint", "")));
        return new[] { context.Engine.AddPromise("debt", text, anchor, triggerHint) };
    }
}

public sealed class CreatePromiseOperation : OperationBase
{
    public CreatePromiseOperation()
        : base(
            "createPromise",
            new[] { "promise", "prophecy", "omen" },
            "Add a prophecy, promise, oath, debt, or omen to the world.",
            "Use for narrative hooks that the world can remember. Fields: kind, text.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        !string.IsNullOrWhiteSpace(Text(effect, "text", ""))
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("A promise needs text.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var anchor = ResolveTargets(context, effect, "selected_target").FirstOrDefault();
        var triggerHint = Text(effect, "trigger", Text(effect, "triggerHint", Text(effect, "trigger_hint", "")));
        return new[]
        {
            context.Engine.AddPromise(
                Text(effect, "kind", "omen"),
                Text(effect, "text", "Something has been promised."),
                anchor,
                triggerHint),
        };
    }
}

public sealed class MessageOperation : OperationBase
{
    public MessageOperation()
        : base(
            "message",
            Array.Empty<string>(),
            "Add a visible outcome message.",
            "Use sparingly as flavor alongside mechanical effects. Fields: text.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        !string.IsNullOrWhiteSpace(Text(effect, "text", ""))
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("Message effects need text.");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect) =>
        new[] { Message(context, "message", "", Text(effect, "text", "")) };
}

internal static class OperationHelpers
{
    public static ResolvedEntitySet ResolveTargetSet(EffectContext context, SpellEffect effect, string fallback) =>
        context.Refs.Resolve(OperationBaseAccess.TargetRef(effect, fallback));

    public static IReadOnlyList<Entity> ResolveTargets(EffectContext context, SpellEffect effect, string fallback)
    {
        var resolved = ResolveTargetSet(context, effect, fallback);
        return resolved.Entities.Take(context.GroupTargetCap).ToArray();
    }

    public static ValidationOutcome RequireOrigin(
        EffectContext context,
        SpellEffect effect,
        string fallback,
        string rejectReason)
    {
        if (TryPoint(effect, out _))
        {
            return ValidationOutcome.Pass;
        }

        var resolved = ResolveTargetSet(context, effect, fallback);
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        if (resolved.Position is not null
            || resolved.Entities.Any(entity => entity.TryGet<PositionComponent>(out _)))
        {
            return ValidationOutcome.Pass;
        }

        return ValidationOutcome.Reject(resolved.Error ?? rejectReason);
    }

    public static GridPoint? ResolveOrigin(EffectContext context, SpellEffect effect, string fallback)
    {
        if (TryPoint(effect, out var explicitPoint))
        {
            return explicitPoint;
        }

        var resolved = ResolveTargetSet(context, effect, fallback);
        if (resolved.Position is { } point)
        {
            return point;
        }

        var entity = resolved.Entities.FirstOrDefault();
        return entity is not null && entity.TryGet<PositionComponent>(out var position)
            ? position.Position
            : null;
    }

    public static GridPoint? Destination(EffectContext context, SpellEffect effect)
    {
        if (TryPoint(effect, out var explicitPoint))
        {
            return explicitPoint;
        }

        var target = context.Refs.Resolve(OperationBaseAccess.TargetRef(effect, "selected_target"));
        return target.Position;
    }

    public static ValidationOutcome RequireActorTargets(EffectContext context, SpellEffect effect, string fallback)
    {
        var resolved = ResolveTargetSet(context, effect, fallback);
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        var targets = resolved.Entities.Take(context.GroupTargetCap).ToArray();
        return targets.Length > 0 && targets.All(target => target.TryGet<ActorComponent>(out _))
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("Spell needs one or more actor targets.");
    }

    public static ValidationOutcome RequirePositionedTargets(EffectContext context, SpellEffect effect, string fallback)
    {
        var resolved = ResolveTargetSet(context, effect, fallback);
        if (IsMalformedTarget(resolved))
        {
            return ValidationOutcome.Technical(resolved.Error ?? "Malformed target reference.");
        }

        var targets = resolved.Entities.Take(context.GroupTargetCap).ToArray();
        return targets.Length > 0 && targets.All(target => target.TryGet<PositionComponent>(out _))
            ? ValidationOutcome.Pass
            : ValidationOutcome.Reject("Spell needs one or more positioned targets.");
    }

    public static bool IsMalformedTarget(ResolvedEntitySet resolved) =>
        resolved.Reference.Kind.Equals("malformed", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<StateDelta> MoveLinearly(EffectContext context, SpellEffect effect, bool away)
    {
        var origin = ResolveOrigin(context, effect, "self")
            ?? context.Caster.Get<PositionComponent>().Position;
        var distance = OperationBaseAccess.Int(effect, "distance", 1, min: 1, max: 5);
        return ResolveTargets(context, effect, "nearest_enemy")
            .Where(target => target.TryGet<PositionComponent>(out _))
            .Select(target =>
            {
                var current = target.Get<PositionComponent>().Position;
                var dx = Math.Sign(current.X - origin.X);
                var dy = Math.Sign(current.Y - origin.Y);
                if (!away)
                {
                    dx *= -1;
                    dy *= -1;
                }

                if (dx == 0 && dy == 0)
                {
                    dx = away ? 1 : -1;
                }

                var destination = current.Translate(dx * distance, dy * distance);
                return context.Engine.MoveEntity(target, destination, away ? "push" : "pull");
            })
            .ToArray();
    }

    public static StateDelta Message(EffectContext context, string operation, string target, string text)
    {
        context.Engine.AddMessage(text);
        return new StateDelta(operation, target, text, new Dictionary<string, object?>());
    }

    public static GridPoint? FindOpenAdjacent(EffectContext context, GridPoint origin)
    {
        var offsets = new[]
        {
            new GridPoint(0, -1),
            new GridPoint(1, 0),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, -1),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(-1, -1),
        };

        foreach (var offset in offsets)
        {
            var candidate = origin.Translate(offset.X, offset.Y);
            if (context.Engine.InBounds(candidate)
                && !context.Engine.State.BlockingTerrain.Contains(candidate)
                && context.Engine.BlockingEntityAt(candidate) is null)
            {
                return candidate;
            }
        }

        return null;
    }

    public static string NormalizeTerrain(string value) =>
        NormalizeToken(string.IsNullOrWhiteSpace(value) ? "wild_growth" : value);

    public static string NormalizeToken(string value, string fallback = "marked")
    {
        var cleaned = string.Join(
            '_',
            (value ?? "").Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    public static IReadOnlyList<string> Tags(SpellEffect effect, string key, IReadOnlyList<string> fallback)
    {
        if (!effect.Fields.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }

        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            return enumerable.Cast<object?>()
                .Select(value => NormalizeToken(Convert.ToString(value) ?? ""))
                .Where(value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        var text = Convert.ToString(raw) ?? "";
        return text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => NormalizeToken(value))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static void AddTags(Entity target, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0)
        {
            return;
        }

        var current = target.TryGet<TagsComponent>(out var existing)
            ? existing.Tags.ToList()
            : new List<string>();
        foreach (var tag in tags)
        {
            if (!current.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(tag);
            }
        }

        target.Set(new TagsComponent(current));
    }

    public static int GameEngineDistance(GridPoint a, GridPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static bool TryPoint(SpellEffect effect, out GridPoint point)
    {
        if (TryPoint(effect.Fields, out point))
        {
            return true;
        }

        if (effect.Fields.TryGetValue("target", out var rawTarget)
            && rawTarget is IReadOnlyDictionary<string, object?> targetFields
            && TryPoint(targetFields, out point))
        {
            return true;
        }

        point = default;
        return false;
    }

    private static bool TryPoint(IReadOnlyDictionary<string, object?> fields, out GridPoint point)
    {
        if (TryIntField(fields, "x", out var x)
            && TryIntField(fields, "y", out var y))
        {
            point = new GridPoint(x, y);
            return true;
        }

        point = default;
        return false;
    }

    private static bool TryIntField(IReadOnlyDictionary<string, object?> fields, string key, out int value)
    {
        value = 0;
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            raw = enumerable.Cast<object?>().FirstOrDefault();
        }

        return int.TryParse(Convert.ToString(raw), out value);
    }
}

internal static class OperationBaseAccess
{
    public static EntityRef TargetRef(SpellEffect effect, string fallback)
    {
        var radius = effect.Fields.TryGetValue("radius", out var rawRadius)
            && int.TryParse(Convert.ToString(rawRadius), out var parsedRadius)
                ? parsedRadius
                : (int?)null;
        var target = effect.Fields.TryGetValue("target", out var value) ? value : fallback;
        return Sorcerer.Core.References.ReferenceBinder.NormalizeEntityRef(target, radius);
    }

    public static int Int(SpellEffect effect, string key, int fallback = 0, int min = int.MinValue, int max = int.MaxValue)
    {
        var value = effect.Fields.TryGetValue(key, out var raw)
            && int.TryParse(Convert.ToString(raw), out var parsed)
                ? parsed
                : fallback;
        return Math.Clamp(value, min, max);
    }
}
