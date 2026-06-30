using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic.Costs;

public static class SpellCostApplier
{
    public static IReadOnlyList<StateDelta> Apply(GameEngine engine, IReadOnlyList<SpellCost> costs)
    {
        var deltas = new List<StateDelta>();
        foreach (var cost in costs)
        {
            switch (cost.Type)
            {
                case "mana":
                    deltas.Add(SpendMana(engine, SpellValidator.ReadInt(cost.Fields, "amount", 1)));
                    break;
                case "health":
                case "hp":
                    deltas.Add(SpendHealth(engine, SpellValidator.ReadInt(cost.Fields, "amount", 1), max: false));
                    break;
                case "maxHealth":
                case "max_health":
                    deltas.Add(SpendHealth(engine, SpellValidator.ReadInt(cost.Fields, "amount", 1), max: true));
                    break;
                case "maxMana":
                case "max_mana":
                    deltas.Add(SpendMaxMana(engine, SpellValidator.ReadInt(cost.Fields, "amount", 1)));
                    break;
                case "item":
                    deltas.Add(SpendItem(engine, cost));
                    break;
                case "status":
                    deltas.Add(AddStatusCost(engine, cost));
                    break;
                case "curse":
                    deltas.Add(AddCurseCost(engine, cost));
                    break;
            }
        }

        return deltas;
    }

    private static StateDelta SpendMana(GameEngine engine, int amount)
    {
        var entity = engine.State.ControlledEntity;
        var actor = entity.Get<ActorComponent>();
        var spent = Math.Min(Math.Max(0, amount), actor.Mana);
        entity.Set(actor with { Mana = actor.Mana - spent });
        var message = $"Cost: {spent} mana.";
        engine.AddMessage(message);
        return new StateDelta("cost:mana", entity.Id.Value, message, new Dictionary<string, object?> { ["amount"] = spent });
    }

    private static StateDelta SpendHealth(GameEngine engine, int amount, bool max)
    {
        var entity = engine.State.ControlledEntity;
        var actor = entity.Get<ActorComponent>();
        var spent = Math.Max(0, amount);
        var updated = max
            ? actor with
            {
                MaxHitPoints = Math.Max(1, actor.MaxHitPoints - spent),
                HitPoints = Math.Min(actor.HitPoints, Math.Max(1, actor.MaxHitPoints - spent)),
            }
            : actor with { HitPoints = Math.Max(1, actor.HitPoints - spent) };
        entity.Set(updated);
        var message = max ? $"Cost: {spent} max HP." : $"Cost: {spent} HP.";
        engine.AddMessage(message);
        return new StateDelta(max ? "cost:maxHealth" : "cost:health", entity.Id.Value, message, new Dictionary<string, object?> { ["amount"] = spent });
    }

    private static StateDelta SpendMaxMana(GameEngine engine, int amount)
    {
        var entity = engine.State.ControlledEntity;
        var actor = entity.Get<ActorComponent>();
        var spent = Math.Max(0, amount);
        entity.Set(actor with
        {
            MaxMana = Math.Max(0, actor.MaxMana - spent),
            Mana = Math.Min(actor.Mana, Math.Max(0, actor.MaxMana - spent)),
        });
        var message = $"Cost: {spent} max mana.";
        engine.AddMessage(message);
        return new StateDelta("cost:maxMana", entity.Id.Value, message, new Dictionary<string, object?> { ["amount"] = spent });
    }

    private static StateDelta SpendItem(GameEngine engine, SpellCost cost)
    {
        var name = SpellValidator.ReadString(cost.Fields, "item", SpellValidator.ReadString(cost.Fields, "name", ""));
        var quantity = Math.Max(1, SpellValidator.ReadInt(cost.Fields, "quantity", SpellValidator.ReadInt(cost.Fields, "amount", 1)));
        var inventory = engine.State.ControlledEntity.Get<InventoryComponent>();
        inventory.Items[name] -= quantity;
        if (inventory.Items[name] <= 0)
        {
            inventory.Items.Remove(name);
            inventory.TreasuredItems.Remove(name);
        }

        var message = $"Cost: {quantity} {name}.";
        engine.AddMessage(message);
        return new StateDelta("cost:item", engine.State.ControlledEntityId.Value, message, new Dictionary<string, object?> { ["item"] = name, ["quantity"] = quantity });
    }

    private static StateDelta AddStatusCost(GameEngine engine, SpellCost cost)
    {
        var status = SpellValidator.ReadString(cost.Fields, "status", "strained");
        var duration = SpellValidator.ReadInt(cost.Fields, "duration", 4);
        var entity = engine.State.ControlledEntity;
        var current = entity.TryGet<StatusContainerComponent>(out var container)
            ? container.Statuses.ToList()
            : new List<StatusInstance>();
        current.Add(new StatusInstance(status, status, engine.State.Turn + duration));
        entity.Set(new StatusContainerComponent(current));
        var message = $"Cost: {status}.";
        engine.AddMessage(message);
        return new StateDelta("cost:status", entity.Id.Value, message, new Dictionary<string, object?> { ["status"] = status, ["duration"] = duration });
    }

    private static StateDelta AddCurseCost(GameEngine engine, SpellCost cost)
    {
        var name = SpellValidator.ReadString(cost.Fields, "name", SpellValidator.ReadString(cost.Fields, "id", "Wild Debt"));
        var text = SpellValidator.ReadString(cost.Fields, "description", name);
        var promise = engine.State.PromiseLedger.Add("debt", text, playerVisible: true);
        var message = $"Cost: {name}.";
        engine.AddMessage(message);
        return new StateDelta("cost:curse", promise.Id, message, new Dictionary<string, object?> { ["name"] = name, ["promiseId"] = promise.Id });
    }
}
