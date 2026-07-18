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
/// <see cref="WorldConsequenceApplier"/> handlers for tactical effects: damage, healing, mana/actor resources, status, resistance/weakness, delayed damage, and persistent effects.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
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
        if (target.Entity!.Id == _state.ControlledEntityId)
        {
            // Phase 2.6: name the hand that struck the sorcerer's body, so a defeat can be
            // treated in the killer's register (imperial paperwork vs. wild transformation).
            _state.LastControlledDamageProvenance = ClassifyBodyDamageProvenance(attacker, consequence, damageType);
        }

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

    // Classifies who dealt a blow to the controlled body. Empire-faction hands (Censorate guards,
    // the emperor) read as "imperial"; wild-magic sources as "wild"; everything else as "mortal".
    private static string ClassifyBodyDamageProvenance(Entity? attacker, WorldConsequence consequence, string damageType)
    {
        if (attacker is not null
            && attacker.TryGet<ActorComponent>(out var atk)
            && string.Equals(atk.Faction, "empire", StringComparison.OrdinalIgnoreCase))
        {
            return "imperial";
        }

        var source = consequence.Source ?? string.Empty;
        if (string.Equals(damageType, "wild", StringComparison.OrdinalIgnoreCase)
            || source.Contains("wild", StringComparison.OrdinalIgnoreCase))
        {
            return "wild";
        }

        return "mortal";
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
        var equipmentDefense = target.TryGet<EquipmentEffectComponent>(out var effect) ? effect.Defense : 0;
        var actual = Math.Max(1, amount - actor.Defense - equipmentDefense);
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
        var resist = 0;
        var weak = 0;
        if (target.TryGet<ResistanceComponent>(out var resistance))
        {
            resist += resistance.Resistances.GetValueOrDefault(damageType);
            weak += resistance.Weaknesses.GetValueOrDefault(damageType);
        }

        // Worn gear contributes resistance/weakness through the same derived cache combat reads for
        // defense, so an armour tag or a cursed item shifts incoming damage without touching base stats.
        if (target.TryGet<EquipmentEffectComponent>(out var effect))
        {
            resist += effect.Resistances.GetValueOrDefault(damageType);
            weak += effect.Weaknesses.GetValueOrDefault(damageType);
        }

        if (resist == 0 && weak == 0)
        {
            return amount;
        }

        var resistPercent = Math.Clamp(resist, 0, 95);
        var weakPercent = Math.Clamp(weak, 0, 200);
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
}
