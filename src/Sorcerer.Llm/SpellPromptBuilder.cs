using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Llm;

/// <summary>
/// Shared prompt assembly for the wild-magic resolver (Ollama and OpenAI-compatible providers).
///
/// Layout is deliberate (docs/OPTIMIZATION_PLAN.md WS2.3): the system prompt opens with content
/// that is byte-identical on every cast — distilled core rules, the consequence-type vocabulary,
/// and compact capability-name menu — so a local backend's KV prefix cache can reuse it across consecutive casts.
/// Cast-specific material (the supported-operation list, operation guidance, loaded capability
/// blocks) follows, and the user message puts the changing spell text last. Operation cards are
/// rendered as compact text lines instead of JSON records. The serialized context is a dedicated
/// compact projection with short target/resource records and only routed state slices; it omits
/// the richer engine/audit view and operation catalog entirely.
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
        builder.Append("\n\n").Append(CostProfileGuidance);
        if (!string.IsNullOrWhiteSpace(request.CapabilityIndex))
        {
            builder.Append("\n\nCapability names for one bounded retry: ");
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

        // Graceful floor: the resolver already spent its capability requests, so it must now commit
        // to a resolution with the operations on hand instead of dead-ending in another needsCapability.
        if (request.FinalAttemptNoEscape)
        {
            builder.Append(FinalAttemptInstruction);
        }

        // Opt-in self-critique, appended last so it never disturbs the cacheable prefix. It asks the
        // resolver to flag a capability it lacked and context it did not need — a corpus we mine to
        // grow real mechanics and trim the prompt. Explicitly severed from resolution.
        if (request.RequestResolverFeedback)
        {
            builder.Append(FeedbackRequest);
        }

        return builder.ToString();
    }

    private const string FinalAttemptInstruction =
        "\n\nFinal pass: no more mechanics can be loaded. Do NOT return needsCapability. You must return a "
        + "resolution now using only the supported effect types above. For an overreach or a request whose "
        + "ideal mechanic is missing, resolve the largest faithful LOCAL version at a fair-to-severe price "
        + "(e.g. a cross-zone teleport becomes a blink to the edge of this area; a world-rewrite becomes a "
        + "vivid local distortion). Only if there is truly no local expression at all, return accepted:false "
        + "with a concrete, in-world rejectedReason — never a bare failure.";

    private const string FeedbackRequest =
        "\n\nAlso include a top-level \"feedback\" object alongside the resolution: "
        + "{\"missingCapability\":\"one supported effect type or new mechanic that would have let you resolve this spell "
        + "more faithfully, or empty string if none\",\"unusedContext\":\"which provided context fields (targets, terrain, "
        + "lore, promises, reagents, lens, scenery) were irrelevant to this spell, or empty string if all were useful\"}. "
        + "This feedback is for tooling only and must not change your resolution, effects, costs, or narration in any way.";

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
        // The provider wire is intentionally not MagicContextView JSON. That view remains rich for
        // the engine, mock provider, audit, and replay; the live resolver receives only the compact
        // target/resource projection below. Operations live in System() and are never duplicated.
        var wireContext = request.Context is MagicContextView view
            ? CompactContext(view)
            : request.Context;
        return JsonSerializer.Serialize(wireContext, WireJsonOptions);
    }

    private static ResolverWireContext CompactContext(MagicContextView view)
    {
        var targets = view.Visible
            .Where(entity => !entity.Id.Equals(view.Caster.Id, StringComparison.OrdinalIgnoreCase))
            .Select(entity => new ResolverWireTarget(
                entity.Id,
                entity.Name,
                new[] { entity.X, entity.Y },
                entity.Faction,
                entity.Material.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : entity.Material,
                entity.Tags.Count == 0 ? null : entity.Tags,
                entity.HitPoints is { } hp ? new[] { hp, entity.MaxHitPoints ?? hp } : null,
                entity.Visibility.Equals("visible", StringComparison.OrdinalIgnoreCase) ? null : entity.Visibility))
            .ToArray();
        var terrain = view.Terrain
            .Select(tile => new ResolverWireTerrain(
                tile.Terrain,
                new[] { tile.X, tile.Y },
                tile.Tags.Contains("blocking", StringComparer.OrdinalIgnoreCase) ? true : null))
            .ToArray();
        var promises = view.KnownPromises
            .Select(promise => new ResolverWirePromise(
                promise.Id,
                promise.Kind,
                promise.Status,
                Shorten(promise.Text, 220),
                EmptyToNull(promise.Subject),
                EmptyToNull(promise.BoundPlace ?? promise.ClaimedPlace),
                EmptyToNull(promise.TriggerHint),
                EmptyToNull(promise.BoundTargetId),
                EmptyToNull(promise.CostProfileId),
                promise.Stacks > 1 ? promise.Stacks : null))
            .ToArray();
        var reagents = (view.Reagents ?? Array.Empty<ReagentCard>())
            .Select(reagent => new ResolverWireReagent(
                reagent.Name,
                reagent.Quantity,
                reagent.Material,
                reagent.Tags.Count == 0 ? null : reagent.Tags,
                EmptyToNull(reagent.SpellBias)))
            .ToArray();
        var lore = (view.Lore ?? Array.Empty<LoreCardView>())
            .Select(card => new ResolverWireLore(card.Id, Shorten(card.Body, 260)))
            .ToArray();
        var scenery = (view.Scenery ?? Array.Empty<SceneryNote>())
            .Select(note => new ResolverWireScenery(
                note.Id,
                note.Name,
                new[] { note.X, note.Y },
                note.Material.Equals("unknown", StringComparison.OrdinalIgnoreCase) ? null : note.Material,
                note.Tags.Count == 0 ? null : note.Tags))
            .ToArray();

        return new ResolverWireContext(
            new ResolverWireCaster(
                view.Caster.Id,
                new[] { view.Caster.X, view.Caster.Y },
                new[] { view.Caster.HitPoints, view.Caster.MaxHitPoints },
                new[] { view.Caster.Mana, view.Caster.MaxMana },
                view.Caster.Statuses.Count == 0
                    ? null
                    : view.Caster.Statuses.Select(status => new ResolverWireStatus(
                        status.Id,
                        EmptyToNull(status.DisplayName),
                        status.Intensity,
                        status.ExpiresTurn)).ToArray()),
            targets.Length == 0 ? null : targets,
            terrain.Length == 0 ? null : terrain,
            view.SelectedTarget is { } selected ? new[] { selected.X, selected.Y } : null,
            promises.Length == 0 ? null : promises,
            view.ResolverLens is { } lens
                ? new ResolverWireLens(
                    lens.Vigor,
                    lens.Attunement,
                    lens.Composure,
                    lens.EffectMagnitudeDelta,
                    EmptyToNull(lens.Signature),
                    lens.Notes.Count == 0 ? null : lens.Notes.Select(note => Shorten(note, 220)).ToArray())
                : null,
            reagents.Length == 0 ? null : reagents,
            lore.Length == 0 ? null : lore,
            scenery.Length == 0 ? null : scenery);
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Shorten(string value, int limit) =>
        value.Length <= limit ? value : value[..limit].TrimEnd() + "…";

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

        var ids = view.Visible
            .Select(entity => entity.Id)
            .Concat((view.Scenery ?? Array.Empty<SceneryNote>()).Select(entity => entity.Id))
            .Take(24)
            .ToArray();
        return ids.Length == 0
            ? ""
            : "Valid target ids: " + string.Join(", ", ids)
              + " (selectors nearest_enemy and selected_target are also valid).";
    }

    /// <summary>
    /// Static on every cast — no per-cast values may appear here, so a local backend can prompt-
    /// cache this whole block. The supported-operation list therefore lives in the variable tail.
    /// Voice: outcome text is grounded and worldly — folk and physical, not cosmic or oracular
    /// (docs/AESTHETICS_AND_TONE.md, narration voice). Wild magic may be strange, but that
    /// strangeness is vivid sensory imagery, never fate-speak about the world or destiny.
    /// </summary>
    private const string CoreRules =
        "You are Sorcerer's wild-magic resolver. Return one JSON object only: "
        + "{\"accepted\":true,\"severity\":\"minor|moderate|major|catastrophic\","
        + "\"outcomeText\":\"short vivid result\",\"effects\":[],\"costs\":[],\"rejectedReason\":null}. "
        + "Effects are flat objects with a type field. Use only supported types and exact target ids from context; "
        + "player means the caster, nearest_enemy means the nearest foe, and selected_target is valid only when context.selected exists. "
        + "Use one mechanical effect per promised outcome. Narration may describe only what those effects make true; "
        + "write concrete sights, sounds, and sensations in a grounded, worldly register -- folk and physical, not cosmic or oracular fate-speak; strangeness is vivid imagery (glass grows warm over the wound), never portent about the world or destiny. "
        + "Binding needs addStatus; healing needs heal; movement, transformation, summoning, social change, and world change need their matching operation. "
        + "Prefer the routed reusable mechanics. targets are important entities; scenery is a small object list; terrain is included only when spatial mechanics need it. "
        + "lens is soft guidance: higher attunement permits stronger effects, lower composure permits stranger answers, vigor informs bodily prices, and signature/place guide imagery. "
        + "reagents are the only carried items available as fuel; an item cost must copy an exact listed name. lore is canon, but becomes mechanical only through effects. "
        + "Almost every real change has a cost. Minor: mana 2-6. Moderate: more mana or a small health/status/reagent toll. "
        + "Major: health, reagent, or curse. Catastrophic: maxHealth/maxMana 1-3 may join another cost. Never add heal/restoreMana unless that is the spell's purpose. "
        + "Answer the largest local version of an overreach at a severe price; reject only win buttons, infinite resources, global rewrites, or requests with no local expression. "
        + "If a missing mechanic is named in the capability menu, you may instead return exactly {\"needsCapability\":\"name\"}; one retry is allowed. "
        + "The engine re-parses, validates, prices, and applies transactionally.";

    /// <summary>
    /// The spell-relevant slice of the shared consequence grammar: a ~300-byte map of everything
    /// the world engine can do beyond direct operations (docs/OPTIMIZATION_PLAN.md WS1.5), so
    /// social/world spells can reach tags, memory, services, routes, canon, or rumors without the
    /// full consequence card being routed in.
    /// </summary>
    private const string ConsequenceTypesLine =
        "World effects use type \"consequence\" with consequenceType: "
        + "apply_status, create_promise, open_or_unlock, create_route, free_captive, modify_inventory, transfer_item, "
        + "add_tags, remove_tags, change_faction, edit_memory, record_memory, update_bond, update_want, "
        + "offer_service, request_service, add_merchant_stock, offer_trade, record_rumor, add_canon, add_legend, "
        + "spawn_fixture, spawn_item, spawn_entity, set_world_flag, adjust_faction_standing, schedule_event, message.";

    // Static across casts, so this compact content-authored menu remains prompt-cacheable. The
    // resolver may select a profile, but the engine still re-resolves the id and applies the
    // resulting journal thread authoritatively.
    private static readonly string CostProfileGuidance = BuildCostProfileGuidance();

    private static string BuildCostProfileGuidance()
    {
        var profiles = CostProfileCatalog.Default.Profiles
            .Where(profile => profile.Kind is "debt" or "curse" or "altered_item")
            .OrderBy(profile => profile.Id, StringComparer.OrdinalIgnoreCase)
            .Select(profile =>
                $"{profile.Id} ({profile.Kind}, {profile.Name}): {Shorten(profile.Condition, 120)} "
                + $"Counterplay: {Shorten(profile.ClearRoutes.FirstOrDefault() ?? "endure it", 100)}")
            .ToArray();
        return "For a story-generating major cost, prefer one supported cost profile as "
            + "{\"type\":\"curse\",\"profileId\":\"exact_id\"}. Available profiles: "
            + string.Join(" | ", profiles)
            + ". Debts create claimants, curses enforce their listed runtime condition, and altered-item profiles modify a concrete carried/focused item; do not invent a profile id.";
    }

    private sealed record ResolverWireContext(
        ResolverWireCaster Caster,
        IReadOnlyList<ResolverWireTarget>? Targets,
        IReadOnlyList<ResolverWireTerrain>? Terrain,
        IReadOnlyList<int>? Selected,
        IReadOnlyList<ResolverWirePromise>? Promises,
        ResolverWireLens? Lens,
        IReadOnlyList<ResolverWireReagent>? Reagents,
        IReadOnlyList<ResolverWireLore>? Lore,
        IReadOnlyList<ResolverWireScenery>? Scenery);

    private sealed record ResolverWireCaster(
        string Id,
        IReadOnlyList<int> At,
        IReadOnlyList<int> Hp,
        IReadOnlyList<int> Mana,
        IReadOnlyList<ResolverWireStatus>? Statuses);

    private sealed record ResolverWireStatus(string Id, string? Name, int Intensity, int? Expires);

    private sealed record ResolverWireTarget(
        string Id,
        string Name,
        IReadOnlyList<int> At,
        string? Faction,
        string? Material,
        IReadOnlyList<string>? Tags,
        IReadOnlyList<int>? Hp,
        string? Visibility);

    private sealed record ResolverWireTerrain(string Type, IReadOnlyList<int> At, bool? Blocking);

    private sealed record ResolverWirePromise(
        string Id,
        string Kind,
        string Status,
        string Text,
        string? Subject,
        string? Place,
        string? Trigger,
        string? Target,
        string? CostProfileId,
        int? Stacks);

    private sealed record ResolverWireLens(
        int Vigor,
        int Attunement,
        int Composure,
        int Power,
        string? Signature,
        IReadOnlyList<string>? Notes);

    private sealed record ResolverWireReagent(
        string Name,
        int Quantity,
        string Material,
        IReadOnlyList<string>? Tags,
        string? Bias);

    private sealed record ResolverWireLore(string Id, string Text);

    private sealed record ResolverWireScenery(
        string Id,
        string Name,
        IReadOnlyList<int> At,
        string? Material,
        IReadOnlyList<string>? Tags);
}
