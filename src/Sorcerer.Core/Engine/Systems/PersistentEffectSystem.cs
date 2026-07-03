using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

/// <summary>
/// Fires <see cref="PersistentEffectRecord"/>s anchored to a combatant when that combatant is hit
/// (<c>on_hit</c>) or lands a hit (<c>on_strike</c>), unlike <see cref="TriggerSystem"/> which
/// fires on turn cadence. Called from <see cref="GameEngine.AttackEntity"/>.
/// </summary>
public sealed class PersistentEffectSystem
{
    private readonly GameEngine _engine;
    private readonly GameState _state;

    public PersistentEffectSystem(GameEngine engine)
    {
        _engine = engine;
        _state = engine.State;
    }

    /// <summary>
    /// Fires every persistent effect anchored to <paramref name="anchor"/> for this hook.
    /// <paramref name="hitAmount"/> is the damage the anchor actually just took or dealt in this
    /// combat exchange (0 if none); a sympathetic link mirrors that real amount onto its partner
    /// rather than running its own embedded effect.
    /// </summary>
    public IReadOnlyList<StateDelta> FireHook(string hook, Entity anchor, Entity counterpart, int hitAmount = 0)
    {
        var deltas = new List<StateDelta>();
        foreach (var record in _state.PersistentEffects.ForAnchorAndHook(anchor.Id.Value, hook))
        {
            var transaction = GameTransaction.Begin(_state);
            var deltaStart = deltas.Count;
            var fireDeltas = record.LinkPartnerId is not null
                ? MirrorSympatheticLink(record, hitAmount)
                : Fire(record, anchor, counterpart);
            deltas.AddRange(fireDeltas);
            if (HasRejected(fireDeltas))
            {
                RollBackPersistentHookTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    record,
                    fireDeltas,
                    $"Persistent effect {record.Id} child consequence rejected.");
                deltas.AddRange(UpdatePersistentEffect(record, "remove").Deltas);
                continue;
            }

            var lifecycle = UpdatePersistentEffect(record, "consume");
            if (!lifecycle.Applied)
            {
                RollBackPersistentHookTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    record,
                    lifecycle.Deltas,
                    lifecycle.Error ?? $"Persistent effect {record.Id} lifecycle could not be updated.");
                continue;
            }

            deltas.AddRange(lifecycle.Deltas);
            transaction.Commit();
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> Fire(PersistentEffectRecord record, Entity anchor, Entity counterpart)
    {
        var effectType = record.EffectType.Trim().ToLowerInvariant();
        if (effectType is "consequence" or "worldconsequence" or "world_consequence")
        {
            return TryBuildPersistentConsequence(record, anchor, counterpart, out var consequence)
                ? ApplyEffect(consequence)
                : new[] { PersistentConsequenceRejectedDelta(record, "Generic persistent effect consequence did not include consequenceType.") };
        }

        var target = Text(record.EffectFields, "target", "other").Trim().ToLowerInvariant() switch
        {
            "self" or "anchor" => anchor,
            _ => counterpart,
        };
        if (!target.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            return Array.Empty<StateDelta>();
        }

        return effectType switch
        {
            "damage" or "harm" => ApplyEffect(WorldConsequence.Damage(
                "persistent_effect",
                target.Id.Value,
                Int(record.EffectFields, "amount", 3, 1, 40),
                Text(record.EffectFields, "damageType", Text(record.EffectFields, "damage_type", "persistent")),
                sourceEntityId: anchor.Id.Value,
                evidence: record.Id,
                reason: $"Persistent effect {record.Id} fired.",
                operation: "persistentDamage",
                details: PersistentDetails(record))),
            "heal" or "restorehealth" => ApplyEffect(WorldConsequence.Heal(
                "persistent_effect",
                target.Id.Value,
                Int(record.EffectFields, "amount", 3, 1, 40),
                sourceEntityId: anchor.Id.Value,
                evidence: record.Id,
                reason: $"Persistent effect {record.Id} fired.",
                operation: "persistentHeal",
                details: PersistentDetails(record))),
            "addstatus" or "status" or "applystatus" => ApplyEffect(WorldConsequence.ApplyStatus(
                "persistent_effect",
                target.Id.Value,
                Text(record.EffectFields, "status", Text(record.EffectFields, "name", "marked")),
                Int(record.EffectFields, "duration", 0, 0, 99),
                Text(record.EffectFields, "displayName", Text(record.EffectFields, "display_name", "")),
                sourceEntityId: anchor.Id.Value,
                evidence: record.Id,
                reason: $"Persistent effect {record.Id} fired.",
                operation: "persistentApplyStatus",
                details: PersistentDetails(record))),
            "message" => FireMessage(record),
            _ => new[]
            {
                PersistentConsequenceRejectedDelta(
                    record,
                    $"Unsupported persistent effect type '{record.EffectType}'."),
            },
        };
    }

