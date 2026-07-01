using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
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
            deltas.AddRange(record.LinkPartnerId is not null
                ? MirrorSympatheticLink(record, hitAmount)
                : Fire(record, anchor, counterpart));
            _state.PersistentEffects.Consume(record.Id);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> Fire(PersistentEffectRecord record, Entity anchor, Entity counterpart)
    {
        var target = Text(record.EffectFields, "target", "other").Trim().ToLowerInvariant() switch
        {
            "self" or "anchor" => anchor,
            _ => counterpart,
        };
        if (!target.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            return Array.Empty<StateDelta>();
        }

        return record.EffectType.Trim().ToLowerInvariant() switch
        {
            "damage" or "harm" => new[]
            {
                _engine.DamageEntity(
                    target,
                    Int(record.EffectFields, "amount", 3, 1, 40),
                    Text(record.EffectFields, "damageType", Text(record.EffectFields, "damage_type", "persistent"))),
            },
            "heal" or "restorehealth" => new[] { _engine.HealEntity(target, Int(record.EffectFields, "amount", 3, 1, 40)) },
            "addstatus" or "status" or "applystatus" => new[]
            {
                _engine.ApplyStatus(
                    target,
                    Text(record.EffectFields, "status", Text(record.EffectFields, "name", "marked")),
                    Int(record.EffectFields, "duration", 0, 0, 99),
                    Text(record.EffectFields, "displayName", Text(record.EffectFields, "display_name", ""))),
            },
            "message" => FireMessage(record),
            _ => UnsupportedEffect(record),
        };
    }

    private IReadOnlyList<StateDelta> UnsupportedEffect(PersistentEffectRecord record)
    {
        var text = $"Persistent effect {record.Id} cannot fire unsupported effect {record.EffectType}.";
        _state.AddMessage(text);
        return new[] { new StateDelta("persistentEffectFailed", record.Id, text, new Dictionary<string, object?>()) };
    }

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

        return new[] { _engine.DamageEntity(partner, hitAmount, "sympathetic") };
    }

    private IReadOnlyList<StateDelta> FireMessage(PersistentEffectRecord record)
    {
        var text = Text(record.EffectFields, "text", "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<StateDelta>();
        }

        _state.AddMessage(text);
        return new[] { new StateDelta("message", record.Id, text, new Dictionary<string, object?>()) };
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
