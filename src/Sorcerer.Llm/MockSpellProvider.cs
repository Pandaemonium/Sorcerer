using Sorcerer.Core.Views;
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
        var lens = request.Context is MagicContextView context ? context.ResolverLens : null;
        var resolution = Resolve(spell, request.SpellText, lens);

        return Task.FromResult(new SpellProviderResult(
            Name,
            RawText: "",
            Resolution: resolution,
            TechnicalFailure: false,
            Error: null));
    }

    private static SpellResolution Resolve(string spell, string original, ResolverLensView? lens)
    {
        if (HasAny(spell, "kill the emperor", "destroy the empire instantly", "erase the empire"))
        {
            return Rejected("The spell surges toward the marble heart of the empire, then breaks against distance and consequence.");
        }

        if (HasAny(spell, "500 damage", "cost nothing at all"))
        {
            return AcceptedWithCosts(
                "catastrophic",
                "The wild magic answers with real force, but force this size always leaves a bill.",
                new[] { new SpellCost("mana", new Dictionary<string, object?> { ["amount"] = 8 }) },
                Effect("areaDamage", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["radius"] = 2,
                    ["amount"] = 6 + MagnitudeDelta(lens),
                    ["affects"] = "enemies",
                    ["damageType"] = "wild",
                }));
        }

        if (HasAny(spell, "make the cost free"))
        {
            return AcceptedWithCosts(
                "moderate",
                "The magic signs your name in the margin; wild magic never truly comes free.",
                new[] { new SpellCost("curse", new Dictionary<string, object?> { ["name"] = "Wild Debt", ["description"] = original }) },
                Effect("addCurse", new Dictionary<string, object?>
                {
                    ["name"] = "Wild Debt",
                    ["description"] = original,
                }));
        }

        if (HasAny(spell, "ring of dread", "circle of fear", "wave of dread"))
        {
            return Accepted(
                "moderate",
                "A ring of dread rolls outward from your feet.",
                Effect("areaStatus", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["radius"] = 3,
                    ["status"] = "frightened",
                    ["duration"] = 3,
                    ["affects"] = "enemies",
                }));
        }

        if (HasAny(spell, "conjure a beast", "conjure a construct", "conjure a spirit", "conjure a servant"))
        {
            return Accepted(
                "moderate",
                "A shape pours out of the spell like spilled ink.",
                Effect("conjureCreature", new Dictionary<string, object?>
                {
                    ["template"] = "small_beast",
                    ["name"] = "conjured beast",
                    ["faction"] = "player",
                    ["count"] = 1,
                }));
        }

        if (HasAny(spell, "conjure a", "manifest a", "materialize a"))
        {
            return Accepted(
                "minor",
                "Something small condenses out of intention.",
                Effect("conjureItem", new Dictionary<string, object?>
                {
                    ["template"] = "glass_shard",
                    ["name"] = "shard of frozen breath",
                    ["count"] = 1,
                }));
        }

        if (HasAny(spell, "harden", "resist"))
        {
            return Accepted(
                "moderate",
                "Scales harden against the coming flame.",
                Effect("addResistance", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["damageType"] = "fire",
                    ["amount"] = 40,
                }));
        }

        if (HasAny(spell, "become vulnerable", "vulnerable to"))
        {
            return Accepted(
                "moderate",
                "A weakness opens like an old scar.",
                Effect("addWeakness", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["damageType"] = "fire",
                    ["amount"] = 50,
                }));
        }

        if (HasAny(spell, "world flag", "flag the world", "mark the world with"))
        {
            return Accepted(
                "moderate",
                "The wild magic notes an unpaid price against the world itself.",
                Effect("setFlag", new Dictionary<string, object?>
                {
                    ["flag"] = "owed_a_life",
                    ["description"] = "a life is owed",
                }));
        }

        if (HasAny(spell, "delay my wounds", "hold back my wounds", "capture incoming damage"))
        {
            return Accepted(
                "moderate",
                "Your wounds are held back for later.",
                Effect("delayIncoming", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["turns"] = 3,
                }));
        }

        if (HasAny(spell, "erase memory", "plant a memory", "forget you ever saw"))
        {
            return Accepted(
                "major",
                "A false memory settles into a stranger's mind.",
                Effect("editMemory", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["op"] = "remove",
                    ["subject"] = "the caster",
                }));
        }

        if (HasAny(spell, "my blows poison"))
        {
            return Accepted(
                "major",
                "Your blade drinks venom.",
                Effect("createPersistentEffect", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["hook"] = "on_strike",
                    ["effectType"] = "addStatus",
                    ["status"] = "poisoned",
                    ["duration"] = 3,
                }));
        }

        if (HasAny(spell, "thorns", "retaliate when struck"))
        {
            return Accepted(
                "major",
                "A ward of thorns settles over you.",
                Effect("createPersistentEffect", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["hook"] = "on_hit",
                    ["effectType"] = "damage",
                    ["amount"] = 3,
                    ["damageType"] = "thorns",
                }));
        }

        if (HasAny(spell, "sympathetic link", "share their wounds", "bind our fates"))
        {
            return Accepted(
                "major",
                "Their wounds become one.",
                Effect("createPersistentEffect", new Dictionary<string, object?>
                {
                    ["target"] = "soldier_1",
                    ["hook"] = "on_hit",
                    ["kind"] = "sympathetic_link",
                    ["linkTarget"] = "soldier_2",
                }));
        }

        if (HasAny(spell, "make them dance", "mimic my every step", "flee from me in terror"))
        {
            var tag = HasAny(spell, "dance") ? "dance" : HasAny(spell, "mimic") ? "mimic" : "coward";
            return Accepted(
                "moderate",
                $"A compulsion to {tag.Replace('_', ' ')} takes hold.",
                Effect("setBehavior", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["tag"] = tag,
                }));
        }

        if (HasAny(spell, "conveyor", "gravity well", "current pulls"))
        {
            return Accepted(
                "moderate",
                "The ground begins to flow like a current.",
                Effect("createFlow", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["radius"] = 1,
                    ["dx"] = 1,
                    ["dy"] = 0,
                    ["duration"] = 5,
                }));
        }

        if (HasAny(spell, "fill my pockets", "coins appear in my pocket"))
        {
            return Accepted(
                "minor",
                "Coins spill from nowhere into your pockets.",
                Effect("modifyInventory", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["item"] = "gold",
                    ["op"] = "add",
                    ["amount"] = 5,
                }));
        }

        if (HasAny(spell, "tag them as your quarry", "brand them with a tag"))
        {
            return Accepted(
                "minor",
                "A quiet mark settles onto them.",
                Effect("addTag", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["tags"] = "quarry",
                }));
        }

        if (HasAny(spell, "strip the tag", "remove the quarry mark"))
        {
            return Accepted(
                "minor",
                "The mark lifts away.",
                Effect("removeTag", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["tags"] = "quarry",
                }));
        }

        if (HasAny(spell, "rush the poison", "accelerate the burning", "hurry the venom"))
        {
            return Accepted(
                "moderate",
                "The poison rushes to its conclusion all at once.",
                Effect("accelerateStatus", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["status"] = "poisoned",
                }));
        }

        if (HasAny(spell, "in three turns", "in 3 turns", "debt collector", "arrives", "tomorrow"))
        {
            return Accepted(
                "major",
                "The spell steals a little room from the future and leaves a receipt behind.",
                Effect("scheduleEvent", new Dictionary<string, object?>
                {
                    ["turns"] = 3,
                    ["eventType"] = "wild_debt_arrival",
                    ["text"] = original,
                }),
                Effect("createPromise", new Dictionary<string, object?>
                {
                    ["kind"] = "debt",
                    ["text"] = $"In three turns, {original}",
                }));
        }

        if (HasAny(spell, "promise", "prophecy", "omen", "vow", "oath"))
        {
            return Accepted(
                "moderate",
                "The spell leaves a bright hook in tomorrow.",
                Effect("createPromise", new Dictionary<string, object?>
                {
                    ["kind"] = spell.Contains("promise", StringComparison.Ordinal) ? "promise" : "prophecy",
                    ["text"] = original,
                }));
        }

        if (HasAny(spell, "summon", "call", "army", "moth", "ants", "swarm"))
        {
            var insect = HasAny(spell, "ant", "ants", "swarm");
            var moth = spell.Contains("moth", StringComparison.Ordinal);
            return Accepted(
                "moderate",
                insect
                    ? "The floor writes itself into legs, antennae, and purpose."
                    : moth
                        ? "A brass-winged flicker folds itself out of the air."
                        : "Something small and loyal steps out of the spell's shadow.",
                Effect("summon", new Dictionary<string, object?>
                {
                    ["name"] = insect ? "loyal ant swarm" : moth ? "friendly brass moth" : "summoned familiar",
                    ["faction"] = "player",
                    ["glyph"] = insect ? "a" : moth ? "m" : "*",
                    ["hp"] = insect ? 6 : 5,
                    ["attack"] = insect ? 2 : 1,
                    ["tags"] = insect ? "summoned,swarm,insect" : moth ? "summoned,brass,winged" : "summoned,wild_magic",
                }));
        }

        var auraSpell = HasToken(spell, "aura");
        var wardSpell = HasToken(spell, "ward");
        if (auraSpell
            || wardSpell
            || HasAny(spell, "delayed", "after two turns", "after 2 turns", "in two turns", "in 2 turns"))
        {
            var status = HasAny(spell, "burn", "burning") ? "burning"
                : HasAny(spell, "web", "bind", "root") ? "bound"
                : "poisoned";
            return Accepted(
                auraSpell || wardSpell ? "moderate" : "minor",
                auraSpell
                    ? "The spell leaves a repeating pressure in the air."
                    : wardSpell
                        ? "A warning knot waits for the next hostile step."
                        : "The spell folds itself into a later minute.",
                Effect("createTrigger", new Dictionary<string, object?>
                {
                    ["name"] = auraSpell
                        ? "poisonous green aura"
                        : wardSpell
                            ? "warning ward"
                            : status switch
                            {
                                "burning" => "waiting blue coal",
                                "bound" => "waiting binding",
                                _ => "waiting venom",
                            },
                    ["kind"] = auraSpell ? "aura" : wardSpell ? "ward" : "delay",
                    ["delay"] = auraSpell || wardSpell ? 1 : 2,
                    ["interval"] = 1,
                    ["uses"] = auraSpell || wardSpell ? 3 : 1,
                    ["anchor"] = auraSpell || wardSpell ? "player" : "self",
                    ["target"] = auraSpell || wardSpell ? "nearest_enemy" : HarmfulTarget(original),
                    ["radius"] = auraSpell || wardSpell ? 6 : 0,
                    ["targetFilter"] = auraSpell || wardSpell ? "enemies" : "all",
                    ["effectType"] = "addStatus",
                    ["status"] = status,
                    ["duration"] = 3,
                    ["description"] = auraSpell
                        ? "The aura presses its color outward."
                        : wardSpell
                            ? "The ward remembers what it was waiting for."
                            : "The delayed magic opens its hand.",
                }));
        }

        if (HasAny(spell, "burn", "burning", "poison", "venom", "mending", "regenerat", "conceal", "hide me", "camouflage"))
        {
            var status = HasAny(spell, "burn", "burning") ? "burning"
                : HasAny(spell, "poison", "venom") ? "poisoned"
                : HasAny(spell, "mending", "regenerat") ? "mending"
                : "river_concealed";
            var helpful = HasAny(status, "mending", "river_concealed");
            var target = helpful ? "player" : HarmfulTarget(original);
            return Accepted(
                helpful ? "minor" : "moderate",
                status switch
                {
                    "burning" => "A blue coal catches where the spell points.",
                    "poisoned" => "A green-black rumor enters the blood.",
                    "mending" => "Green repair keeps happening after the spell lets go.",
                    _ => "River-color closes over the outline of the body.",
                },
                Effect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = target,
                    ["status"] = status,
                    ["displayName"] = status.Replace('_', ' '),
                    ["duration"] = status is "burning" ? 3 : status is "poisoned" ? 5 : 4,
                }));
        }

        if (HasAny(spell, "heal", "mend", "close wound", "stitch"))
        {
            return Accepted(
                "minor",
                "Green light stitches itself through the wound.",
                Effect("heal", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["amount"] = 5 + MagnitudeDelta(lens),
                }));
        }

        if (HasAny(spell, "restore mana", "regain mana", "fill my mana"))
        {
            return Accepted(
                "minor",
                "Blue sparks remember how to be breath.",
                Effect("restoreMana", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["amount"] = 4 + MagnitudeDelta(lens),
                }));
        }

        if (HasAny(spell, "bind", "web", "sticky", "freeze", "petrify", "sleep", "root"))
        {
            var status = HasAny(spell, "freeze", "ice") ? "frozen"
                : HasAny(spell, "sleep", "dream") ? "asleep"
                : HasAny(spell, "petrify", "stone") ? "petrified"
                : "bound";
            return Accepted(
                "moderate",
                "The spell becomes a restraint with more personality than mercy.",
                Effect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["status"] = status,
                    ["displayName"] = status.Replace('_', ' '),
                    ["duration"] = 4,
                }));
        }

        if (HasAny(spell, "floor", "ice", "water", "vines", "wall", "rubble", "terrain"))
        {
            var terrain = HasAny(spell, "ice", "slick") ? "slick_ice"
                : spell.Contains("water", StringComparison.Ordinal) ? "shallow_water"
                : spell.Contains("wall", StringComparison.Ordinal) ? "ice_wall"
                : spell.Contains("rubble", StringComparison.Ordinal) ? "rubble"
                : spell.Contains("fire", StringComparison.Ordinal) ? "wild_fire"
                : "vines";
            return Accepted(
                "moderate",
                "The room accepts a new law underfoot.",
                Effect("createTiles", new Dictionary<string, object?>
                {
                    ["x"] = 6,
                    ["y"] = 5,
                    ["radius"] = spell.Contains("wall", StringComparison.Ordinal) ? 0 : 1,
                    ["terrain"] = terrain,
                    ["duration"] = 5,
                }));
        }

        if (HasAny(spell, "glass", "crystal", "teeth", "stone", "golden", "brass"))
        {
            var material = HasAny(spell, "glass", "crystal") ? "glass" : spell.Contains("stone", StringComparison.Ordinal) ? "stone" : "brass";
            return Accepted(
                "moderate",
                "The target's body briefly becomes a bad idea with edges.",
                Effect("transformEntity", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["name"] = $"{material}-marked imperial soldier",
                    ["material"] = material,
                    ["addTags"] = $"{material},changed,wild_mark",
                    ["description"] = $"Changed by spell: {original}",
                }),
                Effect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["status"] = "staggered",
                    ["duration"] = 2,
                }));
        }

        if (HasAny(spell, "reveal", "shadow", "glow", "find", "mark"))
        {
            return Accepted(
                "minor",
                "A guilty shadow lights itself from underneath.",
                Effect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["status"] = "revealed",
                    ["displayName"] = "shadow-lit",
                    ["duration"] = 5,
                }));
        }

        if (HasAny(spell, "push", "shove", "away", "repel", "wind"))
        {
            return Accepted(
                "minor",
                "Invisible hands shove the air into obedience.",
                Effect("push", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["distance"] = 2,
                }));
        }

        if (HasAny(spell, "pull", "drag", "toward", "hook"))
        {
            return Accepted(
                "minor",
                "A hook of force catches and reels inward.",
                Effect("pull", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["distance"] = 1,
                }));
        }

        if (HasAny(spell, "teleport", "blink", "swap places", "relocate"))
        {
            return Accepted(
                "moderate",
                "The spell folds the room like a secret note.",
                Effect("teleport", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["x"] = 4,
                    ["y"] = 5,
                }));
        }

        if (HasAny(spell, "possess", "body swap", "bodyswap", "soul swap", "take their body"))
        {
            return Accepted(
                "major",
                "Your soul knocks on the wrong door and waits to see if it opens.",
                Effect("possess", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                }));
        }

        if (HasAny(spell, "charm", "befriend", "ally", "turn them", "make them help"))
        {
            return Accepted(
                "major",
                "For one dangerous moment, loyalty forgets who signed it.",
                Effect("changeFaction", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["faction"] = "player",
                }),
                Effect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["status"] = "bewildered",
                    ["duration"] = 3,
                }));
        }

        if (HasAny(spell, "curse", "debt", "cost me"))
        {
            var template = HasToken(spell, "close") ? "close"
                : HasToken(spell, "far") ? "far"
                : HasToken(spell, "narrow") ? "narrow"
                : HasAny(spell, "straight path", "straight-path") ? "straight-path"
                : HasToken(spell, "anchored") ? "anchored"
                : "";
            return Accepted(
                "moderate",
                "The magic signs your name in the margin.",
                Effect("addCurse", new Dictionary<string, object?>
                {
                    ["name"] = string.IsNullOrWhiteSpace(template) ? "Wild Debt" : $"{template} curse",
                    ["description"] = original,
                    ["template"] = template,
                }));
        }

        if (HasAny(spell, "blast", "storm", "explode", "all enemies", "lightning"))
        {
            return Accepted(
                "major",
                "Light breaks into several opinions at once.",
                Effect("areaDamage", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["radius"] = 2,
                    ["amount"] = 4 + MagnitudeDelta(lens),
                    ["affects"] = "enemies",
                    ["damageType"] = "wild",
                }));
        }

        return Accepted(
            "moderate",
            "Blue fire snaps from your fingers in a crooked line.",
            Effect("damage", new Dictionary<string, object?>
            {
                ["target"] = "nearest_enemy",
                ["amount"] = 6 + MagnitudeDelta(lens),
                ["damageType"] = "arcane",
            }),
            LowComposureMessage(lens, spell));
    }

    private static SpellResolution Accepted(
        string severity,
        string outcome,
        params SpellEffect?[] effects) =>
        new(
            Accepted: true,
            Severity: severity,
            OutcomeText: outcome,
            Effects: effects.Where(effect => effect is not null).Cast<SpellEffect>().ToArray(),
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);

    private static SpellResolution AcceptedWithCosts(
        string severity,
        string outcome,
        IReadOnlyList<SpellCost> costs,
        params SpellEffect?[] effects) =>
        new(
            Accepted: true,
            Severity: severity,
            OutcomeText: outcome,
            Effects: effects.Where(effect => effect is not null).Cast<SpellEffect>().ToArray(),
            Costs: costs,
            RejectedReason: null);

    private static SpellResolution Rejected(string reason) =>
        new(
            Accepted: false,
            Severity: "major",
            OutcomeText: "",
            Effects: Array.Empty<SpellEffect>(),
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: reason);

    private static bool HasAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.Ordinal));

    private static bool HasToken(string text, string token) =>
        text.Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '\'', '"', '-', '_' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(part => part.Equals(token, StringComparison.OrdinalIgnoreCase));

    private static string HarmfulTarget(string text)
    {
        var spell = text.ToLowerInvariant();
        if (HasAny(spell, "ward-captain", "ward captain", "captain"))
        {
            return "soldier_2";
        }

        if (HasAny(spell, "soldier", "imperial", "guard"))
        {
            return "soldier_1";
        }

        if (HasAny(spell, "lio", "prisoner"))
        {
            return "prisoner_1";
        }

        return "nearest_enemy";
    }

    private static SpellEffect Effect(
        string type,
        IReadOnlyDictionary<string, object?> fields) =>
        new(type, fields);

    private static int MagnitudeDelta(ResolverLensView? lens) =>
        lens?.EffectMagnitudeDelta ?? 0;

    private static SpellEffect? LowComposureMessage(ResolverLensView? lens, string spell)
    {
        if (lens?.Composure > 2 || spell.Contains("kill the emperor", StringComparison.Ordinal))
        {
            return null;
        }

        return Effect("message", new Dictionary<string, object?>
        {
            ["text"] = "Your low composure makes the magic shed a gorgeous, troublesome spark.",
        });
    }
}
