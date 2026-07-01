using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;
using Sorcerer.Magic.Resolution;
using static Sorcerer.Magic.Operations.OperationHelpers;

namespace Sorcerer.Magic.Operations;

/// <summary>
/// Phase B, group 2 of the Wild Magic resolver port: operations that need one new
/// component/ledger field or a small template table, reusing existing engine systems.
/// See docs/MAGIC_RESOLVER_ARCHITECTURE.md.
/// </summary>
public sealed class ConjureItemOperation : OperationBase
{
    private static readonly IReadOnlyDictionary<string, (char Glyph, string Material, string ItemType, string[] Tags, int Value)> Templates =
        new Dictionary<string, (char, string, string, string[], int)>(StringComparer.OrdinalIgnoreCase)
        {
            ["generic_object"] = ('*', "matter", "curio", new[] { "conjured" }, 3),
            ["body_part"] = ('%', "flesh", "curio", new[] { "conjured", "grisly" }, 5),
            ["glass_shard"] = ('/', "glass", "reagent", new[] { "conjured", "sharp" }, 6),
            ["ritual_component"] = ('&', "ash", "reagent", new[] { "conjured", "ritual" }, 8),
            ["weapon_like"] = ('/', "iron", "weapon", new[] { "conjured", "weapon" }, 10),
            ["food"] = ('%', "bread", "food", new[] { "conjured", "food" }, 2),
            ["key_like"] = ('k', "iron", "key", new[] { "conjured", "key" }, 4),
            ["treasure"] = ('$', "gold", "currency", new[] { "conjured", "treasure" }, 15),
        };

