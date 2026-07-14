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

public sealed partial class WorldConsequenceApplier
{
    private readonly GameState _state;
    private readonly GameEngine? _engine;
    private readonly int _defaultBondDeltaLimit;

    public WorldConsequenceApplier(GameState state, GameEngine? engine = null, int defaultBondDeltaLimit = 2)
    {
        _state = state;
        _engine = engine;
        _defaultBondDeltaLimit = defaultBondDeltaLimit;
    }

    public WorldConsequenceApplyResult Apply(WorldConsequence consequence)
    {
        consequence = consequence with { Timing = WorldConsequenceTiming.Normalize(consequence.Timing) };
        var normalizedType = WorldConsequenceTypes.Normalize(consequence.Type);
        if (WorldConsequenceTypes.IsKnown(normalizedType)
            && !normalizedType.Equals(consequence.Type, StringComparison.Ordinal))
        {
            consequence = consequence with { Type = normalizedType };
        }

        if (ShouldScheduleByTiming(consequence, normalizedType))
        {
            return ApplyScheduleEvent(TimedScheduleConsequence(consequence, normalizedType));
        }

        return FamilyDispatch.TryGetValue(normalizedType, out var handler)
            ? handler(this, consequence)
            : Reject(consequence, $"Unknown world consequence type: {consequence.Type}");
    }