    private IReadOnlyList<StateDelta> ApplyEffect(WorldConsequence consequence) =>
        _engine.ApplyConsequence(consequence).Deltas;

    /// <summary>
    /// A sympathetic link mirrors the real damage the anchor just took or dealt onto the linked
    /// partner, so wounding one wounds the other.
    /// </summary>
    private IReadOnlyList<StateDelta> MirrorSympatheticLink(PersistentEffectRecord record, int hitAmount)
    {
        if (hitAmount <= 0 || string.IsNullOrWhiteSpace(record.LinkPartnerId))
        {
            return Array.Empty<StateDelta>();
        }

        var partner = _engine.EntityById(record.LinkPartnerId);
        if (partner is null || !partner.TryGet<ActorComponent>(out var partnerActor) || !partnerActor.Alive)
        {
            return Array.Empty<StateDelta>();
        }

        return _engine.ApplyConsequence(WorldConsequence.Damage(
            "persistent_effect",
            partner.Id.Value,
            hitAmount,
            "sympathetic",
            sourceEntityId: record.AnchorEntityId,
            evidence: record.Id,
            reason: $"Sympathetic persistent effect {record.Id} mirrored damage.",
            operation: "persistentSympatheticDamage",
            details: PersistentDetails(record))).Deltas;
    }

    private IReadOnlyList<StateDelta> FireMessage(PersistentEffectRecord record)
    {
        var text = Text(record.EffectFields, "text", "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<StateDelta>();
        }

        return _engine.ApplyConsequence(WorldConsequence.Message(
            "persistent_effect",
            text,
            targetEntityId: record.Id,
            visibility: WorldConsequenceVisibility.Message,
            evidence: record.Id,
            reason: $"Persistent effect {record.Id} fired.",
            operation: "persistentMessage",
            details: PersistentDetails(record))).Deltas;
    }

    private WorldConsequenceApplyResult UpdatePersistentEffect(PersistentEffectRecord record, string action) =>
        _engine.ApplyConsequence(WorldConsequence.UpdatePersistentEffect(
            "persistent_effect",
            record.Id,
            action,
            sourceEntityId: record.AnchorEntityId,
            evidence: record.Id,
            reason: $"Persistent effect {record.Id} lifecycle changed.",
            details: PersistentLifecycleDetails(record)));

