using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class ItemSystem
{
    private readonly GameEngine _engine;
    private readonly InventoryService _inventoryService;
    private readonly ItemCatalog _itemCatalog;
    private readonly GameState _state;

    public ItemSystem(
        GameEngine engine,
        ItemCatalog itemCatalog,
        InventoryService inventoryService)
    {
        _engine = engine;
        _itemCatalog = itemCatalog;
        _inventoryService = inventoryService;
        _state = engine.State;
    }

    public ActionResult Pickup(string? target)
    {
        var turnBefore = _state.Turn;
        var item = ResolveNearbyEntity(target, entity => entity.Has<ItemComponent>(), range: 1);
        if (item is null)
        {
            return ActionResult.Simple(
                "pickup",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                "There is nothing here you can pick up.");
        }

        var itemComponent = item.Get<ItemComponent>();
        var quantity = item.TryGet<StackComponent>(out var stack) ? Math.Max(1, stack.Quantity) : 1;
        var key = InventoryKey(item, itemComponent);
        var inventory = EnsureInventory(_state.ControlledEntity);
        inventory.Items.TryGetValue(key, out var current);
        inventory.Items[key] = current + quantity;
        _state.Entities.Remove(item.Id);

        var message = quantity == 1
            ? $"You pick up {key}."
            : $"You pick up {quantity} {key}.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return ActionResult.Simple("pickup", true, true, turnBefore, _state.Turn, message);
    }

    public ActionResult DropItem(string item)
    {
        var turnBefore = _state.Turn;
        var inventory = EnsureInventory(_state.ControlledEntity);
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple(
                "drop",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                $"You are not carrying {item}.");
        }

        ChangeInventory(inventory, key, -1);
        var position = _state.ControlledEntity.Get<PositionComponent>().Position;
        var dropped = BuildItemEntity(key, position, quantity: 1);
        _state.Entities[dropped.Id] = dropped;

        var message = $"You drop {key}.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "drop",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = new[] { message },
            Deltas = new[]
            {
                new StateDelta(
                    "drop",
                    dropped.Id.Value,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["item"] = key,
                        ["x"] = position.X,
                        ["y"] = position.Y,
                    }),
            },
        };
    }

    public ActionResult UseItem(string item)
    {
        var turnBefore = _state.Turn;
        var actor = _state.ControlledEntity;
        var inventory = EnsureInventory(actor);
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple(
                "use",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                $"You are not carrying {item}.");
        }

        var definition = _itemCatalog.Find(key);
        var profile = definition?.UseProfile ?? "inert";
        if (profile.Equals("inert", StringComparison.OrdinalIgnoreCase)
            || profile.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Simple(
                "use",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                $"{key} has no immediate use.");
        }

        var useMessage = $"You use {key}.";
        _state.AddMessage(useMessage);
        var deltas = new List<StateDelta>();
        if (profile.StartsWith("heal:", StringComparison.OrdinalIgnoreCase))
        {
            deltas.Add(_engine.HealEntity(actor, ParseProfileAmount(profile, fallback: 4)));
        }
        else if (profile.StartsWith("mana:", StringComparison.OrdinalIgnoreCase))
        {
            deltas.Add(_engine.RestoreMana(actor, ParseProfileAmount(profile, fallback: 4)));
        }
        else
        {
            return ActionResult.Simple(
                "use",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                $"{key} resists ordinary use.");
        }

        ChangeInventory(inventory, key, -1);
        _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "use",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = new[] { useMessage }.Concat(deltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult EquipItem(string item)
    {
        var turnBefore = _state.Turn;
        var actor = _state.ControlledEntity;
        var inventory = EnsureInventory(actor);
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple("equip", false, false, turnBefore, _state.Turn, $"You are not carrying {item}.");
        }

        var definition = _itemCatalog.Find(key);
        var slot = definition?.EquipmentSlot;
        if (string.IsNullOrWhiteSpace(slot))
        {
            return ActionResult.Simple("equip", false, false, turnBefore, _state.Turn, $"{key} cannot be equipped.");
        }

        var equipment = EnsureEquipment(actor);
        equipment.Slots[slot] = key;
        var message = $"You equip {key} in your {slot}.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return ActionResult.Simple("equip", true, true, turnBefore, _state.Turn, message);
    }

    public ActionResult UnequipItem(string slotOrItem)
    {
        var turnBefore = _state.Turn;
        var equipment = EnsureEquipment(_state.ControlledEntity);
        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            return ActionResult.Simple("unequip", false, false, turnBefore, _state.Turn, $"{slotOrItem} is not equipped.");
        }

        var item = equipment.Slots[slot];
        equipment.Slots.Remove(slot);
        equipment.FocusSlots.Remove(slot);
        var message = $"You unequip {item}.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return ActionResult.Simple("unequip", true, true, turnBefore, _state.Turn, message);
    }

    public ActionResult FocusItem(string slotOrItem)
    {
        var turnBefore = _state.Turn;
        var equipment = EnsureEquipment(_state.ControlledEntity);
        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            return ActionResult.Simple("focus", false, false, turnBefore, _state.Turn, $"{slotOrItem} is not equipped.");
        }

        equipment.FocusSlots.Add(slot);
        var message = $"{equipment.Slots[slot]} is now your magical focus.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return ActionResult.Simple("focus", true, true, turnBefore, _state.Turn, message);
    }

    public ActionResult UnfocusItem(string? slotOrItem)
    {
        var turnBefore = _state.Turn;
        var equipment = EnsureEquipment(_state.ControlledEntity);
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            equipment.FocusSlots.Clear();
            var cleared = "You release your magical focus.";
            _state.AddMessage(cleared);
            return ActionResult.Simple("unfocus", true, false, turnBefore, _state.Turn, cleared);
        }

        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null || !equipment.FocusSlots.Remove(slot))
        {
            return ActionResult.Simple("unfocus", false, false, turnBefore, _state.Turn, $"{slotOrItem} is not focused.");
        }

        var message = $"{equipment.Slots[slot]} is no longer your focus.";
        _state.AddMessage(message);
        return ActionResult.Simple("unfocus", true, false, turnBefore, _state.Turn, message);
    }

    public ActionResult Reagents()
    {
        var cards = _inventoryService.BuildReagentCards(_state.ControlledEntity);
        var messages = cards.Count == 0
            ? new[] { "You have no unprotected reagents." }
            : cards.Select(FormatReagent).ToArray();
        return ActionResult.Simple("reagents", true, false, _state.Turn, _state.Turn, messages);
    }

    public ActionResult Wares(string? target)
    {
        var merchant = ResolveNearbyMerchant(target);
        if (merchant is null)
        {
            return ActionResult.Simple("wares", false, false, _state.Turn, _state.Turn, "No nearby merchant is ready to trade.");
        }

        var wares = merchant.Get<MerchantComponent>().Wares
            .Where(pair => pair.Value > 0)
            .OrderBy(pair => pair.Key)
            .Select(pair =>
            {
                var definition = _itemCatalog.Find(pair.Key);
                var name = definition?.Name ?? pair.Key;
                var value = definition?.Value ?? 1;
                return $"{merchant.Name} offers {pair.Value}x {name} for {value} gold each.";
            })
            .ToArray();
        return ActionResult.Simple(
            "wares",
            true,
            false,
            _state.Turn,
            _state.Turn,
            wares.Length == 0 ? new[] { $"{merchant.Name} has nothing for sale." } : wares);
    }

    public ActionResult Buy(string item, string? target)
    {
        var turnBefore = _state.Turn;
        var merchant = ResolveNearbyMerchant(target);
        if (merchant is null)
        {
            return ActionResult.Simple("buy", false, false, turnBefore, _state.Turn, "No nearby merchant is ready to trade.");
        }

        var merchantComponent = merchant.Get<MerchantComponent>();
        var wareKey = FindWareKey(merchantComponent, item);
        if (wareKey is null)
        {
            return ActionResult.Simple("buy", false, false, turnBefore, _state.Turn, $"{merchant.Name} is not selling {item}.");
        }

        var definition = _itemCatalog.Find(wareKey);
        var price = Math.Max(1, definition?.Value ?? 1);
        var inventory = EnsureInventory(_state.ControlledEntity);
        inventory.Items.TryGetValue("gold", out var gold);
        if (gold < price)
        {
            return ActionResult.Simple("buy", false, false, turnBefore, _state.Turn, $"You need {price} gold for {definition?.Name ?? wareKey}.");
        }

        ChangeInventory(inventory, "gold", -price);
        inventory.Items.TryGetValue(definition?.Name ?? wareKey, out var current);
        inventory.Items[definition?.Name ?? wareKey] = current + 1;
        merchantComponent.Wares[wareKey] -= 1;
        merchant.Set(merchantComponent with { Gold = merchantComponent.Gold + price });
        var message = $"You buy {definition?.Name ?? wareKey} from {merchant.Name} for {price} gold.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return ActionResult.Simple("buy", true, true, turnBefore, _state.Turn, message);
    }

    public ActionResult Sell(string item, string? target)
    {
        var turnBefore = _state.Turn;
        var merchant = ResolveNearbyMerchant(target);
        if (merchant is null)
        {
            return ActionResult.Simple("sell", false, false, turnBefore, _state.Turn, "No nearby merchant is ready to trade.");
        }

        var inventory = EnsureInventory(_state.ControlledEntity);
        var key = FindInventoryKey(inventory, item);
        if (key is null || key.Equals("gold", StringComparison.OrdinalIgnoreCase))
        {
            return ActionResult.Simple("sell", false, false, turnBefore, _state.Turn, $"You are not carrying {item}.");
        }

        var definition = _itemCatalog.Find(key);
        var price = Math.Max(1, definition?.Value ?? 1);
        var merchantComponent = merchant.Get<MerchantComponent>();
        if (merchantComponent.Gold < price)
        {
            return ActionResult.Simple("sell", false, false, turnBefore, _state.Turn, $"{merchant.Name} cannot afford {key}.");
        }

        ChangeInventory(inventory, key, -1);
        inventory.Items.TryGetValue("gold", out var gold);
        inventory.Items["gold"] = gold + price;
        var wareKey = definition?.Name ?? key;
        merchantComponent.Wares.TryGetValue(wareKey, out var current);
        merchantComponent.Wares[wareKey] = current + 1;
        merchant.Set(merchantComponent with { Gold = merchantComponent.Gold - price });
        var message = $"You sell {wareKey} to {merchant.Name} for {price} gold.";
        _state.AddMessage(message);
        _engine.AdvanceTurn();
        return ActionResult.Simple("sell", true, true, turnBefore, _state.Turn, message);
    }

    public bool IsCarrying(string item)
    {
        var inventory = EnsureInventory(_state.ControlledEntity);
        return FindInventoryKey(inventory, item) is not null;
    }

    public bool IsCarrying(Entity entity, string item) =>
        entity.TryGet<InventoryComponent>(out var inventory)
        && FindInventoryKey(inventory, item) is not null;

    public Entity BuildItemEntity(string item, GridPoint position, int quantity)
    {
        var definition = _itemCatalog.Find(item);
        var idPrefix = NormalizeId(definition?.Id ?? item, "item");
        var tags = definition?.Tags ?? new[] { "item" };
        var name = definition?.Name ?? item;
        return new Entity(_state.NextEntityId(idPrefix), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent(definition?.Glyph ?? '*', "item"))
            .Set(new TagsComponent(tags.Concat(new[] { "item" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: definition?.Material ?? "unknown"))
            .Set(new ItemComponent(
                definition?.Id ?? NormalizeId(item, "item"),
                definition?.Value ?? 1,
                definition?.Material ?? "unknown",
                tags,
                definition?.StackPolicy ?? "commodity",
                definition?.UseProfile ?? "inert",
                definition?.EquipmentSlot))
            .Set(new StackComponent(quantity));
    }

    private InventoryComponent EnsureInventory(Entity entity)
    {
        if (entity.TryGet<InventoryComponent>(out var inventory))
        {
            return inventory;
        }

        inventory = InventoryComponent.Empty();
        entity.Set(inventory);
        return inventory;
    }

    private static EquipmentComponent EnsureEquipment(Entity entity)
    {
        if (entity.TryGet<EquipmentComponent>(out var equipment))
        {
            return equipment;
        }

        equipment = EquipmentComponent.Empty();
        entity.Set(equipment);
        return equipment;
    }

    private string? FindInventoryKey(InventoryComponent inventory, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (inventory.Items.ContainsKey(item))
        {
            return item;
        }

        var definition = _itemCatalog.Find(item);
        if (definition is not null)
        {
            var byDefinition = inventory.Items.Keys.FirstOrDefault(key =>
                key.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                || key.Equals(definition.Name, StringComparison.OrdinalIgnoreCase));
            if (byDefinition is not null)
            {
                return byDefinition;
            }
        }

        return inventory.Items.Keys.FirstOrDefault(key =>
            key.Contains(item, StringComparison.OrdinalIgnoreCase)
            || item.Contains(key, StringComparison.OrdinalIgnoreCase));
    }

    private static void ChangeInventory(InventoryComponent inventory, string item, int delta)
    {
        inventory.Items.TryGetValue(item, out var current);
        var next = current + delta;
        if (next <= 0)
        {
            inventory.Items.Remove(item);
            inventory.TreasuredItems.Remove(item);
            return;
        }

        inventory.Items[item] = next;
    }

    private string InventoryKey(Entity entity, ItemComponent item)
    {
        var definition = _itemCatalog.Find(item.ItemType);
        if (definition is not null)
        {
            return definition.Name;
        }

        return string.IsNullOrWhiteSpace(entity.Name) ? item.ItemType : entity.Name;
    }

    private Entity? ResolveNearbyEntity(
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var origin = _state.ControlledEntity.Get<PositionComponent>().Position;
        var candidates = _state.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.Distance(origin, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            return candidates.FirstOrDefault(entity =>
                entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || (entity.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))));
        }

        if (_state.SelectedTarget is { } selected)
        {
            var selectedEntity = candidates.FirstOrDefault(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        return candidates.FirstOrDefault();
    }

    private Entity? ResolveNearbyMerchant(string? target) =>
        ResolveNearbyEntity(target, entity => entity.Has<MerchantComponent>(), range: 2);

    private string? FindWareKey(MerchantComponent merchant, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (merchant.Wares.ContainsKey(item) && merchant.Wares[item] > 0)
        {
            return item;
        }

        var definition = _itemCatalog.Find(item);
        return merchant.Wares.Keys.FirstOrDefault(key =>
            merchant.Wares[key] > 0
            && (key.Equals(item, StringComparison.OrdinalIgnoreCase)
                || key.Contains(item, StringComparison.OrdinalIgnoreCase)
                || item.Contains(key, StringComparison.OrdinalIgnoreCase)
                || (definition is not null
                    && (key.Equals(definition.Id, StringComparison.OrdinalIgnoreCase)
                        || key.Equals(definition.Name, StringComparison.OrdinalIgnoreCase)))));
    }

    private static int ParseProfileAmount(string profile, int fallback)
    {
        var parts = profile.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[1], out var parsed)
            ? Math.Clamp(parsed, 1, 99)
            : fallback;
    }

    private static string FormatReagent(ReagentCard card)
    {
        var tags = card.Tags.Count == 0 ? "" : $"; {string.Join(", ", card.Tags.Take(5))}";
        var bias = string.IsNullOrWhiteSpace(card.SpellBias) ? "" : $"; bias: {card.SpellBias}";
        return $"{card.Quantity}x {card.Name} ({card.Material}, value {card.TotalValue}{tags}{bias})";
    }

    private static string NormalizeId(string value, string fallback)
    {
        var cleaned = string.Join(
            '_',
            value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }
}
