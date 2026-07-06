using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sorcerer.Core.Views;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

/// <summary>
/// Shared prompt assembly for the wild-magic resolver (Ollama and OpenAI-compatible providers).
///
/// Layout is deliberate (docs/OPTIMIZATION_PLAN.md WS2.3): the system prompt opens with content
/// that is byte-identical on every cast — core rules, the consequence-type vocabulary, and the
/// capability index — so a local backend's KV prefix cache can reuse it across consecutive casts.
/// Cast-specific material (the supported-operation list, operation guidance, loaded capability
/// blocks) follows, and the user message puts the changing spell text last. Operation cards are
/// rendered as compact text lines instead of JSON records, and the serialized context omits the
/// operation catalog and null fields entirely: the catalog lives here as text, halving the old
/// user-message payload without changing what the engine advertises or validates.
/// </summary>
internal static class SpellPromptBuilder
{
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ExampleJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static string System(SpellRequest request)
    {
        var builder = new StringBuilder();
        builder.Append(CoreRules);
        builder.Append("\n\n").Append(ConsequenceTypesLine);
        if (!string.IsNullOrWhiteSpace(request.CapabilityIndex))
        {
            builder.Append("\n\nCapability index (mechanics that can be loaded when a spell needs them):\n");
            builder.Append(request.CapabilityIndex);
        }

        builder.Append("\n\nSupported effect types for this cast: ");
        builder.Append(string.Join(", ", request.SupportedOperations));
        builder.Append('.');

        if (request.Context is MagicContextView view && view.Operations.Cards.Count > 0)
        {
            builder.Append("\n\nOperation guidance (one line per effect type; lean lines are valid types whose detail was not loaded):\n");
            builder.Append(RenderOperations(view.Operations));
        }

        if (request.SelectedCapabilities is { Count: > 0 })
        {
            builder.Append("\n\nMechanics loaded for this spell:\n");
            builder.Append(string.Join(
                "\n",
                request.SelectedCapabilities.Select(card =>
                    string.Join("\n", new[] { card.PromptBlock }.Concat(card.Examples)))));
        }

        return builder.ToString();
    }

    public static string User(SpellRequest request) =>
        $"Current magic context JSON:\n{WireContextJson(request)}\n\nSpell: {request.SpellText}";

    /// <summary>
    /// The repair lane resends only the rules, the failure, and a target-id cheat sheet — not the
    /// full context or operation guidance. Repair fixes JSON shape, not world reasoning
    /// (docs/OPTIMIZATION_PLAN.md WS2.7).
    /// </summary>
    public static string RepairSystem() =>
        CoreRules
        + "\n\nThis is a repair attempt after invalid output. Return JSON only; no prose before or after the object.";

    public static string RepairUser(SpellRequest request, string invalidContent, string parseError)
    {
        var previous = invalidContent.Length > 600 ? invalidContent[..600] : invalidContent;
        var builder = new StringBuilder();
        builder.Append("The previous resolver answer was not valid engine JSON. ");
        builder.Append("Parse error: ").Append(parseError).Append('\n');
        builder.Append("Previous invalid answer:\n").Append(previous).Append("\n\n");
        builder.Append("Convert the same spell into the required JSON object using supported effect types: ");
        builder.Append(string.Join(", ", request.SupportedOperations)).Append(". ");
        builder.Append("Each effect must be a flat object with a type field; rewrite keyed or nested effects into separate flat effect objects. ");
        builder.Append("For hiding, cover, protection, disguise, or attention-shifting requests, prefer addStatus on the caster/target, createTile/createTiles near the caster, addTrait on an entity, or message when those operations fit.\n");
        var targets = TargetIdLine(request);
        if (targets.Length > 0)
        {
            builder.Append(targets).Append('\n');
        }

        builder.Append("Spell: ").Append(request.SpellText);
        return builder.ToString();
    }

    internal static string WireContextJson(SpellRequest request)
    {
        // The operation catalog is rendered as prompt text by System(); serializing it again in the
        // user message was the single largest context slice (~10 KB/cast). The runtime null lets
        // WhenWritingNull drop the property from the wire while MagicContextView keeps a
        // non-nullable Operations for the engine, mock provider, audits, and replay.
        var wireContext = request.Context is MagicContextView view
            ? view with { Operations = null! }
            : request.Context;
        return JsonSerializer.Serialize(wireContext, WireJsonOptions);
    }

