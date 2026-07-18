using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

public static class WorldConsequenceTypes
{
    public const string Damage = "damage";
    public const string Heal = "heal";
    public const string RestoreMana = "restore_mana";
    public const string AdjustActorResource = "adjust_actor_resource";
    public const string MoveEntity = "move_entity";
    public const string SetTerrain = "set_terrain";
    public const string UpdateTerrain = "update_terrain";
    public const string ApplyStatus = "apply_status";
    public const string RemoveStatus = "remove_status";
    public const string AccelerateStatus = "accelerate_status";
    public const string SpawnEntity = "spawn_entity";
    public const string SpawnItem = "spawn_item";
    public const string SpawnFixture = "spawn_fixture";
    public const string CreatePromise = "create_promise";
    public const string UpdatePromise = "update_promise";
    public const string OfferBargain = "offer_bargain";
    public const string AcceptBargain = "accept_bargain";
    public const string FulfillBargain = "fulfill_bargain";
    public const string AlterItem = "alter_item";
    public const string ResolveCost = "resolve_cost";
    public const string Message = "message";
    public const string ModifyInventory = "modify_inventory";
    public const string TransferItem = "transfer_item";
    public const string UpdateEquipment = "update_equipment";
    public const string AddTags = "add_tags";
    public const string RemoveTags = "remove_tags";
    public const string ChangeFaction = "change_faction";
    public const string UpdateControl = "update_control";
    public const string SetControlledEntity = "set_controlled_entity";
    public const string SwapSouls = "swap_souls";
    public const string SetWorldFlag = "set_world_flag";
    public const string UpdateRunStatus = "update_run_status";
    public const string SetSelectedTarget = "set_selected_target";
    public const string QueueBackgroundJob = "queue_background_job";
    public const string UpdateBackgroundJob = "update_background_job";
    public const string ScheduleEvent = "schedule_event";
    public const string UpdateScheduledEvent = "update_scheduled_event";
    public const string CreateTrigger = "create_trigger";
    public const string UpdateTrigger = "update_trigger";
    public const string AdjustFactionStanding = "adjust_faction_standing";
    public const string AdjustFactionResource = "adjust_faction_resource";
    public const string RecordSuspicion = "record_suspicion";
    public const string UpdateSuspicion = "update_suspicion";
    public const string RecordDeed = "record_deed";
    public const string UpdateDeed = "update_deed";
    public const string AddLegend = "add_legend";
    public const string AddCanon = "add_canon";
    public const string RecordWorldTurn = "record_world_turn";
    public const string RecordExploration = "record_exploration";
    public const string TransformEntity = "transform_entity";
    public const string SetResistance = "set_resistance";
    public const string SetWeakness = "set_weakness";
    public const string DelayIncomingDamage = "delay_incoming_damage";
    public const string ReleaseDelayedDamage = "release_delayed_damage";
    public const string EditMemory = "edit_memory";
    public const string CreatePersistentEffect = "create_persistent_effect";
    public const string UpdatePersistentEffect = "update_persistent_effect";
    public const string SetBehavior = "set_behavior";
    public const string UpdateBehavior = "update_behavior";
    public const string CreateFlow = "create_flow";
    public const string UpdateFlow = "update_flow";
    public const string RecordClaim = "record_claim";
    public const string UpdateClaim = "update_claim";
    public const string RecordRumor = "record_rumor";
    public const string UpdateRumor = "update_rumor";
    public const string RecordMemory = "record_memory";
    public const string UpdateBond = "update_bond";
    public const string UpdateWant = "update_want";
    public const string AddMerchantStock = "add_merchant_stock";
    public const string OfferTrade = "offer_trade";
    public const string ExecuteTrade = "execute_trade";
    public const string OfferService = "offer_service";
    public const string RequestService = "request_service";
    public const string OpenOrUnlock = "open_or_unlock";
    public const string CreateRoute = "create_route";
    public const string FreeCaptive = "free_captive";
    public const string AnimateEntity = "animate_entity";

    public static string Normalize(string type)
    {
        var normalized = NormalizeToken(type);
        return normalized switch
        {
            Damage => Damage,
            Heal => Heal,
            Message => Message,
            RestoreMana or "restoremana" => RestoreMana,
            AdjustActorResource or "adjustactorresource" => AdjustActorResource,
            MoveEntity or "moveentity" => MoveEntity,
            SetTerrain or "setterrain" => SetTerrain,
            UpdateTerrain or "updateterrain" => UpdateTerrain,
            ApplyStatus or "applystatus" => ApplyStatus,
            RemoveStatus or "removestatus" => RemoveStatus,
            AccelerateStatus or "acceleratestatus" => AccelerateStatus,
            SpawnEntity or "spawnentity" => SpawnEntity,
            SpawnItem or "spawnitem" => SpawnItem,
            SpawnFixture or "spawnfixture" => SpawnFixture,
            CreatePromise or "createpromise" => CreatePromise,
            UpdatePromise or "updatepromise" => UpdatePromise,
            OfferBargain or "offerbargain" => OfferBargain,
            AcceptBargain or "acceptbargain" => AcceptBargain,
            FulfillBargain or "fulfillbargain" => FulfillBargain,
            AlterItem or "alteritem" => AlterItem,
            ResolveCost or "resolvecost" or "clear_cost" or "clearcost" => ResolveCost,
            ModifyInventory or "modifyinventory" => ModifyInventory,
            TransferItem or "transferitem" => TransferItem,
            UpdateEquipment or "updateequipment" => UpdateEquipment,
            AddTags or "addtags" => AddTags,
            RemoveTags or "removetags" => RemoveTags,
            ChangeFaction or "changefaction" => ChangeFaction,
            UpdateControl or "updatecontrol" => UpdateControl,
            SetControlledEntity or "setcontrolledentity" => SetControlledEntity,
            SwapSouls or "swapsouls" => SwapSouls,
            SetWorldFlag or "setworldflag" => SetWorldFlag,
            UpdateRunStatus or "updaterunstatus" => UpdateRunStatus,
            SetSelectedTarget or "setselectedtarget" => SetSelectedTarget,
            QueueBackgroundJob or "queuebackgroundjob" => QueueBackgroundJob,
            UpdateBackgroundJob or "updatebackgroundjob" => UpdateBackgroundJob,
            ScheduleEvent or "scheduleevent" => ScheduleEvent,
            UpdateScheduledEvent or "updatescheduledevent" => UpdateScheduledEvent,
            CreateTrigger or "createtrigger" => CreateTrigger,
            UpdateTrigger or "updatetrigger" => UpdateTrigger,
            AdjustFactionStanding or "adjustfactionstanding" => AdjustFactionStanding,
            AdjustFactionResource or "adjustfactionresource" => AdjustFactionResource,
            RecordSuspicion or "recordsuspicion" => RecordSuspicion,
            UpdateSuspicion or "updatesuspicion" => UpdateSuspicion,
            RecordDeed or "recorddeed" => RecordDeed,
            UpdateDeed or "updatedeed" => UpdateDeed,
            AddLegend or "addlegend" => AddLegend,
            AddCanon or "addcanon" => AddCanon,
            RecordWorldTurn or "recordworldturn" => RecordWorldTurn,
            RecordExploration or "recordexploration" => RecordExploration,
            TransformEntity or "transformentity" => TransformEntity,
            SetResistance or "setresistance" => SetResistance,
            SetWeakness or "setweakness" => SetWeakness,
            DelayIncomingDamage or "delayincomingdamage" => DelayIncomingDamage,
            ReleaseDelayedDamage or "releasedelayeddamage" => ReleaseDelayedDamage,
            EditMemory or "editmemory" => EditMemory,
            CreatePersistentEffect or "createpersistenteffect" => CreatePersistentEffect,
            UpdatePersistentEffect or "updatepersistenteffect" => UpdatePersistentEffect,
            SetBehavior or "setbehavior" => SetBehavior,
            UpdateBehavior or "updatebehavior" => UpdateBehavior,
            CreateFlow or "createflow" => CreateFlow,
            UpdateFlow or "updateflow" => UpdateFlow,
            RecordClaim or "recordclaim" => RecordClaim,
            UpdateClaim or "updateclaim" => UpdateClaim,
            RecordRumor or "recordrumor" => RecordRumor,
            UpdateRumor or "updaterumor" => UpdateRumor,
            RecordMemory or "recordmemory" => RecordMemory,
            UpdateBond or "updatebond" => UpdateBond,
            UpdateWant or "updatewant" => UpdateWant,
            AddMerchantStock or "addmerchantstock" => AddMerchantStock,
            OfferTrade or "offertrade" => OfferTrade,
            ExecuteTrade or "executetrade" => ExecuteTrade,
            OfferService or "offerservice" => OfferService,
            RequestService or "requestservice" => RequestService,
            OpenOrUnlock or "openorunlock" => OpenOrUnlock,
            CreateRoute or "createroute" => CreateRoute,
            FreeCaptive or "freecaptive" or "release_captive" or "releasecaptive" or "free_prisoner" or "freeprisoner" => FreeCaptive,
            AnimateEntity or "animateentity" or "raise_dead" or "raisedead" or "animate_corpse" or "animatecorpse" or "animate_object" or "animateobject" => AnimateEntity,
            _ => normalized,
        };
    }