    /// <summary>
    /// Explicit consequence-family dispatch registry (Phase 0.2): the single authoritative path
    /// mapping each canonical consequence type to the handler that owns it. Replaces the former
    /// ~72-case dispatch switch. Static open delegates keep the table allocation-free per applier
    /// instance; <see cref="DispatchableConsequenceTypes"/> exposes the key set so tests can assert
    /// every catalog type has exactly one dispatch owner and no handler is orphaned. Handlers stay
    /// grouped by cohesive family in the <c>WorldConsequenceApplier.*.cs</c> partial files.
    /// </summary>
    private static readonly Dictionary<string, Func<WorldConsequenceApplier, WorldConsequence, WorldConsequenceApplyResult>> FamilyDispatch =
        new(StringComparer.Ordinal)
        {
            [WorldConsequenceTypes.Damage] = static (a, c) => a.ApplyDamage(c),
            [WorldConsequenceTypes.Heal] = static (a, c) => a.ApplyHeal(c),
            [WorldConsequenceTypes.RestoreMana] = static (a, c) => a.ApplyRestoreMana(c),
            [WorldConsequenceTypes.AdjustActorResource] = static (a, c) => a.ApplyAdjustActorResource(c),
            [WorldConsequenceTypes.MoveEntity] = static (a, c) => a.ApplyMoveEntity(c),
            [WorldConsequenceTypes.SetTerrain] = static (a, c) => a.ApplySetTerrain(c),
            [WorldConsequenceTypes.UpdateTerrain] = static (a, c) => a.ApplyUpdateTerrain(c),
            [WorldConsequenceTypes.ApplyStatus] = static (a, c) => a.ApplyApplyStatus(c),
            [WorldConsequenceTypes.RemoveStatus] = static (a, c) => a.ApplyRemoveStatus(c),
            [WorldConsequenceTypes.AccelerateStatus] = static (a, c) => a.ApplyAccelerateStatus(c),
            [WorldConsequenceTypes.SpawnEntity] = static (a, c) => a.ApplySpawnEntity(c),
            [WorldConsequenceTypes.SpawnItem] = static (a, c) => a.ApplySpawnItem(c),
            [WorldConsequenceTypes.SpawnFixture] = static (a, c) => a.ApplySpawnFixture(c),
            [WorldConsequenceTypes.CreatePromise] = static (a, c) => a.ApplyCreatePromise(c),
            [WorldConsequenceTypes.UpdatePromise] = static (a, c) => a.ApplyUpdatePromise(c),
            [WorldConsequenceTypes.Message] = static (a, c) => a.ApplyMessage(c),
            [WorldConsequenceTypes.ModifyInventory] = static (a, c) => a.ApplyModifyInventory(c),
            [WorldConsequenceTypes.TransferItem] = static (a, c) => a.ApplyTransferItem(c),
            [WorldConsequenceTypes.UpdateEquipment] = static (a, c) => a.ApplyUpdateEquipment(c),
            [WorldConsequenceTypes.AddTags] = static (a, c) => a.ApplyAddTags(c),
            [WorldConsequenceTypes.RemoveTags] = static (a, c) => a.ApplyRemoveTags(c),
            [WorldConsequenceTypes.ChangeFaction] = static (a, c) => a.ApplyChangeFaction(c),
            [WorldConsequenceTypes.UpdateControl] = static (a, c) => a.ApplyUpdateControl(c),
            [WorldConsequenceTypes.SetControlledEntity] = static (a, c) => a.ApplySetControlledEntity(c),
            [WorldConsequenceTypes.SwapSouls] = static (a, c) => a.ApplySwapSouls(c),
            [WorldConsequenceTypes.SetWorldFlag] = static (a, c) => a.ApplySetWorldFlag(c),
            [WorldConsequenceTypes.UpdateRunStatus] = static (a, c) => a.ApplyUpdateRunStatus(c),
            [WorldConsequenceTypes.SetSelectedTarget] = static (a, c) => a.ApplySetSelectedTarget(c),
            [WorldConsequenceTypes.QueueBackgroundJob] = static (a, c) => a.ApplyQueueBackgroundJob(c),
            [WorldConsequenceTypes.UpdateBackgroundJob] = static (a, c) => a.ApplyUpdateBackgroundJob(c),
            [WorldConsequenceTypes.ScheduleEvent] = static (a, c) => a.ApplyScheduleEvent(c),
            [WorldConsequenceTypes.UpdateScheduledEvent] = static (a, c) => a.ApplyUpdateScheduledEvent(c),
            [WorldConsequenceTypes.CreateTrigger] = static (a, c) => a.ApplyCreateTrigger(c),
            [WorldConsequenceTypes.UpdateTrigger] = static (a, c) => a.ApplyUpdateTrigger(c),
            [WorldConsequenceTypes.AdjustFactionStanding] = static (a, c) => a.ApplyAdjustFactionStanding(c),
            [WorldConsequenceTypes.AdjustFactionResource] = static (a, c) => a.ApplyAdjustFactionResource(c),
            [WorldConsequenceTypes.RecordSuspicion] = static (a, c) => a.ApplyRecordSuspicion(c),
            [WorldConsequenceTypes.UpdateSuspicion] = static (a, c) => a.ApplyUpdateSuspicion(c),
            [WorldConsequenceTypes.RecordDeed] = static (a, c) => a.ApplyRecordDeed(c),
            [WorldConsequenceTypes.UpdateDeed] = static (a, c) => a.ApplyUpdateDeed(c),
            [WorldConsequenceTypes.AddLegend] = static (a, c) => a.ApplyAddLegend(c),
            [WorldConsequenceTypes.AddCanon] = static (a, c) => a.ApplyAddCanon(c),
            [WorldConsequenceTypes.RecordWorldTurn] = static (a, c) => a.ApplyRecordWorldTurn(c),
            [WorldConsequenceTypes.RecordExploration] = static (a, c) => a.ApplyRecordExploration(c),
            [WorldConsequenceTypes.TransformEntity] = static (a, c) => a.ApplyTransformEntity(c),
            [WorldConsequenceTypes.SetResistance] = static (a, c) => a.ApplySetResistance(c, weakness: false),
            [WorldConsequenceTypes.SetWeakness] = static (a, c) => a.ApplySetResistance(c, weakness: true),
            [WorldConsequenceTypes.DelayIncomingDamage] = static (a, c) => a.ApplyDelayIncomingDamage(c),
            [WorldConsequenceTypes.ReleaseDelayedDamage] = static (a, c) => a.ApplyReleaseDelayedDamage(c),
            [WorldConsequenceTypes.EditMemory] = static (a, c) => a.ApplyEditMemory(c),
            [WorldConsequenceTypes.CreatePersistentEffect] = static (a, c) => a.ApplyCreatePersistentEffect(c),
            [WorldConsequenceTypes.UpdatePersistentEffect] = static (a, c) => a.ApplyUpdatePersistentEffect(c),
            [WorldConsequenceTypes.SetBehavior] = static (a, c) => a.ApplySetBehavior(c),
            [WorldConsequenceTypes.UpdateBehavior] = static (a, c) => a.ApplyUpdateBehavior(c),
            [WorldConsequenceTypes.CreateFlow] = static (a, c) => a.ApplyCreateFlow(c),
            [WorldConsequenceTypes.UpdateFlow] = static (a, c) => a.ApplyUpdateFlow(c),
            [WorldConsequenceTypes.RecordClaim] = static (a, c) => a.ApplyRecordClaim(c),
            [WorldConsequenceTypes.UpdateClaim] = static (a, c) => a.ApplyUpdateClaim(c),
            [WorldConsequenceTypes.RecordRumor] = static (a, c) => a.ApplyRecordRumor(c),
            [WorldConsequenceTypes.UpdateRumor] = static (a, c) => a.ApplyUpdateRumor(c),
            [WorldConsequenceTypes.RecordMemory] = static (a, c) => a.ApplyRecordMemory(c),
            [WorldConsequenceTypes.UpdateBond] = static (a, c) => a.ApplyUpdateBond(c),
            [WorldConsequenceTypes.UpdateWant] = static (a, c) => a.ApplyUpdateWant(c),
            [WorldConsequenceTypes.AddMerchantStock] = static (a, c) => a.ApplyAddMerchantStock(c),
            [WorldConsequenceTypes.OfferTrade] = static (a, c) => a.ApplyOfferTrade(c),
            [WorldConsequenceTypes.ExecuteTrade] = static (a, c) => a.ApplyExecuteTrade(c),
            [WorldConsequenceTypes.OfferService] = static (a, c) => a.ApplyOfferService(c),
            [WorldConsequenceTypes.RequestService] = static (a, c) => a.ApplyRequestService(c),
            [WorldConsequenceTypes.OpenOrUnlock] = static (a, c) => a.ApplyOpenOrUnlock(c),
            [WorldConsequenceTypes.CreateRoute] = static (a, c) => a.ApplyCreateRoute(c),
            [WorldConsequenceTypes.FreeCaptive] = static (a, c) => a.ApplyFreeCaptive(c),
            [WorldConsequenceTypes.AnimateEntity] = static (a, c) => a.ApplyAnimateEntity(c),
        };