    public ConjureItemOperation()
        : base(
            "conjureItem",
            new[] { "createItem", "conjure_item" },
            "Create a conjured floor item from a template.",
            "Fields: template (generic_object, body_part, glass_shard, ritual_component, weapon_like, food, key_like, treasure), name, material, count (1-20), tags.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) => ValidationOutcome.Pass;

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var template = Text(effect, "template", "generic_object").Trim().ToLowerInvariant();
        var preset = Templates.TryGetValue(template, out var found) ? found : Templates["generic_object"];
        var origin = ResolveOrigin(context, effect, "self") ?? context.Caster.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(context, origin) ?? origin;
        var name = Text(effect, "name", template.Replace('_', ' '));
        var material = NormalizeToken(Text(effect, "material", preset.Material));
        var count = Int(effect, "count", 1, min: 1, max: 20);
        var tags = Tags(effect, "tags", preset.Tags)
            .Concat(new[] { "conjured" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var entity = context.Engine.SpawnItem(
            NormalizeToken(name, "curio"),
            name,
            preset.Glyph,
            position,
            preset.ItemType,
            material,
            tags,
            count,
            preset.Value);
        var summary = count > 1 ? $"{count} {name} appear." : $"{name} appears.";
        context.Engine.AddMessage(summary);
        return new[]
        {
            new StateDelta(
                "conjureItem",
                entity.Id.Value,
                summary,
                new Dictionary<string, object?> { ["template"] = template, ["count"] = count }),
        };
    }
}

public sealed class ConjureCreatureOperation : OperationBase
{
    private static readonly IReadOnlyDictionary<string, (char Glyph, int Hp, int Attack, string[] Tags)> Templates =
        new Dictionary<string, (char, int, int, string[])>(StringComparer.OrdinalIgnoreCase)
        {
            ["tiny_swarm"] = ('s', 3, 1, new[] { "swarm", "tiny" }),
            ["small_beast"] = ('b', 6, 2, new[] { "beast" }),
            ["humanoid"] = ('h', 8, 3, new[] { "humanoid" }),
            ["construct"] = ('c', 12, 3, new[] { "construct" }),
            ["spirit"] = ('g', 5, 2, new[] { "spirit", "incorporeal" }),
            ["slime"] = ('o', 7, 2, new[] { "slime" }),
            ["summoned_servant"] = ('@', 8, 2, new[] { "servant" }),
            ["hazard_creature"] = ('*', 4, 4, new[] { "hazard", "stationary" }),
        };

    public ConjureCreatureOperation()
        : base(
            "conjureCreature",
            new[] { "conjure_creature" },
            "Create one or more template-backed creatures.",
            "Fields: template (tiny_swarm, small_beast, humanoid, construct, spirit, slime, summoned_servant, hazard_creature), name, faction, tags, count (1-12).",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) => ValidationOutcome.Pass;

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var template = Text(effect, "template", "small_beast").Trim().ToLowerInvariant();
        var preset = Templates.TryGetValue(template, out var found) ? found : Templates["small_beast"];
        var origin = ResolveOrigin(context, effect, "self") ?? context.Caster.Get<PositionComponent>().Position;
        var name = Text(effect, "name", template.Replace('_', ' '));
        var faction = Text(effect, "faction", "player");
        var count = Int(effect, "count", 1, min: 1, max: 12);
        var tags = Tags(effect, "tags", preset.Tags)
            .Concat(new[] { "conjured", "wild_magic" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var deltas = new List<StateDelta>();
        for (var index = 0; index < count; index++)
        {
            var position = FindOpenAdjacent(context, origin);
            if (position is null)
            {
                break;
            }

            var entity = context.Engine.SpawnEntity(
                NormalizeToken(name, "conjured"),
                name,
                preset.Glyph,
                position.Value,
                faction,
                preset.Hp,
                preset.Attack,
                tags);
            entity.Set(new SummonedComponent(context.Caster.Id.Value));
            var summary = $"{name} appears at {position.Value.X},{position.Value.Y}.";
            context.Engine.AddMessage(summary);
            deltas.Add(new StateDelta(
                "conjureCreature",
                entity.Id.Value,
                summary,
                new Dictionary<string, object?> { ["template"] = template, ["faction"] = faction }));
        }

        if (deltas.Count == 0)
        {
            deltas.Add(Message(context, "conjureCreature", "", "The conjuration has nowhere to stand."));
        }

        return deltas;
    }
}

public sealed class AddResistanceOperation : OperationBase
{
    public AddResistanceOperation()
        : base(
            "addResistance",
            new[] { "resist", "add_resistance" },
            "Grant a target resistance to a damage type.",
            "Fields: target, damageType, amount (0-95, percent reduction).")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "self");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var damageType = NormalizeToken(Text(effect, "damageType", Text(effect, "damage_type", "physical")));
        var amount = Int(effect, "amount", 25, min: 0, max: 95);
        return ResolveTargets(context, effect, "self")
            .Select(target =>
            {
                var resistance = target.TryGet<ResistanceComponent>(out var existing) ? existing : ResistanceComponent.Empty();
                resistance.Resistances[damageType] = amount;
                target.Set(resistance);
                var summary = $"{Subject(context, target)} {Verb(context, target, "resist", "resists")} {damageType.Replace('_', ' ')} damage by {amount}%.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "addResistance",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["damageType"] = damageType, ["amount"] = amount });
            })
            .ToArray();
    }
}

public sealed class AddWeaknessOperation : OperationBase
{
    public AddWeaknessOperation()
        : base(
            "addWeakness",
            new[] { "vulnerable", "add_weakness" },
            "Grant a target vulnerability to a damage type.",
            "Fields: target, damageType, amount (0-200, percent amplification).")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var damageType = NormalizeToken(Text(effect, "damageType", Text(effect, "damage_type", "physical")));
        var amount = Int(effect, "amount", 50, min: 0, max: 200);
        return ResolveTargets(context, effect, "nearest_enemy")
            .Select(target =>
            {
                var resistance = target.TryGet<ResistanceComponent>(out var existing) ? existing : ResistanceComponent.Empty();
                resistance.Weaknesses[damageType] = amount;
                target.Set(resistance);
                var summary = $"{Subject(context, target)} {Verb(context, target, "grow", "grows")} vulnerable to {damageType.Replace('_', ' ')} damage (+{amount}%).";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "addWeakness",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["damageType"] = damageType, ["amount"] = amount });
            })
            .ToArray();
    }
}

