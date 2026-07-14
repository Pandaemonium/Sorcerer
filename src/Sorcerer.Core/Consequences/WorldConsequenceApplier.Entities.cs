using Sorcerer.Core.Characters;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

/// <summary>
/// <see cref="WorldConsequenceApplier"/> handlers for entity lifecycle: spawn, transform, tags, faction membership, control, souls, behavior, and animation.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplySpawnEntity(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Spawn-entity consequence did not include a tile coordinate.");
        }

        if (!InBounds(point))
        {
            return Reject(consequence, "Spawn-entity consequence target is out of bounds.");
        }

        var name = FirstNonBlank(ReadString(payload, "name"), "summoned wonder")!;
        var prefix = NormalizeToken(FirstNonBlank(ReadString(payload, "prefix"), name)!, "summon");
        var glyph = ReadChar(payload, "glyph", '*');
        var faction = NormalizeToken(FirstNonBlank(ReadString(payload, "faction"), "player")!, "player");
        var hp = Math.Clamp(ReadInt(payload, "hp") ?? 5, 1, 999);
        var attack = Math.Clamp(ReadInt(payload, "attack") ?? 2, 0, 999);
        var tags = ReadStringList(payload, "tags").Count == 0 ? new[] { "summoned", "wild_magic" } : ReadStringList(payload, "tags");
        var material = NormalizeToken(FirstNonBlank(ReadString(payload, "material"), "summoned")!, "summoned");
        var roles = NormalizeTags(ReadStringList(payload, "roles").Concat(ReadStringList(payload, "factionRoles")));
        if (roles.Count == 0)
        {
            roles = new[] { faction };
        }

        var controllerKindText = FirstNonBlank(
            ReadString(payload, "controllerKind"),
            ReadString(payload, "controller_kind"),
            ReadString(payload, "controller"),
            ReadString(payload, "kind"));
        ControllerKind? controllerKind = null;
        if (!string.IsNullOrWhiteSpace(controllerKindText))
        {
            if (!TryReadControllerKind(controllerKindText, out var parsedControllerKind))
            {
                return Reject(consequence, "Spawn-entity consequence included an invalid controller kind.");
            }

            controllerKind = parsedControllerKind;
        }

        var aiPolicy = FirstNonBlank(
            ReadString(payload, "aiPolicyId"),
            ReadString(payload, "ai_policy_id"),
            ReadString(payload, "aiPolicy"),
            ReadString(payload, "ai_policy"),
            ReadString(payload, "policyId"),
            ReadString(payload, "policy"));
        var summoned = ReadBool(payload, "summoned") ?? true;
        var description = ReadString(payload, "description");
        var profileName = FirstNonBlank(ReadString(payload, "profileName"), ReadString(payload, "profile_name"));
        var profileAppearance = FirstNonBlank(
            ReadString(payload, "profileAppearance"),
            ReadString(payload, "profile_appearance"),
            description);
        var profileOrigin = FirstNonBlank(ReadString(payload, "profileOrigin"), ReadString(payload, "profile_origin"), ReadString(payload, "origin"));
        var profileMagicalSignature = FirstNonBlank(
            ReadString(payload, "profileMagicalSignature"),
            ReadString(payload, "profile_magical_signature"),
            ReadString(payload, "magicalSignature"),
            ReadString(payload, "magical_signature"));
        var profileBackstory = FirstNonBlank(ReadString(payload, "profileBackstory"), ReadString(payload, "profile_backstory"), ReadString(payload, "backstory"));
        var promiseIds = ReadStringList(payload, "promiseIds").Concat(ReadStringList(payload, "promise_ids")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var interactableVerbs = ReadStringList(payload, "interactableVerbs").Concat(ReadStringList(payload, "verbs")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var claimSeeds = ReadSpawnClaimSeeds(payload);
        var bodyVigor = ReadInt(payload, "bodyVigor") ?? ReadInt(payload, "body_vigor");
        var includeMemory = ReadBool(payload, "includeMemory") ?? ReadBool(payload, "include_memory") ?? false;
        var wantText = FirstNonBlank(ReadString(payload, "wantText"), ReadString(payload, "want_text"), ReadString(payload, "want"));
        var wantId = FirstNonBlank(ReadString(payload, "wantId"), ReadString(payload, "want_id"));
        var wantStatus = FirstNonBlank(ReadString(payload, "wantStatus"), ReadString(payload, "want_status"), "active")!;
        var wantStakes = FirstNonBlank(ReadString(payload, "wantStakes"), ReadString(payload, "want_stakes"), "")!;
        var wantSalience = Math.Clamp(ReadInt(payload, "wantSalience") ?? ReadInt(payload, "want_salience") ?? 2, 1, 5);
        var wantTags = ReadStringList(payload, "wantTags")
            .Concat(ReadStringList(payload, "want_tags"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var autoWant = ReadBool(payload, "autoWant") ?? ReadBool(payload, "auto_want") ?? true;
        var requestedEntityId = FirstNonBlank(ReadString(payload, "entityId"), ReadString(payload, "entity_id"), ReadString(payload, "id"));
        var entityId = string.IsNullOrWhiteSpace(requestedEntityId)
            ? _state.NextEntityId(prefix)
            : EntityId.Create(NormalizeToken(requestedEntityId, prefix));
        if (_state.Entities.ContainsKey(entityId))
        {
            return Reject(consequence, $"Spawn-entity target already exists: {entityId.Value}");
        }

        var entity = new Entity(entityId, name)
            .Set(new PositionComponent(point))
            .Set(new RenderableComponent(glyph, faction))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: material))
            .Set(new ActorComponent(hp, hp, 0, 0, attack, 0, faction))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent(faction == "player" ? "ally" : "hostile_guard"))
            .Set(StatusContainerComponent.Empty())
            .Set(new BodyStatsComponent(Math.Max(1, bodyVigor ?? 3)))
            .Set(new SoulComponent($"{entityId.Value}_soul"));
        _state.Entities.Add(entity.Id, entity);
        var source = FirstNonBlank(consequence.SourceEntityId, ReadString(payload, "summonedBy"), _state.ControlledEntityId.Value)!;

        if (!string.IsNullOrWhiteSpace(description))
        {
            entity.Set(new DescriptionComponent(description));
        }

        if (!string.IsNullOrWhiteSpace(profileName) || !string.IsNullOrWhiteSpace(profileAppearance))
        {
            entity.Set(new ProfileComponent(
                FirstNonBlank(profileName, name)!,
                FirstNonBlank(profileAppearance, description, name)!,
                Origin: profileOrigin ?? "",
                MagicalSignature: profileMagicalSignature ?? "",
                Backstory: profileBackstory ?? ""));
        }

        entity.Set(new FactionComponent(faction, roles));
        if (controllerKind is { } kind)
        {
            entity.Set(new ControllerComponent(kind));
        }

        if (!string.IsNullOrWhiteSpace(aiPolicy))
        {
            entity.Set(new AiComponent(NormalizeToken(aiPolicy, "idle")));
        }

        if (summoned)
        {
            entity.Set(new SummonedComponent(source));
        }

        if (promiseIds.Length > 0)
        {
            entity.Set(new PromiseAnchorComponent(promiseIds));
        }

        if (interactableVerbs.Length > 0)
        {
            entity.Set(new InteractableComponent(interactableVerbs));
        }

        if (claimSeeds.Count > 0)
        {
            entity.Set(new ClaimSourceComponent(claimSeeds));
        }

        if (includeMemory)
        {
            entity.Set(MemoryComponent.Empty());
        }

        var explicitWant = !string.IsNullOrWhiteSpace(wantText);
        var spawnedWant = SpawnedWantFactory.Create(
            entity.Id.Value,
            entity.Name,
            _state.Factions.RoleOf(faction),
            tags,
            roles,
            interactableVerbs,
            summoned,
            includeMemory,
            aiPolicy,
            promiseIds,
            wantText,
            wantId,
            wantStatus,
            wantStakes,
            wantSalience,
            wantTags,
            autoWant);
        if (spawnedWant is not null)
        {
            entity.Set(spawnedWant);
        }

        if (!entity.Has<KnowledgeComponent>()
            && ShouldSeedDialogueKnowledge(entity, tags, roles, interactableVerbs, summoned, includeMemory))
        {
            entity.Set(DialogueKnowledgeProfile.For(entity, _state.RegionId));
        }

        var operation = ReadString(payload, "operation") ?? "summon";
        var summary = FirstNonBlank(ReadString(payload, "message"), $"{name} appears at {point.X},{point.Y}.")!;
        AddMessageIfAllowed(consequence, payload, summary);

        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("faction", faction),
                ("roles", roles),
                ("tags", tags),
                ("material", material),
                ("controllerKind", entity.TryGet<ControllerComponent>(out var controller) ? controller.Kind.ToString() : ""),
                ("aiPolicyId", entity.TryGet<AiComponent>(out var ai) ? ai.PolicyId : ""),
                ("summoned", summoned),
                ("promiseIds", promiseIds),
                ("interactableVerbs", interactableVerbs),
                ("claimSeedCount", claimSeeds.Count),
                ("explicitEntityId", !string.IsNullOrWhiteSpace(requestedEntityId)),
                ("profileOrigin", profileOrigin),
                ("profileMagicalSignature", profileMagicalSignature),
                ("wantId", entity.TryGet<WantComponent>(out var want) ? want.Id : null),
                ("wantGenerated", entity.Has<WantComponent>() && !explicitWant)));
        return AppliedFromDelta(consequence, delta);
    }

    private static bool ShouldSeedDialogueKnowledge(
        Entity entity,
        IReadOnlyList<string> tags,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> interactableVerbs,
        bool summoned,
        bool includeMemory)
    {
        if (summoned && !entity.Has<WantComponent>() && interactableVerbs.Count == 0)
        {
            return false;
        }

        if (entity.Has<WantComponent>() || includeMemory || interactableVerbs.Contains("talk", StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        return HasAny(tags, "npc", "resident", "merchant", "witness", "prisoner", "soldier", "guard", "clerk")
            || HasAny(roles, "resident", "merchant", "witness", "prisoner", "soldier", "empire", "military", "functionary");
    }

    private static bool HasAny(IReadOnlyList<string> values, params string[] expected) =>
        expected.Any(value => values.Contains(value, StringComparer.OrdinalIgnoreCase));

    private static IReadOnlyList<ClaimSeed> ReadSpawnClaimSeeds(IReadOnlyDictionary<string, object?> payload)
    {
        if (!payload.TryGetValue("claimSeeds", out var raw) || raw is null)
        {
            return Array.Empty<ClaimSeed>();
        }

        return raw switch
        {
            IReadOnlyList<ClaimSeed> claims => claims.ToArray(),
            IEnumerable<ClaimSeed> claims => claims.ToArray(),
            ClaimSeed claim => new[] { claim },
            _ => Array.Empty<ClaimSeed>(),
        };
    }

    private WorldConsequenceApplyResult ApplySpawnItem(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Spawn-item consequence did not include a tile coordinate.");
        }

        if (!InBounds(point))
        {
            return Reject(consequence, "Spawn-item consequence target is out of bounds.");
        }

        var name = FirstNonBlank(ReadString(payload, "name"), "conjured curio")!;
        var prefix = NormalizeToken(FirstNonBlank(ReadString(payload, "prefix"), name)!, "item");
        var glyph = ReadChar(payload, "glyph", '*');
        var itemType = NormalizeToken(FirstNonBlank(ReadString(payload, "itemType"), ReadString(payload, "item_type"), "curio")!, "curio");
        var material = NormalizeToken(FirstNonBlank(ReadString(payload, "material"), "matter")!, "matter");
        var tags = ReadStringList(payload, "tags").Count == 0 ? new[] { "conjured" } : ReadStringList(payload, "tags");
        var quantity = Math.Clamp(ReadInt(payload, "quantity") ?? ReadInt(payload, "count") ?? 1, 1, 999);
        var value = Math.Clamp(ReadInt(payload, "value") ?? 1, 0, 9999);
        var stackPolicy = NormalizeToken(FirstNonBlank(ReadString(payload, "stackPolicy"), ReadString(payload, "stack_policy"), "commodity")!, "commodity");
        var useProfile = NormalizeToken(FirstNonBlank(ReadString(payload, "useProfile"), ReadString(payload, "use_profile"), "inert")!, "inert");
        var equipmentSlot = FirstNonBlank(ReadString(payload, "equipmentSlot"), ReadString(payload, "equipment_slot"));
        var description = ReadString(payload, "description");
        var promiseIds = ReadStringList(payload, "promiseIds").Concat(ReadStringList(payload, "promise_ids")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var item = new Entity(_state.NextEntityId(prefix), name)
            .Set(new PositionComponent(point))
            .Set(new RenderableComponent(glyph, "item"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: material))
            .Set(new ItemComponent(itemType, value, material, tags, stackPolicy, useProfile, equipmentSlot))
            .Set(new StackComponent(quantity));
        _state.Entities.Add(item.Id, item);
        if (!string.IsNullOrWhiteSpace(description))
        {
            item.Set(new DescriptionComponent(description));
        }

        if (promiseIds.Length > 0)
        {
            item.Set(new PromiseAnchorComponent(promiseIds));
        }

        var operation = ReadString(payload, "operation") ?? "conjureItem";
        var summary = FirstNonBlank(ReadString(payload, "message"), $"{name} appears at {point.X},{point.Y}.")!;
        AddMessageIfAllowed(consequence, payload, summary);

        var delta = new StateDelta(
            operation,
            item.Id.Value,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("itemType", itemType),
                ("material", material),
                ("quantity", quantity),
                ("tags", tags),
                ("stackPolicy", stackPolicy),
                ("useProfile", useProfile),
                ("equipmentSlot", equipmentSlot),
                ("promiseIds", promiseIds)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplySpawnFixture(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Spawn-fixture consequence did not include a tile coordinate.");
        }

        if (!InBounds(point))
        {
            return Reject(consequence, "Spawn-fixture consequence target is out of bounds.");
        }

        var name = FirstNonBlank(ReadString(payload, "name"), "strange feature")!;
        var prefix = NormalizeToken(FirstNonBlank(ReadString(payload, "prefix"), name)!, "fixture");
        var glyph = ReadChar(payload, "glyph", '?');
        var fixtureType = NormalizeToken(FirstNonBlank(ReadString(payload, "fixtureType"), ReadString(payload, "fixture_type"), "feature")!, "feature");
        var palette = NormalizeToken(FirstNonBlank(ReadString(payload, "palette"), fixtureType)!, "fixture");
        var material = NormalizeToken(FirstNonBlank(ReadString(payload, "material"), "stone")!, "stone");
        var tags = NormalizeTags(ReadStringList(payload, "tags").Concat(new[] { "fixture", fixtureType }));
        var blocksMovement = ReadBool(payload, "blocksMovement") ?? ReadBool(payload, "blocks_movement") ?? true;
        var blocksSight = ReadBool(payload, "blocksSight") ?? ReadBool(payload, "blocks_sight") ?? false;
        var size = Math.Clamp(ReadInt(payload, "size") ?? 1, 1, 999);
        var durability = Math.Clamp(ReadInt(payload, "durability") ?? 0, 0, 9999);
        var description = ReadString(payload, "description");
        var promiseIds = ReadStringList(payload, "promiseIds").Concat(ReadStringList(payload, "promise_ids")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var readableTitle = FirstNonBlank(ReadString(payload, "readableTitle"), ReadString(payload, "readable_title"));
        var readableText = FirstNonBlank(ReadString(payload, "readableText"), ReadString(payload, "readable_text"));
        var canAnchorMagic = ReadBool(payload, "canAnchorMagic") ?? ReadBool(payload, "can_anchor_magic") ?? true;
        var interiorEntrance = payload.TryGetValue("interiorEntrance", out var rawEntrance)
            ? rawEntrance as InteriorEntranceComponent
            : null;
        var interiorExit = payload.TryGetValue("interiorExit", out var rawExit)
            ? rawExit as InteriorExitComponent
            : null;
        var interactableVerbs = ReadStringList(payload, "interactableVerbs")
            .Concat(ReadStringList(payload, "verbs"))
            .Concat(string.IsNullOrWhiteSpace(readableTitle) && string.IsNullOrWhiteSpace(readableText)
                ? Array.Empty<string>()
                : new[] { "read" })
            .Concat(interiorEntrance is null ? Array.Empty<string>() : new[] { "enter" })
            .Concat(interiorExit is null ? Array.Empty<string>() : new[] { "leave" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var fixtureId = _state.NextEntityId(prefix);
        var fixture = new Entity(fixtureId, name)
            .Set(new PositionComponent(point))
            .Set(new RenderableComponent(glyph, palette))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(blocksMovement, blocksSight, material, size, durability))
            .Set(new FixtureComponent(fixtureType, tags, canAnchorMagic));
        if (!string.IsNullOrWhiteSpace(description))
        {
            fixture.Set(new DescriptionComponent(description));
        }

        if (promiseIds.Length > 0)
        {
            fixture.Set(new PromiseAnchorComponent(promiseIds));
        }

        if (!string.IsNullOrWhiteSpace(readableTitle) || !string.IsNullOrWhiteSpace(readableText))
        {
            fixture.Set(new ReadableComponent(
                FirstNonBlank(readableTitle, name)!,
                FirstNonBlank(readableText, readableTitle, name)!));
        }

        if (interiorEntrance is not null)
        {
            fixture.Set(interiorEntrance);
        }

        if (interiorExit is not null)
        {
            fixture.Set(interiorExit);
        }

        if (interactableVerbs.Length > 0)
        {
            fixture.Set(new InteractableComponent(interactableVerbs));
        }

        _state.Entities[fixtureId] = fixture;
        var operation = ReadString(payload, "operation") ?? "spawnFixture";
        var summary = FirstNonBlank(ReadString(payload, "message"), $"{name} takes shape at {point.X},{point.Y}.")!;
        AddMessageIfAllowed(consequence, payload, summary);

        var delta = new StateDelta(
            operation,
            fixture.Id.Value,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("fixtureType", fixtureType),
                ("material", material),
                ("blocksMovement", blocksMovement),
                ("blocksSight", blocksSight),
                ("size", size),
                ("durability", durability),
                ("tags", tags),
                ("promiseIds", promiseIds),
                ("interactableVerbs", interactableVerbs),
                ("canAnchorMagic", canAnchorMagic),
                ("readableTitle", readableTitle),
                ("interiorZoneId", interiorEntrance?.InteriorZoneId),
                ("interiorId", interiorEntrance?.InteriorId ?? interiorExit?.InteriorId),
                ("exteriorZoneId", interiorEntrance?.ExteriorZoneId ?? interiorExit?.ExteriorZoneId)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyAddTags(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Add-tags consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var tags = NormalizeTags(ReadStringList(payload, "tags").Concat(ReadStringList(payload, "tag")));
        if (tags.Count == 0)
        {
            return Reject(consequence, "Add-tags consequence did not include tags.");
        }

        var current = target.Entity!.TryGet<TagsComponent>(out var existing)
            ? existing.Tags.ToList()
            : new List<string>();
        foreach (var tag in tags)
        {
            if (!current.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                current.Add(tag);
            }
        }

        target.Entity.Set(new TagsComponent(current));
        var operation = ReadString(payload, "operation") ?? "addTag";
        var summary = $"{target.Entity.Name} gains {string.Join(", ", tags)}.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("tags", tags)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("tags", tags));
    }

    private WorldConsequenceApplyResult ApplyRemoveTags(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Remove-tags consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var tags = NormalizeTags(ReadStringList(payload, "tags").Concat(ReadStringList(payload, "tag")));
        if (tags.Count == 0)
        {
            return Reject(consequence, "Remove-tags consequence did not include tags.");
        }

        var current = target.Entity!.TryGet<TagsComponent>(out var existing)
            ? existing.Tags.ToList()
            : new List<string>();
        current.RemoveAll(tag => tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
        target.Entity.Set(new TagsComponent(current));
        var operation = ReadString(payload, "operation") ?? "removeTag";
        var summary = $"{target.Entity.Name} loses {string.Join(", ", tags)}.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("tags", tags)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("tags", tags));
    }

    private WorldConsequenceApplyResult ApplyChangeFaction(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Faction consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<ActorComponent>(out var actor))
        {
            return Reject(consequence, "Faction consequence target is not an actor.");
        }

        var faction = NormalizeToken(FirstNonBlank(ReadString(payload, "faction"), ReadString(payload, "factionId"), "player")!, "player");
        var roles = NormalizeTags(ReadStringList(payload, "roles"));
        if (roles.Count == 0)
        {
            roles = new[] { faction };
        }

        var existingMembership = target.Entity.TryGet<FactionComponent>(out var membership)
            ? membership
            : null;
        var preserveMembership = ReadBool(payload, "preserveMembership")
            ?? ReadBool(payload, "preserve_membership")
            ?? false;
        var membershipFactionId = preserveMembership
            ? FirstNonBlank(ReadString(payload, "membershipFactionId"), ReadString(payload, "membership_faction_id"), existingMembership?.FactionId, faction)!
            : faction;
        var membershipRoles = preserveMembership && existingMembership is not null
            ? NormalizeTags(existingMembership.Roles.Concat(roles))
            : roles;

        target.Entity.Set(actor with { Faction = faction });
        target.Entity.Set(new FactionComponent(membershipFactionId, membershipRoles));
        var operation = ReadString(payload, "operation") ?? "changeFaction";
        var summary = $"{target.Entity.Name} now answers to {faction}.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(
                consequence,
                ("faction", faction),
                ("roles", roles),
                ("membershipFactionId", membershipFactionId),
                ("membershipRoles", membershipRoles),
                ("preserveMembership", preserveMembership)));
        return Applied(
            consequence,
            target.Entity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("faction", faction),
            ("roles", roles),
            ("membershipFactionId", membershipFactionId),
            ("membershipRoles", membershipRoles));
    }

    private WorldConsequenceApplyResult ApplyUpdateControl(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Control consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var kindText = FirstNonBlank(
            ReadString(payload, "controllerKind"),
            ReadString(payload, "controller_kind"),
            ReadString(payload, "controller"),
            ReadString(payload, "kind"));
        if (!TryReadControllerKind(kindText, out var controllerKind))
        {
            return Reject(consequence, "Control consequence did not include a valid controller kind.");
        }

        var previousController = target.Entity!.TryGet<ControllerComponent>(out var controller)
            ? controller.Kind
            : ControllerKind.None;
        var previousAiPolicy = target.Entity.TryGet<AiComponent>(out var existingAi)
            ? existingAi.PolicyId
            : "";
        var aiPolicy = FirstNonBlank(
            ReadString(payload, "aiPolicyId"),
            ReadString(payload, "ai_policy_id"),
            ReadString(payload, "aiPolicy"),
            ReadString(payload, "ai_policy"),
            ReadString(payload, "policyId"),
            ReadString(payload, "policy"));
        var aiParameters = ReadDictionary(payload, "aiParameters")
            ?? ReadDictionary(payload, "ai_parameters")
            ?? ReadDictionary(payload, "parameters");
        var removeAi = ReadBool(payload, "removeAi") ?? ReadBool(payload, "remove_ai") ?? false;

        target.Entity.Set(new ControllerComponent(controllerKind));
        if (!string.IsNullOrWhiteSpace(aiPolicy))
        {
            target.Entity.Set(new AiComponent(NormalizeToken(aiPolicy, "idle"), aiParameters));
        }
        else if (removeAi)
        {
            target.Entity.Remove<AiComponent>();
        }

        var currentAiPolicy = target.Entity.TryGet<AiComponent>(out var updatedAi)
            ? updatedAi.PolicyId
            : "";
        var operation = ReadString(payload, "operation") ?? "updateControl";
        var summary = $"{target.Entity.Name} is now controlled by {controllerKind.ToString().ToLowerInvariant()}.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(
                consequence,
                ("previousControllerKind", previousController.ToString()),
                ("controllerKind", controllerKind.ToString()),
                ("previousAiPolicyId", previousAiPolicy),
                ("aiPolicyId", currentAiPolicy),
                ("removeAi", removeAi)));
        return Applied(
            consequence,
            target.Entity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("controllerKind", controllerKind.ToString()),
            ("aiPolicyId", currentAiPolicy));
    }

    private WorldConsequenceApplyResult ApplySetControlledEntity(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var targetId = FirstNonBlank(
            ReadString(payload, "targetEntityId"),
            ReadString(payload, "entityId"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Reject(consequence, "Controlled-entity consequence did not include an entity id.");
        }

        var entity = EntityById(targetId);
        if (entity is null)
        {
            return Reject(consequence, $"Controlled entity does not exist: {targetId}");
        }

        var previous = _state.ControlledEntityId.Value;
        _state.ControlledEntityId = entity.Id;

        var operation = ReadString(payload, "operation") ?? "setControlledEntity";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"Controlled entity is now {entity.Name}.")!;
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("previousControlledEntityId", previous),
                ("controlledEntityId", entity.Id.Value),
                ("controlledEntityName", entity.Name)));
        return Applied(
            consequence,
            entity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("previousControlledEntityId", previous),
            ("controlledEntityId", entity.Id.Value),
            ("controlledEntityName", entity.Name));
    }

    private WorldConsequenceApplyResult ApplySwapSouls(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var firstEntityId = FirstNonBlank(
            ReadString(payload, "firstEntityId"),
            ReadString(payload, "first_entity_id"),
            ReadString(payload, "oldBody"),
            ReadString(payload, "first"),
            consequence.TargetEntityId);
        var secondEntityId = FirstNonBlank(
            ReadString(payload, "secondEntityId"),
            ReadString(payload, "second_entity_id"),
            ReadString(payload, "newBody"),
            ReadString(payload, "second"));
        if (string.IsNullOrWhiteSpace(firstEntityId) || string.IsNullOrWhiteSpace(secondEntityId))
        {
            return Reject(consequence, "Soul-swap consequence did not include two entity ids.");
        }

        if (firstEntityId.Equals(secondEntityId, StringComparison.OrdinalIgnoreCase))
        {
            return Reject(consequence, "Soul-swap consequence cannot target the same entity twice.");
        }

        var firstEntity = EntityById(firstEntityId);
        if (firstEntity is null)
        {
            return Reject(consequence, $"Soul-swap first entity does not exist: {firstEntityId}");
        }

        var secondEntity = EntityById(secondEntityId);
        if (secondEntity is null)
        {
            return Reject(consequence, $"Soul-swap second entity does not exist: {secondEntityId}");
        }

        if (!firstEntity.TryGet<ActorComponent>(out var firstActor))
        {
            return Reject(consequence, "Soul-swap first entity is not an actor.");
        }

        if (!secondEntity.TryGet<ActorComponent>(out var secondActor))
        {
            return Reject(consequence, "Soul-swap second entity is not an actor.");
        }

        var firstSoul = firstEntity.TryGet<SoulComponent>(out var firstSoulComponent)
            ? firstSoulComponent
            : new SoulComponent($"{firstEntity.Id.Value}_soul");
        var secondSoul = secondEntity.TryGet<SoulComponent>(out var secondSoulComponent)
            ? secondSoulComponent
            : new SoulComponent($"{secondEntity.Id.Value}_soul");
        var firstSoulRecord = CharacterMath.EnsureSoulRecord(_state, firstEntity, firstSoul.SoulId);
        var secondSoulRecord = CharacterMath.EnsureSoulRecord(_state, secondEntity, secondSoul.SoulId);

        firstEntity.Set(secondSoul);
        secondEntity.Set(firstSoul);
        firstEntity.Set(CharacterMath.ActorWithSoulMana(firstActor, secondSoulRecord));
        secondEntity.Set(CharacterMath.ActorWithSoulMana(secondActor, firstSoulRecord));
        CharacterMath.SyncActorFromBodyAndSoul(firstEntity, secondSoulRecord);
        CharacterMath.SyncActorFromBodyAndSoul(secondEntity, firstSoulRecord);

        var firstUpdatedActor = firstEntity.Get<ActorComponent>();
        var secondUpdatedActor = secondEntity.Get<ActorComponent>();
        var operation = ReadString(payload, "operation") ?? "swapSouls";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"{firstEntity.Name} and {secondEntity.Name} exchange souls.")!;
        var delta = new StateDelta(
            operation,
            firstEntity.Id.Value,
            summary,
            Details(
                consequence,
                ("firstEntityId", firstEntity.Id.Value),
                ("secondEntityId", secondEntity.Id.Value),
                ("firstEntityName", firstEntity.Name),
                ("secondEntityName", secondEntity.Name),
                ("firstSoulBefore", firstSoul.SoulId),
                ("secondSoulBefore", secondSoul.SoulId),
                ("firstSoulAfter", secondSoul.SoulId),
                ("secondSoulAfter", firstSoul.SoulId),
                ("firstManaAfter", firstUpdatedActor.Mana),
                ("firstMaxManaAfter", firstUpdatedActor.MaxMana),
                ("secondManaAfter", secondUpdatedActor.Mana),
                ("secondMaxManaAfter", secondUpdatedActor.MaxMana)));
        return Applied(
            consequence,
            firstEntity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("firstEntityId", firstEntity.Id.Value),
            ("secondEntityId", secondEntity.Id.Value),
            ("firstSoulAfter", secondSoul.SoulId),
            ("secondSoulAfter", firstSoul.SoulId));
    }

    private WorldConsequenceApplyResult ApplyTransformEntity(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Transform consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var entity = target.Entity!;
        var before = entity.Name;
        var newName = FirstNonBlank(ReadString(payload, "name"), ReadString(payload, "newName"), ReadString(payload, "new_name"));
        if (!string.IsNullOrWhiteSpace(newName))
        {
            entity.Name = newName;
        }

        var material = FirstNonBlank(ReadString(payload, "material"), ReadString(payload, "newMaterial"), ReadString(payload, "new_material"));
        var blocksMovement = ReadBool(payload, "blocksMovement") ?? ReadBool(payload, "blocks_movement");
        var blocksSight = ReadBool(payload, "blocksSight") ?? ReadBool(payload, "blocks_sight");
        var size = ReadInt(payload, "size");
        var durability = ReadInt(payload, "durability");
        string? currentMaterial = null;
        if (!string.IsNullOrWhiteSpace(material)
            || blocksMovement.HasValue
            || blocksSight.HasValue
            || size.HasValue
            || durability.HasValue)
        {
            var physical = entity.TryGet<PhysicalComponent>(out var existingPhysical)
                ? existingPhysical
                : new PhysicalComponent();
            var normalizedMaterial = !string.IsNullOrWhiteSpace(material)
                ? NormalizeToken(material, "changed")
                : physical.Material;
            var nextPhysical = physical with
            {
                Material = normalizedMaterial,
                BlocksMovement = blocksMovement ?? physical.BlocksMovement,
                BlocksSight = blocksSight ?? physical.BlocksSight,
                Size = Math.Clamp(size ?? physical.Size, 1, 999),
                Durability = Math.Clamp(durability ?? physical.Durability, 0, 9999),
            };
            entity.Set(nextPhysical);
            currentMaterial = nextPhysical.Material;
            if (!string.IsNullOrWhiteSpace(material) && entity.TryGet<ItemComponent>(out var item))
            {
                entity.Set(item with { Material = normalizedMaterial });
            }
        }

        var addTags = NormalizeTags(ReadStringList(payload, "tags")
            .Concat(ReadStringList(payload, "addTags"))
            .Concat(ReadStringList(payload, "add_tags"))
            .Concat(ReadStringList(payload, "tag")));
        var removeTags = NormalizeTags(ReadStringList(payload, "removeTags")
            .Concat(ReadStringList(payload, "remove_tags"))
            .Concat(ReadStringList(payload, "withoutTags"))
            .Concat(ReadStringList(payload, "without_tags")));
        if (addTags.Count > 0 || removeTags.Count > 0)
        {
            var current = entity.TryGet<TagsComponent>(out var existingTags)
                ? existingTags.Tags.ToList()
                : new List<string>();
            if (removeTags.Count > 0)
            {
                current = current
                    .Where(tag => !removeTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            foreach (var tag in addTags)
            {
                if (!current.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    current.Add(tag);
                }
            }

            entity.Set(new TagsComponent(current));
        }

        var glyphText = ReadString(payload, "glyph");
        var palette = FirstNonBlank(ReadString(payload, "palette"), ReadString(payload, "renderPalette"), ReadString(payload, "render_palette"));
        if (!string.IsNullOrWhiteSpace(glyphText) || !string.IsNullOrWhiteSpace(palette))
        {
            var renderable = entity.TryGet<RenderableComponent>(out var existingRenderable)
                ? existingRenderable
                : new RenderableComponent('?');
            entity.Set(renderable with
            {
                Glyph = string.IsNullOrWhiteSpace(glyphText) ? renderable.Glyph : glyphText.Trim()[0],
                Palette = string.IsNullOrWhiteSpace(palette) ? renderable.Palette : NormalizeToken(palette, renderable.Palette),
            });
        }

        var fixtureType = FirstNonBlank(ReadString(payload, "fixtureType"), ReadString(payload, "fixture_type"));
        var canAnchorMagic = ReadBool(payload, "canAnchorMagic") ?? ReadBool(payload, "can_anchor_magic");
        if (entity.TryGet<FixtureComponent>(out var existingFixture)
            || !string.IsNullOrWhiteSpace(fixtureType)
            || canAnchorMagic.HasValue)
        {
            var normalizedFixtureType = NormalizeToken(
                FirstNonBlank(fixtureType, existingFixture?.FixtureType, "feature")!,
                "feature");
            var fixtureTags = entity.TryGet<TagsComponent>(out var transformedTags)
                ? transformedTags.Tags.ToList()
                : existingFixture?.Tags.ToList() ?? new List<string>();
            foreach (var tag in new[] { "fixture", normalizedFixtureType })
            {
                if (!fixtureTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    fixtureTags.Add(tag);
                }
            }

            var normalizedFixtureTags = NormalizeTags(fixtureTags);
            entity.Set(new FixtureComponent(
                normalizedFixtureType,
                normalizedFixtureTags,
                canAnchorMagic ?? existingFixture?.CanAnchorMagic ?? true));
            entity.Set(new TagsComponent(normalizedFixtureTags));
        }

        var interactableVerbs = NormalizeTags(ReadStringList(payload, "interactableVerbs")
            .Concat(ReadStringList(payload, "interactable_verbs"))
            .Concat(ReadStringList(payload, "verbs")));
        if (interactableVerbs.Count > 0)
        {
            EnsureInteractableVerbs(entity, interactableVerbs.ToArray());
        }

        var description = FirstNonBlank(ReadString(payload, "description"), ReadString(payload, "detail"));
        if (!string.IsNullOrWhiteSpace(description))
        {
            entity.Set(new DescriptionComponent(description));
        }

        var operation = ReadString(payload, "operation") ?? "transformEntity";
        currentMaterial ??= entity.TryGet<PhysicalComponent>(out var transformedPhysical)
            ? transformedPhysical.Material
            : entity.TryGet<ItemComponent>(out var transformedItem)
                ? transformedItem.Material
                : null;
        var becomesVerb = Verb(entity, "become", "becomes");
        var summary = entity.Name.Equals(before, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(currentMaterial)
            ? $"{before} {becomesVerb} {currentMaterial.Replace('_', ' ')}."
            : $"{before} {becomesVerb} {entity.Name}.";
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("before", before),
                ("after", entity.Name),
                ("material", currentMaterial),
                ("addTags", addTags),
                ("removeTags", removeTags),
                ("blocksMovement", entity.TryGet<PhysicalComponent>(out var finalPhysical) ? finalPhysical.BlocksMovement : null),
                ("blocksSight", entity.TryGet<PhysicalComponent>(out finalPhysical) ? finalPhysical.BlocksSight : null),
                ("glyph", entity.TryGet<RenderableComponent>(out var finalRenderable) ? finalRenderable.Glyph.ToString() : null),
                ("palette", entity.TryGet<RenderableComponent>(out finalRenderable) ? finalRenderable.Palette : null),
                ("fixtureType", entity.TryGet<FixtureComponent>(out var finalFixture) ? finalFixture.FixtureType : null),
                ("interactableVerbs", entity.TryGet<InteractableComponent>(out var finalInteractable) ? finalInteractable.Verbs : Array.Empty<string>())));
        return Applied(consequence, entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("before", before), ("after", entity.Name));
    }

    private WorldConsequenceApplyResult ApplySetBehavior(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Behavior consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var tag = NormalizeToken(FirstNonBlank(ReadString(payload, "tag"), ReadString(payload, "behavior"), "marked")!, "marked");
        var duration = Math.Clamp(ReadInt(payload, "duration") ?? 0, 0, 999);
        var tags = target.Entity!.TryGet<BehaviorTagsComponent>(out var existing)
            ? new Dictionary<string, int?>(existing.Tags, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        tags[tag] = duration > 0 ? _state.Turn + duration : null;
        target.Entity.Set(new BehaviorTagsComponent(tags));
        var operation = ReadString(payload, "operation") ?? "setBehavior";
        var summary = $"{target.Entity.Name} falls under a {tag.Replace('_', ' ')} compulsion.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("tag", tag), ("duration", duration), ("expiresTurn", tags[tag])));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("tag", tag), ("duration", duration));
    }

    private WorldConsequenceApplyResult ApplyUpdateBehavior(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Behavior update consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<BehaviorTagsComponent>(out var behaviors))
        {
            return Reject(consequence, "Behavior update target has no behavior tags.");
        }

        var tag = NormalizeToken(FirstNonBlank(ReadString(payload, "tag"), ReadString(payload, "behavior"), "marked")!, "marked");
        if (!behaviors.Tags.TryGetValue(tag, out var previousExpiry))
        {
            return Reject(consequence, $"Behavior update target does not have tag: {tag}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "remove")!, "remove");
        var verb = ClassifyTerminalAction(action) switch
        {
            TerminalUpdateAction.Complete => "completes",
            TerminalUpdateAction.Expire => "expires",
            TerminalUpdateAction.Remove => "is removed",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported behavior update action: {action}.");
        }

        var updated = new Dictionary<string, int?>(behaviors.Tags, StringComparer.OrdinalIgnoreCase);
        updated.Remove(tag);
        if (updated.Count == 0)
        {
            target.Entity.Remove<BehaviorTagsComponent>();
        }
        else
        {
            target.Entity.Set(new BehaviorTagsComponent(updated));
        }

        var operation = ReadString(payload, "operation") ?? "updateBehavior";
        var summary = $"{Possessive(target.Entity)} {tag.Replace('_', ' ')} compulsion {verb}.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(
                consequence,
                ("tag", tag),
                ("action", action),
                ("previousExpiresTurn", previousExpiry),
                ("remainingTags", updated.Count)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("tag", tag), ("action", action), ("remainingTags", updated.Count));
    }

    private WorldConsequenceApplyResult ApplyAnimateEntity(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Animate consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var entity = target.Entity!;
        if (entity.Id == _state.ControlledEntityId)
        {
            return Reject(consequence, "Animation cannot seize the caster's own body.");
        }

        if (!entity.Has<PositionComponent>())
        {
            return Reject(consequence, "Animation needs a body or object standing in the world, not something carried.");
        }

        var wasActor = entity.TryGet<ActorComponent>(out var actor);
        if (wasActor && actor.Alive)
        {
            return Reject(consequence, "That one already lives; animation works on the dead and the inert.");
        }

        var faction = NormalizeToken(FirstNonBlank(ReadString(payload, "faction"), "player")!, "player");
        var hp = Math.Clamp(ReadInt(payload, "hp") ?? 6, 1, 12);
        var attack = Math.Clamp(ReadInt(payload, "attack") ?? (wasActor ? Math.Min(Math.Max(actor.Attack, 1), 3) : 2), 0, 4);
        var expiresTurn = ReadInt(payload, "expiresTurn") ?? ReadInt(payload, "expires_turn");
        var beforeName = entity.Name;

        if (wasActor)
        {
            entity.Set(actor with
            {
                HitPoints = hp,
                MaxHitPoints = Math.Max(actor.MaxHitPoints, hp),
                Attack = attack,
                Faction = faction,
            });
            entity.Set(new RenderableComponent('z', faction));
        }
        else
        {
            entity.Set(new ActorComponent(hp, hp, 0, 0, attack, 0, faction));
            // Once something walks, it is no longer floor loot; pickup and stacking stop applying.
            entity.Remove<ItemComponent>();
            entity.Remove<StackComponent>();
        }

        if (entity.TryGet<PhysicalComponent>(out var physical))
        {
            entity.Set(physical with { BlocksMovement = true });
        }
        else
        {
            entity.Set(new PhysicalComponent(BlocksMovement: true, Material: "animated"));
        }

        var tags = entity.TryGet<TagsComponent>(out var existingTags)
            ? existingTags.Tags.Where(tag => !tag.Equals("defeated", StringComparison.OrdinalIgnoreCase)).ToList()
            : new List<string>();
        foreach (var tag in new[] { "animated", "wild_magic" })
        {
            if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(tag);
            }
        }

        entity.Set(new TagsComponent(tags));
        entity.Set(new FactionComponent(faction, new[] { faction }));
        entity.Set(new ControllerComponent(ControllerKind.Ai));
        entity.Set(new AiComponent(faction == "player" ? "ally" : "hostile_guard"));
        if (!entity.Has<StatusContainerComponent>())
        {
            entity.Set(StatusContainerComponent.Empty());
        }

        if (!entity.Has<BodyStatsComponent>())
        {
            entity.Set(new BodyStatsComponent(2));
        }

        if (!entity.Has<SoulComponent>())
        {
            entity.Set(new SoulComponent($"{entity.Id.Value}_soul"));
        }

        var source = FirstNonBlank(consequence.SourceEntityId, _state.ControlledEntityId.Value)!;
        entity.Set(new SummonedComponent(source, expiresTurn));

        var rename = ReadString(payload, "name");
        if (!string.IsNullOrWhiteSpace(rename))
        {
            entity.Name = rename.Trim();
        }
        else if (wasActor && !beforeName.StartsWith("risen ", StringComparison.OrdinalIgnoreCase))
        {
            entity.Name = $"risen {beforeName}";
        }
        else if (!wasActor && !beforeName.StartsWith("animated ", StringComparison.OrdinalIgnoreCase))
        {
            entity.Name = $"animated {beforeName}";
        }

        var summary = wasActor
            ? $"{beforeName} rises, re-strung by wild magic."
            : $"{beforeName} shudders and steps into motion.";
        var messageText = FirstNonBlank(ReadString(payload, "message"), summary)!;
        var emitted = AddMessageIfAllowed(consequence, payload, messageText);
        var operation = ReadString(payload, "operation") ?? "animateEntity";
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("name", entity.Name),
                ("wasActor", wasActor),
                ("faction", faction),
                ("hp", hp),
                ("attack", attack)));
        return Applied(
            consequence,
            entity.Id.Value,
            emitted ? new[] { messageText } : Array.Empty<string>(),
            delta,
            ("name", entity.Name),
            ("faction", faction),
            ("hp", hp),
            ("attack", attack));
    }

    private (Entity? Entity, WorldConsequenceApplyResult? Result) RequiredEntity(
        WorldConsequence consequence,
        string missingIdError)
    {
        var entityId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return (null, Reject(consequence, missingIdError));
        }

        var entity = EntityById(entityId);
        return entity is null
            ? (null, Reject(consequence, "Consequence target entity does not exist."))
            : (entity, null);
    }

    private WorldConsequenceApplyResult AppliedFromDelta(WorldConsequence consequence, StateDelta delta)
    {
        var fields = delta.Details
            .Select(pair => (pair.Key, pair.Value))
            .Concat(new[] { ("operation", (object?)delta.Operation), ("target", delta.Target) })
            .ToArray();
        var enriched = new StateDelta(delta.Operation, delta.Target, delta.Summary, Details(consequence, fields));
        var messages = IsVisible(consequence.Visibility) && enriched.IsPlayerVisible()
            ? new[] { enriched.Summary }
            : Array.Empty<string>();
        return new(
            true,
            delta.Target,
            null,
            messages,
            new[] { enriched },
            Details(consequence, ("operation", delta.Operation), ("target", delta.Target)));
    }

    private static StateDelta WithOperation(StateDelta delta, string? operation) =>
        string.IsNullOrWhiteSpace(operation) || operation.Equals(delta.Operation, StringComparison.OrdinalIgnoreCase)
            ? delta
            : new StateDelta(operation, delta.Target, delta.Summary, delta.Details);
}
