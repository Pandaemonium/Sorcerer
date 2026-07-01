using Sorcerer.Core.Characters;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class CombatSystem
{
    private readonly GameState _state;

    public CombatSystem(GameState state)
    {
        _state = state;
    }

    public StateDelta DamageEntity(Entity target, int amount, string damageType)
    {
        var actor = target.Get<ActorComponent>();
        var actual = Math.Max(1, amount - actor.Defense);
        var updated = actor with { HitPoints = Math.Max(0, actor.HitPoints - actual) };
        target.Set(updated);
        if (!updated.Alive)
        {
            MarkDefeated(target);
        }

        var message = updated.Alive
            ? $"{Subject(target)} {Verb(target, "take", "takes")} {actual} {damageType} damage."
            : $"{Subject(target)} {Verb(target, "fall", "falls")}.";
        _state.AddMessage(message);

        return new StateDelta(
            "damage",
            target.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["amount"] = actual,
                ["damageType"] = damageType,
            });
    }

    public StateDelta AttackEntity(Entity attacker, Entity defender, string damageType = "physical")
    {
        var attackerActor = attacker.Get<ActorComponent>();
        var defenderActor = defender.Get<ActorComponent>();
        var actual = Math.Max(1, attackerActor.Attack - defenderActor.Defense);
        var updated = defenderActor with { HitPoints = Math.Max(0, defenderActor.HitPoints - actual) };
        defender.Set(updated);
        if (!updated.Alive)
        {
            MarkDefeated(defender);
        }

        var message = updated.Alive
            ? $"{Subject(attacker)} {Verb(attacker, "strike", "strikes")} {ObjectName(defender)} for {actual} {damageType} damage."
            : $"{Subject(attacker)} {Verb(attacker, "drop", "drops")} {ObjectName(defender)}.";
        _state.AddMessage(message);
        return new StateDelta(
            "attack",
            defender.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["attacker"] = attacker.Id.Value,
                ["amount"] = actual,
                ["damageType"] = damageType,
            });
    }

    public StateDelta RestoreMana(Entity target, int amount)
    {
        var actor = target.Get<ActorComponent>();
        var soul = CharacterMath.EnsureSoulRecord(_state, target);
        var restored = Math.Max(0, Math.Min(amount, soul.MaxMana - soul.Mana));
        var updatedSoul = soul with { Mana = soul.Mana + restored };
        _state.Souls.Set(updatedSoul);
        target.Set(actor with { Mana = updatedSoul.Mana, MaxMana = updatedSoul.MaxMana });
        var message = restored == 0
            ? $"{Subject(target)} {Verb(target, "are", "is")} already bright with mana."
            : $"{Subject(target)} {Verb(target, "regain", "regains")} {restored} mana.";
        _state.AddMessage(message);
        return new StateDelta(
            "restoreMana",
            target.Id.Value,
            message,
            new Dictionary<string, object?> { ["amount"] = restored });
    }

    public StateDelta HealEntity(Entity target, int amount)
    {
        var actor = target.Get<ActorComponent>();
        var healed = Math.Max(0, Math.Min(amount, actor.MaxHitPoints - actor.HitPoints));
        target.Set(actor with { HitPoints = actor.HitPoints + healed });
        var message = healed == 0
            ? $"{Subject(target)} {Verb(target, "are", "is")} already whole."
            : $"{Subject(target)} {Verb(target, "heal", "heals")} {healed} HP.";
        _state.AddMessage(message);

        return new StateDelta(
            "heal",
            target.Id.Value,
            message,
            new Dictionary<string, object?> { ["amount"] = healed });
    }

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string ObjectName(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "you" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;

    internal static void MarkDefeated(Entity entity)
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
}