    private static void RollBackPersistentHookTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        PersistentEffectRecord record,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        deltas.AddRange(FailureDiagnostics(failedDeltas));
        var rejectedCount = FailureDiagnostics(failedDeltas).Count;
        deltas.Add(new StateDelta(
            "persistentEffectSkipped",
            record.Id,
            $"Persistent effect rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["persistentEffectId"] = record.Id,
                ["hook"] = record.Hook,
                ["effectType"] = record.EffectType,
                ["anchorEntityId"] = record.AnchorEntityId,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas
            .Where(IsRejectedDelta)
            .ToArray();

    private static bool HasRejected(IEnumerable<StateDelta> deltas) =>
        deltas.Any(IsRejectedDelta);

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta PersistentConsequenceRejectedDelta(PersistentEffectRecord record, string error) =>
        new(
            "worldConsequenceRejected",
            record.Id,
            error,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "persistent_effect_consequence",
                ["persistentEffectId"] = record.Id,
                ["hook"] = record.Hook,
                ["effectType"] = record.EffectType,
                ["anchorEntityId"] = record.AnchorEntityId,
                ["error"] = error,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });

    private static void RemoveRangeFrom<T>(List<T> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
    }

    private static IReadOnlyDictionary<string, object?> PersistentDetails(PersistentEffectRecord record) =>
        new Dictionary<string, object?>
        {
            ["persistentEffectId"] = record.Id,
            ["hook"] = record.Hook,
            ["effectType"] = record.EffectType,
            ["anchorEntityId"] = record.AnchorEntityId,
            ["linkPartnerId"] = record.LinkPartnerId,
            ["playerVisible"] = record.PlayerVisible,
        };

    private static IReadOnlyDictionary<string, object?> PersistentLifecycleDetails(PersistentEffectRecord record)
    {
        var details = new Dictionary<string, object?>(PersistentDetails(record), StringComparer.OrdinalIgnoreCase);
        details["playerVisible"] = false;
        return details;
    }

    private static bool TryBuildPersistentConsequence(
        PersistentEffectRecord record,
        Entity anchor,
        Entity counterpart,
        out WorldConsequence consequence)
    {
        var consequenceType = TextOrNull(record.EffectFields, "consequenceType")
            ?? TextOrNull(record.EffectFields, "consequence_type");
        if (string.IsNullOrWhiteSpace(consequenceType))
        {
            consequence = default!;
            return false;
        }

        var target = ResolveConsequenceTarget(record, anchor, counterpart);
        var payload = PersistentConsequencePayload(record);
        consequence = new WorldConsequence(
            consequenceType,
            TextOrNull(record.EffectFields, "source") ?? "persistent_effect",
            SourceEntityId: TextOrNull(record.EffectFields, "sourceEntityId")
                ?? TextOrNull(record.EffectFields, "source_entity_id")
                ?? anchor.Id.Value,
            TargetEntityId: target,
            Salience: IntOrNull(record.EffectFields, "salience") ?? 1,
            Confidence: IntOrNull(record.EffectFields, "confidence") ?? 100,
            Visibility: TextOrNull(record.EffectFields, "visibility") ?? WorldConsequenceVisibility.Hidden,
            Evidence: TextOrNull(record.EffectFields, "evidence") ?? record.Id,
            Reason: TextOrNull(record.EffectFields, "reason") ?? $"Persistent effect {record.Id} delivered {consequenceType}.",
            Payload: payload,
            Timing: TextOrNull(record.EffectFields, "timing") ?? WorldConsequenceTiming.Immediate);
        return true;
    }

    private static string? ResolveConsequenceTarget(PersistentEffectRecord record, Entity anchor, Entity counterpart)
    {
        var raw = TextOrNull(record.EffectFields, "targetEntityId")
            ?? TextOrNull(record.EffectFields, "target_entity_id")
            ?? TextOrNull(record.EffectFields, "target");
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Equals("other", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("counterpart", StringComparison.OrdinalIgnoreCase))
        {
            return counterpart.Id.Value;
        }

        if (raw.Equals("self", StringComparison.OrdinalIgnoreCase)
            || raw.Equals("anchor", StringComparison.OrdinalIgnoreCase))
        {
            return anchor.Id.Value;
        }

        return raw;
    }

    private static IReadOnlyDictionary<string, object?> PersistentConsequencePayload(PersistentEffectRecord record)
    {
        var fields = record.EffectFields;
        var result = WorldConsequencePayloadBuilder.MergeNestedWithTopLevelFields(
            fields,
            PersistentConsequenceMetadataKeys,
            "consequencePayload",
            "consequence_payload",
            "payload");
        AddPersistentPayloadDetails(result, record);
        return result;
    }

    private static void AddPersistentPayloadDetails(Dictionary<string, object?> payload, PersistentEffectRecord record)
    {
        payload.TryAdd("persistentEffectId", record.Id);
        payload.TryAdd("hook", record.Hook);
        payload.TryAdd("persistentEffectType", record.EffectType);
        payload.TryAdd("anchorEntityId", record.AnchorEntityId);
        payload.TryAdd("linkPartnerId", record.LinkPartnerId);
        payload.TryAdd("playerVisible", record.PlayerVisible);
    }

    private static readonly HashSet<string> PersistentConsequenceMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "type",
        "consequenceType",
        "consequence_type",
        "source",
        "sourceEntityId",
        "source_entity_id",
        "targetEntityId",
        "target_entity_id",
        "target",
        "salience",
        "confidence",
        "visibility",
        "evidence",
        "reason",
        "timing",
        "consequencePayload",
        "consequence_payload",
        "payload",
    };

    private static string Text(IReadOnlyDictionary<string, object?> fields, string key, string fallback)
    {
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }

        if (raw is IReadOnlyDictionary<string, object?> nested)
        {
            foreach (var nestedKey in new[] { "id", "name", "value", "text", "type", "description" })
            {
                if (nested.TryGetValue(nestedKey, out var nestedValue))
                {
                    return Convert.ToString(nestedValue) ?? fallback;
                }
            }
        }

        return Convert.ToString(raw) ?? fallback;
    }

    private static string? TextOrNull(IReadOnlyDictionary<string, object?> fields, string key)
    {
        var text = Text(fields, key, "");
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int Int(IReadOnlyDictionary<string, object?> fields, string key, int fallback, int min, int max)
    {
        var value = fields.TryGetValue(key, out var raw) && int.TryParse(Convert.ToString(raw), out var parsed)
            ? parsed
            : fallback;
        return Math.Clamp(value, min, max);
    }

    private static int? IntOrNull(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        if (raw is int value)
        {
            return value;
        }

        return int.TryParse(Convert.ToString(raw), out var parsed) ? parsed : null;
    }

}