    /// <summary>
    /// The canonical consequence catalog: the single source of truth for type membership, replacing
    /// the former hand-maintained <c>IsKnown</c> or-chain (Phase 0.2). Entries reference the type
    /// constants above, so the constants and membership set can no longer drift apart.
    /// <see cref="ConsequenceCatalogTests"/> pins this exact set, and asserts it matches the
    /// applier's dispatch registry so every known type has exactly one dispatch owner.
    /// </summary>
    private static readonly IReadOnlySet<string> CanonicalTypeSet = new HashSet<string>(StringComparer.Ordinal)
    {
        Damage, Heal, RestoreMana, AdjustActorResource, MoveEntity, SetTerrain, UpdateTerrain,
        ApplyStatus, RemoveStatus, AccelerateStatus, SpawnEntity, SpawnItem, SpawnFixture,
        CreatePromise, UpdatePromise, OfferBargain, AcceptBargain, FulfillBargain, AlterItem, ResolveCost, Message, ModifyInventory, TransferItem, UpdateEquipment,
        AddTags, RemoveTags, ChangeFaction, UpdateControl, SetControlledEntity, SwapSouls,
        SetWorldFlag, UpdateRunStatus, SetSelectedTarget, QueueBackgroundJob, UpdateBackgroundJob,
        ScheduleEvent, UpdateScheduledEvent, CreateTrigger, UpdateTrigger, AdjustFactionStanding,
        AdjustFactionResource, RecordSuspicion, UpdateSuspicion, RecordDeed, UpdateDeed, AddLegend,
        AddCanon, RecordWorldTurn, RecordExploration, TransformEntity, SetResistance, SetWeakness,
        DelayIncomingDamage, ReleaseDelayedDamage, EditMemory, CreatePersistentEffect,
        UpdatePersistentEffect, SetBehavior, UpdateBehavior, CreateFlow, UpdateFlow, RecordClaim,
        UpdateClaim, RecordRumor, UpdateRumor, RecordMemory, UpdateBond, UpdateWant, AddMerchantStock,
        OfferTrade, ExecuteTrade, OfferService, RequestService, OpenOrUnlock, CreateRoute, FreeCaptive,
        AnimateEntity,
    };

    /// <summary>The canonical consequence types, in no particular order. The authoritative catalog.</summary>
    public static IReadOnlyCollection<string> Canonical => (IReadOnlyCollection<string>)CanonicalTypeSet;

    public static bool IsKnown(string type) => CanonicalTypeSet.Contains(Normalize(type));

    private static string NormalizeToken(string type)
    {
        var chars = type.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : normalized;
    }
}

public static class WorldConsequenceTiming
{
    public const string Immediate = "immediate";
    public const string AfterTurn = "after_turn";
    public const string WorldPump = "world_pump";
    public const string Deferred = "deferred";

    public static string Normalize(string? timing)
    {
        if (string.IsNullOrWhiteSpace(timing))
        {
            return Immediate;
        }

        var normalized = NormalizeToken(timing);
        return normalized switch
        {
            Immediate or "now" => Immediate,
            AfterTurn or "afterturn" or "next_turn" or "nextturn" => AfterTurn,
            WorldPump or "worldpump" or "pump" or "turn_pump" or "turnpump" => WorldPump,
            Deferred or "defer" or "delayed" or "delay" or "later" or "scheduled" => Deferred,
            _ => normalized,
        };
    }

    private static string NormalizeToken(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? Immediate : normalized;
    }
}

public static class WorldConsequenceVisibility
{
    public const string Hidden = "hidden";
    public const string Message = "message";
    public const string Journal = "journal";
    public const string Lead = "lead";
}

