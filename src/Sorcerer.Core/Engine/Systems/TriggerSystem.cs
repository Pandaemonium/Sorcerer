using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
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
        _state.Triggers.RemoveExpired(_state.Turn);
        foreach (var record in _state.Triggers.Due(_state.Turn))
        {
            deltas.AddRange(Apply(record));
            var remaining = record.RemainingUses - 1;
            if (remaining <= 0)
            {
                _state.Triggers.Remove(record.Id);
                continue;
            }

            var next = record.NextTurn + Math.Max(1, record.Interval);
            if (record.ExpiresTurn is not null && next > record.ExpiresTurn)
            {
                _state.Triggers.Remove(record.Id);
                continue;
            }

            _state.Triggers.Replace(record with
            {
                NextTurn = next,
                RemainingUses = remaining,
            });
        }

        _state.Triggers.RemoveExpired(_state.Turn);
        return deltas;
    }

    private IReadOnlyList<StateDelta> Apply(TriggerRecord record)
    {
        var deltas = new List<StateDelta>();
        if (!string.IsNullOrWhiteSpace(record.Description))
        {
            _state.AddMessage(record.Description);
            deltas.Add(new StateDelta(
                "trigger",
                record.Id,
                record.Description,
                new Dictionary<string, object?>
                {
                    ["effectType"] = record.EffectType,
                    ["turn"] = _state.Turn,
                }));
        }

        switch (record.EffectType.Trim().ToLowerInvariant())
        {
            case "message":
                {
                    var text = Text(record.EffectFields, "text", record.Description);
                    if (!string.IsNullOrWhiteSpace(text) && !text.Equals(record.Description, StringComparison.Ordinal))
                    {
                        _state.AddMessage(text);
                        deltas.Add(new StateDelta("message", record.Id, text, new Dictionary<string, object?>()));
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
                        .Select(target => _engine.ApplyStatus(target, status, duration, displayName)));
                    return deltas;
                }
            case "damage":
            case "harm":
                {
                    var amount = Int(record.EffectFields, "amount", 3, min: 1, max: 40);
                    var damageType = Text(record.EffectFields, "damageType", Text(record.EffectFields, "damage_type", "trigger"));
                    deltas.AddRange(ResolveTargets(record)
                        .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
                        .Select(target => _engine.DamageEntity(target, amount, damageType)));
                    return deltas;
                }
            case "heal":
            case "restorehealth":
                {
                    var amount = Int(record.EffectFields, "amount", 3, min: 1, max: 40);
                    deltas.AddRange(ResolveTargets(record)
                        .Where(target => target.TryGet<ActorComponent>(out var actor) && actor.Alive)
                        .Select(target => _engine.HealEntity(target, amount)));
                    return deltas;
                }
            default:
                _state.AddMessage($"{record.Name} fails to find an engine effect named {record.EffectType}.");
                return deltas;
        }
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
        var target = ResolveExplicitTarget(record, explicitTarget)
            ?? ResolveAnchorEntity(record)
            ?? _state.ControlledEntity;
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

    private static int Int(IReadOnlyDictionary<string, object?> fields, string key, int fallback, int min, int max)
    {
        var value = fields.TryGetValue(key, out var raw) && int.TryParse(Convert.ToString(raw), out var parsed)
            ? parsed
            : fallback;
        return Math.Clamp(value, min, max);
    }
}