public sealed class SetFlagOperation : OperationBase
{
    private static readonly string[] DebtKeywords = { "debt", "owed", "owe", "price", "reckoning" };

    public SetFlagOperation()
        : base(
            "setFlag",
            new[] { "flag", "worldFlag", "set_flag" },
            "Set a persistent world flag.",
            "Fields: flag, value, description. A flag whose id/description reads as a debt (debt, owed, price, reckoning) automatically stacks a wild_debt curse and schedules a future reckoning.")
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        string.IsNullOrWhiteSpace(Text(effect, "flag", Text(effect, "id", "")))
            ? ValidationOutcome.Reject("setFlag needs a flag id.")
            : ValidationOutcome.Pass;

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var flag = NormalizeToken(Text(effect, "flag", Text(effect, "id", "marked")));
        var description = Text(effect, "description", flag.Replace('_', ' '));
        var value = effect.Fields.TryGetValue("value", out var raw) && raw is not null ? raw : true;
        context.Engine.State.WorldFlags[flag] = value;
        var summary = $"A world flag is set: {description}.";
        context.Engine.AddMessage(summary);
        var deltas = new List<StateDelta>
        {
            new StateDelta("setFlag", flag, summary, new Dictionary<string, object?> { ["flag"] = flag, ["value"] = value }),
        };

        if (LooksLikeDebt(flag, description))
        {
            deltas.AddRange(IncurWildDebt(context, description));
        }

        return deltas;
    }

    private static bool LooksLikeDebt(string flag, string description) =>
        DebtKeywords.Any(keyword =>
            flag.Contains(keyword, StringComparison.OrdinalIgnoreCase)
            || description.Contains(keyword, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<StateDelta> IncurWildDebt(EffectContext context, string description)
    {
        var deltas = new List<StateDelta>();
        var text = $"Wild Debt: {description}";
        var existing = context.Engine.State.PromiseLedger.FindActive("curse", text, boundTargetId: null);
        if (existing is not null)
        {
            var stacked = context.Engine.State.PromiseLedger.Stack(existing.Id);
            var stackMessage = $"{text} deepens ({stacked.Stacks} stacks).";
            context.Engine.AddMessage(stackMessage);
            deltas.Add(new StateDelta(
                "addCurse",
                stacked.Id,
                stackMessage,
                new Dictionary<string, object?> { ["stacks"] = stacked.Stacks }));
        }
        else
        {
            deltas.Add(context.Engine.AddPromise("curse", text));
        }

        var turnsOut = context.Engine.State.Rng.NextInt(8, 16);
        var scheduled = context.Engine.State.ScheduledEvents.Schedule(
            context.Engine.State.Turn + turnsOut,
            "debt_collector",
            context.Caster.Id,
            new Dictionary<string, object?> { ["text"] = $"A debt collector arrives, drawn by an old wild debt: {description}." });
        var scheduleMessage = $"Something is scheduled for turn {scheduled.DueTurn}: debt_collector.";
        context.Engine.AddMessage(scheduleMessage);
        deltas.Add(new StateDelta("scheduleEvent", scheduled.Id, scheduleMessage, new Dictionary<string, object?>()));

        return deltas;
    }
}

public sealed class DelayIncomingOperation : OperationBase
{
    public DelayIncomingOperation()
        : base(
            "delayIncoming",
            new[] { "delayDamage", "delay_incoming" },
            "Capture incoming damage into a buffer that releases later instead of applying it immediately.",
            "Fields: target, turns (1-20).",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "self");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var turns = Int(effect, "turns", 3, min: 1, max: 20);
        return ResolveTargets(context, effect, "self")
            .Select(target =>
            {
                target.Set(new DelayedDamageComponent(0, context.Engine.State.Turn + turns));
                var summary = $"{Possessive(context, target)} wounds are held back for {turns} turns.";
                context.Engine.AddMessage(summary);
                return new StateDelta(
                    "delayIncoming",
                    target.Id.Value,
                    summary,
                    new Dictionary<string, object?> { ["turns"] = turns });
            })
            .ToArray();
    }
}

