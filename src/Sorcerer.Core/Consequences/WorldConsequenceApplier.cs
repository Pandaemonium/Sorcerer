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

public sealed class WorldConsequenceApplier
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

        return normalizedType switch
        {
            WorldConsequenceTypes.Damage => ApplyDamage(consequence),
            WorldConsequenceTypes.Heal => ApplyHeal(consequence),
            WorldConsequenceTypes.RestoreMana => ApplyRestoreMana(consequence),
            WorldConsequenceTypes.AdjustActorResource => ApplyAdjustActorResource(consequence),
            WorldConsequenceTypes.MoveEntity => ApplyMoveEntity(consequence),
            WorldConsequenceTypes.SetTerrain => ApplySetTerrain(consequence),
            WorldConsequenceTypes.UpdateTerrain => ApplyUpdateTerrain(consequence),
            WorldConsequenceTypes.ApplyStatus => ApplyApplyStatus(consequence),
            WorldConsequenceTypes.RemoveStatus => ApplyRemoveStatus(consequence),
            WorldConsequenceTypes.AccelerateStatus => ApplyAccelerateStatus(consequence),
            WorldConsequenceTypes.SpawnEntity => ApplySpawnEntity(consequence),
            WorldConsequenceTypes.SpawnItem => ApplySpawnItem(consequence),
            WorldConsequenceTypes.SpawnFixture => ApplySpawnFixture(consequence),
            WorldConsequenceTypes.CreatePromise => ApplyCreatePromise(consequence),
            WorldConsequenceTypes.UpdatePromise => ApplyUpdatePromise(consequence),
            WorldConsequenceTypes.Message => ApplyMessage(consequence),
            WorldConsequenceTypes.ModifyInventory => ApplyModifyInventory(consequence),
            WorldConsequenceTypes.TransferItem => ApplyTransferItem(consequence),
            WorldConsequenceTypes.UpdateEquipment => ApplyUpdateEquipment(consequence),
            WorldConsequenceTypes.AddTags => ApplyAddTags(consequence),
            WorldConsequenceTypes.RemoveTags => ApplyRemoveTags(consequence),
            WorldConsequenceTypes.ChangeFaction => ApplyChangeFaction(consequence),
            WorldConsequenceTypes.UpdateControl => ApplyUpdateControl(consequence),
            WorldConsequenceTypes.SetControlledEntity => ApplySetControlledEntity(consequence),
            WorldConsequenceTypes.SwapSouls => ApplySwapSouls(consequence),
            WorldConsequenceTypes.SetWorldFlag => ApplySetWorldFlag(consequence),
            WorldConsequenceTypes.UpdateRunStatus => ApplyUpdateRunStatus(consequence),
            WorldConsequenceTypes.SetSelectedTarget => ApplySetSelectedTarget(consequence),
            WorldConsequenceTypes.QueueBackgroundJob => ApplyQueueBackgroundJob(consequence),
            WorldConsequenceTypes.UpdateBackgroundJob => ApplyUpdateBackgroundJob(consequence),
            WorldConsequenceTypes.ScheduleEvent => ApplyScheduleEvent(consequence),
            WorldConsequenceTypes.UpdateScheduledEvent => ApplyUpdateScheduledEvent(consequence),
            WorldConsequenceTypes.CreateTrigger => ApplyCreateTrigger(consequence),
            WorldConsequenceTypes.UpdateTrigger => ApplyUpdateTrigger(consequence),
            WorldConsequenceTypes.AdjustFactionStanding => ApplyAdjustFactionStanding(consequence),
            WorldConsequenceTypes.AdjustFactionResource => ApplyAdjustFactionResource(consequence),
            WorldConsequenceTypes.RecordSuspicion => ApplyRecordSuspicion(consequence),
            WorldConsequenceTypes.UpdateSuspicion => ApplyUpdateSuspicion(consequence),
            WorldConsequenceTypes.RecordDeed => ApplyRecordDeed(consequence),
            WorldConsequenceTypes.UpdateDeed => ApplyUpdateDeed(consequence),
            WorldConsequenceTypes.AddLegend => ApplyAddLegend(consequence),
            WorldConsequenceTypes.AddCanon => ApplyAddCanon(consequence),
            WorldConsequenceTypes.RecordWorldTurn => ApplyRecordWorldTurn(consequence),
            WorldConsequenceTypes.RecordExploration => ApplyRecordExploration(consequence),
            WorldConsequenceTypes.TransformEntity => ApplyTransformEntity(consequence),
            WorldConsequenceTypes.SetResistance => ApplySetResistance(consequence, weakness: false),
            WorldConsequenceTypes.SetWeakness => ApplySetResistance(consequence, weakness: true),
            WorldConsequenceTypes.DelayIncomingDamage => ApplyDelayIncomingDamage(consequence),
            WorldConsequenceTypes.ReleaseDelayedDamage => ApplyReleaseDelayedDamage(consequence),
            WorldConsequenceTypes.EditMemory => ApplyEditMemory(consequence),
            WorldConsequenceTypes.CreatePersistentEffect => ApplyCreatePersistentEffect(consequence),
            WorldConsequenceTypes.UpdatePersistentEffect => ApplyUpdatePersistentEffect(consequence),
            WorldConsequenceTypes.SetBehavior => ApplySetBehavior(consequence),
            WorldConsequenceTypes.UpdateBehavior => ApplyUpdateBehavior(consequence),
            WorldConsequenceTypes.CreateFlow => ApplyCreateFlow(consequence),
            WorldConsequenceTypes.UpdateFlow => ApplyUpdateFlow(consequence),
            WorldConsequenceTypes.RecordClaim => ApplyRecordClaim(consequence),
            WorldConsequenceTypes.UpdateClaim => ApplyUpdateClaim(consequence),
            WorldConsequenceTypes.RecordRumor => ApplyRecordRumor(consequence),
            WorldConsequenceTypes.UpdateRumor => ApplyUpdateRumor(consequence),
            WorldConsequenceTypes.RecordMemory => ApplyRecordMemory(consequence),
            WorldConsequenceTypes.UpdateBond => ApplyUpdateBond(consequence),
            WorldConsequenceTypes.UpdateWant => ApplyUpdateWant(consequence),
            WorldConsequenceTypes.AddMerchantStock => ApplyAddMerchantStock(consequence),
            WorldConsequenceTypes.OfferTrade => ApplyOfferTrade(consequence),
            WorldConsequenceTypes.ExecuteTrade => ApplyExecuteTrade(consequence),
            WorldConsequenceTypes.OfferService => ApplyOfferService(consequence),
            WorldConsequenceTypes.RequestService => ApplyRequestService(consequence),
            WorldConsequenceTypes.OpenOrUnlock => ApplyOpenOrUnlock(consequence),
            WorldConsequenceTypes.CreateRoute => ApplyCreateRoute(consequence),
            WorldConsequenceTypes.FreeCaptive => ApplyFreeCaptive(consequence),
            WorldConsequenceTypes.AnimateEntity => ApplyAnimateEntity(consequence),
            _ => Reject(consequence, $"Unknown world consequence type: {consequence.Type}"),
        };
    }

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

    private WorldConsequenceApplyResult ApplyDamage(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Damage consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            return Reject(consequence, "Damage consequence target is not a living actor.");
        }

        var amount = Math.Clamp(ReadInt(payload, "amount") ?? 1, 1, 999);
        var damageType = FirstNonBlank(ReadString(payload, "damageType"), ReadString(payload, "damage_type"), "arcane")!;
        var operation = ReadString(payload, "operation");
        var attacker = FirstNonBlank(ReadString(payload, "attacker"), consequence.SourceEntityId) is { } attackerId
            ? EntityById(attackerId)
            : null;
        var isAttack = operation is not null
            && operation.Contains("attack", StringComparison.OrdinalIgnoreCase)
            && attacker is not null;
        var delta = DamageEntityDelta(target.Entity, amount, damageType);
        if (isAttack && delta.Operation.Equals("delayIncoming", StringComparison.OrdinalIgnoreCase))
        {
            AddMessageIfAllowed(consequence, payload, delta.Summary);
            return AppliedFromDelta(consequence, delta);
        }

        if (isAttack)
        {
            var actual = delta.Details.TryGetValue("amount", out var dealt) ? Convert.ToInt32(dealt) : amount;
            var summary = AttackSummary(attacker!, target.Entity!, actual, damageType);
            AddMessageIfAllowed(consequence, payload, summary);
            delta = new StateDelta(delta.Operation, delta.Target, summary, delta.Details);
        }
        else
        {
            AddMessageIfAllowed(consequence, payload, delta.Summary);
        }

        return AppliedFromDelta(consequence, WithOperation(delta, operation));
    }

    private WorldConsequenceApplyResult ApplyHeal(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Heal consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<ActorComponent>(out var actor))
        {
            return Reject(consequence, "Heal consequence target is not an actor.");
        }

        var amount = Math.Clamp(ReadInt(payload, "amount") ?? 1, 1, 999);
        var delta = HealActorDelta(
            consequence,
            target.Entity,
            actor,
            amount,
            ReadString(payload, "operation") ?? "heal");
        AddMessageIfAllowed(consequence, payload, delta.Summary);

        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyRestoreMana(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Restore-mana consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<ActorComponent>(out var actor))
        {
            return Reject(consequence, "Restore-mana consequence target is not an actor.");
        }

        var amount = Math.Clamp(ReadInt(payload, "amount") ?? 1, 1, 999);
        var soul = CharacterMath.EnsureSoulRecord(_state, target.Entity);
        var restored = Math.Max(0, Math.Min(amount, soul.MaxMana - soul.Mana));
        var updatedSoul = soul with { Mana = soul.Mana + restored };
        _state.Souls.Set(updatedSoul);
        target.Entity.Set(CharacterMath.ActorWithSoulMana(actor, updatedSoul));
        var summary = restored == 0
            ? $"{Subject(target.Entity)} {Verb(target.Entity, "are", "is")} already bright with mana."
            : $"{Subject(target.Entity)} {Verb(target.Entity, "regain", "regains")} {restored} mana.";
        var operation = ReadString(payload, "operation") ?? "restoreMana";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(
                consequence,
                ("amount", restored),
                ("requestedAmount", amount),
                ("mana", updatedSoul.Mana),
                ("maxMana", updatedSoul.MaxMana),
                ("soulId", updatedSoul.SoulId)));
        AddMessageIfAllowed(consequence, payload, summary);

        var messages = IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence)
            ? new[] { summary }
            : Array.Empty<string>();
        return Applied(
            consequence,
            target.Entity.Id.Value,
            messages,
            delta,
            ("amount", restored),
            ("mana", updatedSoul.Mana),
            ("maxMana", updatedSoul.MaxMana),
            ("soulId", updatedSoul.SoulId));
    }

    private WorldConsequenceApplyResult ApplyAdjustActorResource(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Actor-resource consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<ActorComponent>(out var actor))
        {
            return Reject(consequence, "Actor-resource consequence target is not an actor.");
        }

        var resource = NormalizeToken(FirstNonBlank(ReadString(payload, "resource"), ReadString(payload, "stat"), "health")!, "health");
        var delta = ReadInt(payload, "delta") ?? ReadInt(payload, "amount") ?? 0;
        if (delta == 0)
        {
            return Reject(consequence, "Actor-resource consequence did not include a non-zero delta.");
        }

        var min = ReadInt(payload, "min");
        var max = ReadInt(payload, "max");
        var before = ResourceValue(_state, target.Entity, actor, resource);
        if (before is null)
        {
            return Reject(consequence, $"Unknown actor resource: {resource}");
        }

        var after = ApplyActorResourceDelta(_state, target.Entity, actor, resource, delta, min, max);
        var actualDelta = after - before.Value;
        var defaultSummary = FormatActorResourceSummary(target.Entity, resource, actualDelta, after);
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultSummary)!;
        AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false, includeVisible: false);

        var operation = ReadString(payload, "operation") ?? "adjustActorResource";
        var deltaRecord = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(
                consequence,
                ("resource", resource),
                ("before", before.Value),
                ("after", after),
                ("delta", actualDelta),
                ("amount", Math.Abs(actualDelta))));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), deltaRecord, ("resource", resource), ("after", after));
    }

    private StateDelta HealActorDelta(
        WorldConsequence consequence,
        Entity target,
        ActorComponent actor,
        int amount,
        string operation)
    {
        var healed = Math.Max(0, Math.Min(amount, actor.MaxHitPoints - actor.HitPoints));
        target.Set(actor with { HitPoints = actor.HitPoints + healed });
        var summary = healed == 0
            ? $"{Subject(target)} {Verb(target, "are", "is")} already whole."
            : $"{Subject(target)} {Verb(target, "heal", "heals")} {healed} HP.";
        return new StateDelta(
            string.IsNullOrWhiteSpace(operation) ? "heal" : operation,
            target.Id.Value,
            summary,
            new Dictionary<string, object?>
            {
                ["amount"] = healed,
                ["requestedAmount"] = amount,
                ["hitPoints"] = actor.HitPoints + healed,
                ["maxHitPoints"] = actor.MaxHitPoints,
                ["source"] = consequence.Source,
            });
    }

    private StateDelta DamageEntityDelta(Entity target, int amount, string damageType)
    {
        var scaled = ScaleByResistance(target, amount, damageType);
        if (target.TryGet<DelayedDamageComponent>(out var buffer))
        {
            return BufferDelayedDamageDelta(target, buffer, scaled, damageType);
        }

        return ApplyImmediateDamageDelta(target, scaled, damageType);
    }

    private StateDelta ApplyImmediateDamageDelta(Entity target, int amount, string damageType)
    {
        var actor = target.Get<ActorComponent>();
        var actual = Math.Max(1, amount - actor.Defense);
        var updated = actor with { HitPoints = Math.Max(0, actor.HitPoints - actual) };
        target.Set(updated);
        if (!updated.Alive)
        {
            MarkDefeated(target);
        }

        var summary = updated.Alive
            ? $"{Subject(target)} {Verb(target, "take", "takes")} {actual} {damageType} damage."
            : $"{Subject(target)} {Verb(target, "fall", "falls")}.";
        return new StateDelta(
            "damage",
            target.Id.Value,
            summary,
            new Dictionary<string, object?>
            {
                ["amount"] = actual,
                ["damageType"] = damageType,
            });
    }

    private StateDelta BufferDelayedDamageDelta(Entity target, DelayedDamageComponent buffer, int amount, string damageType)
    {
        var buffered = buffer.Buffered + Math.Max(0, amount);
        target.Set(buffer with { Buffered = buffered });
        var summary = $"{Subject(target)} {Verb(target, "feel", "feels")} {damageType} damage gathering, held back for later.";
        return new StateDelta(
            "delayIncoming",
            target.Id.Value,
            summary,
            new Dictionary<string, object?>
            {
                ["buffered"] = buffered,
                ["damageType"] = damageType,
            });
    }

    private static int ScaleByResistance(Entity target, int amount, string damageType)
    {
        if (!target.TryGet<ResistanceComponent>(out var resistance))
        {
            return amount;
        }

        var resistPercent = resistance.Resistances.TryGetValue(damageType, out var resist)
            ? Math.Clamp(resist, 0, 95)
            : 0;
        var weakPercent = resistance.Weaknesses.TryGetValue(damageType, out var weak)
            ? Math.Clamp(weak, 0, 200)
            : 0;
        var scaled = amount * (100 - resistPercent + weakPercent) / 100.0;
        return Math.Max(0, (int)Math.Round(scaled, MidpointRounding.AwayFromZero));
    }

    private static void MarkDefeated(Entity entity)
    {
        if (entity.TryGet<PhysicalComponent>(out var physical))
        {
            entity.Set(physical with { BlocksMovement = false });
        }

        if (entity.TryGet<RenderableComponent>(out var renderable))
        {
            entity.Set(renderable with { Glyph = '%', Palette = "corpse" });
        }

        var tags = entity.TryGet<TagsComponent>(out var existing)
            ? existing.Tags.ToList()
            : new List<string>();
        if (!tags.Contains("defeated", StringComparer.OrdinalIgnoreCase))
        {
            tags.Add("defeated");
        }

        entity.Set(new TagsComponent(tags));
    }

    private static int? ResourceValue(GameState state, Entity entity, ActorComponent actor, string resource) =>
        resource switch
        {
            "health" or "hp" or "hit_points" or "hitpoints" => actor.HitPoints,
            "max_health" or "maxhealth" or "max_hp" or "maxhp" => actor.MaxHitPoints,
            "mana" => CharacterMath.EnsureSoulRecord(state, entity).Mana,
            "max_mana" or "maxmana" => CharacterMath.EnsureSoulRecord(state, entity).MaxMana,
            _ => null,
        };

    private static int ApplyActorResourceDelta(
        GameState state,
        Entity entity,
        ActorComponent actor,
        string resource,
        int delta,
        int? min,
        int? max)
    {
        switch (resource)
        {
            case "health":
            case "hp":
            case "hit_points":
            case "hitpoints":
                {
                    var floor = min ?? 0;
                    var ceiling = max ?? actor.MaxHitPoints;
                    var next = Math.Clamp(actor.HitPoints + delta, floor, Math.Max(floor, ceiling));
                    entity.Set(actor with { HitPoints = next });
                    if (next <= 0)
                    {
                        MarkDefeated(entity);
                    }

                    return next;
                }

            case "max_health":
            case "maxhealth":
            case "max_hp":
            case "maxhp":
                {
                    var floor = min ?? 1;
                    var nextMax = Math.Clamp(actor.MaxHitPoints + delta, floor, max ?? 999);
                    var hitPointFloor = actor.Alive ? 1 : 0;
                    entity.Set(actor with
                    {
                        MaxHitPoints = nextMax,
                        HitPoints = Math.Clamp(actor.HitPoints, hitPointFloor, nextMax),
                    });
                    return nextMax;
                }

            case "mana":
                {
                    var soul = CharacterMath.EnsureSoulRecord(state, entity);
                    var floor = min ?? 0;
                    var ceiling = max ?? soul.MaxMana;
                    var next = Math.Clamp(soul.Mana + delta, floor, Math.Max(floor, ceiling));
                    var updatedSoul = soul with { Mana = next };
                    state.Souls.Set(updatedSoul);
                    // Always sync the actor mirror through the one shared helper (also used by
                    // ApplyRestoreMana) so Mana is re-clamped to [0, MaxMana] the same way
                    // everywhere, even when an explicit payload max exceeds the soul's MaxMana.
                    entity.Set(CharacterMath.ActorWithSoulMana(actor, updatedSoul));
                    return next;
                }

            case "max_mana":
            case "maxmana":
                {
                    var soul = CharacterMath.EnsureSoulRecord(state, entity);
                    var floor = min ?? 0;
                    var nextMax = Math.Clamp(soul.MaxMana + delta, floor, max ?? 999);
                    var updatedSoul = soul with
                    {
                        MaxMana = nextMax,
                        Mana = Math.Clamp(soul.Mana, 0, nextMax),
                    };
                    state.Souls.Set(updatedSoul);
                    entity.Set(CharacterMath.ActorWithSoulMana(actor, updatedSoul));
                    return nextMax;
                }

            default:
                return 0;
        }
    }

    private string FormatActorResourceSummary(Entity entity, string resource, int delta, int after)
    {
        var verb = Verb(entity, delta >= 0 ? "gain" : "lose", delta >= 0 ? "gains" : "loses");
        var label = resource.Replace('_', ' ');
        return $"{Subject(entity)} {verb} {Math.Abs(delta)} {label}; now {after}.";
    }

    private WorldConsequenceApplyResult ApplyMoveEntity(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Move consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<PositionComponent>(out var position))
        {
            return Reject(consequence, "Move consequence target has no position.");
        }

        if (!TryReadPoint(payload, null, out var point))
        {
            return Reject(consequence, "Move consequence did not include a destination coordinate.");
        }

        var operation = FirstNonBlank(ReadString(payload, "operation"), "move")!;
        var emitMessage = ReadBool(payload, "emitMessage") ?? true;
        var swapWithEntityId = FirstNonBlank(
            ReadString(payload, "swapWithEntityId"),
            ReadString(payload, "swap_with_entity_id"));
        StateDelta delta;
        var blocker = BlockingEntityAt(point);
        if (!InBounds(point) || _state.BlockingTerrain.Contains(point))
        {
            var blocked = $"{target.Entity.Name} cannot move to {point.X},{point.Y}.";
            delta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                blocked,
                new Dictionary<string, object?>
                {
                    ["fromX"] = position.Position.X,
                    ["fromY"] = position.Position.Y,
                    ["blocked"] = true,
                });
        }
        else if (blocker is not null && blocker.Id != target.Entity.Id)
        {
            if (!string.IsNullOrWhiteSpace(swapWithEntityId)
                && blocker.Id.Value.Equals(swapWithEntityId, StringComparison.OrdinalIgnoreCase)
                && blocker.TryGet<PositionComponent>(out var blockerPosition))
            {
                var previous = position.Position;
                target.Entity.Set(new PositionComponent(point));
                blocker.Set(new PositionComponent(previous));
                var movementDelta = new GridPoint(point.X - previous.X, point.Y - previous.Y);
                var recordedControlledMovement = false;
                if (ReadBool(payload, "recordControlledMovement") == true
                    && target.Entity.Id == _state.ControlledEntityId
                    && movementDelta != new GridPoint(0, 0))
                {
                    _state.LastControlledMoveDelta = movementDelta;
                    recordedControlledMovement = true;
                }

                var message = FirstNonBlank(
                    ReadString(payload, "message"),
                    ReadString(payload, "summary"),
                    $"{Subject(target.Entity)} {Verb(target.Entity, "trade", "trades")} places with {blocker.Name}.")!;
                delta = new StateDelta(
                    operation,
                    target.Entity.Id.Value,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["fromX"] = previous.X,
                        ["fromY"] = previous.Y,
                        ["toX"] = point.X,
                        ["toY"] = point.Y,
                        ["dx"] = movementDelta.X,
                        ["dy"] = movementDelta.Y,
                        ["recordControlledMovement"] = recordedControlledMovement,
                        ["swappedWithEntityId"] = blocker.Id.Value,
                        ["swappedWithName"] = blocker.Name,
                        ["swappedFromX"] = blockerPosition.Position.X,
                        ["swappedFromY"] = blockerPosition.Position.Y,
                        ["swappedToX"] = previous.X,
                        ["swappedToY"] = previous.Y,
                    });
            }
            else
            {
                var blocked = $"{target.Entity.Name} cannot move to {point.X},{point.Y}.";
                delta = new StateDelta(
                    operation,
                    target.Entity.Id.Value,
                    blocked,
                    new Dictionary<string, object?>
                    {
                        ["fromX"] = position.Position.X,
                        ["fromY"] = position.Position.Y,
                        ["blocked"] = true,
                        ["blockerId"] = blocker.Id.Value,
                    });
            }
        }
        else
        {
            var previous = position.Position;
            target.Entity.Set(new PositionComponent(point));
            var movementDelta = new GridPoint(point.X - previous.X, point.Y - previous.Y);
            var recordedControlledMovement = false;
            if (ReadBool(payload, "recordControlledMovement") == true
                && target.Entity.Id == _state.ControlledEntityId
                && movementDelta != new GridPoint(0, 0))
            {
                _state.LastControlledMoveDelta = movementDelta;
                recordedControlledMovement = true;
            }

            var message = FirstNonBlank(
                ReadString(payload, "message"),
                ReadString(payload, "summary"),
                $"{Subject(target.Entity)} {Verb(target.Entity, "move", "moves")} to {point.X},{point.Y}.")!;
            delta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["fromX"] = previous.X,
                    ["fromY"] = previous.Y,
                    ["toX"] = point.X,
                    ["toY"] = point.Y,
                    ["dx"] = movementDelta.X,
                    ["dy"] = movementDelta.Y,
                    ["recordControlledMovement"] = recordedControlledMovement,
                });
        }

        AddMessageIfAllowed(consequence, payload, delta.Summary, defaultEmitMessage: emitMessage);

        return AppliedFromDelta(
            consequence,
            delta);
    }

    private WorldConsequenceApplyResult ApplySetTerrain(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Terrain consequence did not include a tile coordinate.");
        }

        if (!InBounds(point))
        {
            return Reject(consequence, "Terrain consequence target is out of bounds.");
        }

        var terrain = NormalizeToken(
            FirstNonBlank(ReadString(payload, "terrain"), ReadString(payload, "tile"), "wild_growth")!,
            "wild_growth");
        var duration = ReadInt(payload, "duration");
        _state.Terrain[point] = terrain;
        if (duration is > 0)
        {
            _state.TerrainExpirations[point] = _state.Turn + duration.Value;
        }
        else
        {
            _state.TerrainExpirations.Remove(point);
        }

        if (TerrainBlocksMovement(terrain))
        {
            _state.BlockingTerrain.Add(point);
        }
        else
        {
            _state.BlockingTerrain.Remove(point);
        }

        var operation = ReadString(payload, "operation") ?? "createTile";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"The tile at {point.X},{point.Y} becomes {terrain.Replace('_', ' ')}.")!;
        var delta = new StateDelta(
            operation,
            $"tile:{point.X},{point.Y}",
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("terrain", terrain),
                ("duration", duration)));
        AddMessageIfAllowed(consequence, payload, delta.Summary);

        return Applied(
            consequence,
            delta.Target,
            IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence)
                ? new[] { delta.Summary }
                : Array.Empty<string>(),
            delta,
            ("x", point.X),
            ("y", point.Y),
            ("terrain", terrain),
            ("duration", duration));
    }

    private WorldConsequenceApplyResult ApplyUpdateTerrain(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Terrain update consequence did not include a tile coordinate.");
        }

        if (!engine.InBounds(point))
        {
            return Reject(consequence, "Terrain update consequence target is out of bounds.");
        }

        if (!_state.Terrain.ContainsKey(point) && !_state.TerrainExpirations.ContainsKey(point))
        {
            return Reject(consequence, $"Terrain update target does not exist: {point.X},{point.Y}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "expire")!, "expire");
        var terrain = _state.Terrain.TryGetValue(point, out var existing)
            ? existing
            : "terrain";
        var verb = action switch
        {
            "expire" or "expired" => "fades",
            "remove" or "clear" or "delete" => "is removed",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported terrain update action: {action}.");
        }

        _state.TerrainExpirations.Remove(point);
        _state.Terrain.Remove(point);
        if (!IsBoundaryWall(point))
        {
            _state.BlockingTerrain.Remove(point);
        }

        var operation = ReadString(payload, "operation") ?? "updateTerrain";
        var targetId = $"tile:{point.X},{point.Y}";
        var summary = $"The {terrain.Replace('_', ' ')} at {point.X},{point.Y} {verb}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("terrain", terrain),
                ("action", action)));
        return Applied(consequence, targetId, MaybeVisibleMessage(consequence, summary), delta, ("x", point.X), ("y", point.Y), ("terrain", terrain), ("action", action));
    }

    private WorldConsequenceApplyResult ApplyApplyStatus(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Status consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var status = NormalizeToken(
            FirstNonBlank(ReadString(payload, "status"), ReadString(payload, "trait"), ReadString(payload, "name"), "marked")!,
            "marked");
        var displayName = FirstNonBlank(ReadString(payload, "displayName"), ReadString(payload, "display_name"), status) ?? status;
        var operation = ReadString(payload, "operation") ?? "addStatus";
        var message = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"));
        var requestedDuration = ReadInt(payload, "duration") ?? 0;
        var canonicalStatus = engine.Statuses.Canonicalize(status);
        var duration = requestedDuration > 0
            ? Math.Clamp(requestedDuration, 1, 999)
            : Math.Clamp(engine.Statuses.Find(canonicalStatus)?.DefaultDuration ?? 3, 1, 999);
        var current = target.Entity!.TryGet<StatusContainerComponent>(out var container)
            ? container.Statuses.ToList()
            : new List<StatusInstance>();
        current.Add(new StatusInstance(canonicalStatus, displayName, _state.Turn + duration));
        target.Entity.Set(new StatusContainerComponent(current));
        var summary = string.IsNullOrWhiteSpace(message)
            ? $"{Subject(target.Entity)} {Verb(target.Entity, "are", "is")} {displayName.Replace('_', ' ')}."
            : message;
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            new Dictionary<string, object?>
            {
                ["status"] = canonicalStatus,
                ["displayName"] = displayName,
                ["duration"] = duration,
                ["expiresTurn"] = _state.Turn + duration,
            });
        AddMessageIfAllowed(consequence, payload, delta.Summary);

        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyRemoveStatus(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Remove-status consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var status = NormalizeToken(FirstNonBlank(ReadString(payload, "status"), "marked")!, "marked");
        var emitMessage = ReadBool(payload, "emitMessage") ?? true;
        var canonicalStatus = engine.Statuses.Canonicalize(status);
        var operation = ReadString(payload, "operation") ?? "removeStatus";
        StateDelta delta;
        if (!target.Entity!.TryGet<StatusContainerComponent>(out var container))
        {
            var unchanged = $"{target.Entity.Name} has no {canonicalStatus} to remove.";
            delta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                unchanged,
                new Dictionary<string, object?> { ["status"] = canonicalStatus });
        }
        else
        {
            var remaining = container.Statuses
                .Where(instance => !instance.Id.Equals(canonicalStatus, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            target.Entity.Set(new StatusContainerComponent(remaining));
            var summary = FirstNonBlank(
                ReadString(payload, "message"),
                ReadString(payload, "summary"),
                $"{SentenceCase(canonicalStatus.Replace('_', ' '))} leaves {ObjectName(target.Entity)}.")!;
            delta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                summary,
                new Dictionary<string, object?>
                {
                    ["status"] = canonicalStatus,
                    ["removed"] = remaining.Length != container.Statuses.Count,
                    ["remainingStatuses"] = remaining.Length,
                });
        }

        AddMessageIfAllowed(consequence, payload, delta.Summary, defaultEmitMessage: emitMessage);

        return AppliedFromDelta(
            consequence,
            delta);
    }

    private WorldConsequenceApplyResult ApplyAccelerateStatus(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Accelerate-status consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<StatusContainerComponent>(out var container) || container.Statuses.Count == 0)
        {
            return Reject(consequence, "Accelerate-status target has no statuses.");
        }

        var statusId = NormalizeToken(FirstNonBlank(ReadString(payload, "status"), "")!, "");
        var canonical = string.IsNullOrWhiteSpace(statusId) ? "" : engine.Statuses.Canonicalize(statusId);
        var instance = container.Statuses.FirstOrDefault(status =>
            string.IsNullOrWhiteSpace(canonical)
                ? engine.Statuses.DamagePerTurn(status.Id) != 0 || engine.Statuses.HealPerTurn(status.Id) != 0
                : status.Id.Equals(canonical, StringComparison.OrdinalIgnoreCase));
        if (instance is null)
        {
            return Reject(consequence, "Accelerate-status target does not have a matching ongoing status.");
        }

        var remainingTurns = Math.Max(1, (instance.ExpiresTurn ?? _state.Turn + 1) - _state.Turn);
        var damagePerTurn = engine.Statuses.DamagePerTurn(instance.Id);
        var healPerTurn = engine.Statuses.HealPerTurn(instance.Id);
        target.Entity.Set(new StatusContainerComponent(container.Statuses.Where(status => !ReferenceEquals(status, instance)).ToArray()));

        if (damagePerTurn > 0)
        {
            var delta = DamageEntityDelta(target.Entity, damagePerTurn * remainingTurns, instance.Id);
            AddMessageIfAllowed(consequence, payload, delta.Summary);
            return AppliedFromDelta(consequence, delta);
        }

        if (healPerTurn > 0)
        {
            var actor = target.Entity.Get<ActorComponent>();
            var delta = HealActorDelta(
                consequence,
                target.Entity,
                actor,
                healPerTurn * remainingTurns,
                ReadString(payload, "operation") ?? "accelerateStatusHeal");
            AddMessageIfAllowed(consequence, payload, delta.Summary);
            return AppliedFromDelta(consequence, delta);
        }

        var operation = ReadString(payload, "operation") ?? "accelerateStatus";
        var summary = $"{Possessive(target.Entity)} {instance.DisplayName.Replace('_', ' ')} rushes to its conclusion.";
        var appliedDelta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("status", instance.Id), ("remainingTurns", remainingTurns)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), appliedDelta, ("status", instance.Id));
    }

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
        var interactableVerbs = ReadStringList(payload, "interactableVerbs").Concat(ReadStringList(payload, "verbs")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var fixtureId = _state.NextEntityId(prefix);
        var fixture = new Entity(fixtureId, name)
            .Set(new PositionComponent(point))
            .Set(new RenderableComponent(glyph, palette))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(blocksMovement, blocksSight, material, size, durability))
            .Set(new FixtureComponent(fixtureType, tags));
        if (!string.IsNullOrWhiteSpace(description))
        {
            fixture.Set(new DescriptionComponent(description));
        }

        if (promiseIds.Length > 0)
        {
            fixture.Set(new PromiseAnchorComponent(promiseIds));
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
                ("interactableVerbs", interactableVerbs)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyCreatePromise(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Promise consequence did not include text.");
        }

        var anchor = string.IsNullOrWhiteSpace(consequence.TargetEntityId)
            ? null
            : EntityById(consequence.TargetEntityId);
        if (!string.IsNullOrWhiteSpace(consequence.TargetEntityId) && anchor is null)
        {
            return Reject(consequence, "Promise consequence anchor does not exist.");
        }

        var kind = NormalizeToken(FirstNonBlank(ReadString(payload, "kind"), "omen")!, "omen");
        if (ReadBool(payload, "stackExisting") == true)
        {
            var existing = _state.PromiseLedger.FindActive(kind, text, anchor?.Id.Value)
                ?? (anchor is null
                    ? _state.PromiseLedger.Promises.FirstOrDefault(promise =>
                        promise.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
                        && !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
                        && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                        && promise.Text.Equals(text, StringComparison.OrdinalIgnoreCase))
                    : null);
            if (existing is not null)
            {
                var stacked = _state.PromiseLedger.Stack(existing.Id);
                var stackOperation = ReadString(payload, "operation") ?? "createPromise";
                var stackMessage = FormatStackMessage(
                    FirstNonBlank(
                        ReadString(payload, "stackMessageTemplate"),
                        ReadString(payload, "stackMessage"),
                        ReadString(payload, "stackSummary"),
                        ""),
                    text,
                    kind,
                    stacked.Stacks);
                AddMessageIfAllowed(consequence, payload, stackMessage, playerVisible: stacked.PlayerVisible);

                return AppliedFromDelta(consequence, new StateDelta(
                    stackOperation,
                    stacked.Id,
                    stackMessage,
                    Details(
                        consequence,
                        ("kind", kind),
                        ("promiseId", stacked.Id),
                        ("stacks", stacked.Stacks),
                        ("playerVisible", stacked.PlayerVisible))));
            }
        }

        var triggerHint = FirstNonBlank(ReadString(payload, "triggerHint"), ReadString(payload, "trigger"), ReadString(payload, "trigger_hint"), "")!;
        var realizationKind = FirstNonBlank(ReadString(payload, "realizationKind"), ReadString(payload, "realization_kind"), InferPromiseRealizationKind(kind, text));
        var subject = FirstNonBlank(ReadString(payload, "subject"), SoulIdFor(_state.ControlledEntity))!;
        var claimedPlace = FirstNonBlank(ReadString(payload, "claimedPlace"), ReadString(payload, "claimed_place"));
        if ((ReadBool(payload, "useCurrentRegionAsClaimedPlace") ?? true)
            && string.IsNullOrWhiteSpace(claimedPlace))
        {
            claimedPlace = _state.RegionId;
        }

        var promise = _state.PromiseLedger.Add(
            kind,
            text,
            playerVisible: ReadBool(payload, "playerVisible") ?? true,
            source: consequence.Source,
            salience: ReadInt(payload, "salience") ?? Math.Max(1, consequence.Salience),
            subject: subject,
            claimedPlace: claimedPlace,
            triggerHint: triggerHint,
            realizationKind: realizationKind,
            sourceClaimId: FirstNonBlank(ReadString(payload, "sourceClaimId"), ReadString(payload, "source_claim_id"), ReadString(payload, "claimId"), ReadString(payload, "claim_id")),
            sourceSpeakerId: FirstNonBlank(ReadString(payload, "sourceSpeakerId"), ReadString(payload, "source_speaker_id"), ReadString(payload, "speakerId"), ReadString(payload, "speaker_id")),
            sourceListenerSoulId: FirstNonBlank(ReadString(payload, "sourceListenerSoulId"), ReadString(payload, "source_listener_soul_id"), ReadString(payload, "listenerSoulId"), ReadString(payload, "listener_soul_id")),
            sourceConfidence: ReadInt(payload, "sourceConfidence") ?? ReadInt(payload, "source_confidence") ?? ReadInt(payload, "confidence"));
        var bound = BindPromiseIfPossible(
            promise,
            anchor,
            triggerHint,
            FirstNonBlank(ReadString(payload, "bindPlace"), ReadString(payload, "boundPlace"), ReadString(payload, "bind_place"), ReadString(payload, "bound_place")),
            realizationKind,
            ReadBool(payload, "autoBind") ?? true);
        var finalPromise = bound ?? promise;
        var promiseNoun = finalPromise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase) ? "curse" : "promise";
        var defaultMessage = finalPromise.Status == "bound"
            ? $"A {promiseNoun} binds to {finalPromise.BoundTargetId ?? finalPromise.BoundPlace}: {finalPromise.Text}"
            : $"A {promiseNoun} enters the world: {finalPromise.Text}";
        var message = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultMessage)!;
        AddMessageIfAllowed(consequence, payload, message, playerVisible: finalPromise.PlayerVisible);

        var operation = ReadString(payload, "operation") ?? "createPromise";
        var delta = new StateDelta(
            operation,
            finalPromise.Id,
            message,
            Details(
                consequence,
                ("kind", finalPromise.Kind),
                ("status", finalPromise.Status),
                ("promiseId", finalPromise.Id),
                ("subject", finalPromise.Subject),
                ("playerVisible", finalPromise.PlayerVisible),
                ("salience", finalPromise.Salience),
                ("stacks", finalPromise.Stacks),
                ("claimedPlace", finalPromise.ClaimedPlace),
                ("boundPlace", finalPromise.BoundPlace),
                ("boundTargetId", finalPromise.BoundTargetId),
                ("triggerHint", finalPromise.TriggerHint),
                ("realizationKind", finalPromise.RealizationKind),
                ("sourceClaimId", finalPromise.SourceClaimId),
                ("sourceSpeakerId", finalPromise.SourceSpeakerId),
                ("sourceListenerSoulId", finalPromise.SourceListenerSoulId),
                ("sourceConfidence", finalPromise.SourceConfidence)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyUpdatePromise(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var promiseId = FirstNonBlank(ReadString(payload, "promiseId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(promiseId))
        {
            return Reject(consequence, "Promise update did not include a promise id.");
        }

        var existing = _state.PromiseLedger.Promises.FirstOrDefault(promise =>
            promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Promise does not exist: {promiseId}");
        }

        var status = NormalizeToken(ReadString(payload, "status") ?? "", "");
        var boundPlace = FirstNonBlank(ReadString(payload, "bindPlace"), ReadString(payload, "boundPlace"), ReadString(payload, "bind_place"), ReadString(payload, "bound_place"));
        var boundTargetId = FirstNonBlank(ReadString(payload, "boundTargetId"), ReadString(payload, "targetId"), ReadString(payload, "bound_target_id"));
        // Deliberately does not fall back to a bare "trigger" key: that key is used throughout
        // PromiseRealizationSystem purely as audit context (which trigger evaluated the promise
        // this time), not as a request to rebind the promise's TriggerHint.
        var triggerHint = FirstNonBlank(ReadString(payload, "triggerHint"), ReadString(payload, "trigger_hint"));
        var realizationKind = FirstNonBlank(ReadString(payload, "realizationKind"), ReadString(payload, "realization_kind"));
        var realizedIn = FirstNonBlank(ReadString(payload, "realizedIn"), ReadString(payload, "realized_in"));
        var hasEligibilityFailure = HasAnyKey(payload, "lastEligibilityFailure", "last_eligibility_failure", "eligibilityFailure", "eligibility_failure");
        var eligibilityFailure = FirstNonBlank(
            ReadString(payload, "lastEligibilityFailure"),
            ReadString(payload, "last_eligibility_failure"),
            ReadString(payload, "eligibilityFailure"),
            ReadString(payload, "eligibility_failure"));
        var eligibilityContext = FirstNonBlank(
            ReadString(payload, "lastEligibilityContext"),
            ReadString(payload, "last_eligibility_context"),
            ReadString(payload, "eligibilityContext"),
            ReadString(payload, "eligibility_context"));
        var eligibilityTurn = ReadInt(payload, "lastEligibilityTurn")
            ?? ReadInt(payload, "last_eligibility_turn")
            ?? ReadInt(payload, "eligibilityTurn")
            ?? ReadInt(payload, "eligibility_turn");
        var clearEligibilityFailure = ReadBool(payload, "clearEligibilityFailure")
            ?? ReadBool(payload, "clear_eligibility_failure")
            ?? false;
        var wantsBinding = !string.IsNullOrWhiteSpace(boundPlace)
            || !string.IsNullOrWhiteSpace(boundTargetId)
            || !string.IsNullOrWhiteSpace(triggerHint)
            || !string.IsNullOrWhiteSpace(realizationKind)
            || status.Equals("bound", StringComparison.OrdinalIgnoreCase);
        var wantsEligibilityUpdate = clearEligibilityFailure || hasEligibilityFailure;
        if (!wantsBinding && string.IsNullOrWhiteSpace(status) && !wantsEligibilityUpdate)
        {
            return Reject(consequence, "Promise update did not include any changes.");
        }

        if (!string.IsNullOrWhiteSpace(boundTargetId))
        {
            var target = EntityById(boundTargetId);
            if (target is null
                && !boundTargetId.Equals(existing.BoundTargetId, StringComparison.OrdinalIgnoreCase))
            {
                return Reject(consequence, "Promise update target does not exist.");
            }

            if (target is not null)
            {
                AttachPromiseAnchor(target, existing.Id);
            }
        }

        var updated = existing;
        if (wantsBinding)
        {
            updated = _state.PromiseLedger.Bind(
                existing.Id,
                boundPlace,
                boundTargetId,
                triggerHint,
                realizationKind) ?? existing;
        }

        if (!string.IsNullOrWhiteSpace(status) && !status.Equals("bound", StringComparison.OrdinalIgnoreCase))
        {
            updated = _state.PromiseLedger.SetStatus(updated.Id, status, realizedIn) ?? updated;
        }

        if (wantsEligibilityUpdate)
        {
            updated = clearEligibilityFailure
                ? _state.PromiseLedger.SetEligibilityFailure(updated.Id, null, null, null) ?? updated
                : _state.PromiseLedger.SetEligibilityFailure(
                    updated.Id,
                    eligibilityFailure,
                    eligibilityContext,
                    eligibilityTurn ?? _state.Turn) ?? updated;
        }

        var defaultMessage = updated.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            ? $"A promise binds to {updated.BoundTargetId ?? updated.BoundPlace}: {updated.Text}"
            : $"A promise changes: {updated.Text}";
        var message = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultMessage)!;
        var deltaPlayerVisible = ReadBool(payload, "playerVisible")
            ?? ReadBool(payload, "player_visible")
            ?? (IsVisible(consequence.Visibility) && updated.PlayerVisible);
        AddMessageIfAllowed(
            consequence,
            payload,
            message,
            defaultEmitMessage: false,
            includeVisible: false,
            playerVisible: updated.PlayerVisible);

        var operation = ReadString(payload, "operation") ?? "updatePromise";
        var delta = new StateDelta(
            operation,
            updated.Id,
            message,
            Details(
                consequence,
                ("kind", updated.Kind),
                ("status", updated.Status),
                ("subject", updated.Subject),
                ("playerVisible", deltaPlayerVisible),
                ("salience", updated.Salience),
                ("claimedPlace", updated.ClaimedPlace),
                ("boundPlace", updated.BoundPlace),
                ("boundTargetId", updated.BoundTargetId),
                ("triggerHint", updated.TriggerHint),
                ("realizationKind", updated.RealizationKind),
                ("realizedIn", updated.RealizedIn),
                ("lastEligibilityFailure", updated.LastEligibilityFailure),
                ("lastEligibilityContext", updated.LastEligibilityContext),
                ("lastEligibilityTurn", updated.LastEligibilityTurn),
                ("eligibilityUpdated", wantsEligibilityUpdate)));
        return AppliedFromDelta(consequence, delta);
    }

    private WorldPromise? BindPromiseIfPossible(
        WorldPromise promise,
        Entity? anchor,
        string triggerHint,
        string? explicitBoundPlace = null,
        string? realizationKind = null,
        bool autoBind = true)
    {
        if (anchor is null && autoBind)
        {
            anchor = ResolvePromiseAnchorFromSelectionOrText(promise.Text);
        }

        if (anchor is not null)
        {
            AttachPromiseAnchor(anchor, promise.Id);
            return _state.PromiseLedger.Bind(
                promise.Id,
                boundPlace: string.IsNullOrWhiteSpace(explicitBoundPlace) ? _state.RegionId : explicitBoundPlace,
                boundTargetId: anchor.Id.Value,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferPromiseTriggerHint(promise.Text, anchor) : triggerHint,
                realizationKind: realizationKind ?? promise.RealizationKind);
        }

        if (!string.IsNullOrWhiteSpace(explicitBoundPlace))
        {
            return _state.PromiseLedger.Bind(
                promise.Id,
                boundPlace: explicitBoundPlace,
                boundTargetId: null,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferPromiseTriggerHint(promise.Text, null) : triggerHint,
                realizationKind: realizationKind ?? promise.RealizationKind);
        }

        if (autoBind && CanBindToRegion(promise))
        {
            return _state.PromiseLedger.Bind(
                promise.Id,
                boundPlace: _state.RegionId,
                boundTargetId: null,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferPromiseTriggerHint(promise.Text, null) : triggerHint,
                realizationKind: realizationKind ?? promise.RealizationKind);
        }

        return null;
    }

    private void AttachPromiseAnchor(Entity anchor, string promiseId)
    {
        var ids = anchor.TryGet<PromiseAnchorComponent>(out var existing)
            ? existing.PromiseIds.ToList()
            : new List<string>();
        if (!ids.Contains(promiseId, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(promiseId);
        }

        anchor.Set(new PromiseAnchorComponent(ids));
    }

    private Entity? ResolvePromiseAnchorFromSelectionOrText(string text)
    {
        if (_state.SelectedTarget is { } selected)
        {
            var selectedEntity = _state.Entities.Values.FirstOrDefault(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        var tokens = text.ToLowerInvariant()
            .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var origin = _state.ControlledEntity.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        return _state.Entities.Values
            .Where(entity => entity.Id != _state.ControlledEntityId)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Score = PromiseAnchorScore(entity, tokens),
                Distance = entity.TryGet<PositionComponent>(out var position)
                    ? Distance(origin, position.Position)
                    : int.MaxValue,
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Entity.Id.Value)
            .Select(candidate => candidate.Entity)
            .FirstOrDefault();
    }

    private static int PromiseAnchorScore(Entity entity, HashSet<string> tokens)
    {
        var score = 0;
        foreach (var token in entity.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tokens.Contains(token))
            {
                score += 3;
            }
        }

        if (entity.TryGet<TagsComponent>(out var tags))
        {
            score += tags.Tags.Count(tag => tokens.Contains(tag));
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            score += fixture.Tags.Count(tag => tokens.Contains(tag));
            if (tokens.Contains(fixture.FixtureType))
            {
                score += 2;
            }
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            foreach (var token in readable.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tokens.Contains(token))
                {
                    score += 2;
                }
            }
        }

        return score;
    }

    private static bool CanBindToRegion(WorldPromise promise) =>
        promise.Kind.Equals("prophecy", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("quest", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("threat", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("debt", StringComparison.OrdinalIgnoreCase);

    private static string InferPromiseTriggerHint(string text, Entity? anchor)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("read") || anchor?.Has<ReadableComponent>() == true)
        {
            return "read";
        }

        if (LooksLikeServicePromise(lower))
        {
            return "service";
        }

        if (lower.Contains("open") || lower.Contains("door") || anchor?.Has<DoorComponent>() == true)
        {
            return "open";
        }

        if (lower.Contains("buy")
            || lower.Contains("sell")
            || lower.Contains("wares")
            || lower.Contains("trade")
            || lower.Contains("merchant")
            || lower.Contains("market")
            || lower.Contains("stock"))
        {
            return "trade";
        }

        if (lower.Contains("speak") || lower.Contains("talk") || lower.Contains("name"))
        {
            return "talk";
        }

        return "encounter";
    }

    private static string InferPromiseRealizationKind(string kind, string text)
    {
        var lower = $"{kind} {text}".ToLowerInvariant();
        if (LooksLikeServicePromise(lower))
        {
            return "service";
        }

        if (lower.Contains("owe") || lower.Contains("debt"))
        {
            return "debt";
        }

        if (LooksLikeDoorRulePromise(lower))
        {
            return "door_rule";
        }

        if (lower.Contains("sell")
            || lower.Contains("sells")
            || lower.Contains("selling")
            || lower.Contains("buy")
            || lower.Contains("wares")
            || lower.Contains("trade")
            || lower.Contains("merchant")
            || lower.Contains("market")
            || lower.Contains("stock"))
        {
            return "merchant_stock";
        }

        if (lower.Contains("route")
            || lower.Contains("passage")
            || lower.Contains("hidden path")
            || lower.Contains("escape")
            || lower.Contains("drain")
            || lower.Contains("tunnel")
            || lower.Contains("grate")
            || lower.Contains("hidden exit"))
        {
            return "escape_route";
        }

        if (lower.Contains("item") || lower.Contains("blade") || lower.Contains("key"))
        {
            return "item";
        }

        if (lower.Contains("enemy") || lower.Contains("collector") || lower.Contains("threat"))
        {
            return "threat";
        }

        if (lower.Contains("quest") || lower.Contains("reward"))
        {
            return "quest";
        }

        if (lower.Contains("remember") || lower.Contains("name"))
        {
            return "memory";
        }

        return kind.Equals("debt", StringComparison.OrdinalIgnoreCase) ? "threat" : "omen";
    }

    private static bool LooksLikeServicePromise(string lower) =>
        lower.Contains("service")
        || lower.Contains("can help")
        || lower.Contains("offer a service")
        || lower.Contains("offers a service")
        || lower.Contains("offer folk")
        || lower.Contains("offers folk")
        || lower.Contains("mend")
        || lower.Contains("heal")
        || lower.Contains("guide")
        || lower.Contains("ward-breaking")
        || lower.Contains("break the ward")
        || lower.Contains("break wards")
        || lower.Contains("worry a lock")
        || lower.Contains("unlock for")
        || lower.Contains("open the lock")
        || lower.Contains("lift a curse")
        || lower.Contains("curse-lifting")
        || lower.Contains("folk charm");

    private static bool LooksLikeDoorRulePromise(string lower) =>
        (lower.Contains("door") || lower.Contains("gate") || lower.Contains("lock") || lower.Contains("ward"))
        && (lower.Contains("opens")
            || lower.Contains("open ")
            || lower.Contains("unlock")
            || lower.Contains("only to")
            || lower.Contains("when ")
            || lower.Contains("if "));

    private WorldConsequenceApplyResult ApplyMessage(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Message consequence did not include text.");
        }

        var operation = ReadString(payload, "operation") ?? "message";
        var delta = new StateDelta(operation, consequence.TargetEntityId ?? "", text, Details(consequence));
        if (IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence))
        {
            _state.AddMessage(text);
        }

        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplyModifyInventory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Inventory consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var requestedItem = FirstNonBlank(ReadString(payload, "item"), ReadString(payload, "itemName"), "item")!;
        var normalizedItem = NormalizeToken(requestedItem, "item");
        var op = NormalizeToken(FirstNonBlank(ReadString(payload, "op"), ReadString(payload, "mode"), "add")!, "add");
        var amount = Math.Clamp(ReadInt(payload, "amount") ?? ReadInt(payload, "quantity") ?? 1, 0, 999);
        var inventory = target.Entity!.TryGet<InventoryComponent>(out var existing)
            ? existing
            : InventoryComponent.Empty();
        var item = FirstNonBlank(ReadString(payload, "inventoryKey"), ReadString(payload, "itemKey"))
            ?? FindInventoryKey(inventory, requestedItem)
            ?? FindInventoryKey(inventory, normalizedItem)
            ?? normalizedItem;
        if (op is "protect" or "unprotect" or "set_protected")
        {
            var carriedItem = FindInventoryKey(inventory, requestedItem)
                ?? FindInventoryKey(inventory, normalizedItem);
            if (carriedItem is null)
            {
                return Reject(consequence, $"Inventory protection target is not carrying {requestedItem}.");
            }

            var protectedState = op switch
            {
                "unprotect" => false,
                "set_protected" => ReadBool(payload, "protected") ?? ReadBool(payload, "protectedState") ?? true,
                _ => true,
            };
            var wasProtected = inventory.TreasuredItems.Contains(carriedItem);
            if (protectedState)
            {
                inventory.TreasuredItems.Add(carriedItem);
            }
            else
            {
                inventory.TreasuredItems.Remove(carriedItem);
            }

            target.Entity.Set(inventory);
            var protectionOperation = ReadString(payload, "operation") ?? op;
            var protectionDefaultSummary = protectedState
                ? $"{carriedItem} is protected from wild magic costs."
                : $"{carriedItem} is available as ordinary spell fuel.";
            var protectionSummary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), protectionDefaultSummary)!;
            var protectionDelta = new StateDelta(
                protectionOperation,
                target.Entity.Id.Value,
                protectionSummary,
                Details(
                    consequence,
                    ("item", carriedItem),
                    ("op", op),
                    ("protected", protectedState),
                    ("wasProtected", wasProtected),
                    ("count", inventory.Items.TryGetValue(carriedItem, out var carriedCount) ? carriedCount : 0)));
            var protectionMessages = AddMessageIfAllowed(consequence, payload, protectionSummary, defaultEmitMessage: false)
                ? new[] { protectionSummary }
                : Array.Empty<string>();

            return Applied(consequence, target.Entity.Id.Value, protectionMessages, protectionDelta, ("item", carriedItem), ("protected", protectedState));
        }

        var current = inventory.Items.TryGetValue(item, out var count) ? count : 0;
        if (op is "remove" or "subtract" or "consume" && current < amount)
        {
            return Reject(consequence, $"{target.Entity.Name} is not carrying enough {item.Replace('_', ' ')}.");
        }

        var updated = op switch
        {
            "remove" or "subtract" or "consume" => Math.Max(0, current - amount),
            "set" => Math.Max(0, amount),
            _ => current + Math.Max(1, amount),
        };

        if (updated <= 0)
        {
            inventory.Items.Remove(item);
            inventory.TreasuredItems.Remove(item);
        }
        else
        {
            inventory.Items[item] = updated;
        }

        target.Entity.Set(inventory);
        var operation = ReadString(payload, "operation") ?? "modifyInventory";
        var defaultSummary = $"{Possessive(target.Entity)} {item.Replace('_', ' ')} count becomes {updated}.";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultSummary)!;
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("item", item), ("op", op), ("count", updated)));
        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, target.Entity.Id.Value, messages, delta, ("item", item), ("count", updated));
    }

    private WorldConsequenceApplyResult ApplyTransferItem(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actorId = FirstNonBlank(ReadString(payload, "actorEntityId"), ReadString(payload, "actorId"), consequence.SourceEntityId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Item transfer did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Item transfer actor does not exist.");
        }

        var inventory = actor.TryGet<InventoryComponent>(out var existingInventory)
            ? existingInventory
            : InventoryComponent.Empty();
        if (!actor.Has<InventoryComponent>())
        {
            actor.Set(inventory);
        }

        var mode = NormalizeToken(FirstNonBlank(ReadString(payload, "mode"), "pickup")!, "pickup");
        return mode switch
        {
            "pickup" => ApplyPickupTransfer(consequence, payload, actor, inventory),
            "drop" => ApplyDropTransfer(consequence, payload, actor, inventory),
            "give" => ApplyGiveTransfer(consequence, payload, actor, inventory),
            _ => Reject(consequence, $"Unknown item transfer mode: {mode}"),
        };
    }

    private WorldConsequenceApplyResult ApplyPickupTransfer(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        InventoryComponent inventory)
    {
        var itemEntityId = FirstNonBlank(ReadString(payload, "itemEntityId"), ReadString(payload, "entityId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(itemEntityId))
        {
            return Reject(consequence, "Pickup transfer did not include an item entity id.");
        }

        var item = EntityById(itemEntityId);
        if (item is null || !item.TryGet<ItemComponent>(out var itemComponent))
        {
            return Reject(consequence, "Pickup transfer target is not an item entity.");
        }

        var quantity = item.TryGet<StackComponent>(out var stack) ? Math.Max(1, stack.Quantity) : Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        var key = FirstNonBlank(ReadString(payload, "itemName"), item.Name, itemComponent.ItemType)!;
        AdjustInventory(actor, inventory, key, quantity);
        _state.Entities.Remove(item.Id);
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            quantity == 1 ? $"{actor.Name} picks up {key}." : $"{actor.Name} picks up {quantity} {key}.")!;
        var operation = ReadString(payload, "operation") ?? "pickup";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "pickup"),
                ("actorEntityId", actor.Id.Value),
                ("itemEntityId", item.Id.Value),
                ("item", key),
                ("quantity", quantity)));
        return AppliedTransfer(consequence, actor.Id.Value, payload, summary, delta, ("mode", "pickup"), ("item", key), ("quantity", quantity));
    }

    private WorldConsequenceApplyResult ApplyDropTransfer(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        InventoryComponent inventory)
    {
        var requestedItem = FirstNonBlank(ReadString(payload, "itemName"), ReadString(payload, "item"));
        if (string.IsNullOrWhiteSpace(requestedItem))
        {
            return Reject(consequence, "Drop transfer did not include an item name.");
        }

        var key = FindInventoryKey(inventory, requestedItem);
        if (key is null)
        {
            return Reject(consequence, $"Drop transfer actor is not carrying {requestedItem}.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        inventory.Items.TryGetValue(key, out var carrying);
        if (carrying < quantity)
        {
            return Reject(consequence, $"Drop transfer actor is not carrying enough {requestedItem}.");
        }

        GridPoint position;
        if (TryReadPoint(payload, null, out var point))
        {
            position = point;
        }
        else if (actor.TryGet<PositionComponent>(out var actorPosition))
        {
            position = actorPosition.Position;
        }
        else
        {
            return Reject(consequence, "Drop transfer has no position.");
        }

        AdjustInventory(actor, inventory, key, -quantity);
        var itemType = NormalizeToken(FirstNonBlank(ReadString(payload, "itemType"), ReadString(payload, "item_type"), key)!, "item");
        var tags = NormalizeTags(ReadStringList(payload, "tags").Concat(new[] { "item" }));
        var dropped = new Entity(_state.NextEntityId(NormalizeToken(ReadString(payload, "prefix") ?? itemType, "item")), key)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent(ReadGlyph(payload, '*'), "item"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: NormalizeToken(ReadString(payload, "material") ?? "matter", "matter")))
            .Set(new ItemComponent(
                itemType,
                Math.Max(1, ReadInt(payload, "value") ?? 1),
                NormalizeToken(ReadString(payload, "material") ?? "matter", "matter"),
                tags,
                FirstNonBlank(ReadString(payload, "stackPolicy"), ReadString(payload, "stack_policy"), "commodity")!,
                FirstNonBlank(ReadString(payload, "useProfile"), ReadString(payload, "use_profile"), "inert")!,
                FirstNonBlank(ReadString(payload, "equipmentSlot"), ReadString(payload, "equipment_slot"))))
            .Set(new StackComponent(quantity));
        _state.Entities[dropped.Id] = dropped;

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} drops {key}.")!;
        var operation = ReadString(payload, "operation") ?? "drop";
        var delta = new StateDelta(
            operation,
            dropped.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "drop"),
                ("actorEntityId", actor.Id.Value),
                ("itemEntityId", dropped.Id.Value),
                ("item", key),
                ("quantity", quantity),
                ("x", position.X),
                ("y", position.Y)));
        return AppliedTransfer(consequence, dropped.Id.Value, payload, summary, delta, ("mode", "drop"), ("item", key), ("quantity", quantity));
    }

    private WorldConsequenceApplyResult ApplyGiveTransfer(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        InventoryComponent inventory)
    {
        var requestedItem = FirstNonBlank(ReadString(payload, "itemName"), ReadString(payload, "item"));
        if (string.IsNullOrWhiteSpace(requestedItem))
        {
            return Reject(consequence, "Give transfer did not include an item name.");
        }

        var recipientId = FirstNonBlank(
            ReadString(payload, "recipientEntityId"),
            ReadString(payload, "recipientId"),
            ReadString(payload, "receiverEntityId"),
            ReadString(payload, "receiverId"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(recipientId))
        {
            return Reject(consequence, "Give transfer did not include a recipient entity id.");
        }

        var recipient = EntityById(recipientId);
        if (recipient is null)
        {
            return Reject(consequence, "Give transfer recipient does not exist.");
        }

        var key = FindInventoryKey(inventory, requestedItem);
        if (key is null)
        {
            return Reject(consequence, $"Give transfer actor is not carrying {requestedItem}.");
        }

        if (inventory.TreasuredItems.Contains(key) && ReadBool(payload, "allowProtected") != true)
        {
            return Reject(consequence, $"Give transfer actor cannot give protected item {key}.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        inventory.Items.TryGetValue(key, out var carrying);
        if (carrying < quantity)
        {
            return Reject(consequence, $"Give transfer actor is not carrying enough {requestedItem}.");
        }

        var recipientInventory = recipient.TryGet<InventoryComponent>(out var existingRecipientInventory)
            ? existingRecipientInventory
            : InventoryComponent.Empty();
        if (!recipient.Has<InventoryComponent>())
        {
            recipient.Set(recipientInventory);
        }

        AdjustInventory(actor, inventory, key, -quantity);
        AdjustInventory(recipient, recipientInventory, key, quantity);
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            quantity == 1
                ? $"{actor.Name} gives {key} to {recipient.Name}."
                : $"{actor.Name} gives {quantity} {key} to {recipient.Name}.")!;
        var operation = ReadString(payload, "operation") ?? "give";
        var delta = new StateDelta(
            operation,
            recipient.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "give"),
                ("actorEntityId", actor.Id.Value),
                ("recipientEntityId", recipient.Id.Value),
                ("item", key),
                ("quantity", quantity)));
        return AppliedTransfer(
            consequence,
            recipient.Id.Value,
            payload,
            summary,
            delta,
            ("mode", "give"),
            ("item", key),
            ("quantity", quantity),
            ("recipientEntityId", recipient.Id.Value));
    }

    private WorldConsequenceApplyResult AppliedTransfer(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyDictionary<string, object?> payload,
        string summary,
        StateDelta delta,
        params (string Key, object? Value)[] fields)
    {
        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, targetId, messages, delta, fields);
    }

    private WorldConsequenceApplyResult ApplyUpdateEquipment(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "actorId"),
            consequence.TargetEntityId,
            consequence.SourceEntityId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Equipment consequence did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Equipment consequence actor does not exist.");
        }

        var equipment = actor.TryGet<EquipmentComponent>(out var existing)
            ? existing
            : EquipmentComponent.Empty();
        if (!actor.Has<EquipmentComponent>())
        {
            actor.Set(equipment);
        }

        var mode = NormalizeToken(FirstNonBlank(ReadString(payload, "mode"), ReadString(payload, "op"), "equip")!, "equip");
        return mode switch
        {
            "equip" => ApplyEquip(consequence, payload, actor, equipment),
            "unequip" => ApplyUnequip(consequence, payload, actor, equipment),
            "focus" => ApplyFocus(consequence, payload, actor, equipment),
            "unfocus" or "clear_focus" => ApplyUnfocus(consequence, payload, actor, equipment),
            _ => Reject(consequence, $"Unknown equipment mode: {mode}"),
        };
    }

    private WorldConsequenceApplyResult ApplyEquip(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var requestedItem = FirstNonBlank(ReadString(payload, "item"), ReadString(payload, "itemName"));
        if (string.IsNullOrWhiteSpace(requestedItem))
        {
            return Reject(consequence, "Equip consequence did not include an item.");
        }

        if (!actor.TryGet<InventoryComponent>(out var inventory))
        {
            return Reject(consequence, $"Equip consequence actor is not carrying {requestedItem}.");
        }

        var item = FindInventoryKey(inventory, requestedItem);
        if (item is null)
        {
            return Reject(consequence, $"Equip consequence actor is not carrying {requestedItem}.");
        }

        var slot = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "equipmentSlot"), ReadString(payload, "equipment_slot"));
        if (string.IsNullOrWhiteSpace(slot))
        {
            return Reject(consequence, "Equip consequence did not include a slot.");
        }

        equipment.Slots.TryGetValue(slot, out var previousItem);
        equipment.Slots[slot] = item;
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} equips {item} in {slot}.")!;
        var operation = ReadString(payload, "operation") ?? "equip";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "equip"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot),
                ("previousItem", previousItem)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "equip"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult ApplyUnequip(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var slotOrItem = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "item"), ReadString(payload, "itemName"));
        var slot = FindEquipmentSlot(equipment, slotOrItem);
        if (slot is null)
        {
            return Reject(consequence, $"{slotOrItem ?? "item"} is not equipped.");
        }

        var item = equipment.Slots[slot];
        var wasFocused = equipment.FocusSlots.Remove(slot);
        equipment.Slots.Remove(slot);
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} unequips {item}.")!;
        var operation = ReadString(payload, "operation") ?? "unequip";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "unequip"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot),
                ("wasFocused", wasFocused)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "unequip"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult ApplyFocus(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var slotOrItem = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "item"), ReadString(payload, "itemName"));
        var slot = FindEquipmentSlot(equipment, slotOrItem);
        if (slot is null)
        {
            return Reject(consequence, $"{slotOrItem ?? "item"} is not equipped.");
        }

        var item = equipment.Slots[slot];
        var wasFocused = equipment.FocusSlots.Contains(slot);
        equipment.FocusSlots.Add(slot);
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{item} is now {Possessive(actor)} magical focus.")!;
        var operation = ReadString(payload, "operation") ?? "focus";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "focus"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot),
                ("wasFocused", wasFocused)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "focus"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult ApplyUnfocus(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var slotOrItem = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "item"), ReadString(payload, "itemName"));
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            var removedSlots = equipment.FocusSlots.OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase).ToArray();
            equipment.FocusSlots.Clear();
            actor.Set(equipment);

            var clearSummary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{Subject(actor)} {Verb(actor, "release", "releases")} {(actor.Id == _state.ControlledEntityId ? "your" : "their")} magical focus.")!;
            var clearOperation = ReadString(payload, "operation") ?? "unfocus";
            var clearDelta = new StateDelta(
                clearOperation,
                actor.Id.Value,
                clearSummary,
                Details(
                    consequence,
                    ("mode", "unfocus"),
                    ("actorEntityId", actor.Id.Value),
                    ("removedSlots", removedSlots)));
            return AppliedEquipment(consequence, actor.Id.Value, payload, clearSummary, clearDelta, ("mode", "unfocus"), ("removedSlots", removedSlots));
        }

        var slot = FindEquipmentSlot(equipment, slotOrItem);
        if (slot is null || !equipment.FocusSlots.Contains(slot))
        {
            return Reject(consequence, $"{slotOrItem} is not focused.");
        }

        var item = equipment.Slots[slot];
        equipment.FocusSlots.Remove(slot);
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{item} is no longer {Possessive(actor)} focus.")!;
        var operation = ReadString(payload, "operation") ?? "unfocus";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "unfocus"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "unfocus"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult AppliedEquipment(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyDictionary<string, object?> payload,
        string summary,
        StateDelta delta,
        params (string Key, object? Value)[] fields)
    {
        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, targetId, messages, delta, fields);
    }

    private static string? FindEquipmentSlot(EquipmentComponent equipment, string? slotOrItem)
    {
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            return null;
        }

        return equipment.Slots.Keys.FirstOrDefault(slot =>
            slot.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[slot].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindInventoryKey(InventoryComponent inventory, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (inventory.Items.ContainsKey(item))
        {
            return item;
        }

        var normalized = NormalizeToken(item, item);
        return inventory.Items.Keys.FirstOrDefault(key =>
            NormalizeToken(key, key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static void AdjustInventory(Entity entity, InventoryComponent inventory, string item, int delta)
    {
        var key = FindInventoryKey(inventory, item) ?? item;
        inventory.Items.TryGetValue(key, out var current);
        var next = current + delta;
        if (next <= 0)
        {
            inventory.Items.Remove(key);
            inventory.TreasuredItems.Remove(key);
        }
        else
        {
            inventory.Items[key] = next;
        }

        entity.Set(inventory);
    }

    private static string? FindWareKey(MerchantComponent merchant, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (merchant.Wares.ContainsKey(item))
        {
            return item;
        }

        var normalized = NormalizeToken(item, item);
        return merchant.Wares.Keys.FirstOrDefault(key =>
            key.Contains(item, StringComparison.OrdinalIgnoreCase)
            || item.Contains(key, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(key, key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
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

    private WorldConsequenceApplyResult ApplySetWorldFlag(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var flag = NormalizeToken(
            FirstNonBlank(ReadString(payload, "flag"), ReadString(payload, "id"), consequence.TargetEntityId, "marked")!,
            "marked");
        var value = payload.TryGetValue("value", out var raw) && raw is not null ? raw : true;
        _state.WorldFlags[flag] = value;
        var operation = ReadString(payload, "operation") ?? "setFlag";
        var description = FirstNonBlank(ReadString(payload, "description"), flag.Replace('_', ' '))!;
        var summary = $"A world flag is set: {description}.";
        var delta = new StateDelta(
            operation,
            flag,
            summary,
            Details(consequence, ("flag", flag), ("value", value), ("description", description)));
        return Applied(consequence, flag, MaybeVisibleMessage(consequence, summary), delta, ("flag", flag), ("value", value));
    }

    private WorldConsequenceApplyResult ApplyUpdateRunStatus(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var rawStatus = FirstNonBlank(ReadString(payload, "status"), ReadString(payload, "runStatus"));
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return Reject(consequence, "Run-status consequence did not include a status.");
        }

        var previousStatus = _state.RunStatus;
        var previousConclusion = _state.RunConclusion;
        var status = NormalizeToken(rawStatus, "running");
        var conclusion = FirstNonBlank(ReadString(payload, "conclusion"), ReadString(payload, "runConclusion"), consequence.Evidence);
        _state.RunStatus = status;
        _state.RunConclusion = conclusion;

        var operation = ReadString(payload, "operation") ?? "updateRunStatus";
        var targetId = FirstNonBlank(ReadString(payload, "targetId"), consequence.TargetEntityId, _state.ControlledEntityId.Value)!;
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"Run status is now {status}.")!;
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("previousStatus", previousStatus),
                ("previousConclusion", previousConclusion),
                ("status", status),
                ("conclusion", conclusion),
                ("targetId", targetId)));
        return Applied(
            consequence,
            targetId,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("previousStatus", previousStatus),
            ("status", status),
            ("conclusion", conclusion));
    }

    private WorldConsequenceApplyResult ApplySetSelectedTarget(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var clear = ReadBool(payload, "clear") == true;
        var x = ReadInt(payload, "x");
        var y = ReadInt(payload, "y");
        if (!clear && (x is null || y is null))
        {
            return Reject(consequence, "Selected-target consequence needs both x and y, or clear=true.");
        }

        if (!clear
            && (x!.Value < 0 || y!.Value < 0 || x.Value >= _state.Width || y.Value >= _state.Height))
        {
            return Reject(consequence, $"Selected target is outside the encounter: {x},{y}.");
        }

        var previous = _state.SelectedTarget;
        var next = clear ? (GridPoint?)null : new GridPoint(x!.Value, y!.Value);
        _state.SelectedTarget = next;

        var operation = ReadString(payload, "operation") ?? (clear ? "clearSelectedTarget" : "setSelectedTarget");
        var target = next is { } point ? $"{point.X},{point.Y}" : "selected_target";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            next is { } selected ? $"Selected target set to {selected.X},{selected.Y}." : "Selected target cleared.")!;
        var delta = new StateDelta(
            operation,
            target,
            summary,
            Details(
                consequence,
                ("previousX", previous?.X),
                ("previousY", previous?.Y),
                ("x", next?.X),
                ("y", next?.Y),
                ("clear", clear)));
        return Applied(
            consequence,
            target,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("previousX", previous?.X),
            ("previousY", previous?.Y),
            ("x", next?.X),
            ("y", next?.Y),
            ("clear", clear));
    }

    private WorldConsequenceApplyResult ApplyQueueBackgroundJob(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var targetId = FirstNonBlank(
            ReadString(payload, "targetId"),
            ReadString(payload, "target_id"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(targetId))
        {
            return Reject(consequence, "Background-job consequence did not include a target id.");
        }

        var targetKind = NormalizeToken(
            FirstNonBlank(ReadString(payload, "targetKind"), ReadString(payload, "target_kind"), "entity")!,
            "entity");
        var targetEntity = targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase)
            ? EntityById(targetId)
            : null;
        if (targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase) && targetEntity is null)
        {
            return Reject(consequence, "Background-job consequence target entity does not exist.");
        }

        var purpose = NormalizeToken(FirstNonBlank(ReadString(payload, "purpose"), ReadString(payload, "kind"), "detail")!, "detail");
        var priority = Math.Clamp(ReadInt(payload, "priority") ?? consequence.Salience, 0, 999);
        var operation = ReadString(payload, "operation") ?? "queueBackgroundJob";

        if (!_state.BackgroundSettings.Enabled)
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "background_disabled");
        }

        var activeCount = _state.BackgroundJobs.Jobs.Count(job =>
            job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.Completed);
        if (activeCount >= _state.BackgroundSettings.MaxQueuedJobs)
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "queue_full");
        }

        if (_state.BackgroundJobs.HasActiveJob(purpose, targetId))
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "duplicate_active_job");
        }

        if (targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase)
            && _state.Canon.Records.Any(record =>
                record.AttachedTo.Equals(targetId, StringComparison.OrdinalIgnoreCase)
                && record.Kind.Equals(purpose, StringComparison.OrdinalIgnoreCase)))
        {
            return BackgroundJobSkipped(consequence, targetId, targetKind, targetEntity?.Name, operation, purpose, priority, "canon_already_exists");
        }

        var job = _state.BackgroundJobs.Enqueue(purpose, targetId, priority, _state.Turn);
        var targetLabel = targetEntity?.Name ?? $"{targetKind}:{targetId}";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"Background job queued: {job.Purpose} for {targetLabel}.")!;
        var delta = new StateDelta(
            operation,
            job.Id,
            summary,
            Details(
                consequence,
                ("jobId", job.Id),
                ("purpose", job.Purpose),
                ("targetId", targetId),
                ("targetKind", targetKind),
                ("targetEntityId", targetEntity?.Id.Value),
                ("priority", job.Priority),
                ("state", job.State.ToString()),
                ("createdTurn", job.CreatedTurn),
                ("queued", true),
                ("skipReason", null),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        var messages = IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>();
        return Applied(consequence, job.Id, messages, delta, ("jobId", job.Id), ("purpose", job.Purpose));
    }

    private WorldConsequenceApplyResult BackgroundJobSkipped(
        WorldConsequence consequence,
        string targetId,
        string targetKind,
        string? targetName,
        string operation,
        string purpose,
        int priority,
        string reason)
    {
        var targetLabel = targetName ?? $"{targetKind}:{targetId}";
        var summary = $"Background job skipped for {targetLabel}: {reason}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("purpose", purpose),
                ("targetId", targetId),
                ("targetKind", targetKind),
                ("targetEntityId", targetKind.Equals("entity", StringComparison.OrdinalIgnoreCase) ? targetId : null),
                ("priority", priority),
                ("state", "Skipped"),
                ("queued", false),
                ("skipReason", reason),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            targetId,
            reason,
            Array.Empty<string>(),
            new[] { delta },
            Details(consequence, ("purpose", purpose), ("skipReason", reason)));
    }

    private WorldConsequenceApplyResult ApplyUpdateBackgroundJob(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var jobId = FirstNonBlank(ReadString(payload, "jobId"), ReadString(payload, "job_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return Reject(consequence, "Background job update did not include a job id.");
        }

        var existing = _state.BackgroundJobs.Jobs.FirstOrDefault(job =>
            job.Id.Equals(jobId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Background job does not exist: {jobId}.");
        }

        var stateText = FirstNonBlank(ReadString(payload, "state"), existing.State.ToString())!;
        if (!Enum.TryParse<BackgroundJobState>(stateText, ignoreCase: true, out var state))
        {
            return Reject(consequence, $"Unknown background job state: {stateText}.");
        }

        var updated = existing with
        {
            State = state,
            StartedTurn = ReadInt(payload, "startedTurn") ?? ReadInt(payload, "started_turn") ?? existing.StartedTurn,
            CompletedTurn = ReadInt(payload, "completedTurn") ?? ReadInt(payload, "completed_turn") ?? existing.CompletedTurn,
            AppliedTurn = ReadInt(payload, "appliedTurn") ?? ReadInt(payload, "applied_turn") ?? existing.AppliedTurn,
            ResultText = FirstNonBlank(ReadString(payload, "resultText"), ReadString(payload, "result_text"), existing.ResultText),
            Error = FirstNonBlank(ReadString(payload, "error"), existing.Error),
        };
        _state.BackgroundJobs.Replace(updated);

        var operation = ReadString(payload, "operation") ?? "updateBackgroundJob";
        var summary = FirstNonBlank(
            ReadString(payload, "summary"),
            $"Background job {updated.Id} is {updated.State}.")!;
        var delta = new StateDelta(
            operation,
            updated.Id,
            summary,
            Details(
                consequence,
                ("jobId", updated.Id),
                ("purpose", updated.Purpose),
                ("targetId", updated.TargetId),
                ("priority", updated.Priority),
                ("previousState", existing.State.ToString()),
                ("state", updated.State.ToString()),
                ("createdTurn", updated.CreatedTurn),
                ("startedTurn", updated.StartedTurn),
                ("completedTurn", updated.CompletedTurn),
                ("appliedTurn", updated.AppliedTurn),
                ("hasResultText", !string.IsNullOrWhiteSpace(updated.ResultText)),
                ("error", updated.Error),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        return Applied(consequence, updated.Id, MaybeVisibleMessage(consequence, summary), delta, ("jobId", updated.Id), ("state", updated.State.ToString()));
    }

    private WorldConsequenceApplyResult ApplyScheduleEvent(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var eventType = NormalizeToken(FirstNonBlank(ReadString(payload, "eventType"), ReadString(payload, "event_type"), ReadString(payload, "kind"), "wild_magic")!, "wild_magic");
        var turns = Math.Clamp(ReadInt(payload, "turns") ?? ReadInt(payload, "delay") ?? 1, 1, 999);
        var dueTurn = Math.Max(_state.Turn + 1, ReadInt(payload, "dueTurn") ?? ReadInt(payload, "due_turn") ?? (_state.Turn + turns));
        var eventPayload = ReadDictionary(payload, "eventPayload") ?? ReadDictionary(payload, "payload") ?? PayloadWithoutSchedulerKeys(payload);
        EntityId? sourceEntityId = string.IsNullOrWhiteSpace(consequence.SourceEntityId)
            ? null
            : EntityId.Create(consequence.SourceEntityId);
        var scheduled = _state.ScheduledEvents.Schedule(dueTurn, eventType, sourceEntityId, eventPayload);
        var operation = ReadString(payload, "operation") ?? "scheduleEvent";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"Something is scheduled for turn {scheduled.DueTurn}: {eventType}.")!;
        var delta = new StateDelta(
            operation,
            scheduled.Id,
            summary,
            Details(
                consequence,
                ("eventId", scheduled.Id),
                ("dueTurn", scheduled.DueTurn),
                ("eventType", eventType),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        var messages = IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>();
        return Applied(consequence, scheduled.Id, messages, delta, ("eventId", scheduled.Id), ("dueTurn", scheduled.DueTurn));
    }

    /// <summary>
    /// The three terminal outcomes every Update* consequence (scheduled event, trigger,
    /// persistent effect, behavior tag, tile flow) can end in. Each handler previously grew its
    /// own ad hoc synonym list and they silently diverged -- "expire" worked on triggers and
    /// flows but was rejected on persistent effects, "complete" worked on persistent effects but
    /// was rejected on behavior tags -- so a content-authored consequence using one handler's
    /// vocabulary would fail on another for no reason a player or content author could predict.
    /// Classifying through one shared table keeps all three recognized everywhere; each handler
    /// still picks its own verb text and may accept additional non-terminal actions of its own
    /// (a trigger's "advance", a persistent effect's "consume").
    /// </summary>
    private enum TerminalUpdateAction { Complete, Expire, Remove }

    private static TerminalUpdateAction? ClassifyTerminalAction(string action) => action switch
    {
        "complete" or "completed" => TerminalUpdateAction.Complete,
        "expire" or "expired" => TerminalUpdateAction.Expire,
        "remove" or "clear" or "delete" => TerminalUpdateAction.Remove,
        _ => null,
    };

    private WorldConsequenceApplyResult ApplyUpdateScheduledEvent(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var eventId = FirstNonBlank(ReadString(payload, "eventId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return Reject(consequence, "Scheduled-event update consequence did not include an event id.");
        }

        var record = _state.ScheduledEvents.Events.FirstOrDefault(existing =>
            existing.Id.Equals(eventId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return Reject(consequence, $"Scheduled-event update target does not exist: {eventId}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "due")!, "due");
        var terminal = action == "due" ? TerminalUpdateAction.Complete : ClassifyTerminalAction(action);
        var verb = terminal switch
        {
            TerminalUpdateAction.Complete => "came due",
            TerminalUpdateAction.Remove => "was removed",
            TerminalUpdateAction.Expire => "expired",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported scheduled-event update action: {action}.");
        }

        _state.ScheduledEvents.Remove(record.Id);
        var operation = ReadString(payload, "operation") ?? "updateScheduledEvent";
        var summary = $"{record.Kind} scheduled event {verb}.";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("eventId", record.Id),
                ("eventType", record.Kind),
                ("dueTurn", record.DueTurn),
                ("action", action),
                ("playerVisible", ReadBool(payload, "playerVisible")
                    ?? (ReadBool(payload, "emitMessage") == true || IsVisible(consequence.Visibility)))));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("eventId", record.Id), ("eventType", record.Kind), ("action", action));
    }

    private WorldConsequenceApplyResult ApplyCreateTrigger(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var effectType = FirstNonBlank(ReadString(payload, "effectType"), ReadString(payload, "effect_type"), ReadString(payload, "then"), "message")!;
        var effectFields = ReadDictionary(payload, "effectFields") ?? ReadDictionary(payload, "effect") ?? new Dictionary<string, object?>();
        var anchorEntityId = FirstNonBlank(ReadString(payload, "anchorEntityId"), ReadString(payload, "anchor_entity_id"));
        GridPoint? anchorPoint = null;
        if ((ReadInt(payload, "anchorX") ?? ReadInt(payload, "x")) is { } x
            && (ReadInt(payload, "anchorY") ?? ReadInt(payload, "y")) is { } y)
        {
            anchorPoint = new GridPoint(x, y);
        }
        else if (!string.IsNullOrWhiteSpace(consequence.TargetEntityId)
            && consequence.TargetEntityId.StartsWith("tile:", StringComparison.OrdinalIgnoreCase)
            && TryReadPoint(payload, consequence.TargetEntityId, out var targetPoint))
        {
            anchorPoint = targetPoint;
        }
        else if (string.IsNullOrWhiteSpace(anchorEntityId)
            && !string.IsNullOrWhiteSpace(consequence.TargetEntityId))
        {
            anchorEntityId = consequence.TargetEntityId;
        }

        var sourceEntityId = string.IsNullOrWhiteSpace(consequence.SourceEntityId)
            ? (EntityId?)null
            : EntityId.Create(consequence.SourceEntityId);
        var safeDelay = Math.Clamp(ReadInt(payload, "delay") ?? ReadInt(payload, "turns") ?? 1, 1, 99);
        var safeInterval = Math.Clamp(ReadInt(payload, "interval") ?? 1, 1, 99);
        var safeUses = Math.Clamp(ReadInt(payload, "uses") ?? ReadInt(payload, "maxFires") ?? ReadInt(payload, "max_fires") ?? 1, 1, 20);
        var createdTurn = _state.Turn;
        var duration = ReadInt(payload, "duration");
        var record = _state.Triggers.Add(
            FirstNonBlank(ReadString(payload, "name"), ReadString(payload, "kind"), "trigger")!,
            FirstNonBlank(ReadString(payload, "kind"), "delay")!,
            createdTurn,
            createdTurn + safeDelay,
            safeInterval,
            safeUses,
            duration is null ? null : createdTurn + Math.Max(safeDelay, duration.Value),
            sourceEntityId,
            anchorEntityId,
            anchorPoint,
            Math.Clamp(ReadInt(payload, "radius") ?? 0, 0, 8),
            FirstNonBlank(ReadString(payload, "targetFilter"), ReadString(payload, "affects"), "all")!,
            effectType,
            effectFields,
            FirstNonBlank(ReadString(payload, "description"), ReadString(payload, "text"), "The delayed magic comes due.")!,
            ReadBool(payload, "playerVisible") ?? true);
        var summary = record.Kind.Equals("aura", StringComparison.OrdinalIgnoreCase)
            ? $"{record.Name} begins to pulse."
            : $"{record.Name} settles into a later turn.";
        AddMessageIfAllowed(consequence, payload, summary, includeVisible: false, playerVisible: record.PlayerVisible);

        var operation = ReadString(payload, "operation") ?? "createTrigger";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("triggerId", record.Id),
                ("triggerName", record.Name),
                ("kind", record.Kind),
                ("nextTurn", record.NextTurn),
                ("interval", record.Interval),
                ("remainingUses", record.RemainingUses),
                ("expiresTurn", record.ExpiresTurn),
                ("sourceEntityId", record.SourceEntityId?.Value),
                ("anchorEntityId", record.AnchorEntityId),
                ("anchorX", record.AnchorPoint?.X),
                ("anchorY", record.AnchorPoint?.Y),
                ("radius", record.Radius),
                ("targetFilter", record.TargetFilter),
                ("effectType", record.EffectType),
                ("playerVisible", record.PlayerVisible)));
        var messages = record.PlayerVisible && IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence)
            ? new[] { summary }
            : Array.Empty<string>();
        return Applied(consequence, record.Id, messages, delta, ("triggerId", record.Id), ("kind", record.Kind), ("nextTurn", record.NextTurn));
    }

    private WorldConsequenceApplyResult ApplyUpdateTrigger(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var triggerId = FirstNonBlank(ReadString(payload, "triggerId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(triggerId))
        {
            return Reject(consequence, "Trigger update consequence did not include a trigger id.");
        }

        var record = _state.Triggers.Records.FirstOrDefault(existing =>
            existing.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return Reject(consequence, $"Trigger update target does not exist: {triggerId}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "advance")!, "advance");
        var operation = ReadString(payload, "operation") ?? "updateTrigger";
        var summary = ClassifyTerminalAction(action) switch
        {
            TerminalUpdateAction.Complete => RemoveTrigger(record, "completed"),
            TerminalUpdateAction.Expire => RemoveTrigger(record, "expired"),
            TerminalUpdateAction.Remove => RemoveTrigger(record, "removed"),
            _ => action is "advance" or "reschedule" or "set" ? ApplyTriggerAdvance(payload, record) : null,
        };
        if (summary is null)
        {
            return Reject(consequence, $"Unsupported trigger update action: {action}.");
        }

        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("triggerId", record.Id),
                ("triggerName", record.Name),
                ("action", action),
                ("previousNextTurn", record.NextTurn),
                ("previousRemainingUses", record.RemainingUses),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("triggerId", record.Id), ("action", action));
    }

    private string? ApplyTriggerAdvance(IReadOnlyDictionary<string, object?> payload, TriggerRecord record)
    {
        var nextTurn = ReadInt(payload, "nextTurn") ?? ReadInt(payload, "next_turn");
        var remainingUses = ReadInt(payload, "remainingUses") ?? ReadInt(payload, "remaining_uses");
        if (nextTurn is null && remainingUses is null)
        {
            return null;
        }

        var updated = record with
        {
            NextTurn = Math.Max(_state.Turn + 1, nextTurn ?? record.NextTurn),
            RemainingUses = Math.Max(1, remainingUses ?? record.RemainingUses),
        };
        _state.Triggers.Replace(updated);
        return $"{record.Name} advances to turn {updated.NextTurn} with {updated.RemainingUses} use(s) left.";
    }

    private string RemoveTrigger(TriggerRecord record, string verb)
    {
        _state.Triggers.Remove(record.Id);
        return $"{record.Name} {verb}.";
    }

    private WorldConsequenceApplyResult ApplyAdjustFactionStanding(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var factionOrRole = CleanLedgerKey(
            FirstNonBlank(ReadString(payload, "factionId"), ReadString(payload, "role"), consequence.TargetEntityId, "unknown")!,
            "unknown");
        var axis = CleanLedgerKey(FirstNonBlank(ReadString(payload, "axis"), ReadString(payload, "standing"), "standing")!, "standing");
        var deltaValue = Math.Clamp(ReadInt(payload, "delta") ?? 0, -999, 999);
        if (deltaValue == 0)
        {
            return Reject(consequence, "Faction-standing consequence had zero delta.");
        }

        var targetIsRole = ReadBool(payload, "targetIsRole") ?? !string.IsNullOrWhiteSpace(ReadString(payload, "role"));
        if (targetIsRole)
        {
            _state.Factions.AdjustStandingByRole(factionOrRole, axis, deltaValue);
        }
        else
        {
            _state.Factions.AdjustStanding(factionOrRole, axis, deltaValue);
        }

        var operation = ReadString(payload, "operation") ?? "adjustFactionStanding";
        var summary = targetIsRole
            ? $"Factions with role {factionOrRole} shift {axis} by {deltaValue}."
            : $"{factionOrRole} shifts {axis} by {deltaValue}.";
        var delta = new StateDelta(
            operation,
            factionOrRole,
            summary,
            Details(
                consequence,
                ("axis", axis),
                ("delta", deltaValue),
                ("targetIsRole", targetIsRole),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, factionOrRole, MaybeVisibleMessage(consequence, summary), delta, ("axis", axis), ("delta", deltaValue));
    }

    private WorldConsequenceApplyResult ApplyAdjustFactionResource(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var factionId = CleanLedgerKey(FirstNonBlank(ReadString(payload, "factionId"), consequence.TargetEntityId, "unknown")!, "unknown");
        var resource = CleanLedgerKey(FirstNonBlank(ReadString(payload, "resource"), "heat")!, "heat");
        var rawDelta = ReadInt(payload, "delta") ?? 0;
        // Untrusted content (wild magic, dialogue) is clamped to a sane one-step swing; engine
        // callers that already bound their own delta (see WorldConsequence.AdjustFactionResource)
        // opt out explicitly instead of silently having it truncated out from under them.
        var deltaValue = ReadBool(payload, "allowLargeDelta") == true ? rawDelta : Math.Clamp(rawDelta, -999, 999);
        if (deltaValue == 0)
        {
            return Reject(consequence, "Faction-resource consequence had zero delta.");
        }

        var min = ReadInt(payload, "min") ?? 0;
        var max = ReadInt(payload, "max");
        _state.Factions.AdjustResource(factionId, resource, deltaValue, min, max);
        var value = _state.Factions.ResourceValue(factionId, resource);
        var operation = ReadString(payload, "operation") ?? "adjustFactionResource";
        var summary = $"{factionId} {resource} shifts by {deltaValue} to {value}.";
        var delta = new StateDelta(
            operation,
            factionId,
            summary,
            Details(
                consequence,
                ("resource", resource),
                ("delta", deltaValue),
                ("value", value),
                ("min", min),
                ("max", max),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, factionId, MaybeVisibleMessage(consequence, summary), delta, ("resource", resource), ("delta", deltaValue), ("value", value));
    }

    private WorldConsequenceApplyResult ApplyRecordSuspicion(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var effectX = ReadInt(payload, "effectX") ?? ReadInt(payload, "effect_x") ?? ReadInt(payload, "x");
        var effectY = ReadInt(payload, "effectY") ?? ReadInt(payload, "effect_y") ?? ReadInt(payload, "y");
        if (effectX is null || effectY is null)
        {
            return Reject(consequence, "Suspicion consequence did not include an effect coordinate.");
        }

        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "actor_entity_id"),
            consequence.TargetEntityId,
            consequence.SourceEntityId);
        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        if (!string.IsNullOrWhiteSpace(actorId) && actor is null)
        {
            return Reject(consequence, "Suspicion consequence actor entity does not exist.");
        }

        var kind = NormalizeToken(FirstNonBlank(ReadString(payload, "kind"), "suspicion")!, "suspicion");
        var operation = ReadString(payload, "operation") ?? "recordSuspicion";
        var plans = engine.PlanEffectSuspicion(new GridPoint(effectX.Value, effectY.Value), kind, actor);
        var records = plans
            .Select(plan => _state.Suspicions.Append(
                _state.Turn,
                plan.WitnessSoulId,
                plan.Kind,
                plan.EffectPoint,
                plan.Status,
                plan.SuspectedSoulId,
                plan.AttributedTurn,
                plan.ExpiresTurn))
            .ToArray();
        var deltas = records
            .Select(record => new StateDelta(
                operation,
                record.WitnessSoulId,
                $"Suspicion recorded: {kind} seen by {record.WitnessSoulId} ({record.Status}).",
                Details(
                    consequence,
                    ("suspicionId", record.Id),
                    ("witnessSoulId", record.WitnessSoulId),
                    ("kind", record.Kind),
                    ("effectX", record.EffectPoint.X),
                    ("effectY", record.EffectPoint.Y),
                    ("status", record.Status),
                    ("suspectedSoulId", record.SuspectedSoulId),
                    ("attributedTurn", record.AttributedTurn),
                    ("expiresTurn", record.ExpiresTurn))))
            .ToArray();
        return new WorldConsequenceApplyResult(
            true,
            actor?.Id.Value,
            null,
            Array.Empty<string>(),
            deltas,
            Details(consequence, ("created", records.Length), ("kind", kind)));
    }

    private WorldConsequenceApplyResult ApplyUpdateSuspicion(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var suspicionId = FirstNonBlank(ReadString(payload, "suspicionId"), ReadString(payload, "suspicion_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(suspicionId))
        {
            return Reject(consequence, "Update-suspicion consequence did not include a suspicion id.");
        }

        var existing = _state.Suspicions.Records.FirstOrDefault(record =>
            record.Id.Equals(suspicionId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, "Suspicion record does not exist.");
        }

        var status = NormalizeToken(FirstNonBlank(ReadString(payload, "status"), existing.Status)!, existing.Status);
        var suspectedSoulId = FirstNonBlank(ReadString(payload, "suspectedSoulId"), ReadString(payload, "suspected_soul_id"), existing.SuspectedSoulId);
        var attributedTurn = ReadInt(payload, "attributedTurn") ?? ReadInt(payload, "attributed_turn") ?? existing.AttributedTurn;
        var updated = existing with
        {
            Status = status,
            SuspectedSoulId = suspectedSoulId,
            AttributedTurn = attributedTurn,
        };
        _state.Suspicions.Replace(updated);

        var operation = ReadString(payload, "operation") ?? "updateSuspicion";
        var summary = $"Suspicion {updated.Id} is now {updated.Status}.";
        var delta = new StateDelta(
            operation,
            updated.Id,
            summary,
            Details(
                consequence,
                ("suspicionId", updated.Id),
                ("witnessSoulId", updated.WitnessSoulId),
                ("kind", updated.Kind),
                ("status", updated.Status),
                ("suspectedSoulId", updated.SuspectedSoulId),
                ("attributedTurn", updated.AttributedTurn),
                ("expiresTurn", updated.ExpiresTurn)));
        return Applied(consequence, updated.Id, MaybeVisibleMessage(consequence, summary), delta, ("suspicionId", updated.Id), ("status", updated.Status));
    }

    private WorldConsequenceApplyResult ApplyRecordDeed(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actor = RequiredEntity(consequence, "Deed consequence did not include an actor entity id.");
        if (actor.Result is not null)
        {
            return actor.Result;
        }

        var originX = ReadInt(payload, "originX") ?? ReadInt(payload, "origin_x");
        var originY = ReadInt(payload, "originY") ?? ReadInt(payload, "origin_y");
        if (originX is null || originY is null)
        {
            return Reject(consequence, "Deed consequence did not include an origin coordinate.");
        }

        var effectX = ReadInt(payload, "effectX") ?? ReadInt(payload, "effect_x");
        var effectY = ReadInt(payload, "effectY") ?? ReadInt(payload, "effect_y");
        if ((effectX is null) != (effectY is null))
        {
            return Reject(consequence, "Deed consequence included an incomplete effect coordinate.");
        }

        var kind = NormalizeToken(FirstNonBlank(ReadString(payload, "kind"), "deed")!, "deed");
        var magnitude = Math.Clamp(ReadInt(payload, "magnitude") ?? 1, 1, 999);
        var origin = new GridPoint(originX.Value, originY.Value);
        var effectPoint = effectX is null ? (GridPoint?)null : new GridPoint(effectX.Value, effectY!.Value);
        var tags = NormalizeTags(ReadStringList(payload, "tags"));
        var capture = engine.PlanDeedCapture(
            actor.Entity!,
            kind,
            magnitude,
            origin,
            effectPoint,
            tags);
        var plan = capture.Plan;
        var deed = _state.Deeds.Append(
            plan.Turn,
            plan.ActorSoulId,
            plan.Kind,
            plan.Magnitude,
            plan.PlaceKey,
            plan.Visibility,
            plan.Witnesses,
            plan.Tags,
            plan.EffectWitnesses,
            plan.AttributedSoulId,
            plan.AttributionStatus);
        var operation = ReadString(payload, "operation") ?? "recordDeed";
        var summary = $"Deed recorded: {deed.Kind} ({deed.Visibility}).";
        var delta = new StateDelta(
            operation,
            actor.Entity!.Id.Value,
            summary,
            Details(
                consequence,
                ("deedId", deed.Id),
                ("actorSoulId", deed.ActorSoulId),
                ("kind", deed.Kind),
                ("magnitude", deed.Magnitude),
                ("placeKey", deed.PlaceKey),
                ("visibility", deed.Visibility),
                ("witnesses", deed.Witnesses),
                ("effectWitnesses", deed.EffectWitnesses ?? Array.Empty<string>()),
                ("attributedSoulId", deed.AttributedSoulId),
                ("attributionStatus", deed.AttributionStatus),
                ("tags", deed.Tags),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        var messages = MaybeVisibleMessage(consequence, summary);
        return new WorldConsequenceApplyResult(
            true,
            actor.Entity.Id.Value,
            null,
            messages,
            capture.Deltas.Concat(new[] { delta }).ToArray(),
            Details(consequence, ("deedId", deed.Id), ("visibility", deed.Visibility)));
    }

    private WorldConsequenceApplyResult ApplyUpdateDeed(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var deedId = FirstNonBlank(ReadString(payload, "deedId"), ReadString(payload, "deed_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(deedId))
        {
            return Reject(consequence, "Deed update consequence did not include a deed id.");
        }

        var deed = _state.Deeds.Records.FirstOrDefault(record =>
            record.Id.Equals(deedId, StringComparison.OrdinalIgnoreCase));
        if (deed is null)
        {
            return Reject(consequence, $"Deed update target does not exist: {deedId}");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "mark_applied")!, "mark_applied");
        if (action is not ("mark_applied" or "applied" or "apply"))
        {
            return Reject(consequence, $"Unknown deed update action: {action}");
        }

        var wasApplied = _state.Deeds.IsApplied(deed.Id);
        _state.Deeds.MarkApplied(deed.Id);
        var operation = ReadString(payload, "operation") ?? "updateDeed";
        var summary = wasApplied
            ? $"Deed already applied: {deed.Id}."
            : $"Deed marked applied: {deed.Id}.";
        var delta = new StateDelta(
            operation,
            deed.Id,
            summary,
            Details(
                consequence,
                ("deedId", deed.Id),
                ("kind", deed.Kind),
                ("visibility", deed.Visibility),
                ("action", action),
                ("applied", true),
                ("wasApplied", wasApplied),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(
            consequence,
            deed.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("deedId", deed.Id),
            ("applied", true),
            ("wasApplied", wasApplied));
    }

    private WorldConsequenceApplyResult ApplyAddLegend(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actorSoulId = FirstNonBlank(ReadString(payload, "actorSoulId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(actorSoulId))
        {
            return Reject(consequence, "Legend consequence did not include an actor soul id.");
        }

        var tag = CleanLedgerKey(FirstNonBlank(ReadString(payload, "tag"), "uncanny")!, "uncanny");
        var weight = Math.Clamp(ReadInt(payload, "weight") ?? 1, 1, 999);
        var sourceId = FirstNonBlank(ReadString(payload, "sourceId"), ReadString(payload, "source_id"), consequence.Source, "unknown")!;
        _state.Legend.Add(actorSoulId, tag, weight, sourceId);
        var operation = ReadString(payload, "operation") ?? "addLegend";
        var summary = $"{actorSoulId} gains legend tag {tag} ({weight}).";
        var delta = new StateDelta(
            operation,
            actorSoulId,
            summary,
            Details(
                consequence,
                ("tag", tag),
                ("weight", weight),
                ("sourceId", sourceId),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, actorSoulId, MaybeVisibleMessage(consequence, summary), delta, ("tag", tag), ("weight", weight));
    }

    private WorldConsequenceApplyResult ApplyAddCanon(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var kind = CleanLedgerKey(FirstNonBlank(ReadString(payload, "kind"), "canon")!, "canon");
        var attachedTo = FirstNonBlank(ReadString(payload, "attachedTo"), ReadString(payload, "attached_to"), consequence.TargetEntityId, "world")!;
        var text = FirstNonBlank(ReadString(payload, "text"), consequence.Evidence, "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Canon consequence did not include text.");
        }

        var summary = FirstNonBlank(ReadString(payload, "summary"), text)!;
        var tags = ReadStringList(payload, "tags");
        var record = _state.Canon.Add(
            kind,
            attachedTo,
            text,
            summary,
            tags,
            FirstNonBlank(ReadString(payload, "canonSource"), ReadString(payload, "source"), consequence.Source, "world_consequence")!,
            _state.Turn);
        var operation = ReadString(payload, "operation") ?? "addCanon";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(consequence, ("kind", kind), ("attachedTo", attachedTo), ("tags", tags)));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("canonId", record.Id));
    }

    private WorldConsequenceApplyResult ApplyRecordWorldTurn(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var reason = FirstNonBlank(
            ReadString(payload, "worldTurnReason"),
            ReadString(payload, "world_turn_reason"),
            ReadString(payload, "moveReason"),
            ReadString(payload, "reason"),
            consequence.Reason,
            "turn")!;
        var kind = FirstNonBlank(ReadString(payload, "kind"), ReadString(payload, "moveKind"), "move")!;
        var sourceId = FirstNonBlank(
            ReadString(payload, "worldTurnSourceId"),
            ReadString(payload, "world_turn_source_id"),
            ReadString(payload, "sourceId"),
            ReadString(payload, "source_id"),
            consequence.TargetEntityId,
            consequence.SourceEntityId,
            consequence.Source,
            "unknown")!;
        var summary = FirstNonBlank(ReadString(payload, "summary"), consequence.Evidence);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Reject(consequence, "World-turn consequence did not include a summary.");
        }

        var recordDetails = ReadDictionary(payload, "details")
            ?? ReadDictionary(payload, "recordDetails")
            ?? ReadDictionary(payload, "record_details")
            ?? PayloadWithoutWorldTurnKeys(payload);
        var record = _state.WorldTurns.Add(_state.Turn, reason, kind, sourceId, summary, recordDetails);
        var operation = ReadString(payload, "operation") ?? "worldTurn";
        var deltaDetails = new Dictionary<string, object?>(Details(
            consequence,
            ("worldTurnId", record.Id),
            ("worldTurnReason", record.Reason),
            ("kind", record.Kind),
            ("worldTurnSourceId", record.SourceId),
            ("sourceId", record.SourceId),
            ("recordDetails", record.Details),
            ("auditOnly", ReadBool(payload, "auditOnly") ?? true),
            ("playerVisible", ReadBool(payload, "playerVisible") ?? false)), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in record.Details)
        {
            deltaDetails.TryAdd(pair.Key, pair.Value);
        }

        var delta = new StateDelta(
            operation,
            record.Id,
            record.Summary,
            deltaDetails);
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, record.Summary), delta, ("worldTurnId", record.Id), ("kind", record.Kind));
    }

    private WorldConsequenceApplyResult ApplyRecordExploration(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var soulId = FirstNonBlank(ReadString(payload, "soulId"), ReadString(payload, "soul_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(soulId))
        {
            return Reject(consequence, "Exploration consequence did not include a soul id.");
        }

        var tiles = ReadPointList(payload, "tiles")
            .Concat(ReadPointList(payload, "visibleTiles"))
            .Concat(ReadPointList(payload, "visible_tiles"))
            .Where(InBounds)
            .Distinct()
            .OrderBy(point => point.Y)
            .ThenBy(point => point.X)
            .ToArray();
        if (tiles.Length == 0)
        {
            return Reject(consequence, "Exploration consequence did not include any in-bounds tiles.");
        }

        if (!_state.ExploredBySoulId.TryGetValue(soulId, out var explored))
        {
            explored = new HashSet<GridPoint>();
            _state.ExploredBySoulId[soulId] = explored;
        }

        var before = explored.Count;
        foreach (var point in tiles)
        {
            explored.Add(point);
        }

        var newTileCount = explored.Count - before;
        var operation = ReadString(payload, "operation") ?? "recordExploration";
        var summary = newTileCount == 0
            ? $"{soulId} exploration is unchanged."
            : $"{soulId} explores {newTileCount} new tile(s).";
        var delta = new StateDelta(
            operation,
            soulId,
            summary,
            Details(
                consequence,
                ("soulId", soulId),
                ("tileCount", tiles.Length),
                ("newTileCount", newTileCount),
                ("totalExplored", explored.Count),
                ("auditOnly", ReadBool(payload, "auditOnly") ?? true),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? false)));
        return Applied(
            consequence,
            soulId,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("soulId", soulId),
            ("tileCount", tiles.Length),
            ("newTileCount", newTileCount),
            ("totalExplored", explored.Count));
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

    private WorldConsequenceApplyResult ApplySetResistance(WorldConsequence consequence, bool weakness)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Resistance consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var damageType = NormalizeToken(FirstNonBlank(ReadString(payload, "damageType"), ReadString(payload, "damage_type"), "physical")!, "physical");
        var amount = weakness
            ? Math.Clamp(ReadInt(payload, "amount") ?? 50, 0, 200)
            : Math.Clamp(ReadInt(payload, "amount") ?? 25, 0, 95);
        var resistance = target.Entity!.TryGet<ResistanceComponent>(out var existing)
            ? existing
            : ResistanceComponent.Empty();
        if (weakness)
        {
            resistance.Weaknesses[damageType] = amount;
        }
        else
        {
            resistance.Resistances[damageType] = amount;
        }

        target.Entity.Set(resistance);
        var operation = ReadString(payload, "operation") ?? (weakness ? "addWeakness" : "addResistance");
        var summary = weakness
            ? $"{target.Entity.Name} grows vulnerable to {damageType.Replace('_', ' ')} damage (+{amount}%)."
            : $"{target.Entity.Name} resists {damageType.Replace('_', ' ')} damage by {amount}%.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("damageType", damageType), ("amount", amount), ("weakness", weakness)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("damageType", damageType), ("amount", amount));
    }

    private WorldConsequenceApplyResult ApplyDelayIncomingDamage(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Delay-incoming consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<ActorComponent>(out _))
        {
            return Reject(consequence, "Delay-incoming consequence target is not an actor.");
        }

        var turns = Math.Clamp(ReadInt(payload, "turns") ?? ReadInt(payload, "delay") ?? 3, 1, 999);
        target.Entity.Set(new DelayedDamageComponent(0, _state.Turn + turns));
        var operation = ReadString(payload, "operation") ?? "delayIncoming";
        var summary = $"{Possessive(target.Entity)} wounds are held back for {turns} turns.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("turns", turns), ("releaseTurn", _state.Turn + turns)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("turns", turns));
    }

    private WorldConsequenceApplyResult ApplyReleaseDelayedDamage(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Release-delayed-damage consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<DelayedDamageComponent>(out var buffer))
        {
            return Reject(consequence, "Release-delayed-damage target has no delayed damage buffer.");
        }

        var operation = ReadString(payload, "operation") ?? "releaseDelayedDamage";
        if (!target.Entity.TryGet<ActorComponent>(out _))
        {
            target.Entity.Remove<DelayedDamageComponent>();
            var skippedSummary = $"{Possessive(target.Entity)} delayed wounds dissipate without a living body.";
            var skippedDelta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                skippedSummary,
                Details(
                    consequence,
                    ("buffered", buffer.Buffered),
                    ("releaseTurn", buffer.ReleaseTurn),
                    ("skipped", true),
                    ("reason", "non_actor_target")));
            return Applied(
                consequence,
                target.Entity.Id.Value,
                MaybeVisibleMessage(consequence, skippedSummary),
                skippedDelta,
                ("buffered", buffer.Buffered),
                ("releaseTurn", buffer.ReleaseTurn),
                ("skipped", true),
                ("reason", "non_actor_target"));
        }

        target.Entity.Remove<DelayedDamageComponent>();
        var released = buffer.Buffered > 0
            ? ApplyImmediateDamageDelta(target.Entity, buffer.Buffered, "delayed")
            : null;
        if (released is not null)
        {
            var releaseSummary = FirstNonBlank(ReadString(payload, "message"), released.Summary)!;
            AddMessageIfAllowed(consequence, payload, releaseSummary);

            return AppliedFromDelta(
                consequence,
                WithOperation(new StateDelta(
                    released.Operation,
                    released.Target,
                    releaseSummary,
                    released.Details
                        .Concat(new Dictionary<string, object?>
                        {
                            ["buffered"] = buffer.Buffered,
                            ["releaseTurn"] = buffer.ReleaseTurn,
                        })
                        .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase)),
                    operation));
        }

        var summary = $"{Possessive(target.Entity)} delayed wounds dissipate.";
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("buffered", buffer.Buffered), ("releaseTurn", buffer.ReleaseTurn)));
        return Applied(consequence, target.Entity.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("buffered", buffer.Buffered), ("releaseTurn", buffer.ReleaseTurn));
    }

    private WorldConsequenceApplyResult ApplyEditMemory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Edit-memory consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var op = NormalizeToken(FirstNonBlank(ReadString(payload, "op"), "add")!, "add");
        return op is "remove" or "erase" or "forget"
            ? RemoveMemory(consequence, target.Entity!, payload)
            : AddOrAlterMemory(consequence, target.Entity!, payload, op == "alter" ? "altered by wild magic" : "planted by wild magic");
    }

    private WorldConsequenceApplyResult ApplyCreatePersistentEffect(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Persistent-effect consequence did not include an anchor entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var hook = NormalizeToken(FirstNonBlank(ReadString(payload, "hook"), "on_hit")!, "on_hit");
        if (hook is not "on_hit" and not "on_strike")
        {
            hook = "on_hit";
        }

        var effectType = FirstNonBlank(ReadString(payload, "effectType"), ReadString(payload, "effect_type"), ReadString(payload, "then"), "message")!;
        var effectFields = ReadDictionary(payload, "effectFields") ?? ReadDictionary(payload, "effect") ?? PayloadWithoutPersistentKeys(payload);
        var uses = Math.Clamp(ReadInt(payload, "uses") ?? 3, 1, 999);
        var linkPartnerId = FirstNonBlank(ReadString(payload, "linkPartnerId"), ReadString(payload, "linkPartner"), ReadString(payload, "link_target"));
        var playerVisible = ReadBool(payload, "playerVisible") ?? true;
        var record = _state.PersistentEffects.Add(
            target.Entity!.Id.Value,
            hook,
            effectType,
            effectFields,
            uses,
            linkPartnerId,
            playerVisible);
        var operation = ReadString(payload, "operation") ?? "createPersistentEffect";
        var anchorName = target.Entity.Id == _state.ControlledEntityId ? "you" : target.Entity.Name;
        var summary = string.IsNullOrWhiteSpace(linkPartnerId)
            ? $"A lasting mark settles onto {anchorName}, waiting to answer when {(hook == "on_hit" ? "it is struck" : "it strikes")}."
            : $"A sympathetic link binds {anchorName} to another's wounds.";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("hook", hook),
                ("effectType", effectType),
                ("uses", uses),
                ("linkPartnerId", linkPartnerId),
                ("playerVisible", playerVisible)));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("effectId", record.Id));
    }

    private WorldConsequenceApplyResult ApplyUpdatePersistentEffect(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var effectId = FirstNonBlank(ReadString(payload, "effectId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(effectId))
        {
            return Reject(consequence, "Persistent-effect update consequence did not include an effect id.");
        }

        var record = _state.PersistentEffects.Records.FirstOrDefault(existing =>
            existing.Id.Equals(effectId, StringComparison.OrdinalIgnoreCase));
        if (record is null)
        {
            return Reject(consequence, $"Persistent-effect update target does not exist: {effectId}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "consume")!, "consume");
        var amount = Math.Clamp(ReadInt(payload, "amount") ?? 1, 1, 999);
        var remaining = record.RemainingUses;
        var verb = ClassifyTerminalAction(action) switch
        {
            TerminalUpdateAction.Complete => RemovePersistentEffect(record, out remaining, "completed"),
            TerminalUpdateAction.Expire => RemovePersistentEffect(record, out remaining, "expired"),
            TerminalUpdateAction.Remove => RemovePersistentEffect(record, out remaining, "removed"),
            _ => action is "consume" or "use" or "fire" ? ApplyPersistentEffectConsume(record, amount, out remaining) : null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported persistent-effect update action: {action}.");
        }

        var operation = ReadString(payload, "operation") ?? "updatePersistentEffect";
        var summary = $"Persistent effect {record.Id} {verb}.";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("effectId", record.Id),
                ("hook", record.Hook),
                ("effectType", record.EffectType),
                ("anchorEntityId", record.AnchorEntityId),
                ("linkPartnerId", record.LinkPartnerId),
                ("action", action),
                ("previousRemainingUses", record.RemainingUses),
                ("remainingUses", remaining),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, summary), delta, ("effectId", record.Id), ("action", action), ("remainingUses", remaining));
    }

    private string ApplyPersistentEffectConsume(PersistentEffectRecord record, int amount, out int remaining)
    {
        remaining = Math.Max(0, record.RemainingUses - amount);
        if (remaining <= 0)
        {
            _state.PersistentEffects.Remove(record.Id);
            return "is consumed";
        }

        _state.PersistentEffects.Replace(record with { RemainingUses = remaining });
        return $"has {remaining} use(s) left";
    }

    private string RemovePersistentEffect(PersistentEffectRecord record, out int remaining, string verb)
    {
        remaining = 0;
        _state.PersistentEffects.Remove(record.Id);
        return verb;
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

    private WorldConsequenceApplyResult ApplyCreateFlow(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var origin))
        {
            return Reject(consequence, "Flow consequence did not include a tile coordinate.");
        }

        if (!engine.InBounds(origin))
        {
            return Reject(consequence, "Flow consequence target is out of bounds.");
        }

        var radius = Math.Clamp(ReadInt(payload, "radius") ?? 1, 0, 5);
        var dx = Math.Clamp(ReadInt(payload, "dx") ?? 1, -1, 1);
        var dy = Math.Clamp(ReadInt(payload, "dy") ?? 0, -1, 1);
        var duration = Math.Clamp(ReadInt(payload, "duration") ?? 5, 1, 999);
        var expiresTurn = _state.Turn + duration;
        var changed = 0;
        for (var y = origin.Y - radius; y <= origin.Y + radius; y++)
        {
            for (var x = origin.X - radius; x <= origin.X + radius; x++)
            {
                var point = new GridPoint(x, y);
                if (engine.InBounds(point)
                    && Distance(origin, point) <= radius
                    && !_state.BlockingTerrain.Contains(point))
                {
                    _state.TileFlows[point] = new TileFlow(dx, dy, expiresTurn);
                    changed++;
                }
            }
        }

        var operation = ReadString(payload, "operation") ?? "createFlow";
        var targetId = $"tile:{origin.X},{origin.Y}";
        var summary = $"The ground begins to flow near {origin.X},{origin.Y}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(consequence, ("dx", dx), ("dy", dy), ("radius", radius), ("duration", duration), ("tiles", changed)));
        return Applied(consequence, targetId, MaybeVisibleMessage(consequence, summary), delta, ("tiles", changed));
    }

    private WorldConsequenceApplyResult ApplyUpdateFlow(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Flow update consequence did not include a tile coordinate.");
        }

        if (!_state.TileFlows.TryGetValue(point, out var flow))
        {
            return Reject(consequence, $"Flow update target does not exist: {point.X},{point.Y}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "expire")!, "expire");
        var verb = ClassifyTerminalAction(action) switch
        {
            TerminalUpdateAction.Complete => "completes",
            TerminalUpdateAction.Expire => "expires",
            TerminalUpdateAction.Remove => "is removed",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported flow update action: {action}.");
        }

        _state.TileFlows.Remove(point);
        var operation = ReadString(payload, "operation") ?? "updateFlow";
        var targetId = $"tile:{point.X},{point.Y}";
        var summary = $"The tile flow at {point.X},{point.Y} {verb}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("dx", flow.Dx),
                ("dy", flow.Dy),
                ("expiresTurn", flow.ExpiresTurn),
                ("action", action)));
        return Applied(consequence, targetId, MaybeVisibleMessage(consequence, summary), delta, ("x", point.X), ("y", point.Y), ("action", action));
    }

    private WorldConsequenceApplyResult ApplyRecordRumor(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Rumor consequence did not include text.");
        }

        var sourceKind = NormalizeToken(FirstNonBlank(ReadString(payload, "sourceKind"), ReadString(payload, "source_kind"), consequence.Source)!, "unknown");
        var sourceId = NormalizeToken(FirstNonBlank(ReadString(payload, "sourceId"), ReadString(payload, "source_id"), consequence.TargetEntityId, "unknown")!, "unknown");
        if (_state.Rumors.HasSource(sourceKind, sourceId))
        {
            return new WorldConsequenceApplyResult(
                false,
                sourceId,
                "duplicate_rumor_source",
                Array.Empty<string>(),
                Array.Empty<StateDelta>(),
                Details(consequence, ("sourceKind", sourceKind), ("sourceId", sourceId)));
        }

        var originRegionId = FirstNonBlank(ReadString(payload, "originRegionId"), ReadString(payload, "origin_region_id"), _state.RegionId)!;
        var currentRegionId = FirstNonBlank(ReadString(payload, "currentRegionId"), ReadString(payload, "current_region_id"), originRegionId)!;
        var salience = Math.Clamp(ReadInt(payload, "salience") ?? Math.Max(1, consequence.Salience), 1, 5);
        var status = FirstNonBlank(ReadString(payload, "status"), "active")!;
        var originalText = FirstNonBlank(ReadString(payload, "originalText"), ReadString(payload, "original_text"));
        var hops = Math.Max(0, ReadInt(payload, "hops") ?? 0);
        var rumor = _state.Rumors.Append(
            _state.Turn,
            sourceKind,
            sourceId,
            originRegionId,
            currentRegionId,
            text,
            salience,
            ReadStringList(payload, "carrierIds").Concat(ReadStringList(payload, "carriers")),
            ReadStringList(payload, "tags"),
            status,
            ReadStringList(payload, "distortionHistory").Concat(ReadStringList(payload, "distortion_history")),
            hops,
            originalText);
        var operation = ReadString(payload, "operation") ?? "recordRumor";
        var summary = ReadString(payload, "summary") ?? $"A rumor begins: {rumor.Text}";
        var delta = new StateDelta(
            operation,
            rumor.Id,
            summary,
            Details(
                consequence,
                ("rumorId", rumor.Id),
                ("sourceKind", rumor.SourceKind),
                ("sourceId", rumor.SourceId),
                ("originRegionId", rumor.OriginRegionId),
                ("currentRegionId", rumor.CurrentRegionId),
                ("salience", rumor.Salience),
                ("carrierIds", rumor.CarrierIds),
                ("tags", rumor.Tags)));
        return Applied(
            consequence,
            rumor.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("rumorId", rumor.Id),
            ("sourceKind", rumor.SourceKind),
            ("sourceId", rumor.SourceId),
            ("salience", rumor.Salience));
    }

    private WorldConsequenceApplyResult ApplyUpdateRumor(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var rumorId = FirstNonBlank(ReadString(payload, "rumorId"), ReadString(payload, "rumor_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(rumorId))
        {
            return Reject(consequence, "Update-rumor consequence did not include a rumor id.");
        }

        var existing = _state.Rumors.Records.FirstOrDefault(rumor =>
            rumor.Id.Equals(rumorId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Rumor not found: {rumorId}");
        }

        IEnumerable<string> carrierIds = HasAnyKey(payload, "carrierIds", "carrier_ids", "carriers")
            ? ReadStringList(payload, "carrierIds")
                .Concat(ReadStringList(payload, "carrier_ids"))
                .Concat(ReadStringList(payload, "carriers"))
            : existing.CarrierIds;
        carrierIds = carrierIds
            .Concat(ReadStringList(payload, "addCarrierIds"))
            .Concat(ReadStringList(payload, "add_carrier_ids"))
            .Concat(ReadStringList(payload, "newCarriers"));

        IEnumerable<string> tags = HasAnyKey(payload, "tags")
            ? ReadStringList(payload, "tags")
            : existing.Tags;
        tags = tags
            .Concat(ReadStringList(payload, "addTags"))
            .Concat(ReadStringList(payload, "add_tags"));

        IEnumerable<string> history = HasAnyKey(payload, "distortionHistory", "distortion_history")
            ? ReadStringList(payload, "distortionHistory")
                .Concat(ReadStringList(payload, "distortion_history"))
            : existing.DistortionHistory;
        history = history
            .Concat(ReadStringList(payload, "appendDistortionHistory"))
            .Concat(ReadStringList(payload, "append_distortion_history"))
            .Concat(ReadStringList(payload, "historyEntry"))
            .TakeLast(12);

        var salience = Math.Clamp(ReadInt(payload, "salience") ?? existing.Salience, 1, 5);
        var hops = ReadInt(payload, "hops")
            ?? existing.Hops + (ReadBool(payload, "incrementHops") == true ? 1 : 0);
        var updated = existing with
        {
            LastTurn = ReadInt(payload, "lastTurn") ?? ReadInt(payload, "last_turn") ?? _state.Turn,
            CurrentRegionId = FirstNonBlank(ReadString(payload, "currentRegionId"), ReadString(payload, "current_region_id"), existing.CurrentRegionId)!,
            Text = FirstNonBlank(ReadString(payload, "text"), existing.Text)!,
            OriginalText = FirstNonBlank(ReadString(payload, "originalText"), ReadString(payload, "original_text"), existing.OriginalText)!,
            Salience = salience,
            Status = FirstNonBlank(ReadString(payload, "status"), existing.Status)!,
            CarrierIds = carrierIds.ToArray(),
            Tags = tags.ToArray(),
            DistortionHistory = history.ToArray(),
            Hops = Math.Max(0, hops),
        };

        var rumor = _state.Rumors.Replace(updated);
        if (rumor is null)
        {
            return Reject(consequence, $"Rumor not found: {rumorId}");
        }

        var operation = ReadString(payload, "operation") ?? "updateRumor";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"Rumor {rumor.Id} changes: {rumor.Text}")!;
        var delta = new StateDelta(
            operation,
            rumor.Id,
            summary,
            Details(
                consequence,
                ("rumorId", rumor.Id),
                ("sourceKind", rumor.SourceKind),
                ("sourceId", rumor.SourceId),
                ("originRegionId", rumor.OriginRegionId),
                ("currentRegionId", rumor.CurrentRegionId),
                ("salience", rumor.Salience),
                ("status", rumor.Status),
                ("carrierIds", rumor.CarrierIds),
                ("tags", rumor.Tags),
                ("hops", rumor.Hops)));
        return Applied(
            consequence,
            rumor.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("rumorId", rumor.Id),
            ("salience", rumor.Salience),
            ("status", rumor.Status),
            ("hops", rumor.Hops));
    }

    private WorldConsequenceApplyResult ApplyRecordClaim(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Claim consequence did not include text.");
        }

        var speakerId = FirstNonBlank(ReadString(payload, "speakerId"), ReadString(payload, "speaker_id"), consequence.TargetEntityId, consequence.SourceEntityId, "unknown")!;
        var listenerSoulId = FirstNonBlank(
            ReadString(payload, "listenerSoulId"),
            ReadString(payload, "listener_soul_id"),
            SoulIdFor(_state.ControlledEntity),
            "unknown")!;
        var category = NormalizeToken(FirstNonBlank(ReadString(payload, "category"), ReadString(payload, "kind"), "memory")!, "memory");
        var subject = FirstNonBlank(ReadString(payload, "subject"), text)!;
        var salience = Math.Clamp(ReadInt(payload, "salience") ?? Math.Max(1, consequence.Salience), 1, 5);
        var confidence = Math.Clamp(ReadInt(payload, "confidence") ?? Math.Clamp(consequence.Confidence, 0, 100), 0, 100);
        var playerVisible = ReadBool(payload, "playerVisible")
            ?? ReadBool(payload, "player_visible")
            ?? IsVisible(consequence.Visibility);
        var source = FirstNonBlank(ReadString(payload, "claimSource"), ReadString(payload, "claim_source"), consequence.Source, "unknown")!;
        var status = FirstNonBlank(ReadString(payload, "status"), "reported")!;
        var duplicate = _state.Claims.Records.FirstOrDefault(record =>
            record.SpeakerId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
            && record.ListenerSoulId.Equals(listenerSoulId, StringComparison.OrdinalIgnoreCase)
            && record.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
            && NormalizeClaimText(record.Text).Equals(NormalizeClaimText(text), StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            var duplicateOperation = ReadString(payload, "duplicateOperation") ?? "claimDuplicate";
            var duplicateSummary = $"Claim already recorded: {duplicate.Text}";
            var duplicateDelta = new StateDelta(
                duplicateOperation,
                duplicate.Id,
                duplicateSummary,
                Details(
                    consequence,
                    ("claimId", duplicate.Id),
                    ("speakerId", duplicate.SpeakerId),
                    ("listenerSoulId", duplicate.ListenerSoulId),
                    ("category", duplicate.Category),
                    ("subject", duplicate.Subject),
                    ("salience", duplicate.Salience),
                    ("confidence", duplicate.Confidence),
                    ("playerVisible", false),
                    ("status", duplicate.Status),
                    ("tags", duplicate.Tags),
                    ("duplicate", true)));
            return Applied(
                consequence,
                duplicate.Id,
                Array.Empty<string>(),
                duplicateDelta,
                ("claimId", duplicate.Id),
                ("duplicate", true),
                ("playerVisible", false));
        }

        var record = _state.Claims.Append(
            _state.Turn,
            source,
            speakerId,
            listenerSoulId,
            text,
            category,
            subject,
            salience,
            confidence,
            playerVisible,
            ReadStringList(payload, "tags"),
            status);

        var operation = ReadString(payload, "operation") ?? "claimRecorded";
        var summary = ReadString(payload, "summary") ?? $"A reported claim is recorded: {record.Text}";
        var delta = new StateDelta(
            operation,
            record.Id,
            summary,
            Details(
                consequence,
                ("claimId", record.Id),
                ("speakerId", record.SpeakerId),
                ("listenerSoulId", record.ListenerSoulId),
                ("category", record.Category),
                ("subject", record.Subject),
                ("salience", record.Salience),
                ("confidence", record.Confidence),
                ("playerVisible", record.PlayerVisible),
                ("status", record.Status),
                ("tags", record.Tags)));
        return Applied(
            consequence,
            record.Id,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("claimId", record.Id),
            ("speakerId", record.SpeakerId),
            ("category", record.Category),
            ("salience", record.Salience),
            ("confidence", record.Confidence),
            ("playerVisible", record.PlayerVisible));
    }

    private WorldConsequenceApplyResult ApplyUpdateClaim(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var claimId = FirstNonBlank(ReadString(payload, "claimId"), ReadString(payload, "claim_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(claimId))
        {
            return Reject(consequence, "Claim update did not include a claim id.");
        }

        var existing = _state.Claims.Records.FirstOrDefault(record =>
            record.Id.Equals(claimId, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
        {
            return Reject(consequence, $"Claim update target does not exist: {claimId}");
        }

        var status = FirstNonBlank(ReadString(payload, "status"));
        var boundPromiseId = FirstNonBlank(ReadString(payload, "boundPromiseId"), ReadString(payload, "bound_promise_id"), ReadString(payload, "promiseId"));
        var appliedTo = FirstNonBlank(ReadString(payload, "appliedTo"), ReadString(payload, "applied_to"), ReadString(payload, "targetId"));
        var updated = _state.Claims.Update(existing.Id, status, boundPromiseId, appliedTo);
        if (updated is null)
        {
            return Reject(consequence, $"Claim update target does not exist: {claimId}");
        }

        var operation = ReadString(payload, "operation") ?? "updateClaim";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"Claim {updated.Id} is now {updated.Status}.")!;
        var delta = new StateDelta(
            operation,
            updated.Id,
            summary,
            Details(
                consequence,
                ("claimId", updated.Id),
                ("previousStatus", existing.Status),
                ("status", updated.Status),
                ("boundPromiseId", updated.BoundPromiseId),
                ("appliedTo", updated.AppliedTo),
                ("category", updated.Category),
                ("subject", updated.Subject),
                ("playerVisible", updated.PlayerVisible)));
        var messages = AddMessageIfAllowed(
                consequence,
                payload,
                summary,
                defaultEmitMessage: false,
                playerVisible: updated.PlayerVisible)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, updated.Id, messages, delta, ("claimId", updated.Id), ("status", updated.Status));
    }

    private WorldConsequenceApplyResult AddOrAlterMemory(
        WorldConsequence consequence,
        Entity target,
        IReadOnlyDictionary<string, object?> payload,
        string fallbackProvenance)
    {
        var text = FirstNonBlank(ReadString(payload, "text"), ReadString(payload, "subject"), "something that did not happen")!;
        var strength = Math.Clamp(ReadInt(payload, "strength") ?? consequence.Salience, 1, 5);
        var provenance = FirstNonBlank(ReadString(payload, "provenance"), fallbackProvenance, consequence.Source)!;
        var shareable = ReadBool(payload, "shareable") ?? strength >= 4;
        var operation = ReadString(payload, "operation") ?? "editMemory";
        var summary = $"{Possessive(target)} memory shifts: {text}";
        var memory = Apply(WorldConsequence.RecordMemory(
            consequence.Source,
            target.Id.Value,
            text,
            provenance,
            strength,
            shareable,
            consequence.Visibility,
            consequence.SourceEntityId,
            consequence.Evidence,
            "Memory edit added or altered a concrete remembered fact through the shared memory lifecycle.",
            operation,
            new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["parentOperation"] = operation,
                ["op"] = "add",
                ["strength"] = strength,
                ["summary"] = summary,
            }));
        if (!memory.Applied)
        {
            return memory;
        }

        return new WorldConsequenceApplyResult(
            true,
            target.Id.Value,
            null,
            memory.Messages,
            memory.Deltas,
            Details(consequence, ("op", "add"), ("text", text), ("strength", strength), ("provenance", provenance)));
    }

    private WorldConsequenceApplyResult RemoveMemory(
        WorldConsequence consequence,
        Entity target,
        IReadOnlyDictionary<string, object?> payload)
    {
        var snapshot = GameStateSnapshot.Capture(_state);
        var subject = FirstNonBlank(ReadString(payload, "subject"), ReadString(payload, "text"), "") ?? "";
        var inferredAboutCaster = subject.Contains("caster", StringComparison.OrdinalIgnoreCase)
            || subject.Contains("player", StringComparison.OrdinalIgnoreCase)
            || subject.Equals("me", StringComparison.OrdinalIgnoreCase);
        var aboutCaster = ReadBool(payload, "aboutCaster") ?? inferredAboutCaster;
        if (target.TryGet<MemoryComponent>(out var memory))
        {
            var remaining = memory.Records
                .Where(record => !RecordMentionsMemory(record, subject, aboutCaster))
                .ToArray();
            target.Set(new MemoryComponent(remaining));
        }

        var operation = ReadString(payload, "operation") ?? "editMemory";
        var summary = aboutCaster
            ? $"{target.Name} no longer remembers the caster; the hostility drains out of them."
            : $"{Possessive(target)} memory of {subject} fades.";
        var delta = new StateDelta(
            operation,
            target.Id.Value,
            summary,
            Details(consequence, ("op", "remove"), ("subject", subject), ("aboutCaster", aboutCaster)));
        var messages = MaybeVisibleMessage(consequence, summary).ToList();
        var deltas = new List<StateDelta> { delta };
        var bondDelta = 0;
        if (aboutCaster && target.TryGet<SoulComponent>(out var npcSoul))
        {
            var playerSoulId = _state.ControlledEntity.TryGet<SoulComponent>(out var playerSoul)
                ? playerSoul.SoulId
                : _state.ControlledEntityId.Value;
            var loyaltyFloor = Math.Clamp(ReadInt(payload, "loyaltyFloor") ?? 5, -10, 10);
            var currentLoyalty = _state.Bonds.TryGet(npcSoul.SoulId, playerSoulId, out var existingBond)
                ? existingBond.Loyalty
                : 0;
            bondDelta = Math.Max(0, loyaltyFloor - currentLoyalty);
            if (bondDelta > 0)
            {
                var bond = Apply(WorldConsequence.UpdateBond(
                    consequence.Source,
                    target.Id.Value,
                    playerSoulId,
                    bondDelta,
                    0,
                    0,
                    0,
                    posture: null,
                    sourceEntityId: consequence.SourceEntityId,
                    evidence: consequence.Evidence,
                    reason: "Forgetting the caster calms hostility through the shared bond lifecycle.",
                    operation: "memoryBondFloor",
                    maxDelta: bondDelta,
                    details: new Dictionary<string, object?>
                    {
                        ["parentConsequenceType"] = consequence.Type,
                        ["parentOperation"] = operation,
                        ["loyaltyFloor"] = loyaltyFloor,
                        ["aboutCaster"] = true,
                        ["playerVisible"] = false,
                    }));
                if (!bond.Applied)
                {
                    return RollBackEditMemory(
                        consequence,
                        snapshot,
                        target.Id.Value,
                        operation,
                        subject,
                        bond.Deltas,
                        bond.Messages,
                        bond.Error ?? "memory_bond_floor_rejected");
                }

                messages.AddRange(bond.Messages);
                deltas.AddRange(bond.Deltas);
            }
        }

        return new WorldConsequenceApplyResult(
            true,
            target.Id.Value,
            null,
            messages,
            deltas,
            Details(consequence, ("op", "remove"), ("subject", subject), ("bondDelta", bondDelta)));
    }

    private WorldConsequenceApplyResult RollBackEditMemory(
        WorldConsequence consequence,
        GameStateSnapshot snapshot,
        string targetId,
        string operation,
        string subject,
        IReadOnlyList<StateDelta> failedDeltas,
        IReadOnlyList<string> failedMessages,
        string failure)
    {
        snapshot.Restore(_state);
        var skipped = new StateDelta(
            "editMemorySkipped",
            targetId,
            $"Memory edit rolled back: {failure}.",
            Details(
                consequence,
                ("operation", operation),
                ("op", "remove"),
                ("subject", subject),
                ("failure", failure),
                ("rejectedCount", failedDeltas.Count(delta =>
                    delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            targetId,
            failure,
            Array.Empty<string>(),
            failedDeltas.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("error", failure),
                ("operation", operation),
                ("op", "remove"),
                ("subject", subject),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count)));
    }

    private bool RequireEngine(
        WorldConsequence consequence,
        out GameEngine engine,
        out WorldConsequenceApplyResult result)
    {
        if (_engine is not null)
        {
            engine = _engine;
            result = WorldConsequenceApplyResult.Empty();
            return true;
        }

        engine = null!;
        result = Reject(consequence, "This consequence type requires engine services.");
        return false;
    }

    /// <summary>
    /// Animation makes an existing world entity act: a defeated actor rises again, or an inert
    /// fixture/prop/floor item gains a bounded body. Stats are capped well below summoned-boss
    /// range regardless of what the payload asks for, and the result is marked summoned so zone
    /// rules treat it like any other conjured ally.
    /// </summary>
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

    private WorldConsequenceApplyResult ApplyRecordMemory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Memory consequence did not include text.");
        }

        var ownerId = FirstNonBlank(consequence.TargetEntityId, consequence.SourceEntityId, _state.ControlledEntityId.Value)!;
        var salience = Math.Clamp(consequence.Salience, 1, 5);
        var provenance = FirstNonBlank(ReadString(payload, "provenance"), consequence.Source) ?? consequence.Source;
        var shareable = ReadBool(payload, "shareable") ?? true;
        var requireOwnerEntity = ReadBool(payload, "requireOwnerEntity")
            ?? ReadBool(payload, "require_owner_entity")
            ?? false;
        var owner = EntityById(ownerId);
        if (requireOwnerEntity && owner is null)
        {
            return Reject(consequence, "Memory consequence owner entity does not exist.");
        }

        var worldMemory = _state.Memories.Append(ownerId, text, provenance, salience, shareable);
        if (owner is not null)
        {
            var memories = owner.TryGet<MemoryComponent>(out var existing)
                ? existing.Records.ToList()
                : new List<EntityMemoryRecord>();
            memories.Add(new EntityMemoryRecord(
                $"memory_{NormalizeToken(consequence.Source, "source")}_{_state.Turn}_{memories.Count + 1}",
                text,
                consequence.Source,
                provenance,
                salience,
                shareable));
            owner.Set(new MemoryComponent(memories.TakeLast(24).ToArray()));
        }

        var operation = ReadString(payload, "operation") ?? "recordMemory";
        var summary = ReadString(payload, "summary") ?? $"Memory recorded: {text}";
        var delta = new StateDelta(
            operation,
            ownerId,
            summary,
            Details(
                consequence,
                ("memoryId", worldMemory.Id),
                ("salience", salience),
                ("provenance", provenance),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, ownerId, MaybeVisibleMessage(consequence, summary), delta, ("memoryId", worldMemory.Id), ("salience", salience), ("provenance", provenance));
    }

    private WorldConsequenceApplyResult ApplyUpdateBond(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var entityId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Reject(consequence, "Bond consequence did not include an entity id.");
        }

        var operation = ReadString(payload, "operation") ?? "updateBond";
        var entity = EntityById(entityId);
        if (entity is null)
        {
            return new WorldConsequenceApplyResult(
                false,
                entityId,
                "missing_entity",
                Array.Empty<string>(),
                new[]
                {
                    new StateDelta(
                        "worldConsequenceRejected",
                        entityId,
                        "Bond consequence rejected because the entity no longer exists.",
                        Details(consequence, ("proposalType", "bond"), ("operation", operation))),
                },
                Details(consequence, ("proposalType", "bond"), ("operation", operation)));
        }

        var targetSoulId = ReadString(payload, "targetSoulId");
        if (string.IsNullOrWhiteSpace(targetSoulId))
        {
            return Reject(consequence, "Bond consequence did not include a target soul id.");
        }

        var maxDelta = Math.Max(0, ReadInt(payload, "maxDelta") ?? _defaultBondDeltaLimit);
        var bond = _state.Bonds.Adjust(
            SoulIdFor(entity),
            targetSoulId,
            ClampDelta(ReadInt(payload, "loyaltyDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "fearDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "admirationDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "resentmentDelta") ?? 0, maxDelta),
            FirstNonBlank(ReadString(payload, "posture")));
        var summary = $"{Possessive(entity)} posture shifts: {BondSummary(bond)}.";
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("loyalty", bond.Loyalty),
                ("fear", bond.Fear),
                ("admiration", bond.Admiration),
                ("resentment", bond.Resentment),
                ("posture", bond.Posture)));
        return Applied(
            consequence,
            entity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("loyalty", bond.Loyalty),
            ("fear", bond.Fear),
            ("admiration", bond.Admiration),
            ("resentment", bond.Resentment),
            ("posture", bond.Posture));
    }

    private WorldConsequenceApplyResult ApplyUpdateWant(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var entityId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Reject(consequence, "Want consequence did not include an entity id.");
        }

        var entity = EntityById(entityId);
        if (entity is null)
        {
            return Reject(consequence, $"Want target does not exist: {entityId}");
        }

        var hasText = HasAnyKey(payload, "text");
        var hasSalience = HasAnyKey(payload, "salience");
        var hasStatus = HasAnyKey(payload, "status");
        var hasStakes = HasAnyKey(payload, "stakes");
        var hasTags = HasAnyKey(payload, "tags");
        var addTags = NormalizeTags(ReadStringList(payload, "addTags").Concat(ReadStringList(payload, "add_tags")));
        var removeTags = NormalizeTags(ReadStringList(payload, "removeTags").Concat(ReadStringList(payload, "remove_tags")));
        if (!hasText && !hasSalience && !hasStatus && !hasStakes && !hasTags && addTags.Count == 0 && removeTags.Count == 0)
        {
            return Reject(consequence, "Want update did not include any changes.");
        }

        var operation = ReadString(payload, "operation") ?? "updateWant";
        var recordMemory = ReadBool(payload, "recordMemory") ?? ReadBool(payload, "record_memory") ?? false;
        var snapshot = recordMemory
            ? GameStateSnapshot.Capture(_state)
            : null;
        var existing = entity.TryGet<WantComponent>(out var want)
            ? want
            : null;
        var nextText = hasText
            ? FirstNonBlank(ReadString(payload, "text"), existing?.Text)
            : existing?.Text;
        if (string.IsNullOrWhiteSpace(nextText))
        {
            return Reject(consequence, "A new want needs text.");
        }

        var nextId = existing?.Id ?? $"want_{NormalizeToken(entity.Id.Value, "entity")}";
        var nextSalience = hasSalience
            ? Math.Clamp(ReadInt(payload, "salience") ?? existing?.Salience ?? consequence.Salience, 1, 5)
            : existing?.Salience ?? Math.Clamp(consequence.Salience, 1, 5);
        var nextStatus = hasStatus
            ? NormalizeToken(FirstNonBlank(ReadString(payload, "status"), existing?.Status, "active")!, "active")
            : existing?.Status ?? "active";
        var nextStakes = hasStakes
            ? FirstNonBlank(ReadString(payload, "stakes"), "")!
            : existing?.Stakes ?? "";
        var nextTags = hasTags
            ? NormalizeTags(ReadStringList(payload, "tags"))
            : NormalizeTags(existing?.Tags ?? Array.Empty<string>());
        nextTags = NormalizeTags(nextTags.Concat(addTags).Where(tag => !removeTags.Contains(tag, StringComparer.OrdinalIgnoreCase)));

        var updated = new WantComponent(
            nextId,
            nextText,
            nextSalience,
            nextStatus,
            nextStakes,
            nextTags);
        entity.Set(updated);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{entity.Name}'s want shifts: {updated.Text}")!;
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("hadWant", existing is not null),
                ("previousWantId", existing?.Id),
                ("previousText", existing?.Text),
                ("previousSalience", existing?.Salience),
                ("previousStatus", existing?.Status),
                ("previousStakes", existing?.Stakes),
                ("previousTags", existing?.Tags),
                ("wantId", updated.Id),
                ("text", updated.Text),
                ("salience", updated.Salience),
                ("status", updated.Status),
                ("stakes", updated.Stakes),
                ("tags", updated.Tags)));
        var messages = IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>();
        var deltas = new List<StateDelta> { delta };
        if (recordMemory)
        {
            var memoryText = FirstNonBlank(
                ReadString(payload, "memoryText"),
                ReadString(payload, "memory_text"),
                consequence.Reason,
                $"{entity.Name}'s want changed: {updated.Text}")!;
            var memory = Apply(WorldConsequence.RecordMemory(
                consequence.Source,
                entity.Id.Value,
                memoryText,
                FirstNonBlank(
                    ReadString(payload, "memoryProvenance"),
                    ReadString(payload, "memory_provenance"),
                    consequence.Source,
                    operation)!,
                Math.Clamp(
                    ReadInt(payload, "memorySalience")
                    ?? ReadInt(payload, "memory_salience")
                    ?? updated.Salience,
                    1,
                    5),
                (ReadBool(payload, "memoryShareable")
                    ?? ReadBool(payload, "memory_shareable")
                    ?? updated.Salience >= 4),
                WorldConsequenceVisibility.Hidden,
                consequence.SourceEntityId,
                consequence.Evidence,
                "Want update requested durable memory through the shared consequence lifecycle.",
                $"{operation}Memory",
                new Dictionary<string, object?>
                {
                    ["parentConsequenceType"] = consequence.Type,
                    ["parentOperation"] = operation,
                    ["wantId"] = updated.Id,
                    ["previousWantStatus"] = existing?.Status,
                    ["wantStatus"] = updated.Status,
                    ["playerVisible"] = false,
                }));
            deltas.AddRange(memory.Deltas);
            if (!memory.Applied)
            {
                snapshot!.Restore(_state);
                var skipped = new StateDelta(
                    "updateWantSkipped",
                    entity.Id.Value,
                    $"Want update rolled back: {memory.Error ?? "memory_record_rejected"}.",
                    Details(
                        consequence,
                        ("operation", operation),
                        ("failure", memory.Error ?? "memory_record_rejected"),
                        ("rolledBackDeltaCount", deltas.Count),
                        ("wantId", updated.Id),
                        ("previousWantId", existing?.Id),
                        ("previousStatus", existing?.Status),
                        ("attemptedStatus", updated.Status),
                        ("auditOnly", true),
                        ("playerVisible", false)));
                return new WorldConsequenceApplyResult(
                    false,
                    entity.Id.Value,
                    memory.Error ?? "memory_record_rejected",
                    Array.Empty<string>(),
                    memory.Deltas.Concat(new[] { skipped }).ToArray(),
                    Details(
                        consequence,
                        ("error", memory.Error ?? "memory_record_rejected"),
                        ("operation", operation),
                        ("rolledBackDeltaCount", deltas.Count)));
            }
        }

        return new WorldConsequenceApplyResult(
            true,
            entity.Id.Value,
            null,
            messages,
            deltas,
            Details(
                consequence,
                ("hadWant", existing is not null),
                ("previousWantId", existing?.Id),
                ("previousText", existing?.Text),
                ("previousSalience", existing?.Salience),
                ("previousStatus", existing?.Status),
                ("previousStakes", existing?.Stakes),
                ("previousTags", existing?.Tags),
                ("wantId", updated.Id),
                ("text", updated.Text),
                ("salience", updated.Salience),
                ("status", updated.Status),
                ("stakes", updated.Stakes),
                ("tags", updated.Tags)));
    }

    private WorldConsequenceApplyResult ApplyAddMerchantStock(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Merchant stock consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null || !merchant.TryGet<MerchantComponent>(out var stock))
        {
            return Reject(consequence, "Merchant stock consequence target is not a merchant.");
        }

        var itemName = ReadString(payload, "itemName")?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return Reject(consequence, "Merchant stock consequence did not include an item name.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        stock.Wares.TryGetValue(itemName, out var current);
        stock.Wares[itemName] = current + quantity;

        var operation = ReadString(payload, "operation") ?? "addMerchantStock";
        var summary = $"{merchant.Name}'s stock now includes {itemName}.";
        var delta = new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(consequence, ("item", itemName), ("quantity", stock.Wares[itemName])));
        return Applied(
            consequence,
            merchant.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("item", itemName),
            ("quantity", stock.Wares[itemName]));
    }

    private WorldConsequenceApplyResult ApplyOfferTrade(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Trade offer consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null)
        {
            return Reject(consequence, "Trade offer consequence target does not exist.");
        }

        if (!merchant.TryGet<MerchantComponent>(out var stock))
        {
            stock = new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), ReadInt(payload, "gold") ?? 30);
            merchant.Set(stock);
        }

        var itemName = ReadString(payload, "itemName")?.Trim();
        if (!string.IsNullOrWhiteSpace(itemName))
        {
            var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
            stock.Wares.TryGetValue(itemName, out var current);
            stock.Wares[itemName] = current + quantity;
        }

        EnsureInteractableVerbs(merchant, "wares", "buy", "sell", "talk");
        var operation = ReadString(payload, "operation") ?? "offerTrade";
        var summary = string.IsNullOrWhiteSpace(itemName)
            ? $"{merchant.Name} is ready to trade."
            : $"{merchant.Name} offers trade in {itemName}.";
        var delta = new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(consequence, ("item", itemName), ("quantity", string.IsNullOrWhiteSpace(itemName) ? 0 : stock.Wares[itemName])));
        return Applied(consequence, merchant.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("item", itemName));
    }

    private WorldConsequenceApplyResult ApplyExecuteTrade(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = FirstNonBlank(ReadString(payload, "merchantId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Trade consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null || !merchant.TryGet<MerchantComponent>(out var stock))
        {
            return Reject(consequence, "Trade consequence target is not a merchant.");
        }

        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "buyerEntityId"),
            ReadString(payload, "sellerEntityId"),
            consequence.SourceEntityId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Trade consequence did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Trade consequence actor does not exist.");
        }

        var mode = NormalizeToken(FirstNonBlank(ReadString(payload, "mode"), "buy")!, "buy");
        var itemName = FirstNonBlank(ReadString(payload, "itemName"), ReadString(payload, "item"), ReadString(payload, "wareKey"));
        var requestedWareKey = FirstNonBlank(ReadString(payload, "wareKey"), itemName);
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(requestedWareKey))
        {
            return Reject(consequence, "Trade consequence did not include an item.");
        }

        var price = Math.Max(0, ReadInt(payload, "price") ?? 1);
        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        var totalPrice = price * quantity;
        var inventory = actor.TryGet<InventoryComponent>(out var existingInventory)
            ? existingInventory
            : InventoryComponent.Empty();
        if (!actor.Has<InventoryComponent>())
        {
            actor.Set(inventory);
        }

        StateDelta delta;
        string summary;
        switch (mode)
        {
            case "buy":
            case "purchase":
                {
                    var wareKey = FindWareKey(stock, requestedWareKey);
                    if (wareKey is null || !stock.Wares.TryGetValue(wareKey, out var available) || available < quantity)
                    {
                        return Reject(consequence, $"{merchant.Name} is not selling {itemName}.");
                    }

                    inventory.Items.TryGetValue("gold", out var gold);
                    if (gold < totalPrice)
                    {
                        return Reject(consequence, $"Trade actor needs {totalPrice} gold for {itemName}.");
                    }

                    AdjustInventory(actor, inventory, "gold", -totalPrice);
                    AdjustInventory(actor, inventory, itemName, quantity);
                    stock.Wares[wareKey] = available - quantity;
                    merchant.Set(stock with { Gold = stock.Gold + totalPrice });
                    summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} buys {itemName} from {merchant.Name} for {totalPrice} gold.")!;
                    delta = TradeDelta(consequence, payload, merchant, actor, mode, itemName, wareKey, price, quantity, totalPrice, stock.Gold, summary);
                    break;
                }

            case "sell":
                {
                    var itemKey = FindInventoryKey(inventory, itemName);
                    if (itemKey is null || itemKey.Equals("gold", StringComparison.OrdinalIgnoreCase))
                    {
                        return Reject(consequence, $"Trade actor is not carrying {itemName}.");
                    }

                    inventory.Items.TryGetValue(itemKey, out var carrying);
                    if (carrying < quantity)
                    {
                        return Reject(consequence, $"Trade actor is not carrying enough {itemName}.");
                    }

                    if (stock.Gold < totalPrice)
                    {
                        return Reject(consequence, $"{merchant.Name} cannot afford {itemName}.");
                    }

                    // Resolve against an existing ware the same way buy does, so selling an item
                    // under a slightly different name or case than the merchant's stock key does
                    // not fragment one ware into two separate, unreconciled stock entries. Falls
                    // back to the requested key only when the merchant never carried this ware.
                    var wareKey = FindWareKey(stock, requestedWareKey) ?? requestedWareKey;
                    AdjustInventory(actor, inventory, itemKey, -quantity);
                    AdjustInventory(actor, inventory, "gold", totalPrice);
                    stock.Wares.TryGetValue(wareKey, out var current);
                    stock.Wares[wareKey] = current + quantity;
                    merchant.Set(stock with { Gold = stock.Gold - totalPrice });
                    summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} sells {itemName} to {merchant.Name} for {totalPrice} gold.")!;
                    delta = TradeDelta(consequence, payload, merchant, actor, mode, itemName, wareKey, price, quantity, totalPrice, stock.Gold, summary);
                    break;
                }

            default:
                return Reject(consequence, $"Unknown trade mode: {mode}");
        }

        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(
            consequence,
            merchant.Id.Value,
            messages,
            delta,
            ("mode", mode),
            ("item", itemName),
            ("price", price),
            ("quantity", quantity),
            ("totalPrice", totalPrice),
            ("merchantGold", stock.Gold));
    }

    private StateDelta TradeDelta(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity merchant,
        Entity actor,
        string mode,
        string itemName,
        string wareKey,
        int price,
        int quantity,
        int totalPrice,
        int merchantGold,
        string summary)
    {
        var operation = ReadString(payload, "operation") ?? "executeTrade";
        return new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(
                consequence,
                ("merchantId", merchant.Id.Value),
                ("actorEntityId", actor.Id.Value),
                ("mode", mode),
                ("item", itemName),
                ("wareKey", wareKey),
                ("price", price),
                ("quantity", quantity),
                ("totalPrice", totalPrice),
                ("merchantGold", merchantGold)));
    }

    private WorldConsequenceApplyResult ApplyOfferService(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var providerId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Reject(consequence, "Service offer consequence did not include a provider id.");
        }

        var provider = EntityById(providerId);
        if (provider is null)
        {
            return Reject(consequence, "Service offer consequence target does not exist.");
        }

        var serviceId = NormalizeToken(ReadString(payload, "serviceId") ?? ReadString(payload, "name") ?? "service", "service");
        var name = FirstNonBlank(ReadString(payload, "name"), serviceId) ?? serviceId;
        var service = new ServiceOffer(
            serviceId,
            name,
            ReadString(payload, "description") ?? consequence.Evidence ?? name,
            NormalizeToken(ReadString(payload, "effectKind") ?? "record_memory", "record_memory"),
            Math.Max(0, ReadInt(payload, "goldCost") ?? 0),
            FirstNonBlank(ReadString(payload, "itemCost")),
            FirstNonBlank(ReadString(payload, "targetHint")),
            ReadBool(payload, "revealed") ?? true,
            ReadStringList(payload, "tags"),
            FirstNonBlank(ReadString(payload, "wantStatusOnComplete"), ReadString(payload, "want_status_on_complete")),
            FirstNonBlank(ReadString(payload, "wantStakesOnComplete"), ReadString(payload, "want_stakes_on_complete")),
            ReadStringList(payload, "wantAddTagsOnComplete").Concat(ReadStringList(payload, "want_add_tags_on_complete")).ToArray(),
            ReadStringList(payload, "wantRemoveTagsOnComplete").Concat(ReadStringList(payload, "want_remove_tags_on_complete")).ToArray());
        var services = provider.TryGet<ServiceComponent>(out var existing)
            ? existing.Offers.ToList()
            : new List<ServiceOffer>();
        var existingIndex = services.FindIndex(offer => offer.Id.Equals(service.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            services[existingIndex] = service;
        }
        else
        {
            services.Add(service);
        }

        provider.Set(new ServiceComponent(services.OrderBy(offer => offer.Id, StringComparer.OrdinalIgnoreCase).ToArray()));
        EnsureInteractableVerbs(provider, "services", "request_service", "talk");
        var operation = ReadString(payload, "operation") ?? "offerService";
        var summary = $"{provider.Name} can offer {service.Name}.";
        var delta = new StateDelta(
            operation,
            provider.Id.Value,
            summary,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("targetHint", service.TargetHint),
                ("wantStatusOnComplete", service.WantStatusOnComplete),
                ("wantStakesOnComplete", service.WantStakesOnComplete),
                ("wantAddTagsOnComplete", service.WantAddTagsOnComplete?.ToArray()),
                ("wantRemoveTagsOnComplete", service.WantRemoveTagsOnComplete?.ToArray())));
        return Applied(consequence, provider.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("serviceId", service.Id));
    }

    private WorldConsequenceApplyResult ApplyRequestService(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var providerId = FirstNonBlank(
            ReadString(payload, "providerId"),
            ReadString(payload, "provider_id"),
            ReadString(payload, "serviceProviderId"),
            ReadString(payload, "service_provider_id"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Reject(consequence, "Service request consequence did not include a provider id.");
        }

        var provider = EntityById(providerId);
        if (provider is null || !provider.TryGet<ServiceComponent>(out var services))
        {
            return Reject(consequence, "Service request target is not a service provider.");
        }

        var serviceText = FirstNonBlank(
            ReadString(payload, "service"),
            ReadString(payload, "serviceId"),
            ReadString(payload, "service_id"),
            ReadString(payload, "serviceName"),
            ReadString(payload, "service_name"),
            ReadString(payload, "name"));
        if (string.IsNullOrWhiteSpace(serviceText))
        {
            return Reject(consequence, "Service request consequence did not name a service.");
        }

        var allowHidden = ReadBool(payload, "allowHidden") ?? ReadBool(payload, "allow_hidden") ?? false;
        var service = FindServiceOffer(allowHidden ? services.Offers : services.Offers.Where(offer => offer.Revealed), serviceText);
        if (service is null)
        {
            return Reject(consequence, $"{provider.Name} is not offering {serviceText}.");
        }

        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "actor_entity_id"),
            ReadString(payload, "requesterEntityId"),
            ReadString(payload, "requester_entity_id"),
            ReadString(payload, "actorId"),
            ReadString(payload, "actor_id"),
            consequence.SourceEntityId,
            _state.ControlledEntityId.Value);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Service request consequence did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Service request actor does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost)
            && actor.TryGet<InventoryComponent>(out var inventory)
            && FindInventoryKey(inventory, service.ItemCost) is { } itemKey
            && inventory.TreasuredItems.Contains(itemKey))
        {
            return Reject(consequence, $"{itemKey} is protected; unprotect it before offering it.");
        }

        var transaction = GameTransaction.Begin(_state);
        var effect = ApplyServiceEffect(provider, service, actor);
        if (!effect.Applied)
        {
            return RollBackServiceRequestTransaction(
                transaction,
                consequence,
                provider,
                service,
                effect.Deltas,
                effect.Error ?? $"{provider.Name} cannot complete that service here.");
        }

        var payment = PayServiceCost(consequence, actor, provider, service);
        if (!payment.Applied)
        {
            return RollBackServiceRequestTransaction(
                transaction,
                consequence,
                provider,
                service,
                payment.Deltas,
                payment.Error ?? $"{provider.Name} cannot complete that payment.");
        }

        var wantCompletion = ApplyServiceWantCompletion(provider, service);
        if (ServiceHasWantCompletion(service) && !wantCompletion.Applied)
        {
            return RollBackServiceRequestTransaction(
                transaction,
                consequence,
                provider,
                service,
                wantCompletion.Deltas,
                wantCompletion.Error ?? "service_want_completion_failed");
        }

        var operation = ReadString(payload, "operation") ?? "requestService";
        var serviceMessage = FirstNonBlank(ReadString(payload, "message"), $"{provider.Name} provides {service.Name}.")!;
        var messages = new List<string>();
        messages.AddRange(effect.Messages);
        messages.AddRange(payment.Messages);
        messages.AddRange(wantCompletion.Messages);
        if (AddMessageIfAllowed(consequence, payload, serviceMessage, defaultEmitMessage: false))
        {
            messages.Add(serviceMessage);
        }

        var delta = new StateDelta(
            operation,
            provider.Id.Value,
            serviceMessage,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("actorEntityId", actor.Id.Value)));
        transaction.Commit();
        var deltas = new List<StateDelta> { delta };
        deltas.AddRange(payment.Deltas);
        deltas.AddRange(effect.Deltas);
        deltas.AddRange(wantCompletion.Deltas);
        return new WorldConsequenceApplyResult(
            true,
            provider.Id.Value,
            null,
            messages,
            deltas,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("actorEntityId", actor.Id.Value)));
    }

    private WorldConsequenceApplyResult RollBackServiceRequestTransaction(
        GameTransaction transaction,
        WorldConsequence consequence,
        Entity provider,
        ServiceOffer service,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        var skipped = ServiceRequestSkipped(consequence, provider, service, failure, failedDeltas);
        return new WorldConsequenceApplyResult(
            false,
            provider.Id.Value,
            failure,
            Array.Empty<string>(),
            failedDeltas.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("failure", failure),
                ("error", failure)));
    }

    private StateDelta ServiceRequestSkipped(
        WorldConsequence consequence,
        Entity provider,
        ServiceOffer service,
        string failure,
        IReadOnlyList<StateDelta> failedDeltas) =>
        new(
            "serviceRequestSkipped",
            provider.Id.Value,
            $"Service request rolled back: {failure}.",
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("failure", failure),
                ("rejectedCount", failedDeltas.Count(delta =>
                    delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))),
                ("auditOnly", true),
                ("playerVisible", false)));

    private WorldConsequenceApplyResult ApplyServiceEffect(Entity provider, ServiceOffer service, Entity requester)
    {
        var effect = NormalizeServiceEffect(service.EffectKind);
        if (effect is "open_or_unlock" or "unlock_or_open" or "ward_breaking")
        {
            var door = ResolveServiceDoor(provider, service);
            if (door is null)
            {
                return WorldConsequenceApplyResult.Empty("There is no nearby door for that service.");
            }

            return Apply(WorldConsequence.OpenOrUnlock(
                "service",
                door.Id.Value,
                actorId: provider.Id.Value,
                unlock: true,
                open: true,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: provider.Id.Value,
                evidence: service.Description,
                operation: "serviceOpenOrUnlock",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["beneficiaryId"] = requester.Id.Value,
                }));
        }

        if (effect is "create_route" or "escape_route" or "reveal_route")
        {
            return Apply(WorldConsequence.CreateRoute(
                "service",
                provider.Id.Value,
                string.IsNullOrWhiteSpace(service.TargetHint) ? service.Name : service.TargetHint,
                service.Description,
                effect,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: provider.Id.Value,
                evidence: service.Description,
                operation: "serviceCreateRoute",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                }));
        }

        return Apply(WorldConsequence.RecordMemory(
            "service",
            provider.Id.Value,
            $"{provider.Name} provided {service.Name}: {service.Description}",
            "service",
            2,
            shareable: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: provider.Id.Value,
            operation: "serviceMemory",
            details: new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
            }));
    }

    private ServicePaymentResult PayServiceCost(
        WorldConsequence consequence,
        Entity actor,
        Entity provider,
        ServiceOffer service)
    {
        var deltas = new List<StateDelta>();
        var messages = new List<string>();
        if (service.GoldCost > 0)
        {
            var applied = Apply(WorldConsequence.ModifyInventory(
                "service",
                actor.Id.Value,
                "gold",
                op: "consume",
                amount: service.GoldCost,
                sourceEntityId: actor.Id.Value,
                evidence: service.Description,
                operation: "serviceCost",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["providerId"] = provider.Id.Value,
                    ["costKind"] = "gold",
                    ["parentConsequenceType"] = consequence.Type,
                }));
            deltas.AddRange(applied.Deltas);
            messages.AddRange(applied.Messages);
            if (!applied.Applied)
            {
                return new ServicePaymentResult(false, applied.Error, deltas, messages);
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost))
        {
            var applied = Apply(WorldConsequence.ModifyInventory(
                "service",
                actor.Id.Value,
                service.ItemCost,
                op: "consume",
                amount: 1,
                sourceEntityId: actor.Id.Value,
                evidence: service.Description,
                operation: "serviceCost",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["providerId"] = provider.Id.Value,
                    ["costKind"] = "item",
                    ["parentConsequenceType"] = consequence.Type,
                }));
            deltas.AddRange(applied.Deltas);
            messages.AddRange(applied.Messages);
            if (!applied.Applied)
            {
                return new ServicePaymentResult(false, applied.Error, deltas, messages);
            }
        }

        return new ServicePaymentResult(true, null, deltas, messages);
    }

    private WorldConsequenceApplyResult ApplyServiceWantCompletion(Entity provider, ServiceOffer service)
    {
        if (!ServiceHasWantCompletion(service))
        {
            return WorldConsequenceApplyResult.Empty();
        }

        if (!provider.Has<WantComponent>())
        {
            return ServiceWantSkipped(provider, service, "provider_has_no_want");
        }

        var applied = Apply(WorldConsequence.UpdateWant(
            "service",
            provider.Id.Value,
            status: service.WantStatusOnComplete,
            stakes: service.WantStakesOnComplete,
            addTags: service.WantAddTagsOnComplete,
            removeTags: service.WantRemoveTagsOnComplete,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: provider.Id.Value,
            evidence: $"{provider.Name} provided {service.Name}.",
            reason: "Completing this service updates the provider's active want through the shared consequence lifecycle.",
            operation: "serviceWantCompletion",
            details: new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
                ["effectKind"] = service.EffectKind,
                ["playerVisible"] = false,
            },
            recordMemory: true,
            memoryText: $"{provider.Name} provided {service.Name}, changing their active want.",
            memoryProvenance: $"service:{service.Id}",
            memoryShareable: false));
        return applied.Applied
            ? applied
            : applied with
            {
                Deltas = applied.Deltas
                    .Concat(ServiceWantSkipped(provider, service, applied.Error ?? "want_update_rejected").Deltas)
                    .ToArray(),
            };
    }

    private static bool ServiceHasWantCompletion(ServiceOffer service) =>
        !string.IsNullOrWhiteSpace(service.WantStatusOnComplete)
        || !string.IsNullOrWhiteSpace(service.WantStakesOnComplete)
        || (service.WantAddTagsOnComplete?.Count ?? 0) > 0
        || (service.WantRemoveTagsOnComplete?.Count ?? 0) > 0;

    private WorldConsequenceApplyResult ServiceWantSkipped(
        Entity provider,
        ServiceOffer service,
        string reason)
    {
        var delta = new StateDelta(
            "serviceWantSkipped",
            provider.Id.Value,
            "Service want completion skipped.",
            new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
                ["reason"] = reason,
                ["effectKind"] = service.EffectKind,
                ["playerVisible"] = false,
                ["auditOnly"] = true,
            });
        return new WorldConsequenceApplyResult(
            false,
            provider.Id.Value,
            reason,
            Array.Empty<string>(),
            new[] { delta },
            new Dictionary<string, object?>());
    }

    private Entity? ResolveServiceDoor(Entity provider, ServiceOffer service)
    {
        var target = FirstNonBlank(service.TargetHint, service.Name);
        return ResolveNearbyEntity(provider, target, entity => entity.Has<DoorComponent>(), range: 2)
            ?? ResolveNearbyEntity(provider, null, entity => entity.Has<DoorComponent>(), range: 2);
    }

    private Entity? ResolveNearbyEntity(
        Entity origin,
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var candidates = NearbyEntities(origin, predicate, range);
        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            return candidates.FirstOrDefault(entity =>
                entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || (entity.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))));
        }

        return candidates.FirstOrDefault();
    }

    private IReadOnlyList<Entity> NearbyEntities(
        Entity origin,
        Func<Entity, bool> predicate,
        int range)
    {
        if (!origin.TryGet<PositionComponent>(out var originPosition))
        {
            return Array.Empty<Entity>();
        }

        return _state.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && Distance(originPosition.Position, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? Distance(originPosition.Position, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ServiceOffer? FindServiceOffer(IEnumerable<ServiceOffer> services, string serviceText)
    {
        var normalized = NormalizeToken(serviceText, "");
        return services
            .OrderBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(service =>
                service.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || service.Id.Equals(serviceText.Trim(), StringComparison.OrdinalIgnoreCase)
                || service.Name.Equals(serviceText.Trim(), StringComparison.OrdinalIgnoreCase)
                || service.Name.Contains(serviceText.Trim(), StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(NormalizeToken(service.Name, service.Name), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeServiceEffect(string effect)
    {
        var normalized = string.Join(
            "_",
            effect.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "record_memory" : normalized;
    }

    private sealed record ServicePaymentResult(
        bool Applied,
        string? Error,
        IReadOnlyList<StateDelta> Deltas,
        IReadOnlyList<string> Messages);

    private WorldConsequenceApplyResult ApplyOpenOrUnlock(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var doorId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(doorId))
        {
            return Reject(consequence, "Open/unlock consequence did not include a door id.");
        }

        var door = EntityById(doorId);
        if (door is null || !door.TryGet<DoorComponent>(out var doorComponent))
        {
            return Reject(consequence, "Open/unlock consequence target is not a door.");
        }

        var actorId = ReadString(payload, "actorId") ?? consequence.SourceEntityId;
        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        // Wild magic may work a lock it can see across the room; social/engine paths stay
        // adjacency-bound so dialogue proposals cannot teleport-open distant doors.
        var reach = consequence.Source.Equals("wild_magic", StringComparison.OrdinalIgnoreCase) ? 12 : 2;
        if (actor is not null && !CanReach(actor, door, range: reach))
        {
            return Reject(consequence, $"{actor.Name} cannot reach {door.Name}.");
        }

        var unlock = ReadBool(payload, "unlock") ?? true;
        var open = ReadBool(payload, "open") ?? true;
        if (!unlock && open && !string.IsNullOrWhiteSpace(doorComponent.KeyId))
        {
            return Reject(consequence, $"{door.Name} is locked.");
        }

        var wasLocked = !string.IsNullOrWhiteSpace(doorComponent.KeyId);
        var wasOpen = doorComponent.IsOpen;
        var releaseCaptives = ReadBool(payload, "releaseCaptives") ?? ReadBool(payload, "release_captives") ?? true;
        var shouldTryCaptiveRelease = releaseCaptives
            && open
            && !wasOpen
            && DoorCanReleaseCaptives(door);
        var snapshot = shouldTryCaptiveRelease
            ? GameStateSnapshot.Capture(_state)
            : null;
        var nextDoor = doorComponent;
        if (unlock)
        {
            nextDoor = nextDoor with { KeyId = null };
        }

        if (open)
        {
            nextDoor = nextDoor with { IsOpen = true };
        }

        door.Set(nextDoor);
        if (nextDoor.IsOpen && door.TryGet<PhysicalComponent>(out var physical))
        {
            door.Set(physical with { BlocksMovement = false, BlocksSight = false });
        }

        if (nextDoor.IsOpen && door.TryGet<RenderableComponent>(out var renderable))
        {
            door.Set(renderable with { Glyph = '/', Palette = "open" });
        }

        var actorName = actor is null ? "Something" : actor.Name;
        var defaultSummary = nextDoor.IsOpen && !wasOpen
            ? $"{actorName} opens {door.Name}."
            : wasLocked && unlock
                ? $"{actorName} unlocks {door.Name}."
                : $"{door.Name} is already open.";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultSummary)!;
        var operation = ReadString(payload, "operation") ?? "openOrUnlock";
        var delta = new StateDelta(
            operation,
            door.Id.Value,
            summary,
            Details(
                consequence,
                ("actorId", actor?.Id.Value),
                ("unlocked", wasLocked && unlock),
                ("open", nextDoor.IsOpen)));
        var messages = (IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>()).ToList();
        var deltas = new List<StateDelta> { delta };
        if (shouldTryCaptiveRelease && nextDoor.IsOpen && !wasOpen)
        {
            var captiveRelease = ApplyDoorCaptiveRelease(consequence, payload, door, actor, operation);
            if (!captiveRelease.Applied
                && (!string.IsNullOrWhiteSpace(captiveRelease.Error) || captiveRelease.Deltas.Count > 0))
            {
                return RollBackOpenOrUnlock(
                    consequence,
                    snapshot!,
                    door.Id.Value,
                    operation,
                    captiveRelease.Deltas,
                    captiveRelease.Messages,
                    captiveRelease.Error ?? "captive_release_rejected");
            }

            messages.AddRange(captiveRelease.Messages);
            deltas.AddRange(captiveRelease.Deltas);
        }

        return new WorldConsequenceApplyResult(
            true,
            door.Id.Value,
            null,
            messages,
            deltas,
            Details(consequence, ("open", nextDoor.IsOpen), ("unlocked", wasLocked && unlock)));
    }

    private WorldConsequenceApplyResult RollBackOpenOrUnlock(
        WorldConsequence consequence,
        GameStateSnapshot snapshot,
        string doorId,
        string operation,
        IReadOnlyList<StateDelta> failedDeltas,
        IReadOnlyList<string> failedMessages,
        string failure)
    {
        snapshot.Restore(_state);
        var skipped = new StateDelta(
            "openOrUnlockSkipped",
            doorId,
            $"Open/unlock rolled back: {failure}.",
            Details(
                consequence,
                ("operation", operation),
                ("failure", failure),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count),
                ("rejectedCount", failedDeltas.Count(delta =>
                    delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            doorId,
            failure,
            Array.Empty<string>(),
            failedDeltas.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("error", failure),
                ("operation", operation),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count)));
    }

    private WorldConsequenceApplyResult ApplyDoorCaptiveRelease(
        WorldConsequence parent,
        IReadOnlyDictionary<string, object?> payload,
        Entity door,
        Entity? actor,
        string parentOperation)
    {
        if (!DoorCanReleaseCaptives(door)
            || !door.TryGet<PositionComponent>(out var doorPosition))
        {
            return WorldConsequenceApplyResult.Empty();
        }

        var captive = _state.Entities.Values
            .Where(IsUnreleasedCaptive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && Distance(position.Position, doorPosition.Position) <= 2)
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (captive is null)
        {
            return WorldConsequenceApplyResult.Empty();
        }

        var beneficiaryId = FirstNonBlank(
            ReadString(payload, "beneficiaryId"),
            ReadString(payload, "beneficiary_id"),
            ReadString(payload, "liberatorId"),
            ReadString(payload, "liberator_id"),
            actor is not null && actor.Has<ActorComponent>() ? actor.Id.Value : null,
            _state.ControlledEntityId.Value);
        var beneficiary = string.IsNullOrWhiteSpace(beneficiaryId) ? null : EntityById(beneficiaryId);
        if (beneficiary is null)
        {
            return Reject(parent, $"Captive-release beneficiary does not exist: {beneficiaryId}");
        }

        return Apply(WorldConsequence.FreeCaptive(
            parent.Source,
            captive.Id.Value,
            beneficiary.Id.Value,
            anchorEntityId: door.Id.Value,
            deedTags: FreeCaptiveDeedTags(captive),
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: beneficiary.Id.Value,
            evidence: FirstNonBlank(parent.Evidence, $"{captive.Name} was released by opening {door.Name}."),
            reason: "Opening a captive-door requested the shared free_captive consequence.",
            operation: "freeCaptive",
            message: beneficiary.Id == _state.ControlledEntityId
                ? $"{captive.Name} is free enough to choose you, for now."
                : $"{captive.Name} is free enough to choose {beneficiary.Name}, for now.",
            details: new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = parent.Type,
                ["parentOperation"] = parentOperation,
                ["doorId"] = door.Id.Value,
                ["doorName"] = door.Name,
            }));
    }

    private static bool DoorCanReleaseCaptives(Entity door)
    {
        if (door.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => tag.Equals("cell", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("jail", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("cage", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("prison", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("captive_door", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return door.Name.Contains("cell", StringComparison.OrdinalIgnoreCase)
            || door.Name.Contains("jail", StringComparison.OrdinalIgnoreCase)
            || door.Name.Contains("cage", StringComparison.OrdinalIgnoreCase)
            || door.Name.Contains("prison", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnreleasedCaptive(Entity entity)
    {
        if (!entity.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            return false;
        }

        var taggedCaptive = entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => tag.Equals("prisoner", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("captive", StringComparison.OrdinalIgnoreCase));
        var captivePolicy = entity.TryGet<AiComponent>(out var ai)
            && ai.PolicyId.Equals("captive", StringComparison.OrdinalIgnoreCase);
        if (!taggedCaptive && !captivePolicy)
        {
            return false;
        }

        return !actor.Faction.Equals("player", StringComparison.OrdinalIgnoreCase)
            && (!entity.TryGet<AiComponent>(out var updatedAi)
                || !updatedAi.PolicyId.Equals("follower", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> FreeCaptiveDeedTags(Entity captive)
    {
        var tags = new List<string> { "mercy", "anti_empire", "rescued" };
        if (captive.TryGet<FactionComponent>(out var faction))
        {
            tags.Add(faction.FactionId);
        }

        if (captive.TryGet<TagsComponent>(out var entityTags))
        {
            tags.AddRange(entityTags.Tags.Where(tag =>
                tag.Equals("hollowmere", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("folk_magic", StringComparison.OrdinalIgnoreCase)));
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private WorldConsequenceApplyResult ApplyFreeCaptive(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var captiveId = FirstNonBlank(
            ReadString(payload, "captiveId"),
            ReadString(payload, "captive_id"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(captiveId))
        {
            return Reject(consequence, "Free-captive consequence did not include a captive entity id.");
        }

        var captive = EntityById(captiveId);
        if (captive is null)
        {
            return Reject(consequence, $"Captive does not exist: {captiveId}");
        }

        if (!captive.TryGet<ActorComponent>(out var captiveActor) || !captiveActor.Alive)
        {
            return Reject(consequence, "Free-captive target is not a living actor.");
        }

        var liberatorId = FirstNonBlank(
            ReadString(payload, "liberatorId"),
            ReadString(payload, "liberator_id"),
            ReadString(payload, "actorId"),
            ReadString(payload, "actor_id"),
            consequence.SourceEntityId,
            _state.ControlledEntityId.Value);
        var liberator = string.IsNullOrWhiteSpace(liberatorId) ? null : EntityById(liberatorId);
        if (liberator is null)
        {
            return Reject(consequence, $"Liberator does not exist: {liberatorId}");
        }

        var shouldRecordDeed = ReadBool(payload, "recordDeed") ?? ReadBool(payload, "record_deed") ?? true;
        if (shouldRecordDeed && _engine is null)
        {
            return Reject(consequence, "Free-captive deed recording requires engine services.");
        }

        var snapshot = GameStateSnapshot.Capture(_state);
        var deltas = new List<StateDelta>();
        var messages = new List<string>();
        WorldConsequenceApplyResult ApplyChild(WorldConsequence child)
        {
            var applied = Apply(child);
            deltas.AddRange(applied.Deltas);
            messages.AddRange(applied.Messages);
            return applied;
        }

        var source = FirstNonBlank(ReadString(payload, "childSource"), ReadString(payload, "child_source"), consequence.Source)!;
        var evidence = FirstNonBlank(consequence.Evidence, $"{captive.Name} was released.");
        var faction = NormalizeToken(
            FirstNonBlank(ReadString(payload, "faction"), ReadString(payload, "factionId"), "player")!,
            "player");
        var roles = NormalizeTags(ReadStringList(payload, "roles"));
        if (roles.Count == 0)
        {
            roles = new[] { "rescued", "follower" };
        }

        var preserveMembership = ReadBool(payload, "preserveMembership")
            ?? ReadBool(payload, "preserve_membership")
            ?? true;
        var factionApplied = ApplyChild(WorldConsequence.ChangeFaction(
            source,
            captive.Id.Value,
            faction,
            roles,
            preserveMembership,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: liberator.Id.Value,
            evidence: evidence,
            reason: "A captive release changed faction allegiance through the shared consequence lifecycle.",
            operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Faction",
            details: new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["playerVisible"] = false,
            }));
        if (!factionApplied.Applied)
        {
            return RollBackFreeCaptive(consequence, snapshot, deltas, messages, factionApplied.Error ?? "faction_update_rejected");
        }

        var aiPolicy = FirstNonBlank(ReadString(payload, "aiPolicyId"), ReadString(payload, "ai_policy_id"), "follower");
        var controlApplied = ApplyChild(WorldConsequence.UpdateControl(
            source,
            captive.Id.Value,
            "ai",
            aiPolicy,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: liberator.Id.Value,
            evidence: evidence,
            reason: "A captive release changed controller policy through the shared consequence lifecycle.",
            operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Control",
            details: new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["playerVisible"] = false,
            }));
        if (!controlApplied.Applied)
        {
            return RollBackFreeCaptive(consequence, snapshot, deltas, messages, controlApplied.Error ?? "control_update_rejected");
        }

        var bondMaxDelta = Math.Max(1, ReadInt(payload, "bondMaxDelta") ?? ReadInt(payload, "bond_max_delta") ?? 5);
        var bondApplied = ApplyChild(WorldConsequence.UpdateBond(
            source,
            captive.Id.Value,
            SoulIdFor(liberator),
            ReadInt(payload, "loyaltyDelta") ?? ReadInt(payload, "loyalty_delta") ?? 4,
            ReadInt(payload, "fearDelta") ?? ReadInt(payload, "fear_delta") ?? 0,
            ReadInt(payload, "admirationDelta") ?? ReadInt(payload, "admiration_delta") ?? 2,
            ReadInt(payload, "resentmentDelta") ?? ReadInt(payload, "resentment_delta") ?? 0,
            FirstNonBlank(ReadString(payload, "posture"), "follower"),
            WorldConsequenceVisibility.Hidden,
            liberator.Id.Value,
            evidence,
            "A captive release changed the social bond through the shared consequence lifecycle.",
            $"{ReadString(payload, "operation") ?? "freeCaptive"}Bond",
            bondMaxDelta,
            new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = consequence.Type,
                ["playerVisible"] = false,
            }));
        if (!bondApplied.Applied)
        {
            return RollBackFreeCaptive(consequence, snapshot, deltas, messages, bondApplied.Error ?? "bond_update_rejected");
        }

        var satisfyWant = ReadBool(payload, "satisfyWant") ?? ReadBool(payload, "satisfy_want") ?? true;
        if (satisfyWant && captive.Has<WantComponent>())
        {
            var wantAddTags = NormalizeTags(ReadStringList(payload, "wantAddTags")
                .Concat(ReadStringList(payload, "want_add_tags")));
            if (wantAddTags.Count == 0)
            {
                wantAddTags = new[] { "rescued", "satisfied_by_player" };
            }

            var wantRemoveTags = NormalizeTags(ReadStringList(payload, "wantRemoveTags")
                .Concat(ReadStringList(payload, "want_remove_tags")));
            if (wantRemoveTags.Count == 0)
            {
                wantRemoveTags = new[] { "escape" };
            }

            var wantApplied = ApplyChild(WorldConsequence.UpdateWant(
                source,
                captive.Id.Value,
                status: FirstNonBlank(ReadString(payload, "wantStatus"), ReadString(payload, "want_status"), "satisfied"),
                stakes: FirstNonBlank(
                    ReadString(payload, "wantStakes"),
                    ReadString(payload, "want_stakes"),
                    "The immediate escape happened; future choices can shift toward trust, danger, or a new refuge."),
                addTags: wantAddTags,
                removeTags: wantRemoveTags,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: liberator.Id.Value,
                evidence: evidence,
                reason: "Releasing the captive satisfied or redirected an active want.",
                operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Want",
                details: new Dictionary<string, object?>
                {
                    ["parentConsequenceType"] = consequence.Type,
                    ["outcome"] = "captive_released",
                    ["playerVisible"] = false,
                },
                recordMemory: true,
                memoryText: FirstNonBlank(
                    ReadString(payload, "memoryText"),
                    ReadString(payload, "memory_text"),
                    $"{captive.Name} was released, satisfying the immediate escape want."),
                memoryProvenance: FirstNonBlank(ReadString(payload, "memoryProvenance"), ReadString(payload, "memory_provenance"), "free_captive"),
                memoryShareable: false));
            if (!wantApplied.Applied)
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, wantApplied.Error ?? "want_update_rejected");
            }
        }

        if (shouldRecordDeed)
        {
            if (!liberator.TryGet<PositionComponent>(out var liberatorPosition)
                || !captive.TryGet<PositionComponent>(out var captivePosition))
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, "missing_deed_position");
            }

            var deedTags = NormalizeTags(ReadStringList(payload, "deedTags")
                .Concat(ReadStringList(payload, "deed_tags")));
            if (deedTags.Count == 0)
            {
                deedTags = new[] { "mercy", "anti_empire", "rescued" };
            }

            var deedApplied = ApplyChild(WorldConsequence.RecordDeed(
                source,
                liberator.Id.Value,
                NormalizeToken(FirstNonBlank(ReadString(payload, "deedKind"), ReadString(payload, "deed_kind"), "freed_prisoner")!, "freed_prisoner"),
                Math.Clamp(ReadInt(payload, "deedMagnitude") ?? ReadInt(payload, "deed_magnitude") ?? consequence.Salience, 1, 999),
                liberatorPosition.Position.X,
                liberatorPosition.Position.Y,
                captivePosition.Position.X,
                captivePosition.Position.Y,
                deedTags,
                sourceEntityId: liberator.Id.Value,
                evidence: evidence,
                reason: "A captive release became a world-reactive deed.",
                operation: $"{ReadString(payload, "operation") ?? "freeCaptive"}Deed"));
            if (!deedApplied.Applied)
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, deedApplied.Error ?? "deed_record_rejected");
            }
        }

        var operation = ReadString(payload, "operation") ?? "freeCaptive";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"{captive.Name} is free enough to choose {ObjectName(liberator)}, for now.")!;
        if (ReadBool(payload, "emitMessage") ?? IsVisible(consequence.Visibility))
        {
            var messageApplied = ApplyChild(WorldConsequence.Message(
                source,
                summary,
                targetEntityId: captive.Id.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: liberator.Id.Value,
                evidence: evidence,
                reason: "A captive release produced a legible player-facing receipt.",
                operation: $"{operation}Message",
                details: new Dictionary<string, object?>
                {
                    ["parentConsequenceType"] = consequence.Type,
                    ["faction"] = faction,
                }));
            if (!messageApplied.Applied)
            {
                return RollBackFreeCaptive(consequence, snapshot, deltas, messages, messageApplied.Error ?? "message_rejected");
            }
        }

        var delta = new StateDelta(
            operation,
            captive.Id.Value,
            summary,
            Details(
                consequence,
                ("captiveId", captive.Id.Value),
                ("liberatorId", liberator.Id.Value),
                ("faction", faction),
                ("roles", roles),
                ("aiPolicyId", aiPolicy),
                ("recordDeed", shouldRecordDeed),
                ("satisfyWant", satisfyWant)));
        deltas.Add(delta);
        return new WorldConsequenceApplyResult(
            true,
            captive.Id.Value,
            null,
            messages,
            deltas,
            Details(
                consequence,
                ("captiveId", captive.Id.Value),
                ("liberatorId", liberator.Id.Value),
                ("faction", faction),
                ("roles", roles),
                ("aiPolicyId", aiPolicy)));
    }

    private WorldConsequenceApplyResult ApplyCreateRoute(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var anchorId = consequence.TargetEntityId;
        var name = FirstNonBlank(ReadString(payload, "name"), "hidden route")!;
        var description = FirstNonBlank(ReadString(payload, "description"), consequence.Evidence, name)!;
        var routeKind = NormalizeToken(ReadString(payload, "routeKind") ?? "hidden_route", "hidden_route");
        var anchor = string.IsNullOrWhiteSpace(anchorId) ? null : EntityById(anchorId);
        GridPoint position;
        string? anchorEntityId = null;
        if (TryReadPoint(payload, null, out var explicitPosition))
        {
            position = explicitPosition;
        }
        else if (anchor is not null && anchor.TryGet<PositionComponent>(out var anchorPosition))
        {
            position = FindOpenAdjacent(anchorPosition.Position) ?? anchorPosition.Position;
            anchorEntityId = anchor.Id.Value;
        }
        else
        {
            return Reject(consequence, "Route consequence needs an anchor with a position or explicit x/y coordinates.");
        }

        if (!InBounds(position))
        {
            return Reject(consequence, "Route consequence target is out of bounds.");
        }

        var routeId = _state.NextEntityId("route");
        var tags = new[] { "route", "escape_route", routeKind, "promise_payoff" }
            .Concat(ReadStringList(payload, "tags"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var promiseIds = ReadStringList(payload, "promiseIds")
            .Concat(ReadStringList(payload, "promise_ids"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var material = NormalizeToken(FirstNonBlank(ReadString(payload, "material"), "passage")!, "passage");
        var route = new Entity(routeId, name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('>', "route"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(description))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: material))
            .Set(new FixtureComponent(routeKind, tags))
            .Set(new InteractableComponent(new[] { "examine", "travel" }));
        if (promiseIds.Length > 0)
        {
            route.Set(new PromiseAnchorComponent(promiseIds));
        }

        _state.Entities[routeId] = route;

        var operation = ReadString(payload, "operation") ?? "createRoute";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"A route is now discoverable: {route.Name}.")!;
        var delta = new StateDelta(
            operation,
            route.Id.Value,
            summary,
            Details(
                consequence,
                ("routeId", route.Id.Value),
                ("routeKind", routeKind),
                ("x", position.X),
                ("y", position.Y),
                ("tags", tags),
                ("promiseIds", promiseIds),
                ("material", material),
                ("anchorEntityId", anchorEntityId ?? anchorId)));
        return Applied(consequence, route.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("routeId", route.Id.Value));
    }

    private WorldConsequenceApplyResult Reject(WorldConsequence consequence, string error)
    {
        var delta = new StateDelta(
            "worldConsequenceRejected",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            error,
            Details(consequence, ("error", error)));
        return new WorldConsequenceApplyResult(false, consequence.TargetEntityId, error, Array.Empty<string>(), new[] { delta }, Details(consequence, ("error", error)));
    }

    private WorldConsequenceApplyResult RollBackFreeCaptive(
        WorldConsequence consequence,
        GameStateSnapshot snapshot,
        IReadOnlyList<StateDelta> failedDeltas,
        IReadOnlyList<string> failedMessages,
        string failure)
    {
        snapshot.Restore(_state);
        var diagnostics = failedDeltas
            .Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var skipped = new StateDelta(
            "freeCaptiveSkipped",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            $"Captive release rolled back: {failure}.",
            Details(
                consequence,
                ("failure", failure),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count),
                ("rejectedCount", diagnostics.Length),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            consequence.TargetEntityId,
            failure,
            Array.Empty<string>(),
            diagnostics.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("error", failure),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count)));
    }

    private WorldConsequenceApplyResult Applied(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyList<string> messages,
        StateDelta delta,
        params (string Key, object? Value)[] fields) =>
        new(true, targetId, null, messages, new[] { delta }, Details(consequence, fields));

    private IReadOnlyList<string> MaybeVisibleMessage(WorldConsequence consequence, string message)
    {
        if (!IsVisible(consequence.Visibility) || !PayloadAllowsPlayerMessage(consequence))
        {
            return Array.Empty<string>();
        }

        _state.AddMessage(message);
        return new[] { message };
    }

    private bool AddMessageIfAllowed(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        string message,
        bool defaultEmitMessage = true,
        bool includeVisible = true,
        bool playerVisible = true)
    {
        var shouldEmit = (includeVisible && IsVisible(consequence.Visibility))
            || (ReadBool(payload, "emitMessage") ?? defaultEmitMessage);
        if (shouldEmit && playerVisible && PayloadAllowsPlayerMessage(consequence))
        {
            _state.AddMessage(message);
            return true;
        }

        return false;
    }

    private static bool PayloadAllowsPlayerMessage(WorldConsequence consequence)
    {
        var payload = consequence.Payload;
        if (payload is null)
        {
            return true;
        }

        return ReadBool(payload, "playerVisible") != false
            && ReadBool(payload, "player_visible") != false
            && ReadBool(payload, "auditOnly") != true
            && ReadBool(payload, "audit_only") != true;
    }

    private static bool IsVisible(string visibility) =>
        NormalizeToken(visibility, WorldConsequenceVisibility.Hidden) is
            WorldConsequenceVisibility.Message or WorldConsequenceVisibility.Journal or WorldConsequenceVisibility.Lead or "visible";

    private IReadOnlyDictionary<string, object?> Details(WorldConsequence consequence, params (string Key, object? Value)[] fields)
    {
        var details = consequence.Payload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(consequence.Payload, StringComparer.OrdinalIgnoreCase);
        details["consequenceType"] = consequence.Type;
        details["source"] = consequence.Source;
        details["sourceEntityId"] = consequence.SourceEntityId;
        details["visibility"] = consequence.Visibility;
        details["timing"] = consequence.Timing;
        details["salience"] = consequence.Salience;
        details["confidence"] = consequence.Confidence;
        details["evidence"] = consequence.Evidence;
        details["reason"] = consequence.Reason;
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        return details;
    }

    private static void EnsureInteractableVerbs(Entity entity, params string[] verbs)
    {
        var existing = entity.TryGet<InteractableComponent>(out var interactable)
            ? interactable.Verbs
            : Array.Empty<string>();
        var merged = existing
            .Concat(verbs)
            .Where(verb => !string.IsNullOrWhiteSpace(verb))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(verb => verb, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        entity.Set(new InteractableComponent(merged));
    }

    private bool CanReach(Entity actor, Entity target, int range)
    {
        if (!actor.TryGet<PositionComponent>(out var actorPosition)
            || !target.TryGet<PositionComponent>(out var targetPosition))
        {
            return false;
        }

        return Distance(actorPosition.Position, targetPosition.Position) <= range;
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        var offsets = new[]
        {
            new GridPoint(0, 1),
            new GridPoint(1, 0),
            new GridPoint(0, -1),
            new GridPoint(-1, 0),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(1, -1),
            new GridPoint(-1, -1),
        };
        foreach (var point in offsets
            .Select(offset => new GridPoint(origin.X + offset.X, origin.Y + offset.Y))
            .Where(point => point.X > 0 && point.Y > 0 && point.X < _state.Width - 1 && point.Y < _state.Height - 1)
            .Where(point => !_state.BlockingTerrain.Contains(point))
            .Where(point => !_state.Entities.Values.Any(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == point
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement)))
        {
            return point;
        }

        return null;
    }

    private static int Distance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private bool IsBoundaryWall(GridPoint point) =>
        point.X == 0 || point.Y == 0 || point.X == _state.Width - 1 || point.Y == _state.Height - 1;

    private bool InBounds(GridPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X < _state.Width && point.Y < _state.Height;

    private Entity? BlockingEntityAt(GridPoint point) =>
        _state.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement
            && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive));

    private static bool TerrainBlocksMovement(string terrain) =>
        terrain is "wall" or "ice_wall" or "rubble" or "vines";

    private static string SentenceCase(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : $"{char.ToUpperInvariant(text[0])}{text[1..]}";

    private Entity? EntityById(string entityId) =>
        _state.Entities.TryGetValue(EntityId.Create(entityId), out var entity) ? entity : null;

    private string AttackSummary(Entity attacker, Entity defender, int amount, string damageType)
    {
        var alive = defender.TryGet<ActorComponent>(out var actor) && actor.Alive;
        return alive
            ? $"{Subject(attacker)} {Verb(attacker, "strike", "strikes")} {ObjectName(defender)} for {amount} {damageType} damage."
            : $"{Subject(attacker)} {Verb(attacker, "drop", "drops")} {ObjectName(defender)}.";
    }

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string ObjectName(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "you" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;

    private string Possessive(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "Your" : $"{entity.Name}'s";

    private static int ClampDelta(int value, int maxDelta) =>
        Math.Clamp(value, -maxDelta, maxDelta);

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static string BondSummary(BondRecord bond)
    {
        if (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase))
        {
            return "following";
        }

        if (bond.Loyalty + bond.Admiration >= 5)
        {
            return "warm enough to risk something";
        }

        if (bond.Fear > bond.Loyalty + bond.Admiration)
        {
            return "afraid";
        }

        if (bond.Resentment >= 5)
        {
            return "resentful";
        }

        return bond.Posture;
    }

    private static string NormalizeToken(string text, string fallback)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string NormalizeClaimText(string text) =>
        string.Join(
            " ",
            text.Trim()
                .ToLowerInvariant()
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .TrimEnd('.', '!', '?');

    private static string CleanLedgerKey(string text, string fallback)
    {
        var normalized = text.Trim().ToLowerInvariant().Replace(' ', '_');
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string FormatStackMessage(string? template, string text, string kind, int stacks)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return $"{text} deepens ({stacks} stacks).";
        }

        return template
            .Replace("{text}", text, StringComparison.OrdinalIgnoreCase)
            .Replace("{kind}", kind, StringComparison.OrdinalIgnoreCase)
            .Replace("{stacks}", stacks.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value switch
        {
            string text => text,
            _ => value?.ToString(),
        } : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int typed => typed,
            long typed => typed > int.MaxValue ? int.MaxValue : typed < int.MinValue ? int.MinValue : (int)typed,
            double typed => (int)Math.Round(typed),
            float typed => (int)Math.Round(typed),
            decimal typed => (int)Math.Round(typed),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static char ReadChar(IReadOnlyDictionary<string, object?> map, string key, char fallback)
    {
        var text = ReadString(map, key);
        return string.IsNullOrWhiteSpace(text) ? fallback : text.Trim()[0];
    }

    private static bool TryReadPoint(IReadOnlyDictionary<string, object?> map, string? target, out GridPoint point)
    {
        if ((ReadInt(map, "x") ?? ReadInt(map, "X")) is { } x
            && (ReadInt(map, "y") ?? ReadInt(map, "Y")) is { } y)
        {
            point = new GridPoint(x, y);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(target)
            && target.StartsWith("tile:", StringComparison.OrdinalIgnoreCase))
        {
            var parts = target["tile:".Length..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var targetX)
                && int.TryParse(parts[1], out var targetY))
            {
                point = new GridPoint(targetX, targetY);
                return true;
            }
        }

        point = default;
        return false;
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool typed => typed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool TryReadControllerKind(string? text, out ControllerKind kind)
    {
        kind = ControllerKind.None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        kind = NormalizeToken(text, "") switch
        {
            "none" or "no_controller" or "uncontrolled" or "inert" => ControllerKind.None,
            "player" or "human" or "controlled" or "controlled_by_player" => ControllerKind.Player,
            "ai" or "npc" or "agent" or "computer" => ControllerKind.Ai,
            _ => kind,
        };
        return NormalizeToken(text, "") is
            "none" or "no_controller" or "uncontrolled" or "inert"
            or "player" or "human" or "controlled" or "controlled_by_player"
            or "ai" or "npc" or "agent" or "computer";
    }

    private static IReadOnlyDictionary<string, object?>? ReadDictionary(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            IReadOnlyDictionary<string, object?> readOnly => new Dictionary<string, object?>(readOnly, StringComparer.OrdinalIgnoreCase),
            IDictionary<string, object?> dictionary => new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase),
            _ => null,
        };
    }

    private static IReadOnlyDictionary<string, object?> PayloadWithoutSchedulerKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var copy = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "eventType", "event_type", "kind", "turns", "delay", "dueTurn", "due_turn", "operation" })
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, object?> PayloadWithoutPersistentKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var copy = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[] { "target", "hook", "kind", "uses", "linkPartnerId", "linkPartner", "link_target", "effectType", "effect_type", "effectFields", "effect", "operation", "playerVisible" })
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static IReadOnlyDictionary<string, object?> PayloadWithoutWorldTurnKeys(IReadOnlyDictionary<string, object?> payload)
    {
        var copy = new Dictionary<string, object?>(payload, StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
        {
            "worldTurnReason",
            "world_turn_reason",
            "moveReason",
            "reason",
            "kind",
            "moveKind",
            "worldTurnSourceId",
            "world_turn_source_id",
            "sourceId",
            "source_id",
            "summary",
            "details",
            "recordDetails",
            "record_details",
            "operation",
        })
        {
            copy.Remove(key);
        }

        return copy;
    }

    private static IReadOnlyList<string> ReadStringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string text => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object?> objects => objects.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    private static IReadOnlyList<GridPoint> ReadPointList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<GridPoint>();
        }

        return value switch
        {
            string text => ParsePointListText(text),
            IEnumerable<GridPoint> points => points.ToArray(),
            IEnumerable<string> strings => strings.SelectMany(ParsePointListText).ToArray(),
            IEnumerable<object?> objects => objects.SelectMany(ParsePointObject).ToArray(),
            _ => Array.Empty<GridPoint>(),
        };
    }

    private static IReadOnlyList<GridPoint> ParsePointObject(object? value)
    {
        if (value is null)
        {
            return Array.Empty<GridPoint>();
        }

        if (value is GridPoint point)
        {
            return new[] { point };
        }

        if (value is IReadOnlyDictionary<string, object?> readOnly
            && (ReadInt(readOnly, "x") ?? ReadInt(readOnly, "X")) is { } x
            && (ReadInt(readOnly, "y") ?? ReadInt(readOnly, "Y")) is { } y)
        {
            return new[] { new GridPoint(x, y) };
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            var map = new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
            if ((ReadInt(map, "x") ?? ReadInt(map, "X")) is { } mapX
                && (ReadInt(map, "y") ?? ReadInt(map, "Y")) is { } mapY)
            {
                return new[] { new GridPoint(mapX, mapY) };
            }
        }

        return ParsePointListText(value.ToString() ?? "");
    }

    private static IReadOnlyList<GridPoint> ParsePointListText(string text) =>
        text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParsePointText)
            .Where(point => point is not null)
            .Select(point => point!.Value)
            .ToArray();

    private static GridPoint? ParsePointText(string text)
    {
        var parts = text.Trim()
            .TrimStart('(')
            .TrimEnd(')')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out var x)
            && int.TryParse(parts[1], out var y)
                ? new GridPoint(x, y)
                : null;
    }

    private static bool HasAnyKey(IReadOnlyDictionary<string, object?> map, params string[] keys) =>
        keys.Any(key => map.Any(pair =>
            pair.Value is not null
            && pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase)));

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string> tags) =>
        tags
            .Select(tag => NormalizeToken(tag, ""))
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static char ReadGlyph(IReadOnlyDictionary<string, object?> map, char fallback)
    {
        var value = ReadString(map, "glyph");
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[0];
    }

    private static bool RecordMentionsMemory(EntityMemoryRecord record, string? subject, bool aboutCaster)
    {
        if (aboutCaster)
        {
            return record.Text.Contains("caster", StringComparison.OrdinalIgnoreCase)
                || record.Text.Contains("player", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.Contains("wild_magic", StringComparison.OrdinalIgnoreCase);
        }

        return !string.IsNullOrWhiteSpace(subject)
            && record.Text.Contains(subject, StringComparison.OrdinalIgnoreCase);
    }
}
