namespace Sorcerer.Magic.Capabilities;

public sealed record CapabilityCard(
    string Id,
    IReadOnlyList<string> Triggers,
    string IndexLine,
    IReadOnlyList<string> EffectTypes,
    IReadOnlyList<string> RequiredContext,
    string PromptBlock,
    IReadOnlyList<string> Examples,
    IReadOnlyList<string> CommonCombos,
    int Version = 1);

public sealed class CapabilityRegistry
{
    private static readonly string[] Connectives =
    {
        " and ", " while ", " then ", "but also", " except ", " into ", " after ", " before ",
    };

    private const int BaseCapWithHits = 5;
    private const int BaseCapWithoutHits = 3;
    private const int HardCeiling = 7;

    private readonly Dictionary<string, CapabilityCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _registryOrder = new();

    public IReadOnlyCollection<CapabilityCard> Cards => _cards.Values;

    public void Add(CapabilityCard card)
    {
        if (!_cards.ContainsKey(card.Id))
        {
            _registryOrder.Add(card.Id);
        }

        _cards[card.Id] = card;
    }

    /// <summary>
    /// Tier-1 keyword routing + one-hop combo expansion, recall-biased and capped. Mirrors the
    /// Wild Magic prototype's `select_cards`: rank by trigger hit count (ties broken by registry
    /// order), expand one hop via CommonCombos, then apply a dynamic cap that favors false
    /// positives (an extra loaded card) over false negatives (a missing card that would have made
    /// the spell work).
    /// </summary>
    public IReadOnlyList<CapabilityCard> Select(string spellText)
    {
        var text = $" {(spellText ?? string.Empty).ToLowerInvariant()} ";

        var ranked = _registryOrder
            .Select(id => _cards[id])
            .Select(card => (Card: card, Hits: card.Triggers.Count(trigger => text.Contains(trigger, StringComparison.OrdinalIgnoreCase))))
            .Where(entry => entry.Hits > 0)
            .OrderByDescending(entry => entry.Hits)
            .ThenBy(entry => _registryOrder.IndexOf(entry.Card.Id))
            .Select(entry => entry.Card)
            .ToList();

        var selected = new List<CapabilityCard>(ranked);
        var seen = new HashSet<string>(selected.Select(card => card.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var card in ranked)
        {
            foreach (var comboId in card.CommonCombos)
            {
                if (!seen.Contains(comboId) && _cards.TryGetValue(comboId, out var combo))
                {
                    selected.Add(combo);
                    seen.Add(combo.Id);
                }
            }
        }

        var cap = ranked.Count > 0 ? BaseCapWithHits : BaseCapWithoutHits;
        if (Connectives.Any(connective => text.Contains(connective, StringComparison.OrdinalIgnoreCase)))
        {
            cap++;
        }

        cap = Math.Min(cap, HardCeiling);
        return selected.Take(cap).ToArray();
    }

    public string CapabilityIndex() =>
        string.Join("\n", _registryOrder.Select(id => $"- {_cards[id].IndexLine}"));

    public static CapabilityRegistry CreateDefault()
    {
        var registry = new CapabilityRegistry();
        foreach (var card in BuiltInCards())
        {
            registry.Add(card);
        }

        foreach (var card in CapabilityCardLoader.LoadDefaultContentCards())
        {
            registry.Add(card);
        }

        return registry;
    }

    private static IEnumerable<CapabilityCard> BuiltInCards()
    {
        yield return new CapabilityCard(
            "terrain_shape",
            new[] { "wall", "floor", "ice", "fire", "water", "mist", "bridge", "terrain" },
            "terrain_shape - create or alter local tiles and hazards",
            new[] { "createTile", "createTiles" },
            new[] { "nearby_tiles", "selected_target" },
            "Use terrain operations for local tile changes, never global map rewrites.",
            Array.Empty<string>(),
            new[] { "environment_flow" });

        yield return new CapabilityCard(
            "summoning",
            new[] { "summon", "call", "conjure", "create creature", "familiar" },
            "summoning - create bounded creatures or helpers, including template-backed conjuration",
            new[] { "summon", "createEntity", "conjureCreature" },
            new[] { "visible_tiles", "factions" },
            "Summons must have bounded stats, a faction, and a valid placement. conjureCreature "
                + "additionally accepts template (tiny_swarm, small_beast, humanoid, construct, "
                + "spirit, slime, summoned_servant, hazard_creature) and count (1-12).",
            Array.Empty<string>(),
            new[] { "conjure_item" });

        yield return new CapabilityCard(
            "transformation",
            new[] { "turn", "transform", "change into", "make into", "teeth", "glass" },
            "transformation - alter entities, items, props, bodies, or materials",
            new[] { "transformEntity", "transformItem", "addTrait" },
            new[] { "visible_entities", "spell_anchors" },
            "Transformations should preserve engine authority and avoid free win-button changes.",
            Array.Empty<string>(),
            Array.Empty<string>());

        yield return new CapabilityCard(
            "prophecy",
            new[] { "promise", "prophecy", "omen", "debt", "future", "tomorrow" },
            "prophecy - write a future commitment into the Promise Ledger",
            new[] { "createPromise", "scheduleEvent", "consequence" },
            new[] { "promises", "region" },
            "Promises are powerful because the world may later honor them. Prefer createPromise for "
                + "ordinary promises; effectType:'consequence' with consequenceType:'create_promise' "
                + "is also valid when the spell is already using the shared consequence grammar. Use real costs.",
            Array.Empty<string>(),
            new[] { "delayed_effects" });

        yield return new CapabilityCard(
            "conjure_item",
            new[] { "conjure", "create item", "spawn", "glass", "tooth", "teeth", "shard", "key", "coin", "weapon", "potion", "vial", "trinket", "transmute", "turn my" },
            "conjure_item - create or transmute objects, materials, body parts, and loose items",
            new[] { "conjureItem", "modifyInventory" },
            new[] { "conjurable_items" },
            "conjureItem fields: template (generic_object, body_part, glass_shard, "
                + "ritual_component, weapon_like, food, key_like, treasure), name, material, count "
                + "(1-20), tags. modifyInventory fields: item, op (add/remove/set), amount.",
            Array.Empty<string>(),
            Array.Empty<string>());

        yield return new CapabilityCard(
            "memory_edit",
            new[] { "remember", "forget", "memory", "memories", "recall", "convince", "erase", "implant", "amnesia", "wipe", "plant a memory" },
            "memory_edit - alter, plant, or erase what an NPC remembers or knows",
            new[] { "editMemory" },
            new[] { "target_memories" },
            "editMemory fields: target, op (add/remove/alter), subject ('the caster' means the "
                + "player), text, strength (1-5). Removing the caster from a hostile NPC's memory "
                + "also calms it. This is major magic: pair with a real cost.",
            Array.Empty<string>(),
            new[] { "faction_charm" });

        yield return new CapabilityCard(
            "faction_charm",
            new[] { "charm", "befriend", "convince the", "turn against", "oath", "make a friend", "change side", "loyal" },
            "faction_charm - shift allegiance, tags, and faction standing",
            new[] { "changeFaction", "addTag", "removeTag" },
            Array.Empty<string>(),
            "changeFaction/addTag/removeTag act on a target's faction and descriptive tags.",
            Array.Empty<string>(),
            new[] { "transformation" });

        yield return new CapabilityCard(
            "delayed_effects",
            new[] { "in three turns", "in five turns", "later", "delayed", "comes back", "will arrive", "ticking", "fuse", "countdown", "debt", "delay my wounds", "delay incoming damage", "store my wounds", "release it afterward" },
            "delayed_effects - postpone consequences and accelerate ongoing statuses",
            new[] { "scheduleEvent", "delayIncoming", "accelerateStatus" },
            Array.Empty<string>(),
            "delayIncoming captures incoming damage into a buffer that releases after N turns. "
                + "accelerateStatus applies all remaining ticks of a status (burning, poisoned, "
                + "regenerating) at once and clears it.",
            Array.Empty<string>(),
            new[] { "triggers_reactions" });

        yield return new CapabilityCard(
            "triggers_reactions",
            new[] { "next time", "whenever", "when they", "the next attack", "react", "reaction", "contingency", "would die", "lethal", "last breath", "ward that", "trap that", "retaliate", "counter" },
            "triggers_reactions - delayed effects, wards, and auras via createTrigger",
            new[] { "createTrigger" },
            Array.Empty<string>(),
            "createTrigger is the general delayed/ward/aura primitive already in the core set.",
            Array.Empty<string>(),
            new[] { "persistent_effect" });

        yield return new CapabilityCard(
            "persistent_effect",
            new[] { "lingering", "ongoing", "festering", "haunt", "hex the", "hex on", "ward on", "anyone who strikes", "anyone who touches", "whoever strikes", "keeps bleeding", "persists", "persistent", "my blows", "my strikes", "every blow i", "sympath", "bind the", "bound to", "tether", "share the pain", "share its pain", "voodoo" },
            "persistent_effect - anchor an effect that fires when the anchor hits or is hit, including sympathetic links",
            new[] { "createPersistentEffect" },
            Array.Empty<string>(),
            "createPersistentEffect fields: target, hook (on_hit fires when the anchor is struck, "
                + "on_strike fires when the anchor lands a hit), effect (a nested effect object), "
                + "uses. The nested effect can be a shorthand damage/heal/status/message effect or "
                + "type:'consequence' with consequenceType plus typed payload fields. For a sympathetic "
                + "link, set kind:'sympathetic_link' and linkTarget to mirror a fraction of incoming "
                + "damage onto the partner.",
            Array.Empty<string>(),
            Array.Empty<string>());

        yield return new CapabilityCard(
            "behavior_control",
            new[] { "make them dance", "make it dance", "dance", "coward", "cowardly", "flee from blood", "afraid of blood", "mimic my movement", "copy my movement", "freeze with dread" },
            "behavior_control - reshape how a creature decides its turn",
            new[] { "setBehavior" },
            Array.Empty<string>(),
            "setBehavior fields: target, tag (coward flees the player instead of closing; "
                + "dance/freeze_dread skip the actor's turn; mimic copies the player's last "
                + "movement), duration (turns; omit for permanent).",
            Array.Empty<string>(),
            new[] { "faction_charm" });

        yield return new CapabilityCard(
            "environment_flow",
            new[] { "conveyor", "current", "flow", "river of force", "gust", "pushes every turn", "pulls every turn", "shifting sand", "tilt", "tilted", "slide", "drift", "gravity well", "black hole", "vortex", "whirlpool", "magnet", "magnetic" },
            "environment_flow - standing tile fields that move whoever stands on them each turn",
            new[] { "createFlow" },
            Array.Empty<string>(),
            "createFlow fields: target/x/y, radius, dx, dy, duration. Every turn, actors standing "
                + "on a flow tile are translated by (dx, dy) if the destination is open.",
            Array.Empty<string>(),
            new[] { "terrain_shape" });
    }
}
