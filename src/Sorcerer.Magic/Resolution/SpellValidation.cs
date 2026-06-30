using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.References;
using Sorcerer.Magic.Operations;

namespace Sorcerer.Magic.Resolution;

public sealed record SpellValidationIssue(string Code, string Message, bool TechnicalFailure = false);

public sealed record SpellValidationReport(IReadOnlyList<SpellValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}

public sealed class SpellValidator
{
    private static readonly HashSet<string> SupportedCosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "mana",
        "health",
        "hp",
        "maxHealth",
        "max_health",
        "maxMana",
        "max_mana",
        "item",
        "status",
        "curse",
    };

    public SpellValidationReport Validate(
        GameEngine engine,
        SpellResolution resolution,
        OperationRegistry registry)
    {
        var issues = new List<SpellValidationIssue>();
        if (resolution.Accepted && resolution.Effects.Count == 0)
        {
            issues.Add(new SpellValidationIssue(
                "no_effects",
                "Accepted spell resolutions need at least one effect."));
        }

        foreach (var effect in resolution.Effects)
        {
            if (!registry.Supports(effect.Type))
            {
                issues.Add(new SpellValidationIssue(
                    "unsupported_effect",
                    $"Unsupported effect type {effect.Type}."));
                continue;
            }

            if (effect.Fields.TryGetValue("target", out var target))
            {
                var bound = ReferenceBinder.Bind(engine, ReferenceBinder.Normalize(target));
                if (!bound.Success)
                {
                    issues.Add(new SpellValidationIssue(
                        "invalid_target",
                        bound.Error ?? "Target could not be bound."));
                }
            }
        }

        foreach (var cost in resolution.Costs)
        {
            if (!SupportedCosts.Contains(cost.Type))
            {
                issues.Add(new SpellValidationIssue(
                    "unsupported_cost",
                    $"Unsupported cost type {cost.Type}."));
                continue;
            }

            if (cost.Type.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                ValidateItemCost(engine, cost, issues);
            }
        }

        return new SpellValidationReport(issues);
    }

    private static void ValidateItemCost(
        GameEngine engine,
        SpellCost cost,
        List<SpellValidationIssue> issues)
    {
        var name = ReadString(cost.Fields, "item", ReadString(cost.Fields, "name", ""));
        var quantity = Math.Max(1, ReadInt(cost.Fields, "quantity", ReadInt(cost.Fields, "amount", 1)));
        var allowProtected = ReadBool(cost.Fields, "allowProtected", ReadBool(cost.Fields, "allow_protected", false));
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new SpellValidationIssue("item_cost_missing_name", "Item cost does not name an item."));
            return;
        }

        if (!engine.State.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
            || !inventory.Items.TryGetValue(name, out var available)
            || available < quantity)
        {
            issues.Add(new SpellValidationIssue("item_cost_unavailable", $"Item cost is unavailable: {name}."));
            return;
        }

        if (!allowProtected && inventory.TreasuredItems.Contains(name))
        {
            issues.Add(new SpellValidationIssue(
                "item_cost_protected",
                $"Item cost would consume protected item: {name}."));
        }
    }

    internal static string ReadString(IReadOnlyDictionary<string, object?> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    internal static int ReadInt(IReadOnlyDictionary<string, object?> fields, string key, int fallback) =>
        fields.TryGetValue(key, out var value) && int.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : fallback;

    internal static bool ReadBool(IReadOnlyDictionary<string, object?> fields, string key, bool fallback) =>
        fields.TryGetValue(key, out var value) && bool.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : fallback;
}