public sealed record WorldConsequence(
    string Type,
    string Source,
    string? SourceEntityId = null,
    string? TargetEntityId = null,
    int Salience = 1,
    int Confidence = 100,
    string Visibility = WorldConsequenceVisibility.Hidden,
    string? Evidence = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Payload = null,
    string Timing = WorldConsequenceTiming.Immediate)
{
    public static WorldConsequence Damage(
        string source,
        string targetEntityId,
        int amount,
        string damageType = "arcane",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "damage",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.Damage,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("amount", amount),
                ("damageType", damageType),
                ("operation", operation)));

    public static WorldConsequence Heal(
        string source,
        string targetEntityId,
        int amount,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "heal",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.Heal,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("amount", amount), ("operation", operation)));

    public static WorldConsequence RestoreMana(
        string source,
        string targetEntityId,
        int amount,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "restoreMana",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RestoreMana,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("amount", amount), ("operation", operation)));

    public static WorldConsequence AdjustActorResource(
        string source,
        string targetEntityId,
        string resource,
        int delta,
        int? min = null,
        int? max = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "adjustActorResource",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AdjustActorResource,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("resource", resource),
                ("delta", delta),
                ("min", min),
                ("max", max),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence MoveEntity(
        string source,
        string targetEntityId,
        int x,
        int y,
        string operation = "move",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        bool emitMessage = true,
        string? message = null,
        bool recordControlledMovement = false,
        string? swapWithEntityId = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.MoveEntity,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("x", x),
                ("y", y),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message),
                ("swapWithEntityId", swapWithEntityId),
                ("recordControlledMovement", recordControlledMovement || ExistingBool(details, "recordControlledMovement"))));

    public static WorldConsequence SetTerrain(
        string source,
        int x,
        int y,
        string terrain,
        int? duration = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createTile",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetTerrain,
            source,
            sourceEntityId,
            $"tile:{x},{y}",
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("x", x),
                ("y", y),
                ("terrain", terrain),
                ("duration", duration),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence UpdateTerrain(
        string source,
        int x,
        int y,
        string action,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateTerrain",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateTerrain,
            source,
            sourceEntityId,
            $"tile:{x},{y}",
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("x", x),
                ("y", y),
                ("action", action),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence ApplyStatus(
        string source,
        string targetEntityId,
        string status,
        int duration = 0,
        string displayName = "",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addStatus",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ApplyStatus,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("status", status),
                ("duration", duration),
                ("displayName", displayName),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence RemoveStatus(
        string source,
        string targetEntityId,
        string status,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "removeStatus",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RemoveStatus,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("status", status),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence AccelerateStatus(
        string source,
        string targetEntityId,
        string status = "",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "accelerateStatus",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AccelerateStatus,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("status", status), ("operation", operation)));

    public static WorldConsequence SpawnEntity(
        string source,
        string name,
        int x,
        int y,
        string prefix = "summon",
        char glyph = '*',
        string faction = "player",
        int hp = 5,
        int attack = 2,
        IReadOnlyList<string>? tags = null,
        string material = "summoned",
        IReadOnlyList<string>? roles = null,
        string? controllerKind = null,
        string? aiPolicyId = null,
        IReadOnlyDictionary<string, object?>? aiParameters = null,
        bool summoned = true,
        string? description = null,
        IReadOnlyList<string>? promiseIds = null,
        IReadOnlyList<string>? interactableVerbs = null,
        int? bodyVigor = null,
        bool includeMemory = false,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "summon",
        bool emitMessage = true,
        string? message = null,
        bool? autoWant = null,
        string? entityId = null,
        string? wantText = null,
        string? wantId = null,
        string? wantStatus = null,
        string? wantStakes = null,
        int? wantSalience = null,
        IReadOnlyList<string>? wantTags = null,
        IReadOnlyList<ClaimSeed>? claimSeeds = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SpawnEntity,
            source,
            sourceEntityId,
            null,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("prefix", prefix),
                ("name", name),
                ("x", x),
                ("y", y),
                ("glyph", glyph.ToString()),
                ("faction", faction),
                ("hp", hp),
                ("attack", attack),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("material", material),
                ("roles", roles?.ToArray() ?? Array.Empty<string>()),
                ("controllerKind", controllerKind),
                ("aiPolicyId", aiPolicyId),
                ("aiParameters", aiParameters),
                ("summoned", summoned),
                ("description", description),
                ("promiseIds", promiseIds?.ToArray() ?? Array.Empty<string>()),
                ("interactableVerbs", interactableVerbs?.ToArray() ?? Array.Empty<string>()),
                ("bodyVigor", bodyVigor),
                ("includeMemory", includeMemory),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message),
                ("autoWant", autoWant),
                ("entityId", entityId),
                ("wantText", wantText),
                ("wantId", wantId),
                ("wantStatus", wantStatus),
                ("wantStakes", wantStakes),
                ("wantSalience", wantSalience),
                ("wantTags", wantTags?.ToArray()),
                ("claimSeeds", claimSeeds?.ToArray())));

    public static WorldConsequence SpawnItem(
        string source,
        string name,
        int x,
        int y,
        string prefix = "item",
        char glyph = '*',
        string itemType = "curio",
        string material = "matter",
        IReadOnlyList<string>? tags = null,
        int quantity = 1,
        int value = 1,
        string stackPolicy = "commodity",
        string useProfile = "inert",
        string? equipmentSlot = null,
        string? description = null,
        IReadOnlyList<string>? promiseIds = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "conjureItem",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SpawnItem,
            source,
            sourceEntityId,
            null,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("prefix", prefix),
                ("name", name),
                ("x", x),
                ("y", y),
                ("glyph", glyph.ToString()),
                ("itemType", itemType),
                ("material", material),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("quantity", quantity),
                ("value", value),
                ("stackPolicy", stackPolicy),
                ("useProfile", useProfile),
                ("equipmentSlot", equipmentSlot),
                ("description", description),
                ("promiseIds", promiseIds?.ToArray() ?? Array.Empty<string>()),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence SpawnFixture(
        string source,
        string name,
        int x,
        int y,
        string prefix = "fixture",
        char glyph = '?',
        string palette = "fixture",
        string fixtureType = "feature",
        string material = "stone",
        IReadOnlyList<string>? tags = null,
        bool blocksMovement = true,
        bool blocksSight = false,
        int size = 1,
        int durability = 0,
        string? description = null,
        IReadOnlyList<string>? promiseIds = null,
        IReadOnlyList<string>? interactableVerbs = null,
        bool canAnchorMagic = true,
        string? readableTitle = null,
        string? readableText = null,
        InteriorEntranceComponent? interiorEntrance = null,
        InteriorExitComponent? interiorExit = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "spawnFixture",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SpawnFixture,
            source,
            sourceEntityId,
            null,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("prefix", prefix),
                ("name", name),
                ("x", x),
                ("y", y),
                ("glyph", glyph.ToString()),
                ("palette", palette),
                ("fixtureType", fixtureType),
                ("material", material),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("blocksMovement", blocksMovement),
                ("blocksSight", blocksSight),
                ("size", size),
                ("durability", durability),
                ("description", description),
                ("promiseIds", promiseIds?.ToArray() ?? Array.Empty<string>()),
                ("interactableVerbs", interactableVerbs?.ToArray() ?? Array.Empty<string>()),
                ("canAnchorMagic", canAnchorMagic),
                ("readableTitle", readableTitle),
                ("readableText", readableText),
                ("interiorEntrance", interiorEntrance),
                ("interiorExit", interiorExit),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence CreatePromise(
        string source,
        string kind,
        string text,
        string? anchorEntityId = null,
        string triggerHint = "",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createPromise",
        bool stackExisting = false,
        bool playerVisible = true,
        int salience = 2,
        string? subject = null,
        string? claimedPlace = null,
        string? realizationKind = null,
        string? bindPlace = null,
        string? sourceClaimId = null,
        string? sourceSpeakerId = null,
        string? sourceListenerSoulId = null,
        int? sourceConfidence = null,
        bool useCurrentRegionAsClaimedPlace = true,
        bool autoBind = true,
        bool emitMessage = true,
        string? message = null,
        string? stackMessageTemplate = null,
        string? costProfileId = null,
        JourneyPlan? journey = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.CreatePromise,
            source,
            sourceEntityId,
            anchorEntityId,
            Salience: salience,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("kind", kind),
                ("text", text),
                ("triggerHint", triggerHint),
                ("operation", operation),
                ("stackExisting", stackExisting),
                ("playerVisible", playerVisible),
                ("subject", subject),
                ("claimedPlace", claimedPlace),
                ("realizationKind", realizationKind),
                ("bindPlace", bindPlace),
                ("sourceClaimId", sourceClaimId),
                ("sourceSpeakerId", sourceSpeakerId),
                ("sourceListenerSoulId", sourceListenerSoulId),
                ("sourceConfidence", sourceConfidence),
                ("useCurrentRegionAsClaimedPlace", useCurrentRegionAsClaimedPlace),
                ("autoBind", autoBind),
                ("emitMessage", emitMessage),
                ("message", message),
                ("stackMessageTemplate", stackMessageTemplate),
                ("costProfileId", costProfileId),
                ("journey", journey)));

    public static WorldConsequence UpdatePromise(
        string source,
        string promiseId,
        string? status = null,
        string? boundPlace = null,
        string? boundTargetId = null,
        string? triggerHint = null,
        string? realizationKind = null,
        string? realizedIn = null,
        string? claimedPlace = null,
        string? lastEligibilityFailure = null,
        string? lastEligibilityContext = null,
        int? lastEligibilityTurn = null,
        bool clearEligibilityFailure = false,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updatePromise",
        bool emitMessage = false,
        string? message = null,
        JourneyPlan? journey = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdatePromise,
            source,
            sourceEntityId,
            promiseId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("promiseId", promiseId),
                ("status", status),
                ("boundPlace", boundPlace),
                ("boundTargetId", boundTargetId),
                ("triggerHint", triggerHint),
                ("realizationKind", realizationKind),
                ("realizedIn", realizedIn),
                ("claimedPlace", claimedPlace),
                ("lastEligibilityFailure", lastEligibilityFailure),
                ("lastEligibilityContext", lastEligibilityContext),
                ("lastEligibilityTurn", lastEligibilityTurn),
                ("clearEligibilityFailure", clearEligibilityFailure),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message),
                ("journey", journey)));

    public static WorldConsequence OfferBargain(
        string source,
        string promiseId,
        BargainOffer offer,
        string visibility = WorldConsequenceVisibility.Journal,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "offerBargain",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OfferBargain,
            source,
            sourceEntityId,
            promiseId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("promiseId", promiseId),
                ("offer", offer),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence AcceptBargain(
        string source,
        string promiseId,
        string optionId,
        string actorEntityId,
        string visibility = WorldConsequenceVisibility.Message,
        string? evidence = null,
        string? reason = null,
        string operation = "acceptBargain",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AcceptBargain,
            source,
            actorEntityId,
            promiseId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("promiseId", promiseId),
                ("optionId", optionId),
                ("actorEntityId", actorEntityId),
                ("operation", operation)));

    public static WorldConsequence FulfillBargain(
        string source,
        string promiseId,
        string termId,
        string actorEntityId,
        string action = "fulfill",
        string visibility = WorldConsequenceVisibility.Message,
        string? evidence = null,
        string? reason = null,
        string operation = "fulfillBargain",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.FulfillBargain,
            source,
            actorEntityId,
            promiseId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("promiseId", promiseId),
                ("termId", termId),
                ("actorEntityId", actorEntityId),
                ("action", action),
                ("operation", operation)));

    public static WorldConsequence AlterItem(
        string source,
        string carrierEntityId,
        string item,
        string profileId,
        string action = "apply",
        string visibility = WorldConsequenceVisibility.Message,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "alterItem",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AlterItem,
            source,
            sourceEntityId,
            carrierEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("item", item),
                ("profileId", profileId),
                ("action", action),
                ("operation", operation)));

    public static WorldConsequence ResolveCost(
        string source,
        string targetEntityId,
        string? profileId = null,
        string? item = null,
        string category = "curse",
        string visibility = WorldConsequenceVisibility.Message,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "resolveCost",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ResolveCost,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("profileId", profileId),
                ("item", item),
                ("category", category),
                ("operation", operation)));

    public static WorldConsequence Message(
        string source,
        string text,
        string targetEntityId = "",
        string visibility = WorldConsequenceVisibility.Message,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "message",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.Message,
            source,
            sourceEntityId,
            string.IsNullOrWhiteSpace(targetEntityId) ? null : targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("text", text), ("operation", operation)));

    public static WorldConsequence ModifyInventory(
        string source,
        string targetEntityId,
        string item,
        string op = "add",
        int amount = 1,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "modifyInventory",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ModifyInventory,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("item", item),
                ("op", op),
                ("amount", amount),
                ("operation", operation),
                ("emitMessage", emitMessage || ExistingBool(details, "emitMessage")),
                ("message", message ?? ExistingValue(details, "message"))));

    public static WorldConsequence TransferItem(
        string source,
        string actorEntityId,
        string mode,
        string itemName,
        int quantity = 1,
        string? itemEntityId = null,
        string? recipientEntityId = null,
        int? x = null,
        int? y = null,
        string prefix = "item",
        char glyph = '*',
        string itemType = "curio",
        string material = "matter",
        IReadOnlyList<string>? tags = null,
        int value = 1,
        string stackPolicy = "commodity",
        string useProfile = "inert",
        string? equipmentSlot = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "transferItem",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.TransferItem,
            source,
            sourceEntityId ?? actorEntityId,
            mode.Equals("pickup", StringComparison.OrdinalIgnoreCase)
                ? itemEntityId
                : mode.Equals("give", StringComparison.OrdinalIgnoreCase)
                    ? recipientEntityId
                    : actorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("actorEntityId", actorEntityId),
                ("mode", mode),
                ("itemName", itemName),
                ("quantity", quantity),
                ("itemEntityId", itemEntityId),
                ("recipientEntityId", recipientEntityId),
                ("x", x),
                ("y", y),
                ("prefix", prefix),
                ("glyph", glyph.ToString()),
                ("itemType", itemType),
                ("material", material),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("value", value),
                ("stackPolicy", stackPolicy),
                ("useProfile", useProfile),
                ("equipmentSlot", equipmentSlot),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence UpdateEquipment(
        string source,
        string actorEntityId,
        string mode,
        string? item = null,
        string? slot = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateEquipment",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateEquipment,
            source,
            sourceEntityId ?? actorEntityId,
            actorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("actorEntityId", actorEntityId),
                ("mode", mode),
                ("item", item),
                ("slot", slot),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence AddTags(
        string source,
        string targetEntityId,
        IReadOnlyList<string> tags,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addTag",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AddTags,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("tags", tags.ToArray()), ("operation", operation)));

    public static WorldConsequence RemoveTags(
        string source,
        string targetEntityId,
        IReadOnlyList<string> tags,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "removeTag",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RemoveTags,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("tags", tags.ToArray()), ("operation", operation)));

    public static WorldConsequence ChangeFaction(
        string source,
        string targetEntityId,
        string faction,
        IReadOnlyList<string>? roles = null,
        bool preserveMembership = false,
        string? membershipFactionId = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "changeFaction",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ChangeFaction,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("faction", faction),
                ("roles", roles?.ToArray() ?? Array.Empty<string>()),
                ("preserveMembership", preserveMembership),
                ("membershipFactionId", membershipFactionId),
                ("operation", operation)));

    public static WorldConsequence UpdateControl(
        string source,
        string targetEntityId,
        string controllerKind,
        string? aiPolicyId = null,
        IReadOnlyDictionary<string, object?>? aiParameters = null,
        bool removeAi = false,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateControl",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateControl,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("controllerKind", controllerKind),
                ("aiPolicyId", aiPolicyId),
                ("aiParameters", aiParameters),
                ("removeAi", removeAi),
                ("operation", operation)));

    public static WorldConsequence SetControlledEntity(
        string source,
        string targetEntityId,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "setControlledEntity",
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetControlledEntity,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("targetEntityId", targetEntityId),
                ("operation", operation),
                ("message", message)));

    public static WorldConsequence SwapSouls(
        string source,
        string firstEntityId,
        string secondEntityId,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "swapSouls",
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SwapSouls,
            source,
            sourceEntityId,
            firstEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("firstEntityId", firstEntityId),
                ("secondEntityId", secondEntityId),
                ("operation", operation),
                ("message", message)));

    public static WorldConsequence SetWorldFlag(
        string source,
        string flag,
        object? value,
        string description = "",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "setFlag",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetWorldFlag,
            source,
            sourceEntityId,
            flag,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("flag", flag),
                ("value", value),
                ("description", description),
                ("operation", operation)));

    public static WorldConsequence UpdateRunStatus(
        string source,
        string status,
        string? conclusion = null,
        string? targetId = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "runComplete",
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateRunStatus,
            source,
            sourceEntityId,
            string.IsNullOrWhiteSpace(targetId) ? null : targetId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("status", status),
                ("conclusion", conclusion),
                ("operation", operation),
                ("message", message)));

    public static WorldConsequence SetSelectedTarget(
        string source,
        int? x = null,
        int? y = null,
        bool clear = false,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "setSelectedTarget",
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetSelectedTarget,
            source,
            sourceEntityId,
            null,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("x", x),
                ("y", y),
                ("clear", clear),
                ("operation", operation),
                ("message", message)));

    public static WorldConsequence QueueBackgroundJob(
        string source,
        string targetId,
        string purpose,
        int priority,
        string targetKind = "entity",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "queueBackgroundJob",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.QueueBackgroundJob,
            source,
            sourceEntityId,
            targetId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("targetId", targetId),
                ("targetKind", targetKind),
                ("purpose", purpose),
                ("priority", priority),
                ("operation", operation),
                ("emitMessage", emitMessage || ExistingBool(details, "emitMessage")),
                ("message", message ?? ExistingValue(details, "message"))));

    public static WorldConsequence UpdateBackgroundJob(
        string source,
        string jobId,
        string state,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateBackgroundJob",
        int? startedTurn = null,
        int? completedTurn = null,
        int? appliedTurn = null,
        string? resultText = null,
        string? error = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateBackgroundJob,
            source,
            sourceEntityId,
            jobId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("jobId", jobId),
                ("state", state),
                ("startedTurn", startedTurn),
                ("completedTurn", completedTurn),
                ("appliedTurn", appliedTurn),
                ("resultText", resultText),
                ("error", error),
                ("operation", operation)));

    public static WorldConsequence ScheduleEvent(
        string source,
        string eventType,
        int turns,
        IReadOnlyDictionary<string, object?>? eventPayload = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "scheduleEvent",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ScheduleEvent,
            source,
            sourceEntityId,
            null,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("eventType", eventType),
                ("turns", turns),
                ("eventPayload", eventPayload ?? new Dictionary<string, object?>()),
                ("operation", operation),
                ("emitMessage", emitMessage || ExistingBool(details, "emitMessage")),
                ("message", message ?? ExistingValue(details, "message"))));

    public static WorldConsequence UpdateScheduledEvent(
        string source,
        string eventId,
        string action,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateScheduledEvent",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateScheduledEvent,
            source,
            sourceEntityId,
            eventId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("eventId", eventId),
                ("action", action),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence CreateTrigger(
        string source,
        string name,
        string kind,
        int delay,
        int interval,
        int uses,
        int? duration,
        string effectType,
        IReadOnlyDictionary<string, object?> effectFields,
        string description,
        string? anchorEntityId = null,
        int? anchorX = null,
        int? anchorY = null,
        int radius = 0,
        string targetFilter = "all",
        bool playerVisible = true,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createTrigger",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.CreateTrigger,
            source,
            sourceEntityId,
            anchorEntityId ?? (anchorX is not null && anchorY is not null ? $"tile:{anchorX},{anchorY}" : null),
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("name", name),
                ("kind", kind),
                ("delay", delay),
                ("interval", interval),
                ("uses", uses),
                ("duration", duration),
                ("effectType", effectType),
                ("effectFields", effectFields),
                ("description", description),
                ("anchorEntityId", anchorEntityId),
                ("anchorX", anchorX),
                ("anchorY", anchorY),
                ("radius", radius),
                ("targetFilter", targetFilter),
                ("playerVisible", playerVisible),
                ("operation", operation)));

    public static WorldConsequence UpdateTrigger(
        string source,
        string triggerId,
        string action,
        int? nextTurn = null,
        int? remainingUses = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateTrigger",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateTrigger,
            source,
            sourceEntityId,
            triggerId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("triggerId", triggerId),
                ("action", action),
                ("nextTurn", nextTurn),
                ("remainingUses", remainingUses),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence AdjustFactionStanding(
        string source,
        string factionId,
        string axis,
        int delta,
        bool targetIsRole = false,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "adjustFactionStanding",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AdjustFactionStanding,
            source,
            sourceEntityId,
            factionId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("factionId", factionId),
                ("axis", axis),
                ("delta", delta),
                ("targetIsRole", targetIsRole),
                ("operation", operation)));

    public static WorldConsequence AdjustFactionResource(
        string source,
        string factionId,
        string resource,
        int delta,
        int min = 0,
        int? max = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "adjustFactionResource",
        IReadOnlyDictionary<string, object?>? details = null,
        // The applier clamps an ordinary delta to +/-999 as a safety rail against a wild/
        // hallucinated swing from untrusted magic or dialogue content. Engine-internal callers
        // (e.g. WorldTurnSystem setting an absolute cooldown-until turn number) compute an
        // already-bounded delta themselves and can legitimately need a larger one-step swing as
        // a run's turn count grows; they opt out explicitly here rather than have the applier
        // special-case a source string.
        bool allowLargeDelta = false) =>
        new(
            WorldConsequenceTypes.AdjustFactionResource,
            source,
            sourceEntityId,
            factionId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("factionId", factionId),
                ("resource", resource),
                ("delta", delta),
                ("min", min),
                ("max", max),
                ("operation", operation),
                ("allowLargeDelta", allowLargeDelta)));

    public static WorldConsequence RecordSuspicion(
        string source,
        string kind,
        int effectX,
        int effectY,
        string? actorEntityId = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "recordSuspicion",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordSuspicion,
            source,
            sourceEntityId ?? actorEntityId,
            actorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("kind", kind),
                ("effectX", effectX),
                ("effectY", effectY),
                ("actorEntityId", actorEntityId),
                ("operation", operation)));

    public static WorldConsequence UpdateSuspicion(
        string source,
        string suspicionId,
        string status,
        string? suspectedSoulId = null,
        int? attributedTurn = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateSuspicion",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateSuspicion,
            source,
            sourceEntityId,
            suspicionId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("suspicionId", suspicionId),
                ("status", status),
                ("suspectedSoulId", suspectedSoulId),
                ("attributedTurn", attributedTurn),
                ("operation", operation)));

    public static WorldConsequence AddLegend(
        string source,
        string actorSoulId,
        string tag,
        int weight,
        string sourceId,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addLegend",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AddLegend,
            source,
            sourceEntityId,
            actorSoulId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("actorSoulId", actorSoulId),
                ("tag", tag),
                ("weight", weight),
                ("sourceId", sourceId),
                ("operation", operation)));

    public static WorldConsequence RecordDeed(
        string source,
        string actorEntityId,
        string kind,
        int magnitude,
        int originX,
        int originY,
        int? effectX = null,
        int? effectY = null,
        IReadOnlyList<string>? tags = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "recordDeed",
        IReadOnlyDictionary<string, object?>? details = null,
        string? summary = null) =>
        new(
            WorldConsequenceTypes.RecordDeed,
            source,
            sourceEntityId ?? actorEntityId,
            actorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("kind", kind),
                ("magnitude", magnitude),
                ("originX", originX),
                ("originY", originY),
                ("effectX", effectX),
                ("effectY", effectY),
                ("tags", tags ?? Array.Empty<string>()),
                ("summary", summary),
                ("operation", operation)));

    public static WorldConsequence UpdateDeed(
        string source,
        string deedId,
        string action = "mark_applied",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateDeed",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateDeed,
            source,
            sourceEntityId,
            deedId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("deedId", deedId),
                ("action", action),
                ("operation", operation)));

    public static WorldConsequence AddCanon(
        string source,
        string kind,
        string attachedTo,
        string text,
        string summary,
        IReadOnlyList<string>? tags = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addCanon",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AddCanon,
            source,
            sourceEntityId,
            attachedTo,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("kind", kind),
                ("attachedTo", attachedTo),
                ("text", text),
                ("summary", summary),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("operation", operation)));

    public static WorldConsequence RecordWorldTurn(
        string source,
        string reason,
        string kind,
        string sourceId,
        string summary,
        IReadOnlyDictionary<string, object?>? recordDetails = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? consequenceReason = null,
        string operation = "worldTurn",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordWorldTurn,
            source,
            sourceEntityId,
            sourceId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: consequenceReason,
            Payload: MergePayload(
                details,
                ("worldTurnReason", reason),
                ("kind", kind),
                ("worldTurnSourceId", sourceId),
                ("summary", summary),
                ("details", recordDetails ?? new Dictionary<string, object?>()),
                ("operation", operation)));

    public static WorldConsequence RecordExploration(
        string source,
        string soulId,
        IReadOnlyList<Sorcerer.Core.Primitives.GridPoint> visibleTiles,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "recordExploration",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordExploration,
            source,
            sourceEntityId,
            soulId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("soulId", soulId),
                ("tiles", visibleTiles.Select(point => $"{point.X},{point.Y}").ToArray()),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence TransformEntity(
        string source,
        string targetEntityId,
        string? name = null,
        string? material = null,
        string? description = null,
        IReadOnlyList<string>? tags = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "transformEntity",
        IReadOnlyDictionary<string, object?>? details = null,
        bool? blocksMovement = null,
        bool? blocksSight = null,
        int? size = null,
        int? durability = null,
        char? glyph = null,
        string? palette = null,
        string? fixtureType = null,
        bool? canAnchorMagic = null,
        IReadOnlyList<string>? removeTags = null,
        IReadOnlyList<string>? interactableVerbs = null) =>
        new(
            WorldConsequenceTypes.TransformEntity,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("name", name),
                ("material", material),
                ("description", description),
                ("tags", tags?.ToArray()),
                ("removeTags", removeTags?.ToArray()),
                ("blocksMovement", blocksMovement),
                ("blocksSight", blocksSight),
                ("size", size),
                ("durability", durability),
                ("glyph", glyph?.ToString()),
                ("palette", palette),
                ("fixtureType", fixtureType),
                ("canAnchorMagic", canAnchorMagic),
                ("interactableVerbs", interactableVerbs?.ToArray()),
                ("operation", operation)));

    public static WorldConsequence SetResistance(
        string source,
        string targetEntityId,
        string damageType,
        int amount,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addResistance",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetResistance,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("damageType", damageType), ("amount", amount), ("operation", operation)));

    public static WorldConsequence SetWeakness(
        string source,
        string targetEntityId,
        string damageType,
        int amount,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addWeakness",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetWeakness,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("damageType", damageType), ("amount", amount), ("operation", operation)));

    public static WorldConsequence DelayIncomingDamage(
        string source,
        string targetEntityId,
        int turns,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "delayIncoming",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.DelayIncomingDamage,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("turns", turns), ("operation", operation)));

    public static WorldConsequence ReleaseDelayedDamage(
        string source,
        string targetEntityId,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "releaseDelayedDamage",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ReleaseDelayedDamage,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("operation", operation),
                ("emitMessage", emitMessage || ExistingBool(details, "emitMessage")),
                ("message", message ?? ExistingValue(details, "message"))));

    public static WorldConsequence EditMemory(
        string source,
        string targetEntityId,
        string op,
        string text = "",
        string subject = "",
        int strength = 3,
        bool? aboutCaster = null,
        string provenance = "wild_magic",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "editMemory",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.EditMemory,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("op", op),
                ("text", text),
                ("subject", subject),
                ("strength", strength),
                ("aboutCaster", aboutCaster),
                ("provenance", provenance),
                ("operation", operation)));

    public static WorldConsequence CreatePersistentEffect(
        string source,
        string anchorEntityId,
        string hook,
        string effectType,
        IReadOnlyDictionary<string, object?> effectFields,
        int uses,
        string? linkPartnerId = null,
        bool playerVisible = true,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createPersistentEffect",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.CreatePersistentEffect,
            source,
            sourceEntityId,
            anchorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("hook", hook),
                ("effectType", effectType),
                ("effectFields", effectFields),
                ("uses", uses),
                ("linkPartnerId", linkPartnerId),
                ("playerVisible", playerVisible),
                ("operation", operation)));

    public static WorldConsequence UpdatePersistentEffect(
        string source,
        string effectId,
        string action,
        int amount = 1,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updatePersistentEffect",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdatePersistentEffect,
            source,
            sourceEntityId,
            effectId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("effectId", effectId),
                ("action", action),
                ("amount", amount),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence SetBehavior(
        string source,
        string targetEntityId,
        string tag,
        int duration = 0,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "setBehavior",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.SetBehavior,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(details, ("tag", tag), ("duration", duration), ("operation", operation)));

    public static WorldConsequence UpdateBehavior(
        string source,
        string targetEntityId,
        string tag,
        string action,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateBehavior",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateBehavior,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("tag", tag),
                ("action", action),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence CreateFlow(
        string source,
        int x,
        int y,
        int radius,
        int dx,
        int dy,
        int duration,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createFlow",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.CreateFlow,
            source,
            sourceEntityId,
            $"tile:{x},{y}",
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("x", x),
                ("y", y),
                ("radius", radius),
                ("dx", dx),
                ("dy", dy),
                ("duration", duration),
                ("operation", operation)));

    public static WorldConsequence UpdateFlow(
        string source,
        int x,
        int y,
        string action,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateFlow",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateFlow,
            source,
            sourceEntityId,
            $"tile:{x},{y}",
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("x", x),
                ("y", y),
                ("action", action),
                ("operation", operation),
                ("playerVisible", ExistingBool(details, "playerVisible"))));

    public static WorldConsequence RecordRumor(
        string source,
        string sourceKind,
        string sourceId,
        string originRegionId,
        string currentRegionId,
        string text,
        int salience,
        IReadOnlyList<string>? carrierIds = null,
        IReadOnlyList<string>? tags = null,
        string status = "active",
        IReadOnlyList<string>? distortionHistory = null,
        string? originalText = null,
        int hops = 0,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "recordRumor",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordRumor,
            source,
            sourceEntityId,
            sourceId,
            salience,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("sourceKind", sourceKind),
                ("sourceId", sourceId),
                ("originRegionId", originRegionId),
                ("currentRegionId", currentRegionId),
                ("text", text),
                ("salience", salience),
                ("carrierIds", carrierIds?.ToArray() ?? Array.Empty<string>()),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("status", status),
                ("distortionHistory", distortionHistory?.ToArray() ?? Array.Empty<string>()),
                ("originalText", originalText),
                ("hops", hops),
                ("operation", operation)));

    public static WorldConsequence UpdateRumor(
        string source,
        string rumorId,
        string? currentRegionId = null,
        string? text = null,
        int? salience = null,
        string? status = null,
        IReadOnlyList<string>? carrierIds = null,
        IReadOnlyList<string>? addCarrierIds = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? addTags = null,
        IReadOnlyList<string>? distortionHistory = null,
        IReadOnlyList<string>? appendDistortionHistory = null,
        bool incrementHops = false,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateRumor",
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateRumor,
            source,
            sourceEntityId,
            rumorId,
            salience ?? 1,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("rumorId", rumorId),
                ("currentRegionId", currentRegionId),
                ("text", text),
                ("salience", salience),
                ("status", status),
                ("carrierIds", carrierIds?.ToArray()),
                ("addCarrierIds", addCarrierIds?.ToArray()),
                ("tags", tags?.ToArray()),
                ("addTags", addTags?.ToArray()),
                ("distortionHistory", distortionHistory?.ToArray()),
                ("appendDistortionHistory", appendDistortionHistory?.ToArray()),
                ("incrementHops", incrementHops),
                ("operation", operation),
                ("message", message)));

    public static WorldConsequence RecordClaim(
        string source,
        string speakerId,
        string listenerSoulId,
        string text,
        string category,
        string subject,
        int salience,
        int confidence,
        bool playerVisible,
        IReadOnlyList<string>? tags = null,
        string status = "reported",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "claimRecorded",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordClaim,
            source,
            sourceEntityId ?? speakerId,
            speakerId,
            salience,
            confidence,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("speakerId", speakerId),
                ("listenerSoulId", listenerSoulId),
                ("text", text),
                ("category", category),
                ("subject", subject),
                ("salience", salience),
                ("confidence", confidence),
                ("playerVisible", playerVisible),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("status", status),
                ("operation", operation)));

    public static WorldConsequence UpdateClaim(
        string source,
        string claimId,
        string? status = null,
        string? boundPromiseId = null,
        string? appliedTo = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateClaim",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateClaim,
            source,
            sourceEntityId,
            claimId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("claimId", claimId),
                ("status", status),
                ("boundPromiseId", boundPromiseId),
                ("appliedTo", appliedTo),
                ("operation", operation),
                ("emitMessage", emitMessage || ExistingBool(details, "emitMessage")),
                ("message", message ?? ExistingValue(details, "message"))));

    public static WorldConsequence RecordMemory(
        string source,
        string ownerEntityId,
        string text,
        string provenance,
        int salience,
        bool shareable,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "recordMemory",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordMemory,
            source,
            sourceEntityId,
            ownerEntityId,
            salience,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("text", text),
                ("provenance", provenance),
                ("shareable", shareable),
                ("operation", operation)));

    public static WorldConsequence UpdateBond(
        string source,
        string entityId,
        string targetSoulId,
        int loyaltyDelta,
        int fearDelta,
        int admirationDelta,
        int resentmentDelta,
        string? posture,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateBond",
        int maxDelta = 2,
        IReadOnlyDictionary<string, object?>? details = null,
        string? subjectSoulId = null) =>
        new(
            WorldConsequenceTypes.UpdateBond,
            source,
            sourceEntityId,
            entityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("targetSoulId", targetSoulId),
                ("loyaltyDelta", loyaltyDelta),
                ("fearDelta", fearDelta),
                ("admirationDelta", admirationDelta),
                ("resentmentDelta", resentmentDelta),
                ("posture", posture),
                ("operation", operation),
                ("maxDelta", maxDelta),
                ("subjectSoulId", subjectSoulId)));

    public static WorldConsequence UpdateWant(
        string source,
        string entityId,
        string? text = null,
        int? salience = null,
        string? status = null,
        string? stakes = null,
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? addTags = null,
        IReadOnlyList<string>? removeTags = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateWant",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null,
        bool recordMemory = false,
        string? memoryText = null,
        string? memoryProvenance = null,
        int? memorySalience = null,
        bool? memoryShareable = null) =>
        new(
            WorldConsequenceTypes.UpdateWant,
            source,
            sourceEntityId,
            entityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("text", text),
                ("salience", salience),
                ("status", status),
                ("stakes", stakes),
                ("tags", tags?.ToArray()),
                ("addTags", addTags?.ToArray() ?? Array.Empty<string>()),
                ("removeTags", removeTags?.ToArray() ?? Array.Empty<string>()),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message),
                ("recordMemory", recordMemory || ExistingBool(details, "recordMemory") || ExistingBool(details, "record_memory")),
                ("memoryText", memoryText ?? ExistingValue(details, "memoryText") ?? ExistingValue(details, "memory_text")),
                ("memoryProvenance", memoryProvenance ?? ExistingValue(details, "memoryProvenance") ?? ExistingValue(details, "memory_provenance")),
                ("memorySalience", memorySalience ?? ExistingValue(details, "memorySalience") ?? ExistingValue(details, "memory_salience")),
                ("memoryShareable", memoryShareable ?? ExistingValue(details, "memoryShareable") ?? ExistingValue(details, "memory_shareable"))));

    public static WorldConsequence AddMerchantStock(
        string source,
        string merchantId,
        string itemName,
        int quantity = 1,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addMerchantStock",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AddMerchantStock,
            source,
            sourceEntityId,
            merchantId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("itemName", itemName),
                ("quantity", quantity),
                ("operation", operation)));

    public static WorldConsequence OfferTrade(
        string source,
        string merchantId,
        string? itemName = null,
        int quantity = 1,
        int gold = 30,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "offerTrade",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OfferTrade,
            source,
            sourceEntityId,
            merchantId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("itemName", itemName),
                ("quantity", quantity),
                ("gold", gold),
                ("operation", operation)));

    public static WorldConsequence ExecuteTrade(
        string source,
        string merchantId,
        string actorEntityId,
        string mode,
        string itemName,
        string wareKey,
        int price,
        int quantity = 1,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "executeTrade",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.ExecuteTrade,
            source,
            sourceEntityId ?? actorEntityId,
            merchantId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("merchantId", merchantId),
                ("actorEntityId", actorEntityId),
                ("mode", mode),
                ("itemName", itemName),
                ("wareKey", wareKey),
                ("price", price),
                ("quantity", quantity),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence OfferService(
        string source,
        string providerId,
        string serviceId,
        string name,
        string description,
        string effectKind,
        int goldCost = 0,
        string? itemCost = null,
        string? targetHint = null,
        bool revealed = true,
        IReadOnlyList<string>? tags = null,
        string? wantStatusOnComplete = null,
        string? wantStakesOnComplete = null,
        IReadOnlyList<string>? wantAddTagsOnComplete = null,
        IReadOnlyList<string>? wantRemoveTagsOnComplete = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "offerService",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OfferService,
            source,
            sourceEntityId,
            providerId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("serviceId", serviceId),
                ("name", name),
                ("description", description),
                ("effectKind", effectKind),
                ("goldCost", goldCost),
                ("itemCost", itemCost),
                ("targetHint", targetHint),
                ("revealed", revealed),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("wantStatusOnComplete", wantStatusOnComplete),
                ("wantStakesOnComplete", wantStakesOnComplete),
                ("wantAddTagsOnComplete", wantAddTagsOnComplete?.ToArray()),
                ("wantRemoveTagsOnComplete", wantRemoveTagsOnComplete?.ToArray()),
                ("operation", operation)));

    public static WorldConsequence RequestService(
        string source,
        string providerId,
        string service,
        string actorEntityId,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "requestService",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RequestService,
            source,
            sourceEntityId ?? actorEntityId,
            providerId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("providerId", providerId),
                ("service", service),
                ("actorEntityId", actorEntityId),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence OpenOrUnlock(
        string source,
        string doorId,
        string? actorId = null,
        bool unlock = true,
        bool open = true,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "openOrUnlock",
        bool emitMessage = false,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OpenOrUnlock,
            source,
            sourceEntityId ?? actorId,
            doorId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("actorId", actorId),
                ("unlock", unlock),
                ("open", open),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence FreeCaptive(
        string source,
        string captiveId,
        string? liberatorId = null,
        string? anchorEntityId = null,
        string faction = "player",
        IReadOnlyList<string>? roles = null,
        bool preserveMembership = true,
        string aiPolicyId = "follower",
        int loyaltyDelta = 4,
        int fearDelta = 0,
        int admirationDelta = 2,
        int resentmentDelta = 0,
        string? posture = "follower",
        bool satisfyWant = true,
        bool recordDeed = true,
        string deedKind = "freed_prisoner",
        int deedMagnitude = 3,
        IReadOnlyList<string>? deedTags = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "freeCaptive",
        bool emitMessage = true,
        string? message = null,
        bool offerObjectiveHandoff = true,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.FreeCaptive,
            source,
            sourceEntityId ?? liberatorId,
            captiveId,
            deedMagnitude,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("captiveId", captiveId),
                ("liberatorId", liberatorId),
                ("anchorEntityId", anchorEntityId),
                ("faction", faction),
                ("roles", roles?.ToArray() ?? Array.Empty<string>()),
                ("preserveMembership", preserveMembership),
                ("aiPolicyId", aiPolicyId),
                ("loyaltyDelta", loyaltyDelta),
                ("fearDelta", fearDelta),
                ("admirationDelta", admirationDelta),
                ("resentmentDelta", resentmentDelta),
                ("posture", posture),
                ("satisfyWant", satisfyWant),
                ("recordDeed", recordDeed),
                ("deedKind", deedKind),
                ("deedMagnitude", deedMagnitude),
                ("deedTags", deedTags?.ToArray() ?? Array.Empty<string>()),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message),
                ("offerObjectiveHandoff", offerObjectiveHandoff)));

    public static WorldConsequence AnimateEntity(
        string source,
        string targetEntityId,
        string faction = "player",
        int? hp = null,
        int? attack = null,
        string? name = null,
        int? expiresTurn = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "animateEntity",
        bool emitMessage = true,
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AnimateEntity,
            source,
            sourceEntityId,
            targetEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("faction", faction),
                ("hp", hp),
                ("attack", attack),
                ("name", name),
                ("expiresTurn", expiresTurn),
                ("operation", operation),
                ("emitMessage", emitMessage),
                ("message", message)));

    public static WorldConsequence CreateRoute(
        string source,
        string anchorEntityId,
        string name,
        string description,
        string routeKind = "hidden_route",
        IReadOnlyList<string>? tags = null,
        IReadOnlyList<string>? promiseIds = null,
        string material = "passage",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createRoute",
        string? message = null,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.CreateRoute,
            source,
            sourceEntityId,
            anchorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("name", name),
                ("description", description),
                ("routeKind", routeKind),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("promiseIds", promiseIds?.ToArray() ?? Array.Empty<string>()),
                ("material", material),
                ("operation", operation),
                ("message", message)));

    private static IReadOnlyDictionary<string, object?> MergePayload(
        IReadOnlyDictionary<string, object?>? details,
        params (string Key, object? Value)[] fields)
    {
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            if (value is not null)
            {
                payload[key] = value;
            }
        }

        return payload;
    }

    private static object? ExistingValue(IReadOnlyDictionary<string, object?>? details, string key) =>
        details is not null && details.TryGetValue(key, out var value) ? value : null;

    private static bool ExistingBool(IReadOnlyDictionary<string, object?>? details, string key) =>
        ExistingValue(details, key) switch
        {
            bool typed => typed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => false,
        };
}

public sealed record WorldConsequenceApplyResult(
    bool Applied,
    string? TargetId,
    string? Error,
    IReadOnlyList<string> Messages,
    IReadOnlyList<StateDelta> Deltas,
    IReadOnlyDictionary<string, object?> Details)
{
    public static WorldConsequenceApplyResult Empty(string? error = null) =>
        new(false, null, error, Array.Empty<string>(), Array.Empty<StateDelta>(), new Dictionary<string, object?>());
}