    internal static string RenderOperations(OperationIndex operations)
    {
        var lines = new List<string>(operations.Cards.Count);
        foreach (var card in operations.Cards)
        {
            var line = new StringBuilder("- ").Append(card.Name).Append(": ").Append(card.Summary.TrimEnd());
            if (!string.IsNullOrWhiteSpace(card.PromptGuidance))
            {
                line.Append(' ').Append(card.PromptGuidance.Trim());
            }

            if (card.Fields.Count > 0)
            {
                line.Append(" Fields: ");
                line.Append(string.Join("; ", card.Fields.Select(field => $"{field.Key}: {field.Value}")));
                line.Append('.');
            }

            if (card.Examples.Count > 0)
            {
                line.Append(" e.g. ").Append(JsonSerializer.Serialize(card.Examples[0], ExampleJsonOptions));
            }

            lines.Add(line.ToString());
        }

        return string.Join("\n", lines);
    }

    private static string TargetIdLine(SpellRequest request)
    {
        if (request.Context is not MagicContextView view)
        {
            return "";
        }

        var ids = view.Visible.Select(entity => entity.Id).Take(16).ToArray();
        return ids.Length == 0
            ? ""
            : "Valid target ids: " + string.Join(", ", ids)
              + " (selectors nearest_enemy and selected_target are also valid).";
    }

