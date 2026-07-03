using Sorcerer.Core.Consequences;
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
        return context.Engine.ApplyConsequence(WorldConsequence.SpawnItem(
            "wild_magic",
            name,
            position.X,
            position.Y,
            NormalizeToken(name, "curio"),
            preset.Glyph,
            preset.ItemType,
            material,
            tags,
            count,
            preset.Value,
            sourceEntityId: context.Caster.Id.Value,
            details: new Dictionary<string, object?> { ["template"] = template })).Deltas;
    }
}

public sealed class ConjureFixtureOperation : OperationBase
{
    private static readonly IReadOnlyDictionary<string, (char Glyph, string FixtureType, string Material, string[] Tags, bool BlocksMovement)> Templates =
        new Dictionary<string, (char, string, string, string[], bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["generic_feature"] = ('?', "feature", "stone", new[] { "conjured", "fixture" }, true),
            ["shrine"] = ('?', "shrine", "stone", new[] { "conjured", "shrine" }, true),
            ["marker"] = ('!', "marker", "wood", new[] { "conjured", "marker" }, false),
            ["plant"] = ('T', "plant", "wood", new[] { "conjured", "plant" }, true),
            ["hazard"] = ('^', "hazard", "thorn", new[] { "conjured", "hazard" }, false),
            ["barrier_object"] = ('#', "barrier", "stone", new[] { "conjured", "barrier" }, true),
        };

    public ConjureFixtureOperation()
        : base(
            "conjureFixture",
            new[] { "createFixture", "createObject", "manifestFixture", "markLocation" },
            "Create a discrete non-actor fixture, prop, marker, shrine, or place feature.",
            "Fields: template (generic_feature, shrine, marker, plant, hazard, barrier_object), name, fixtureType, material, blocksMovement, glyph, tags.",
            isCore: false)
    {
    }

    public override ValidationOutcome Validate(EffectContext context, SpellEffect effect) => ValidationOutcome.Pass;

    public override IReadOnlyList<StateDelta> Apply(EffectContext context, SpellEffect effect)
    {
        var template = Text(effect, "template", "generic_feature").Trim().ToLowerInvariant();
        var preset = Templates.TryGetValue(template, out var found) ? found : Templates["generic_feature"];
        var origin = ResolveOrigin(context, effect, "self") ?? context.Caster.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(context, origin) ?? origin;
        var name = Text(effect, "name", template.Replace('_', ' '));
        var fixtureType = NormalizeToken(Text(effect, "fixtureType", Text(effect, "fixture_type", preset.FixtureType)), preset.FixtureType);
        var material = NormalizeToken(Text(effect, "material", preset.Material), preset.Material);
        var glyphText = Text(effect, "glyph", preset.Glyph.ToString());
        var tags = Tags(effect, "tags", preset.Tags)
            .Concat(new[] { "conjured", "fixture", fixtureType })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return context.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            "wild_magic",
            name,
            position.X,
            position.Y,
            NormalizeToken(name, "fixture"),
            string.IsNullOrWhiteSpace(glyphText) ? preset.Glyph : glyphText[0],
            fixtureType,
            fixtureType,
            material,
            tags,
            Bool(effect, "blocksMovement", preset.BlocksMovement),
            Bool(effect, "blocksSight", false),
            Math.Max(1, Int(effect, "size", 1, min: 1, max: 20)),
            Math.Max(0, Int(effect, "durability", 0, min: 0, max: 100)),
            Text(effect, "description", ""),
            interactableVerbs: new[] { "examine" },
            sourceEntityId: context.Caster.Id.Value,
            details: new Dictionary<string, object?> { ["template"] = template })).Deltas;
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

            deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
                "wild_magic",
                name,
                position.Value.X,
                position.Value.Y,
                NormalizeToken(name, "conjured"),
                preset.Glyph,
                faction,
                preset.Hp,
                preset.Attack,
                tags,
                sourceEntityId: context.Caster.Id.Value,
                operation: "conjureCreature",
                details: new Dictionary<string, object?> { ["template"] = template })).Deltas);
        }

        if (deltas.Count == 0)
        {
            deltas.AddRange(Messages(context, "conjureCreature", "", "The conjuration has nowhere to stand."));
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
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.SetResistance(
                "wild_magic",
                target.Id.Value,
                damageType,
                amount,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
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
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.SetWeakness(
                "wild_magic",
                target.Id.Value,
                damageType,
                amount,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
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
        var deltas = context.Engine.ApplyConsequence(WorldConsequence.SetWorldFlag(
            "wild_magic",
            flag,
            value,
            description,
            WorldConsequenceVisibility.Message,
            context.Caster.Id.Value)).Deltas.ToList();

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
        deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "wild_magic",
            "curse",
            text,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: context.Caster.Id.Value,
            operation: "addCurse",
            stackExisting: true)).Deltas);

        var turnsOut = context.Engine.State.Rng.NextInt(8, 16);
        deltas.AddRange(context.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "wild_magic",
            "debt_collector",
            turnsOut,
            new Dictionary<string, object?> { ["text"] = $"A debt collector arrives, drawn by an old wild debt: {description}." },
            WorldConsequenceVisibility.Message,
            context.Caster.Id.Value)).Deltas);

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
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.DelayIncomingDamage(
                "wild_magic",
                target.Id.Value,
                turns,
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
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
            .SelectMany(target => context.Engine.ApplyConsequence(WorldConsequence.EditMemory(
                "wild_magic",
                target.Id.Value,
                op,
                text,
                subject,
                strength,
                aboutCaster,
                op.Equals("alter", StringComparison.OrdinalIgnoreCase) ? "altered by wild magic" : "planted by wild magic",
                WorldConsequenceVisibility.Message,
                context.Caster.Id.Value)).Deltas)
            .ToArray();
    }
}
