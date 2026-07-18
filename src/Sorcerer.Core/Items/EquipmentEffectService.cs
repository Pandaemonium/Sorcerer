using Sorcerer.Core.Entities;

namespace Sorcerer.Core.Items;

/// <summary>
/// The single owner of derived equipment effects (WP2, docs/CONTENT_SPRINT_PLAN.md). Every reader
/// — the strike site, the damage/resistance site, the character sheet, resolver context — goes
/// through here, and it never mutates an actor's base stats. It aggregates the modifiers of the
/// items an entity has equipped into a cached <see cref="EquipmentEffectComponent"/>; focus bias is
/// contributed only by items in a focused slot.
/// </summary>
public static class EquipmentEffectService
{
    public static EquipmentEffectComponent Compute(Entity entity, ItemCatalog catalog)
    {
        if (!entity.TryGet<EquipmentComponent>(out var equipment) || equipment.Slots.Count == 0)
        {
            return EquipmentEffectComponent.Empty;
        }

        var attack = 0;
        var defense = 0;
        var resistances = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var weaknesses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var focusBias = new List<string>();

        foreach (var (slot, itemKey) in equipment.Slots.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            var modifier = catalog.Find(itemKey)?.Modifier;
            if (modifier is null)
            {
                continue;
            }

            attack += modifier.Attack;
            defense += modifier.Defense;
            Merge(resistances, modifier.Resistances);
            Merge(weaknesses, modifier.Weaknesses);
            if (equipment.FocusSlots.Contains(slot) && !string.IsNullOrWhiteSpace(modifier.FocusBias))
            {
                focusBias.Add(modifier.FocusBias);
            }
        }

        if (attack == 0 && defense == 0 && resistances.Count == 0 && weaknesses.Count == 0 && focusBias.Count == 0)
        {
            return EquipmentEffectComponent.Empty;
        }

        return new EquipmentEffectComponent(attack, defense, resistances, weaknesses, focusBias);
    }

    /// <summary>Recomputes and re-stamps the cache. Removes it when equipment produces no effect so
    /// a bare actor carries no dead component.</summary>
    public static EquipmentEffectComponent Recompute(Entity entity, ItemCatalog catalog)
    {
        var effect = Compute(entity, catalog);
        if (effect.IsMeaningful)
        {
            entity.Set(effect);
        }
        else if (entity.Has<EquipmentEffectComponent>())
        {
            entity.Remove<EquipmentEffectComponent>();
        }

        return effect;
    }

    public static EquipmentEffectComponent Effect(Entity entity) =>
        entity.TryGet<EquipmentEffectComponent>(out var effect) ? effect : EquipmentEffectComponent.Empty;

    /// <summary>Human-readable one-line effect of an equipment modifier, so equip/inspect/character
    /// and inventory views can tell a player what a piece of gear actually does.</summary>
    public static string Summary(EquipmentModifier? modifier)
    {
        if (modifier is null || !modifier.IsMeaningful)
        {
            return "";
        }

        var parts = new List<string>();
        if (modifier.Attack != 0)
        {
            parts.Add($"{Signed(modifier.Attack)} attack");
        }

        if (modifier.Defense != 0)
        {
            parts.Add($"{Signed(modifier.Defense)} defense");
        }

        if (modifier.Resistances is { Count: > 0 })
        {
            parts.Add(string.Join(", ", modifier.Resistances.Select(pair => $"{pair.Value}% {pair.Key} resist")));
        }

        if (modifier.Weaknesses is { Count: > 0 })
        {
            parts.Add(string.Join(", ", modifier.Weaknesses.Select(pair => $"{pair.Value}% {pair.Key} weakness")));
        }

        if (!string.IsNullOrWhiteSpace(modifier.FocusBias))
        {
            parts.Add($"focus: {modifier.FocusBias}");
        }

        return parts.Count == 0 ? "" : $"({string.Join("; ", parts)})";
    }

    private static string Signed(int value) => value >= 0 ? $"+{value}" : value.ToString();

    private static void Merge(Dictionary<string, int> target, IReadOnlyDictionary<string, int>? source)
    {
        if (source is null)
        {
            return;
        }

        foreach (var (key, value) in source)
        {
            target[key] = target.GetValueOrDefault(key) + value;
        }
    }
}