    /// <summary>
    /// Static on every cast — no per-cast values may appear here, so a local backend can prompt-
    /// cache this whole block. The supported-operation list therefore lives in the variable tail.
    /// Voice: outcome text stays concrete and sensory (docs/AESTHETICS_AND_TONE.md, narration
    /// voice) — wild magic may be strange, but strangeness is imagery, not vagueness.
    /// </summary>
    private const string CoreRules =
        "You are the wild magic resolver for Sorcerer. Return exactly one JSON object. "
        + "Use this shape: {\"accepted\":true,\"severity\":\"minor|moderate|major|catastrophic\","
        + "\"outcomeText\":\"short vivid result\",\"effects\":[],\"costs\":[],\"rejectedReason\":null}. "
        + "Use only supported effect types and the provided target references. "
        + "Every target-taking effect must include target or targetId from the context; for the caster, use target:\"player\" unless the caster id differs. "
        + "When the spell wording names how to find the target - \"nearest enemy\", \"the nearest foe\", \"whatever is closest\" - use the matching selector such as nearest_enemy directly; only use target:\"selected_target\" when the spell text refers to a target the caster has already selected or is looking at (\"that\", \"there\", \"my target\"), since selected_target fails if nothing is currently selected. "
        + "Effects must be an array of flat objects with a type field, such as {\"type\":\"addStatus\",\"target\":\"player\",\"status\":\"river_concealed\",\"duration\":4}; never write {\"addStatus\":{...}} or put two operation keys inside one effect object. "
        + "Prefer reusable operations over custom mechanics. "
        + "Outcome text must describe only what the listed effects make true. "
        + "Write outcomeText as what is concretely seen, heard, or felt where the spell lands, in the region's voice; never as fate-speak about stories, whispers, or what the world will remember. "
        + "Do not claim a target is immobilized, asleep, dead, transformed, summoned, moved, healed, harmed, cursed, or allied unless a matching effect operation is present. "
        + "If the spell should stop movement or action, include an addStatus effect with a binding status such as rooted, webbed, pinned, asleep, or petrified. "
        + "If a spell asks a local place, room, shrine, terrain, fixture, or object to help, hide, protect, reveal, remember, or answer, convert that appeal into concrete effects on existing targets, nearby terrain, statuses, traits, summons, or messages. "
        + "Use second person only for the controlled player/caster; name non-player targets instead of calling them 'you' or 'your'. "
        + "Write outcomeText and any effect message field in grammatically correct person and number (\"you gain\", not \"you gains\"; \"the soldier gains\", not \"the soldier gain\"). "
        + "If the spell names part of an entity's appearance, clothing, gear, rope, hair, voice, shadow, or body, target that owning entity with addTrait, addStatus, or transformEntity; do not pick an unrelated item just because it is object-like. "
        + "If context.resolverLens is present, use it as soft guidance for magnitude, volatility, costs, and recurring magical signature. "
        + "If context.reagents is present, those are unprotected carried materials available as spell fuel; use their material, tags, and spellBias as soft guidance for costs or theming, but do not assume protected inventory is spendable. "
        + "If context.lore is present, use those lore cards as canon and voice guidance, but only make lore mechanically true through supported effect operations. "
        + "Assign costs deliberately: almost every spell that actually changes the world should cost something, and the price should scale with how much it bends reality. "
        + "The cost palette, from cheapest to gravest: mana for ordinary workings ({\"type\":\"mana\",\"amount\":2-6}); a bodily toll for spells that strain the caster ({\"type\":\"health\",\"amount\":3-8} or {\"type\":\"status\",\"status\":\"strained\",\"duration\":3}); a consumed reagent when the spell leans on a carried material; a lingering debt for dangerous, unnatural, or morally fraught magic ({\"type\":\"curse\",\"name\":\"short title\",\"description\":\"what is owed\"}); and, only for catastrophic or reality-bending spells, a permanent sacrifice ({\"type\":\"maxHealth\",\"amount\":1-3} or {\"type\":\"maxMana\",\"amount\":1-3}). "
        + "Match the cost to severity: minor spends a little mana; moderate spends more mana or a small bodily/reagent toll; major demands a real price such as health, a reagent, or a curse; catastrophic may demand max health or max mana. Prefer two mixed costs (e.g. mana plus a reagent, or health plus a curse) over one flat mana cost for anything above minor. "
        + "When a carried reagent in context.reagents thematically fits the spell - by its name, material, tags, or spellBias - prefer spending it as {\"type\":\"item\",\"item\":\"<exact reagent name from context>\",\"quantity\":1}, alongside or instead of mana; a named object powering the spell is more interesting than raw mana. "
        + "Never name an item cost that is not listed in context.reagents (the caster cannot spend what they do not carry); if no carried reagent fits, use mana, health, a status, or a curse instead. "
        + "Only a truly trivial cantrip or a pure-flavor spell may leave costs empty. "
        + "Costs are not effects: never add a restoreMana (or heal) effect unless restoring magic (or "
        + "healing) is the spell's actual purpose - a fire strike or an ice wall must not hand the "
        + "caster mana back. "
        + "Costs must use type fields such as {\"type\":\"mana\",\"amount\":4} or {\"type\":\"item\",\"item\":\"grave salt\",\"quantity\":1}; never use an item name as the cost type. "
        + "Before rejecting an overreaching spell, deliver the largest local version the supported operations allow, at a severe cost, and let outcomeText admit the magic answered smaller than asked. "
        + "Reject only literal win buttons, infinite resources, global rewrites, or spells with no possible local expression in the current encounter. "
        + "Technical JSON mistakes are failures, but intentional in-world rejection should be accepted:false. "
        + "If none of the loaded mechanics fit the spell but the capability index lists one that would, "
        + "answer instead with exactly {\"needsCapability\":\"<index name>\"} and nothing else; that "
        + "capability's detail will be loaded and you will be asked once more. Use this only when a "
        + "listed capability is genuinely needed, not to avoid an ordinary resolution. "
        + "The engine validates everything and applies all effects transactionally.";

    /// <summary>
    /// The spell-relevant slice of the shared consequence grammar: a ~300-byte map of everything
    /// the world engine can do beyond direct operations (docs/OPTIMIZATION_PLAN.md WS1.5), so
    /// social/world spells can reach tags, memory, services, routes, canon, or rumors without the
    /// full consequence card being routed in.
    /// </summary>
    private const string ConsequenceTypesLine =
        "World effects beyond the listed operations go through effectType \"consequence\" with a consequenceType: "
        + "apply_status, create_promise, open_or_unlock, create_route, free_captive, modify_inventory, transfer_item, "
        + "add_tags, remove_tags, change_faction, edit_memory, record_memory, update_bond, update_want, "
        + "offer_service, request_service, add_merchant_stock, offer_trade, record_rumor, add_canon, "
        + "spawn_fixture, spawn_item, spawn_entity, set_world_flag, adjust_faction_standing, schedule_event, message.";
}