    /// <summary>
    /// Read-only view of the consequence types with a registered dispatch handler. Tests assert
    /// this equals the <see cref="WorldConsequenceTypes"/> canonical catalog (one owner per type).
    /// </summary>
    public static IReadOnlyCollection<string> DispatchableConsequenceTypes => FamilyDispatch.Keys;

    private static bool ShouldScheduleByTiming(WorldConsequence consequence, string normalizedType) =>
        WorldConsequenceTypes.IsKnown(normalizedType)
        && !normalizedType.Equals(WorldConsequenceTypes.ScheduleEvent, StringComparison.OrdinalIgnoreCase)
        && !normalizedType.Equals(WorldConsequenceTypes.UpdateScheduledEvent, StringComparison.OrdinalIgnoreCase)
        && !consequence.Timing.Equals(WorldConsequenceTiming.Immediate, StringComparison.OrdinalIgnoreCase);

    private static WorldConsequence TimedScheduleConsequence(WorldConsequence consequence, string normalizedType)
    {
        var timing = WorldConsequenceTiming.Normalize(consequence.Timing);
        var turns = TimedConsequenceDelay(consequence, timing);
        var eventPayload = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["consequenceType"] = normalizedType,
            ["source"] = consequence.Source,
            ["sourceEntityId"] = consequence.SourceEntityId,
            ["targetEntityId"] = consequence.TargetEntityId,
            ["salience"] = consequence.Salience,
            ["confidence"] = consequence.Confidence,
            ["visibility"] = consequence.Visibility,
            ["evidence"] = consequence.Evidence,
            ["reason"] = consequence.Reason ?? $"Timed consequence delivered {normalizedType}.",
            ["consequencePayload"] = consequence.Payload ?? new Dictionary<string, object?>(),
            ["timing"] = WorldConsequenceTiming.Immediate,
            ["scheduledTiming"] = timing,
        };
        return WorldConsequence.ScheduleEvent(
            "consequence_timing",
            "timed_consequence",
            turns,
            eventPayload,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: consequence.SourceEntityId,
            evidence: consequence.Evidence,
            reason: consequence.Reason ?? $"Queued {normalizedType} for {timing}.",
            operation: "scheduleTimedConsequence",
            details: new Dictionary<string, object?>
            {
                ["scheduledConsequenceType"] = normalizedType,
                ["scheduledTiming"] = timing,
                ["turns"] = turns,
            });
    }

    private static int TimedConsequenceDelay(WorldConsequence consequence, string timing)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var explicitDelay = ReadInt(payload, "turns")
            ?? ReadInt(payload, "delay")
            ?? ReadInt(payload, "delayTurns")
            ?? ReadInt(payload, "delay_turns");
        return timing switch
        {
            WorldConsequenceTiming.AfterTurn or WorldConsequenceTiming.WorldPump => Math.Clamp(explicitDelay ?? 1, 1, 999),
            WorldConsequenceTiming.Deferred => Math.Clamp(explicitDelay ?? 1, 1, 999),
            _ => 1,
        };
    }
}
