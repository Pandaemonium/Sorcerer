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

    /// <summary>Looks up a card by exact id (case-insensitive); null if no such card is registered.</summary>
    public CapabilityCard? Find(string id) =>
        !string.IsNullOrWhiteSpace(id) && _cards.TryGetValue(id.Trim(), out var card) ? card : null;

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
    public IReadOnlyList<CapabilityCard> Select(string spellText) =>
        Select(spellText, Array.Empty<string>());

    /// <summary>
    /// Keyword routing unioned with an LLM router's explicit picks. Keyword hits are ranked and
    /// capped exactly as before; <paramref name="routerCardNames"/> are added as high-confidence
    /// seeds right after the keyword hits (validated against the registry, so unknown names are
    /// ignored). Combo expansion and the recall-biased cap then run over the union. When
    /// <paramref name="routerCardNames"/> is empty this is byte-for-byte the old keyword behavior.
    /// </summary>
    public IReadOnlyList<CapabilityCard> Select(string spellText, IEnumerable<string> routerCardNames)
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

        var routerCards = (routerCardNames ?? Array.Empty<string>())
            .Select(name => name?.Trim())
            .Where(name => !string.IsNullOrEmpty(name) && _cards.ContainsKey(name!))
            .Select(name => _cards[name!])
            .ToList();

        // Explicit picks (keyword hits, then router picks) come before combo expansions so the cap
        // trims speculative combos first and never a deliberately-selected card.
        var selected = new List<CapabilityCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var card in ranked.Concat(routerCards))
        {
            if (seen.Add(card.Id))
            {
                selected.Add(card);
            }
        }

        var explicitCount = selected.Count;
        foreach (var card in selected.Take(explicitCount).ToArray())
        {
            foreach (var comboId in card.CommonCombos)
            {
                if (_cards.TryGetValue(comboId, out var combo) && seen.Add(combo.Id))
                {
                    selected.Add(combo);
                }
            }
        }

        var cap = ranked.Count > 0 || routerCards.Count > 0 ? BaseCapWithHits : BaseCapWithoutHits;
        if (Connectives.Any(connective => text.Contains(connective, StringComparison.OrdinalIgnoreCase)))
        {
            cap++;
        }

        cap = Math.Min(cap, HardCeiling);
        if (routerCards.Count > 0)
        {
            // Router picks are deliberate: never let the cap drop an explicit pick, only speculative
            // combos. (Keyword-only selection keeps its original cap so its behavior is unchanged.)
            cap = Math.Max(cap, explicitCount);
        }

        return selected.Take(cap).ToArray();
    }

    /// <summary>
    /// Every effect type any capability card can unlock, canonicalization aside. Used by operation
    /// routing to decide which operations are "gateable" (safe to trim to a lean card when their card
    /// was not selected) versus operations no card can bring in (which must stay fully advertised).
    /// </summary>
    public IReadOnlyCollection<string> AllEffectTypes() =>
        _cards.Values
            .SelectMany(card => card.EffectTypes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

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
            "Summons must have bounded stats, a faction, and a valid placement. Default faction to "
                + "'player' so the creature fights alongside the caster; only use 'enemy' or another "
                + "faction when the spell explicitly calls up something hostile, wild, or uncontrolled. "
                + "conjureCreature additionally accepts template (tiny_swarm, small_beast, humanoid, "
                + "construct, spirit, slime, summoned_servant, hazard_creature) and count (1-12).",
            new[]
            {
                "Example: {\"type\":\"conjureCreature\",\"template\":\"small_beast\",\"name\":\"river hound\",\"faction\":\"player\",\"count\":1}",
            },
            new[] { "conjure_item", "animation" });

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
            new[] { "createPromise", "scheduleEvent", "consequence", "setFlag" },
            new[] { "promises", "region" },
            "Promises are powerful because the world may later honor them. Prefer createPromise for "
                + "ordinary promises; effectType:'consequence' with consequenceType:'create_promise' "
                + "is also valid when the spell is already using the shared consequence grammar. Use real costs.",
            new[]
            {
                "Example: {\"type\":\"createPromise\",\"kind\":\"debt\",\"text\":\"In three dawns a collector arrives for what was borrowed\"}",
            },
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
            // hidden_entities: a memory-edit spell may reach a mind the caster cannot currently
            // perceive, so this card opts into off-screen entity context (see Lever B).
            new[] { "target_memories", "hidden_entities" },
            "editMemory fields: target, op (add/remove/alter), subject ('the caster' means the "
                + "player), text, strength (1-5). Removing the caster from a hostile NPC's memory "
                + "also calms it. This is major magic: pair with a real cost.",
            new[]
            {
                "Example: {\"type\":\"editMemory\",\"target\":\"soldier_1\",\"op\":\"remove\",\"subject\":\"the caster\"}",
            },
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
            "createTrigger is the general delayed/ward/aura primitive already in the core set. For "
                + "a ward or aura, set anchor, radius, and targetFilter ('enemies') and leave the "
                + "nested effect untargeted so the radius chooses the victims.",
            new[]
            {
                "Example: {\"type\":\"createTrigger\",\"kind\":\"ward\",\"anchor\":\"player\",\"radius\":2,\"targetFilter\":\"enemies\",\"effectType\":\"addStatus\",\"status\":\"webbed_thorns\",\"duration\":3,\"uses\":3}",
            },
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

        // The six cards below give the operations demoted from the always-full core
        // (docs/OPTIMIZATION_PLAN.md WS2.2) a routed home: a spell that names them loads full
        // guidance; every other cast carries only their lean name+summary line.
        yield return new CapabilityCard(
            "motion_kinetics",
            new[] { "push", "pull", "shove", "throw", "hurl", "fling", "toss", "drag", "yank", "knock", "slam", "blink", "teleport", "step through", "swap places", "launch", "reel" },
            "motion_kinetics - push, pull, or teleport bodies through space",
            new[] { "push", "pull", "teleport" },
            Array.Empty<string>(),
            "push/pull move a target away from or toward a point (fields: target, distance, and a "
                + "source/anchor when the spell names one). teleport moves a target to open in-zone "
                + "coordinates (fields: target, x, y). Keep motion local; never move a target out of "
                + "the zone or into blocking terrain.",
            Array.Empty<string>(),
            new[] { "environment_flow" });

        yield return new CapabilityCard(
            "area_burst",
            new[] { "all enemies", "everyone", "everything near", "around me", "explode", "explosion", "blast", "burst", "nova", "shockwave", "wave of", "storm", "rain of", "in a circle", "radius" },
            "area_burst - damage or statuses applied to every target in a radius",
            new[] { "areaDamage", "areaStatus" },
            new[] { "nearby_tiles" },
            "areaDamage/areaStatus hit every valid target within a radius of a center point (fields: "
                + "x/y or target, radius 1-4, then amount/damageType or status/duration). Keep the "
                + "radius local to the encounter; a burst is not a map-wide event.",
            Array.Empty<string>(),
            new[] { "terrain_shape" });

        yield return new CapabilityCard(
            "protection_wards",
            new[] { "protect", "shield", "ward me", "ward myself", "armor", "resist", "resistance", "immune", "harden", "brace", "vulnerable", "weakness", "expose", "sunder" },
            "protection_wards - resistances and weaknesses to kinds of harm",
            new[] { "addResistance", "addWeakness" },
            Array.Empty<string>(),
            "addResistance/addWeakness change how much damage of one kind a target takes (fields: "
                + "target, damageType, amount, duration). Prefer these over inventing a bespoke "
                + "damage-reduction status.",
            Array.Empty<string>(),
            new[] { "triggers_reactions" });

        yield return new CapabilityCard(
            "restoration",
            new[] { "restore", "replenish", "recover", "refresh", "renew", "mana", "stamina", "second wind", "invigorate" },
            "restoration - return spent mana when restoring magic is the spell's purpose",
            new[] { "restoreMana" },
            Array.Empty<string>(),
            "restoreMana returns mana to a target (fields: target, amount). Use it only when "
                + "restoring magic is the spell's stated purpose - never as a side bonus and never "
                + "to hand back the spell's own cost.",
            new[]
            {
                "Example: {\"type\":\"restoreMana\",\"target\":\"player\",\"amount\":8}",
            },
            Array.Empty<string>());

        yield return new CapabilityCard(
            "curse_mark",
            new[] { "curse", "hex", "doom", "brand", "mark of", "blight", "bad luck", "misfortune", "wither" },
            "curse_mark - lay a visible curse or debt on a target",
            new[] { "addCurse" },
            Array.Empty<string>(),
            "addCurse writes a visible debt/curse promise (fields: name, description, and an "
                + "optional template close/far/narrow/straight-path/anchored when the curse should "
                + "mechanically constrain future casts).",
            Array.Empty<string>(),
            new[] { "persistent_effect", "prophecy" });

        yield return new CapabilityCard(
            "possession",
            new[] { "possess", "take over", "puppet", "inhabit", "wear his", "wear her", "wear its", "ride the mind", "body swap", "swap bodies", "steal the body" },
            "possession - move the caster's control into another body",
            new[] { "possess" },
            new[] { "visible_entities" },
            "possess moves the player's control into a target body (field: target). This is major "
                + "magic: demand a severe cost, and expect the engine to reject impossible vessels.",
            Array.Empty<string>(),
            new[] { "memory_edit" });

        yield return new CapabilityCard(
            "animation",
            new[] { "raise the", "raise this", "raise my", "rise", "animate", "corpse", "the dead", "bones", "skeleton", "statue", "come alive", "come to life", "wake the", "stand and", "golem", "necroman", "undead", "reanimate" },
            "animation - wake a corpse, statue, prop, or floor object into a bounded servant",
            new[] { "animateEntity" },
            Array.Empty<string>(),
            "animateEntity wakes something that already exists in the world: a defeated actor "
                + "rises again, or an inert fixture, statue, prop, or floor object gains a body. "
                + "Fields: target (the corpse or object), faction (default 'player' so it serves "
                + "the caster), hp (1-12), attack (0-4), name (optional new name). Use "
                + "conjureCreature when the spell creates something new from nothing; use "
                + "animateEntity when it wakes something already here. Major magic: pair it with "
                + "a real cost.",
            new[]
            {
                "Example: {\"type\":\"animateEntity\",\"target\":\"soldier_1\",\"faction\":\"player\",\"name\":\"risen imperial soldier\"}",
            },
            new[] { "summoning" });

        yield return new CapabilityCard(
            "dispelling",
            new[] { "dispel", "unravel", "undo the", "break the ward", "break the spell", "break the enchantment", "break the curse", "end the spell", "end the enchantment", "cancel", "counterspell", "counter the", "quiet the magic", "snuff", "strip the", "cleanse", "purge", "lift the curse", "cut the strings", "unbind", "unweave", "silence the ward" },
            "dispelling - end active magic: statuses, wards, triggers, enchantments, and tile flows",
            new[] { "dispelMagic", "removeStatus" },
            Array.Empty<string>(),
            "dispelMagic ends active magic instead of narrating it away. Fields: target (the "
                + "entity whose magic should be stripped; default the caster) or x/y (a tile "
                + "whose flows and anchored wards should end), scope (all, statuses, triggers, "
                + "persistent, flows; default all), radius (0-3). It rejects when there is no "
                + "active magic to unravel. removeStatus stays right for ending one named status.",
            new[]
            {
                "Example: {\"type\":\"dispelMagic\",\"target\":\"player\",\"scope\":\"triggers\"}",
            },
            Array.Empty<string>());

        yield return new CapabilityCard(
            "rumor_legend",
            new[] { "rumor", "whisper my name", "spread the word", "spread a tale", "let them tell", "let them speak", "story of me", "reputation", "legend", "infamous", "renown", "notorious", "believe", "make them fear me", "clear my name", "let it be known", "sing of", "myth" },
            "rumor_legend - seed rumors, bend reputation, write legend and canon",
            new[] { "consequence" },
            Array.Empty<string>(),
            "Reputation magic goes through effectType 'consequence'. consequenceType "
                + "'record_rumor' (field: text) starts a rumor that travels on its own. "
                + "consequenceType 'adjust_faction_standing' (fields: factionId, axis such as "
                + "fear/notoriety/gratitude/legitimacy, delta -3..3) bends how a faction sees the "
                + "caster. consequenceType 'add_legend' (fields: actorSoulId 'player_soul', tag "
                + "such as uncanny/dangerous/merciful, weight 1-3) writes soul-bound legend. "
                + "consequenceType 'add_canon' (fields: attachedTo, text) makes a small fact "
                + "permanently true of a place or thing. Social magic that rewrites how the world "
                + "sees you is major: price it honestly.",
            new[]
            {
                "Example: {\"type\":\"consequence\",\"consequenceType\":\"record_rumor\",\"text\":\"A sorcerer walks the market whose shadow arrives before them\"}",
                "Example: {\"type\":\"consequence\",\"consequenceType\":\"adjust_faction_standing\",\"factionId\":\"empire\",\"axis\":\"fear\",\"delta\":2}",
            },
            new[] { "prophecy" });

        yield return new CapabilityCard(
            "heart_bond",
            new[] { "love me", "trust me", "adore", "soothe", "calm the", "calm her", "calm him", "soften", "heart", "grief", "comfort", "courage", "despair", "terrify", "dread of me", "loyalty", "devotion", "resent", "forgive" },
            "heart_bond - shift how one being feels toward another, or plant fear and calm",
            new[] { "consequence", "addStatus" },
            Array.Empty<string>(),
            "Feelings toward the caster go through effectType 'consequence' with consequenceType "
                + "'update_bond': target is the NPC, targetSoulId is 'player_soul' for feelings "
                + "toward the caster, and loyaltyDelta/fearDelta/admirationDelta/resentmentDelta "
                + "are small pushes (-2..2; the engine clamps larger asks), with optional posture. "
                + "Momentary emotion (terror, calm, awe) is addStatus with a status such as "
                + "frightened or becalmed. Hearts move by degrees: a stranger cannot be made "
                + "devoted in one cast, and forced feeling should carry a real cost.",
            new[]
            {
                "Example: {\"type\":\"consequence\",\"consequenceType\":\"update_bond\",\"target\":\"prisoner_1\",\"targetSoulId\":\"player_soul\",\"loyaltyDelta\":2,\"posture\":\"warm\"}",
            },
            new[] { "memory_edit", "faction_charm" });

        yield return new CapabilityCard(
            "ways_and_seals",
            new[] { "unlock", "open the door", "open the gate", "open the cell", "lock forgets", "forget its shape", "unbar", "unseal", "seal the", "the lock", "lock forget", "unchain", "free the", "release the", "way out", "path out", "escape route", "a way through", "no key", "steal", "snatch", "leaps to my hand", "leap to my hand", "fly to my hand", "yank the", "disarm", "pickpocket" },
            "ways_and_seals - open, unlock, or seal doors; free captives; reveal routes",
            new[] { "consequence" },
            Array.Empty<string>(),
            "Doors and ways go through effectType 'consequence'. consequenceType "
                + "'open_or_unlock' (fields: target = the door entity, unlock true/false, open "
                + "true/false) opens or unlocks a reachable door - the engine rejects doors out "
                + "of reach. consequenceType 'free_captive' (field: target = the captive) frees a "
                + "held being and lets it react. consequenceType 'transfer_item' with mode 'give' "
                + "(fields: actorEntityId = current holder, target = who receives, item) pulls a "
                + "carried item to a new holder - treasured possessions refuse. A route or "
                + "passage reveal needs a backing createPromise, createTrigger, terrain change, "
                + "or spawned-site operation, not narration.",
            new[]
            {
                "Example: {\"type\":\"consequence\",\"consequenceType\":\"open_or_unlock\",\"target\":\"cell_door_1\",\"unlock\":true,\"open\":true}",
                "Example: {\"type\":\"consequence\",\"consequenceType\":\"transfer_item\",\"mode\":\"give\",\"actorEntityId\":\"soldier_1\",\"target\":\"player\",\"item\":\"imperial cell key\"}",
            },
            new[] { "prophecy" });
    }
}
