using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
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
                    if (BitingAmount(cost.Fields, "amount", 1) is { } manaAmount)
                    {
                        deltas.AddRange(SpendMana(engine, manaAmount));
                    }

                    break;
                case "health":
                case "hp":
                    if (BitingAmount(cost.Fields, "amount", 1) is { } healthAmount)
                    {
                        deltas.AddRange(SpendHealth(engine, healthAmount, max: false));
                    }

                    break;
                case "maxHealth":
                case "max_health":
                    if (BitingAmount(cost.Fields, "amount", 1) is { } maxHealthAmount)
                    {
                        deltas.AddRange(SpendHealth(engine, maxHealthAmount, max: true));
                    }

                    break;
                case "maxMana":
                case "max_mana":
                    if (BitingAmount(cost.Fields, "amount", 1) is { } maxManaAmount)
                    {
                        deltas.AddRange(SpendMaxMana(engine, maxManaAmount));
                    }

                    break;
                case "item":
                    deltas.AddRange(SpendItem(engine, cost));
                    break;
                case "status":
                    deltas.AddRange(AddStatusCost(engine, cost));
                    break;
                case "curse":
                    deltas.AddRange(AddCurseCost(engine, cost));
                    break;
            }
        }

        return deltas;
    }

    /// <summary>
    /// A specified cost must usually bite: a negative or unparseable amount floors to at least 1
    /// instead of silently voiding the cost (mirrors Wild Magic's <c>_positive_cost_amount</c>
    /// discipline). An explicit <c>0</c> is still honored as the model's deliberate signal that
    /// this cost is free (returns null, meaning "apply nothing") rather than being corrected —
    /// that reading is the established Sorcerer contract and only negative/missing amounts are a
    /// clear model mistake worth overriding.
    /// </summary>
    private static int? BitingAmount(IReadOnlyDictionary<string, object?> fields, string key, int fallback)
    {
        var value = SpellValidator.ReadInt(fields, key, fallback);
        return value == 0 ? null : Math.Max(1, Math.Abs(value));
    }

    private static IReadOnlyList<StateDelta> SpendMana(GameEngine engine, int amount)
    {
        var entity = engine.State.ControlledEntity;
        var available = entity.Get<Sorcerer.Core.Entities.ActorComponent>().Mana;
        var spent = Math.Min(Math.Max(0, amount), available);
        return ApplyCostConsequence(engine, WorldConsequence.AdjustActorResource(
            "spell_cost",
            entity.Id.Value,
            "mana",
            -amount,
            min: 0,
            sourceEntityId: entity.Id.Value,
            operation: "cost:mana",
            emitMessage: true,
            message: $"Cost: {spent} mana.",
            details: new Dictionary<string, object?> { ["requestedAmount"] = amount }));
    }

    private static IReadOnlyList<StateDelta> SpendHealth(GameEngine engine, int amount, bool max)
    {
        var entity = engine.State.ControlledEntity;
        var spent = Math.Max(0, amount);
        var message = max ? $"Cost: {spent} max HP." : $"Cost: {spent} HP.";
        return ApplyCostConsequence(engine, WorldConsequence.AdjustActorResource(
            "spell_cost",
            entity.Id.Value,
            max ? "max_health" : "health",
            -spent,
            min: 1,
            sourceEntityId: entity.Id.Value,
            operation: max ? "cost:maxHealth" : "cost:health",
            emitMessage: true,
            message: message,
            details: new Dictionary<string, object?> { ["requestedAmount"] = amount }));
    }

    private static IReadOnlyList<StateDelta> SpendMaxMana(GameEngine engine, int amount)
    {
        var entity = engine.State.ControlledEntity;
        var spent = Math.Max(0, amount);
        return ApplyCostConsequence(engine, WorldConsequence.AdjustActorResource(
            "spell_cost",
            entity.Id.Value,
            "max_mana",
            -spent,
            min: 0,
            sourceEntityId: entity.Id.Value,
            operation: "cost:maxMana",
            emitMessage: true,
            message: $"Cost: {spent} max mana.",
            details: new Dictionary<string, object?> { ["requestedAmount"] = amount }));
    }

    private static IReadOnlyList<StateDelta> SpendItem(GameEngine engine, SpellCost cost)
    {
        var name = SpellValidator.ReadString(cost.Fields, "item", SpellValidator.ReadString(cost.Fields, "name", ""));
        var quantity = Math.Max(1, SpellValidator.ReadInt(cost.Fields, "quantity", SpellValidator.ReadInt(cost.Fields, "amount", 1)));
        var message = $"Cost: {quantity} {name}.";
        return ApplyCostConsequence(engine, WorldConsequence.ModifyInventory(
            "spell_cost",
            engine.State.ControlledEntityId.Value,
            name,
            op: "consume",
            amount: quantity,
            sourceEntityId: engine.State.ControlledEntityId.Value,
            operation: "cost:item",
            details: new Dictionary<string, object?>
            {
                ["quantity"] = quantity,
                ["message"] = message,
                ["emitMessage"] = true,
            }));
    }

    private static IReadOnlyList<StateDelta> AddStatusCost(GameEngine engine, SpellCost cost)
    {
        var status = SpellValidator.ReadString(cost.Fields, "status", "strained");
        var duration = SpellValidator.ReadInt(cost.Fields, "duration", 4);
        var message = $"Cost: {status}.";
        return ApplyCostConsequence(engine, WorldConsequence.ApplyStatus(
            "spell_cost",
            engine.State.ControlledEntityId.Value,
            status,
            duration,
            status,
            sourceEntityId: engine.State.ControlledEntityId.Value,
            operation: "cost:status",
            details: new Dictionary<string, object?>
            {
                ["message"] = message,
            }));
    }

    private static IReadOnlyList<StateDelta> AddCurseCost(GameEngine engine, SpellCost cost)
    {
        var name = SpellValidator.ReadString(cost.Fields, "name", SpellValidator.ReadString(cost.Fields, "id", "Wild Debt"));
        var text = SpellValidator.ReadString(cost.Fields, "description", name);

        return ApplyCostConsequence(engine, WorldConsequence.CreatePromise(
            "spell_cost",
            "debt",
            text,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: engine.State.ControlledEntityId.Value,
            operation: "cost:curse",
            stackExisting: true,
            useCurrentRegionAsClaimedPlace: false,
            autoBind: false,
            message: $"Cost: {name}.",
            stackMessageTemplate: $"Cost: {name} deepens ({{stacks}} stacks).",
            details: new Dictionary<string, object?> { ["name"] = name }));
    }

    private static IReadOnlyList<StateDelta> ApplyCostConsequence(GameEngine engine, WorldConsequence consequence)
    {
        var applied = engine.ApplyConsequence(consequence);
        return applied.Deltas.Count > 0
            ? applied.Deltas
            : new[]
            {
                new StateDelta(
                    "worldConsequenceRejected",
                    engine.State.ControlledEntityId.Value,
                    applied.Error ?? "Spell cost could not be applied.",
                    HiddenAuditDetails(applied.Details)),
            };
    }

    private static IReadOnlyDictionary<string, object?> HiddenAuditDetails(IReadOnlyDictionary<string, object?> details)
    {
        var hidden = new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase)
        {
            ["auditOnly"] = true,
            ["playerVisible"] = false,
        };
        return hidden;
    }
}