public sealed class EditMemoryOperation : OperationBase
{
    public EditMemoryOperation()
        : base(
            "editMemory",
            new[] { "memoryEdit", "edit_memory" },
            "Add, alter, or erase a nearby NPC's memory.",
            "Fields: target, op (add/remove/alter), subject ('the caster' means the player), text, strength (1-5). Removing the caster from a hostile NPC's memory also calms it. Major magic: pair with a real cost.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) =>
        RequireTargets(context, effect, "nearest_enemy");

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var op = Text(effect, "op", "add").Trim().ToLowerInvariant();
        var subject = Text(effect, "subject", "");
        var text = Text(effect, "text", subject);
        var strength = Int(effect, "strength", 3, min: 1, max: 5);
        var aboutCaster = subject.Contains("caster", StringComparison.OrdinalIgnoreCase)
            || subject.Contains("player", StringComparison.OrdinalIgnoreCase)
            || subject.Equals("me", StringComparison.OrdinalIgnoreCase);

        return ResolveTargets(context, effect, "nearest_enemy")
            .Select(target => op switch
            {
                "remove" => RemoveMemory(context, target, subject, aboutCaster),
                "alter" => AddMemory(target, context, text, strength, "altered by wild magic"),
                _ => AddMemory(target, context, text, strength, "planted by wild magic"),
            })
            .ToArray();
    }

    private static StateDelta AddMemory(Entity target, EffectContext context, string text, int strength, string provenance)
    {
        var trimmed = string.IsNullOrWhiteSpace(text) ? "something that did not happen" : text;
        context.Engine.State.Memories.Append(target.Id.Value, trimmed, provenance, strength, shareable: strength >= 4);
        var existing = target.TryGet<MemoryComponent>(out var memory) ? memory.Records.ToList() : new List<EntityMemoryRecord>();
        existing.Add(new EntityMemoryRecord($"editMemory_{existing.Count + 1}", trimmed, "wild_magic", provenance, strength, Shareable: strength >= 4));
        target.Set(new MemoryComponent(existing));
        var summary = $"{Possessive(context, target)} memory shifts: {trimmed}";
        context.Engine.AddMessage(summary);
        return new StateDelta(
            "editMemory",
            target.Id.Value,
            summary,
            new Dictionary<string, object?> { ["op"] = "add", ["text"] = trimmed });
    }

    private static StateDelta RemoveMemory(EffectContext context, Entity target, string subject, bool aboutCaster)
    {
        if (target.TryGet<MemoryComponent>(out var memory))
        {
            var remaining = memory.Records
                .Where(record => !RecordMentionsCaster(record, subject, aboutCaster))
                .ToArray();
            target.Set(new MemoryComponent(remaining));
        }

        var summary = aboutCaster
            ? $"{Subject(context, target)} no longer {Verb(context, target, "remember", "remembers")} the caster; the hostility drains out of them."
            : $"{Possessive(context, target)} memory of {subject} fades.";

        if (aboutCaster && target.TryGet<SoulComponent>(out var npcSoul))
        {
            var playerSoulId = context.Engine.State.ControlledEntity.TryGet<SoulComponent>(out var playerSoul)
                ? playerSoul.SoulId
                : context.Engine.State.ControlledEntityId.Value;
            var bond = context.Engine.State.Bonds.GetOrCreate(npcSoul.SoulId, playerSoulId);
            context.Engine.State.Bonds.Set(bond with { Loyalty = Math.Max(bond.Loyalty, 5) });
        }

        context.Engine.AddMessage(summary);
        return new StateDelta(
            "editMemory",
            target.Id.Value,
            summary,
            new Dictionary<string, object?> { ["op"] = "remove", ["subject"] = subject });
    }

    private static bool RecordMentionsCaster(EntityMemoryRecord record, string subject, bool aboutCaster)
    {
        if (aboutCaster)
        {
            return record.Text.Contains("caster", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.Contains("wild_magic", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(subject) && record.Text.Contains(subject, StringComparison.OrdinalIgnoreCase);
    }
}
