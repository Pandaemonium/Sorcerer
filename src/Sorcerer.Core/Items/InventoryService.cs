using Sorcerer.Core.Entities;
using Sorcerer.Core.Views;

namespace Sorcerer.Core.Items;

public sealed class InventoryService
{
    private readonly ItemCatalog _catalog;

    public InventoryService(ItemCatalog catalog)
    {
        _catalog = catalog;
    }

    public IReadOnlyList<ItemCard> BuildInventoryCards(Entity entity)
    {
        if (!entity.TryGet<InventoryComponent>(out var inventory))
        {
            return Array.Empty<ItemCard>();
        }

        var equipment = entity.TryGet<EquipmentComponent>(out var equipped)
            ? equipped
            : EquipmentComponent.Empty();
        var equippedItems = new HashSet<string>(equipment.Slots.Values, StringComparer.OrdinalIgnoreCase);

        return inventory.Items
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                var definition = _catalog.Find(pair.Key);
                var value = definition?.Value ?? 1;
                return new ItemCard(
                    pair.Key,
                    definition?.Name ?? pair.Key,
                    pair.Value,
                    value,
                    definition?.Material ?? "unknown",
                    definition?.Tags ?? Array.Empty<string>(),
                    inventory.TreasuredItems.Contains(pair.Key),
                    equippedItems.Contains(pair.Key),
                    equipment.FocusSlots.Any(slot => equipment.Slots.TryGetValue(slot, out var item)
                        && item.Equals(pair.Key, StringComparison.OrdinalIgnoreCase)));
            })
            .ToArray();
    }

    public IReadOnlyList<ReagentCard> BuildReagentCards(Entity entity)
    {
        if (!entity.TryGet<InventoryComponent>(out var inventory))
        {
            return Array.Empty<ReagentCard>();
        }

        return inventory.Items
            .Where(pair => !inventory.TreasuredItems.Contains(pair.Key))
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                var definition = _catalog.Find(pair.Key);
                var unitValue = definition?.Value ?? 1;
                return new ReagentCard(
                    definition?.Name ?? pair.Key,
                    pair.Value,
                    unitValue,
                    unitValue * pair.Value,
                    definition?.Material ?? "unknown",
                    definition?.Tags ?? Array.Empty<string>());
            })
            .ToArray();
    }
}
