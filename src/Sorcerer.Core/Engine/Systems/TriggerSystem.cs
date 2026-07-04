using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class TriggerSystem
{
    private readonly GameEngine _engine;
    private readonly GameState _state;

    public TriggerSystem(GameEngine engine)
    {
        _engine = engine;
        _state = engine.State;
    }

    public IReadOnlyList<StateDelta> ApplyDue()
    {
        var deltas = new List<StateDelta>();
        ApplyExpired(deltas);
        foreach (var record in _state.Triggers.Due(_state.Turn).ToArray())
        {
            var snapshot = GameStateSnapshot.Capture(_state);
            var triggerDeltas = new List<StateDelta>();
            triggerDeltas.AddRange(Apply(record));
            var remaining = record.RemainingUses - 1;
            if (remaining <= 0)
            {
                triggerDeltas.AddRange(UpdateTrigger(record, "complete"));
            }
            else
            {
                var next = record.NextTurn + Math.Max(1, record.Interval);
                if (record.ExpiresTurn is not null && next > record.ExpiresTurn)
                {
                    triggerDeltas.AddRange(UpdateTrigger(record, "expire"));
                }
                else
                {
                    triggerDeltas.AddRange(UpdateTrigger(record, "advance", next, remaining));
                }
            }

            if (HasRejected(triggerDeltas))
            {
                snapshot.Restore(_state);
                deltas.AddRange(triggerDeltas.Where(IsRejectedDelta));
                deltas.Add(TriggerSkippedDelta(record, triggerDeltas));
                deltas.AddRange(UpdateTrigger(record, "expire"));
                continue;
            }

            deltas.AddRange(triggerDeltas);
        }

        ApplyExpired(deltas);
        return deltas;
    }

    private IReadOnlyList<StateDelta> Apply(TriggerRecord record)
    {
        var deltas = new List<StateDelta>();
        if (!string.IsNullOrWhiteSpace(record.Description))
        {
            deltas.AddRange(ApplyMessage(record, record.Description, "trigger"));
        }

        switch (record.EffectType.Trim().ToLowerInvariant())
        {
            case "consequence":
            case "worldconsequence":
            case "world_consequence":
                {
                    var radiusMode = record.Radius > 0
                        || record.Kind.Equals("aura", StringComparison.OrdinalIgnoreCase)
                        || record.Kind.Equals("ward", StringComparison.OrdinalIgnoreCase);
                    if (radiusMode)
                    {
                        foreach (var target in ResolveTargets(record)
                            .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive))
                        {
                            if (!TryBuildTriggerConsequence(record, out var areaConsequence, target.Id.Value))
                            {
                                deltas.Add(TriggerConsequenceRejectedDelta(
                                    record,
                                    "Generic trigger consequence did not include consequenceType."));
                                return deltas;
                            }

                            deltas.AddRange(_engine.ApplyConsequence(areaConsequence).Deltas);
                        }

                        return deltas;
                    }

                    if (!TryBuildTriggerConsequence(record, out var consequence))
                    {
                        deltas.Add(TriggerConsequenceRejectedDelta(
                            record,
                            "Generic trigger consequence did not include consequenceType."));
                        return deltas;
                    }

                    deltas.AddRange(_engine.ApplyConsequence(consequence).Deltas);
                    return deltas;
                }
            case "message":
                {
                    var text = Text(record.EffectFields, "text", record.Description);
                    if (!string.IsNullOrWhiteSpace(text) && !text.Equals(record.Description, StringComparison.Ordinal))
                    {
                        deltas.AddRange(ApplyMessage(record, text, "message"));
                    }

                    return deltas;
                }
            case "addstatus":
            case "status":
            case "applystatus":
                {
                    var status = Text(record.EffectFields, "status", Text(record.EffectFields, "name", "marked"));
                    var displayName = Text(record.EffectFields, "displayName", Text(record.EffectFields, "display_name", status));
                    var duration = Int(record.EffectFields, "duration", 0, min: 0, max: 99);
                    deltas.AddRange(ResolveTargets(record)
                        .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
                        .SelectMany(target => _engine.ApplyConsequence(WorldConsequence.ApplyStatus(
                            "trigger",
                            target.Id.Value,
                            status,
                            duration,
                            displayName,
                            sourceEntityId: record.SourceEntityId?.Value,
                            evidence: record.Description,
                            reason: $"Trigger {record.Id} fired.",
                            operation: "triggerApplyStatus",
                            details: TriggerDetails(record))).Deltas));
                    return deltas;
                }
            case "damage":
            case "harm":
                {
                    var amount = Int(record.EffectFields, "amount", 3, min: 1, max: 40);
                    var damageType = Text(record.EffectFields, "damageType", Text(record.EffectFields, "damage_type", "trigger"));
                    deltas.AddRange(ResolveTargets(record)
                        .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
                        .SelectMany(target => _engine.ApplyConsequence(WorldConsequence.Damage(
                            "trigger",
                            target.Id.Value,
                            amount,
                            damageType,
                            sourceEntityId: record.SourceEntityId?.Value,
                            evidence: record.Description,
                            reason: $"Trigger {record.Id} fired.",
                            operation: "triggerDamage",
                            details: TriggerDetails(record))).Deltas));
                    return deltas;
                }
            case "heal":
            case "restorehealth":
                {
                    var amount = Int(record.EffectFields, "amount", 3, min: 1, max: 40);
                    deltas.AddRange(ResolveTargets(record)
                        .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
                        .SelectMany(target => _engine.ApplyConsequence(WorldConsequence.Heal(
                            "trigger",
                            target.Id.Value,
                            amount,
                            sourceEntityId: record.SourceEntityId?.Value,
                            evidence: record.Description,
                            reason: $"Trigger {record.Id} fired.",
                            operation: "triggerHeal",
                            details: TriggerDetails(record))).Deltas));
                    return deltas;
                }
            default:
                deltas.Add(TriggerConsequenceRejectedDelta(
                    record,
                    $"Unsupported trigger effect type '{record.EffectType}'."));
                return deltas;
        }
    }

    private IReadOnlyList<StateDelta> ApplyMessage(TriggerRecord record, string text, string operation) =>
        _engine.ApplyConsequence(WorldConsequence.Message(
            "trigger",
            text,
            targetEntityId: record.Id,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: record.SourceEntityId?.Value,
            evidence: record.Description,
            reason: $"Trigger {record.Id} fired.",
            operation: operation,
            details: TriggerDetails(record))).Deltas;

    private void ApplyExpired(List<StateDelta> deltas)
    {
        foreach (var record in _state.Triggers.Records
            .Where(record => record.RemainingUses <= 0
                || (record.ExpiresTurn is not null && record.ExpiresTurn < _state.Turn))
            .ToArray())
        {
            deltas.AddRange(UpdateTrigger(record, "expire"));
        }
    }

    private IReadOnlyList<StateDelta> UpdateTrigger(
        TriggerRecord record,
        string action,
        int? nextTurn = null,
        int? remainingUses = null) =>
        _engine.ApplyConsequence(WorldConsequence.UpdateTrigger(
            "trigger",
            record.Id,
            action,
            nextTurn,
            remainingUses,
            evidence: record.Description,
            reason: $"Trigger {record.Id} lifecycle changed.",
            details: TriggerLifecycleDetails(record))).Deltas;

    private static IReadOnlyDictionary<string, object?> TriggerDetails(TriggerRecord record) =>
        new Dictionary<string, object?>
        {
            ["triggerId"] = record.Id,
            ["triggerName"] = record.Name,
            ["effectType"] = record.EffectType,
            ["playerVisible"] = record.PlayerVisible,
        };

    private static IReadOnlyDictionary<string, object?> TriggerLifecycleDetails(TriggerRecord record)
    {
        var details = new Dictionary<string, object?>(TriggerDetails(record), StringComparer.OrdinalIgnoreCase);
        details["playerVisible"] = false;
        return details;
    }

    private IReadOnlyList<Entity> ResolveTargets(TriggerRecord record)
    {
        var radiusMode = record.Radius > 0
            || record.Kind.Equals("aura", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("ward", StringComparison.OrdinalIgnoreCase);
        if (radiusMode && ResolveAnchorPoint(record) is { } anchorPoint)
        {
            return _state.Entities.Values
                .Where(entity => entity.TryGet<PositionComponent>(out var position)
                    && GameEngine.Distance(position.Position, anchorPoint) <= record.Radius
                    && MatchesFilter(record, entity))
                .OrderBy(entity => entity.Id.Value)
                .Take(8)
                .ToArray();
        }

        var explicitTarget = Text(record.EffectFields, "target", "");
        var target = string.IsNullOrWhiteSpace(explicitTarget)
            ? ResolveAnchorEntity(record) ?? _state.ControlledEntity
            : ResolveExplicitTarget(record, explicitTarget);
        if (target is null)
        {
            return Array.Empty<Entity>();
        }

        return MatchesFilter(record, target) ? new[] { target } : Array.Empty<Entity>();
    }

    private Entity? ResolveExplicitTarget(TriggerRecord record, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || target.Equals("anchor", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveAnchorEntity(record);
        }

        if (target.Equals("self", StringComparison.OrdinalIgnoreCase)
            || target.Equals("player", StringComparison.OrdinalIgnoreCase)
            || target.Equals("caster", StringComparison.OrdinalIgnoreCase))
        {
            return _state.ControlledEntity;
        }

        if (target.Equals("nearest_enemy", StringComparison.OrdinalIgnoreCase)
            || target.Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            return _engine.FindNearestHostile();
        }

        return _engine.EntityById(target);
    }

    private static bool HasRejected(IEnumerable<StateDelta> deltas) =>
        deltas.Any(IsRejectedDelta);

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta TriggerConsequenceRejectedDelta(TriggerRecord record, string error) =>
        new(
            "worldConsequenceRejected",
            record.Id,
            error,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "trigger_consequence",
                ["triggerId"] = record.Id,
                ["triggerName"] = record.Name,
                ["effectType"] = record.EffectType,
                ["error"] = error,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });

    private static StateDelta TriggerSkippedDelta(TriggerRecord record, IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = rejectedDeltas
            .Where(IsRejectedDelta)
            .Select(delta => delta.Summary)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StateDelta(
            "triggerSkipped",
            record.Id,
            $"Trigger {record.Id} was rolled back after a rejected consequence.",
            new Dictionary<string, object?>
            {
                ["triggerId"] = record.Id,
                ["triggerName"] = record.Name,
                ["effectType"] = record.EffectType,
                ["rejectedCount"] = errors.Length,
                ["errors"] = errors,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private static bool TryBuildTriggerConsequence(TriggerRecord record, out WorldConsequence consequence, string? targetOverride = null)
    {
        var consequenceType = TextOrNull(record.EffectFields, "consequenceType")
            ?? TextOrNull(record.EffectFields, "consequence_type");
        if (string.IsNullOrWhiteSpace(consequenceType))
        {
            consequence = default!;
            return false;
        }

        var payload = TriggerConsequencePayload(record);
        var target = targetOverride
            ?? TextOrNull(record.EffectFields, "targetEntityId")
            ?? TextOrNull(record.EffectFields, "target_entity_id")
            ?? TextOrNull(record.EffectFields, "target")
            ?? record.AnchorEntityId;
        consequence = new WorldConsequence(
            consequenceType,
            TextOrNull(record.EffectFields, "source") ?? "trigger",
            SourceEntityId: TextOrNull(record.EffectFields, "sourceEntityId")
                ?? TextOrNull(record.EffectFields, "source_entity_id")
                ?? record.SourceEntityId?.Value,
            TargetEntityId: target,
            Salience: IntOrNull(record.EffectFields, "salience") ?? 1,
            Confidence: IntOrNull(record.EffectFields, "confidence") ?? 100,
            Visibility: TextOrNull(record.EffectFields, "visibility") ?? WorldConsequenceVisibility.Hidden,
            Evidence: TextOrNull(record.EffectFields, "evidence") ?? record.Description,
            Reason: TextOrNull(record.EffectFields, "reason") ?? $"Trigger {record.Id} delivered {consequenceType}.",
            Payload: payload,
            Timing: TextOrNull(record.EffectFields, "timing") ?? WorldConsequenceTiming.Immediate);
        return true;
    }

    private static IReadOnlyDictionary<string, object?> TriggerConsequencePayload(TriggerRecord record)
    {
        var fields = record.EffectFields;
        var result = WorldConsequencePayloadBuilder.MergeNestedWithTopLevelFields(
            fields,
            TriggerConsequenceMetadataKeys,
            "consequencePayload",
            "consequence_payload",
            "payload");
        AddTriggerPayloadDetails(result, record);
        return result;
    }

    private static void AddTriggerPayloadDetails(Dictionary<string, object?> payload, TriggerRecord record)
    {
        payload.TryAdd("triggerId", record.Id);
        payload.TryAdd("triggerName", record.Name);
        payload.TryAdd("triggerKind", record.Kind);
        payload.TryAdd("triggerEffectType", record.EffectType);
        payload.TryAdd("playerVisible", record.PlayerVisible);
    }

    private static readonly HashSet<string> TriggerConsequenceMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
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

    private Entity? ResolveAnchorEntity(TriggerRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.AnchorEntityId))
        {
            return null;
        }

        return _engine.EntityById(record.AnchorEntityId);
    }

    private GridPoint? ResolveAnchorPoint(TriggerRecord record)
    {
        if (record.AnchorPoint is { } point)
        {
            return point;
        }

        var anchor = ResolveAnchorEntity(record);
        return anchor is not null && anchor.TryGet<PositionComponent>(out var position)
            ? position.Position
            : null;
    }

    private bool MatchesFilter(TriggerRecord record, Entity target)
    {
        var source = record.SourceEntityId is { } sourceId && _state.Entities.TryGetValue(sourceId, out var entity)
            ? entity
            : _state.ControlledEntity;
        return record.TargetFilter.Trim().ToLowerInvariant() switch
        {
            "all" or "everyone" or "any" => true,
            "self" or "caster" => target.Id == source.Id,
            "allies" or "ally" or "friendly" => target.Id == source.Id || !_engine.IsHostile(source, target),
            "enemies" or "enemy" or "hostile" or "foes" => target.Id != source.Id && _engine.IsHostile(source, target),
            _ => true,
        };
    }

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
