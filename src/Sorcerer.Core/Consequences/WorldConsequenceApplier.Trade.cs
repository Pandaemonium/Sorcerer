using Sorcerer.Core.Characters;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

/// <summary>
/// <see cref="WorldConsequenceApplier"/> handlers for inventory, item transfer, equipment, merchant stock, trade, and services.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyModifyInventory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Inventory consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        var requestedItem = FirstNonBlank(ReadString(payload, "item"), ReadString(payload, "itemName"), "item")!;
        var normalizedItem = NormalizeToken(requestedItem, "item");
        var op = NormalizeToken(FirstNonBlank(ReadString(payload, "op"), ReadString(payload, "mode"), "add")!, "add");
        var amount = Math.Clamp(ReadInt(payload, "amount") ?? ReadInt(payload, "quantity") ?? 1, 0, 999);
        var inventory = target.Entity!.TryGet<InventoryComponent>(out var existing)
            ? existing
            : InventoryComponent.Empty();
        var item = FirstNonBlank(ReadString(payload, "inventoryKey"), ReadString(payload, "itemKey"))
            ?? FindInventoryKey(inventory, requestedItem)
            ?? FindInventoryKey(inventory, normalizedItem)
            ?? normalizedItem;
        if (op is "protect" or "unprotect" or "set_protected")
        {
            var carriedItem = FindInventoryKey(inventory, requestedItem)
                ?? FindInventoryKey(inventory, normalizedItem);
            if (carriedItem is null)
            {
                return Reject(consequence, $"Inventory protection target is not carrying {requestedItem}.");
            }

            var protectedState = op switch
            {
                "unprotect" => false,
                "set_protected" => ReadBool(payload, "protected") ?? ReadBool(payload, "protectedState") ?? true,
                _ => true,
            };
            var wasProtected = inventory.TreasuredItems.Contains(carriedItem);
            if (protectedState)
            {
                inventory.TreasuredItems.Add(carriedItem);
            }
            else
            {
                inventory.TreasuredItems.Remove(carriedItem);
            }

            target.Entity.Set(inventory);
            var protectionOperation = ReadString(payload, "operation") ?? op;
            var protectionDefaultSummary = protectedState
                ? $"{carriedItem} is protected from wild magic costs."
                : $"{carriedItem} is available as ordinary spell fuel.";
            var protectionSummary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), protectionDefaultSummary)!;
            var protectionDelta = new StateDelta(
                protectionOperation,
                target.Entity.Id.Value,
                protectionSummary,
                Details(
                    consequence,
                    ("item", carriedItem),
                    ("op", op),
                    ("protected", protectedState),
                    ("wasProtected", wasProtected),
                    ("count", inventory.Items.TryGetValue(carriedItem, out var carriedCount) ? carriedCount : 0)));
            var protectionMessages = AddMessageIfAllowed(consequence, payload, protectionSummary, defaultEmitMessage: false)
                ? new[] { protectionSummary }
                : Array.Empty<string>();

            return Applied(consequence, target.Entity.Id.Value, protectionMessages, protectionDelta, ("item", carriedItem), ("protected", protectedState));
        }

        var current = inventory.Items.TryGetValue(item, out var count) ? count : 0;
        if (op is "remove" or "subtract" or "consume" && current < amount)
        {
            return Reject(consequence, $"{target.Entity.Name} is not carrying enough {item.Replace('_', ' ')}.");
        }

        var updated = op switch
        {
            "remove" or "subtract" or "consume" => Math.Max(0, current - amount),
            "set" => Math.Max(0, amount),
            _ => current + Math.Max(1, amount),
        };

        if (updated <= 0)
        {
            inventory.Items.Remove(item);
            inventory.TreasuredItems.Remove(item);
        }
        else
        {
            inventory.Items[item] = updated;
        }

        target.Entity.Set(inventory);
        var operation = ReadString(payload, "operation") ?? "modifyInventory";
        var defaultSummary = $"{Possessive(target.Entity)} {item.Replace('_', ' ')} count becomes {updated}.";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultSummary)!;
        var delta = new StateDelta(
            operation,
            target.Entity.Id.Value,
            summary,
            Details(consequence, ("item", item), ("op", op), ("count", updated)));
        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, target.Entity.Id.Value, messages, delta, ("item", item), ("count", updated));
    }

    private WorldConsequenceApplyResult ApplyTransferItem(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actorId = FirstNonBlank(ReadString(payload, "actorEntityId"), ReadString(payload, "actorId"), consequence.SourceEntityId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Item transfer did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Item transfer actor does not exist.");
        }

        var inventory = actor.TryGet<InventoryComponent>(out var existingInventory)
            ? existingInventory
            : InventoryComponent.Empty();
        if (!actor.Has<InventoryComponent>())
        {
            actor.Set(inventory);
        }

        var mode = NormalizeToken(FirstNonBlank(ReadString(payload, "mode"), "pickup")!, "pickup");
        return mode switch
        {
            "pickup" => ApplyPickupTransfer(consequence, payload, actor, inventory),
            "drop" => ApplyDropTransfer(consequence, payload, actor, inventory),
            "give" => ApplyGiveTransfer(consequence, payload, actor, inventory),
            _ => Reject(consequence, $"Unknown item transfer mode: {mode}"),
        };
    }

    private WorldConsequenceApplyResult ApplyPickupTransfer(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        InventoryComponent inventory)
    {
        var itemEntityId = FirstNonBlank(ReadString(payload, "itemEntityId"), ReadString(payload, "entityId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(itemEntityId))
        {
            return Reject(consequence, "Pickup transfer did not include an item entity id.");
        }

        var item = EntityById(itemEntityId);
        if (item is null || !item.TryGet<ItemComponent>(out var itemComponent))
        {
            return Reject(consequence, "Pickup transfer target is not an item entity.");
        }

        var quantity = item.TryGet<StackComponent>(out var stack) ? Math.Max(1, stack.Quantity) : Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        var key = FirstNonBlank(ReadString(payload, "itemName"), item.Name, itemComponent.ItemType)!;
        AdjustInventory(actor, inventory, key, quantity);
        _state.Entities.Remove(item.Id);
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            quantity == 1 ? $"{actor.Name} picks up {key}." : $"{actor.Name} picks up {quantity} {key}.")!;
        var operation = ReadString(payload, "operation") ?? "pickup";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "pickup"),
                ("actorEntityId", actor.Id.Value),
                ("itemEntityId", item.Id.Value),
                ("item", key),
                ("quantity", quantity)));
        return AppliedTransfer(consequence, actor.Id.Value, payload, summary, delta, ("mode", "pickup"), ("item", key), ("quantity", quantity));
    }

    private WorldConsequenceApplyResult ApplyDropTransfer(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        InventoryComponent inventory)
    {
        var requestedItem = FirstNonBlank(ReadString(payload, "itemName"), ReadString(payload, "item"));
        if (string.IsNullOrWhiteSpace(requestedItem))
        {
            return Reject(consequence, "Drop transfer did not include an item name.");
        }

        var key = FindInventoryKey(inventory, requestedItem);
        if (key is null)
        {
            return Reject(consequence, $"Drop transfer actor is not carrying {requestedItem}.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        inventory.Items.TryGetValue(key, out var carrying);
        if (carrying < quantity)
        {
            return Reject(consequence, $"Drop transfer actor is not carrying enough {requestedItem}.");
        }

        GridPoint position;
        if (TryReadPoint(payload, null, out var point))
        {
            position = point;
        }
        else if (actor.TryGet<PositionComponent>(out var actorPosition))
        {
            position = actorPosition.Position;
        }
        else
        {
            return Reject(consequence, "Drop transfer has no position.");
        }

        AdjustInventory(actor, inventory, key, -quantity);
        var itemType = NormalizeToken(FirstNonBlank(ReadString(payload, "itemType"), ReadString(payload, "item_type"), key)!, "item");
        var tags = NormalizeTags(ReadStringList(payload, "tags").Concat(new[] { "item" }));
        var dropped = new Entity(_state.NextEntityId(NormalizeToken(ReadString(payload, "prefix") ?? itemType, "item")), key)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent(ReadGlyph(payload, '*'), "item"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: NormalizeToken(ReadString(payload, "material") ?? "matter", "matter")))
            .Set(new ItemComponent(
                itemType,
                Math.Max(1, ReadInt(payload, "value") ?? 1),
                NormalizeToken(ReadString(payload, "material") ?? "matter", "matter"),
                tags,
                FirstNonBlank(ReadString(payload, "stackPolicy"), ReadString(payload, "stack_policy"), "commodity")!,
                FirstNonBlank(ReadString(payload, "useProfile"), ReadString(payload, "use_profile"), "inert")!,
                FirstNonBlank(ReadString(payload, "equipmentSlot"), ReadString(payload, "equipment_slot"))))
            .Set(new StackComponent(quantity));
        _state.Entities[dropped.Id] = dropped;

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} drops {key}.")!;
        var operation = ReadString(payload, "operation") ?? "drop";
        var delta = new StateDelta(
            operation,
            dropped.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "drop"),
                ("actorEntityId", actor.Id.Value),
                ("itemEntityId", dropped.Id.Value),
                ("item", key),
                ("quantity", quantity),
                ("x", position.X),
                ("y", position.Y)));
        return AppliedTransfer(consequence, dropped.Id.Value, payload, summary, delta, ("mode", "drop"), ("item", key), ("quantity", quantity));
    }

    private WorldConsequenceApplyResult ApplyGiveTransfer(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        InventoryComponent inventory)
    {
        var requestedItem = FirstNonBlank(ReadString(payload, "itemName"), ReadString(payload, "item"));
        if (string.IsNullOrWhiteSpace(requestedItem))
        {
            return Reject(consequence, "Give transfer did not include an item name.");
        }

        var recipientId = FirstNonBlank(
            ReadString(payload, "recipientEntityId"),
            ReadString(payload, "recipientId"),
            ReadString(payload, "receiverEntityId"),
            ReadString(payload, "receiverId"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(recipientId))
        {
            return Reject(consequence, "Give transfer did not include a recipient entity id.");
        }

        var recipient = EntityById(recipientId);
        if (recipient is null)
        {
            return Reject(consequence, "Give transfer recipient does not exist.");
        }

        var key = FindInventoryKey(inventory, requestedItem);
        if (key is null)
        {
            return Reject(consequence, $"Give transfer actor is not carrying {requestedItem}.");
        }

        if (inventory.TreasuredItems.Contains(key) && ReadBool(payload, "allowProtected") != true)
        {
            return Reject(consequence, $"Give transfer actor cannot give protected item {key}.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        inventory.Items.TryGetValue(key, out var carrying);
        if (carrying < quantity)
        {
            return Reject(consequence, $"Give transfer actor is not carrying enough {requestedItem}.");
        }

        var recipientInventory = recipient.TryGet<InventoryComponent>(out var existingRecipientInventory)
            ? existingRecipientInventory
            : InventoryComponent.Empty();
        if (!recipient.Has<InventoryComponent>())
        {
            recipient.Set(recipientInventory);
        }

        AdjustInventory(actor, inventory, key, -quantity);
        AdjustInventory(recipient, recipientInventory, key, quantity);
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            quantity == 1
                ? $"{actor.Name} gives {key} to {recipient.Name}."
                : $"{actor.Name} gives {quantity} {key} to {recipient.Name}.")!;
        var operation = ReadString(payload, "operation") ?? "give";
        var delta = new StateDelta(
            operation,
            recipient.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "give"),
                ("actorEntityId", actor.Id.Value),
                ("recipientEntityId", recipient.Id.Value),
                ("item", key),
                ("quantity", quantity)));
        return AppliedTransfer(
            consequence,
            recipient.Id.Value,
            payload,
            summary,
            delta,
            ("mode", "give"),
            ("item", key),
            ("quantity", quantity),
            ("recipientEntityId", recipient.Id.Value));
    }

    private WorldConsequenceApplyResult AppliedTransfer(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyDictionary<string, object?> payload,
        string summary,
        StateDelta delta,
        params (string Key, object? Value)[] fields)
    {
        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, targetId, messages, delta, fields);
    }

    private WorldConsequenceApplyResult ApplyUpdateEquipment(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "actorId"),
            consequence.TargetEntityId,
            consequence.SourceEntityId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Equipment consequence did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Equipment consequence actor does not exist.");
        }

        var equipment = actor.TryGet<EquipmentComponent>(out var existing)
            ? existing
            : EquipmentComponent.Empty();
        if (!actor.Has<EquipmentComponent>())
        {
            actor.Set(equipment);
        }

        var mode = NormalizeToken(FirstNonBlank(ReadString(payload, "mode"), ReadString(payload, "op"), "equip")!, "equip");
        return mode switch
        {
            "equip" => ApplyEquip(consequence, payload, actor, equipment),
            "unequip" => ApplyUnequip(consequence, payload, actor, equipment),
            "focus" => ApplyFocus(consequence, payload, actor, equipment),
            "unfocus" or "clear_focus" => ApplyUnfocus(consequence, payload, actor, equipment),
            _ => Reject(consequence, $"Unknown equipment mode: {mode}"),
        };
    }

    private WorldConsequenceApplyResult ApplyEquip(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var requestedItem = FirstNonBlank(ReadString(payload, "item"), ReadString(payload, "itemName"));
        if (string.IsNullOrWhiteSpace(requestedItem))
        {
            return Reject(consequence, "Equip consequence did not include an item.");
        }

        if (!actor.TryGet<InventoryComponent>(out var inventory))
        {
            return Reject(consequence, $"Equip consequence actor is not carrying {requestedItem}.");
        }

        var item = FindInventoryKey(inventory, requestedItem);
        if (item is null)
        {
            return Reject(consequence, $"Equip consequence actor is not carrying {requestedItem}.");
        }

        var slot = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "equipmentSlot"), ReadString(payload, "equipment_slot"));
        if (string.IsNullOrWhiteSpace(slot))
        {
            return Reject(consequence, "Equip consequence did not include a slot.");
        }

        equipment.Slots.TryGetValue(slot, out var previousItem);
        equipment.Slots[slot] = item;
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} equips {item} in {slot}.")!;
        var operation = ReadString(payload, "operation") ?? "equip";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "equip"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot),
                ("previousItem", previousItem)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "equip"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult ApplyUnequip(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var slotOrItem = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "item"), ReadString(payload, "itemName"));
        var slot = FindEquipmentSlot(equipment, slotOrItem);
        if (slot is null)
        {
            return Reject(consequence, $"{slotOrItem ?? "item"} is not equipped.");
        }

        var item = equipment.Slots[slot];
        var wasFocused = equipment.FocusSlots.Remove(slot);
        equipment.Slots.Remove(slot);
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} unequips {item}.")!;
        var operation = ReadString(payload, "operation") ?? "unequip";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "unequip"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot),
                ("wasFocused", wasFocused)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "unequip"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult ApplyFocus(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var slotOrItem = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "item"), ReadString(payload, "itemName"));
        var slot = FindEquipmentSlot(equipment, slotOrItem);
        if (slot is null)
        {
            return Reject(consequence, $"{slotOrItem ?? "item"} is not equipped.");
        }

        var item = equipment.Slots[slot];
        var wasFocused = equipment.FocusSlots.Contains(slot);
        equipment.FocusSlots.Add(slot);
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{item} is now {Possessive(actor)} magical focus.")!;
        var operation = ReadString(payload, "operation") ?? "focus";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "focus"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot),
                ("wasFocused", wasFocused)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "focus"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult ApplyUnfocus(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity actor,
        EquipmentComponent equipment)
    {
        var slotOrItem = FirstNonBlank(ReadString(payload, "slot"), ReadString(payload, "item"), ReadString(payload, "itemName"));
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            var removedSlots = equipment.FocusSlots.OrderBy(slot => slot, StringComparer.OrdinalIgnoreCase).ToArray();
            equipment.FocusSlots.Clear();
            actor.Set(equipment);

            var clearSummary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{Subject(actor)} {Verb(actor, "release", "releases")} {(actor.Id == _state.ControlledEntityId ? "your" : "their")} magical focus.")!;
            var clearOperation = ReadString(payload, "operation") ?? "unfocus";
            var clearDelta = new StateDelta(
                clearOperation,
                actor.Id.Value,
                clearSummary,
                Details(
                    consequence,
                    ("mode", "unfocus"),
                    ("actorEntityId", actor.Id.Value),
                    ("removedSlots", removedSlots)));
            return AppliedEquipment(consequence, actor.Id.Value, payload, clearSummary, clearDelta, ("mode", "unfocus"), ("removedSlots", removedSlots));
        }

        var slot = FindEquipmentSlot(equipment, slotOrItem);
        if (slot is null || !equipment.FocusSlots.Contains(slot))
        {
            return Reject(consequence, $"{slotOrItem} is not focused.");
        }

        var item = equipment.Slots[slot];
        equipment.FocusSlots.Remove(slot);
        actor.Set(equipment);

        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{item} is no longer {Possessive(actor)} focus.")!;
        var operation = ReadString(payload, "operation") ?? "unfocus";
        var delta = new StateDelta(
            operation,
            actor.Id.Value,
            summary,
            Details(
                consequence,
                ("mode", "unfocus"),
                ("actorEntityId", actor.Id.Value),
                ("item", item),
                ("slot", slot)));
        return AppliedEquipment(consequence, actor.Id.Value, payload, summary, delta, ("mode", "unfocus"), ("item", item), ("slot", slot));
    }

    private WorldConsequenceApplyResult AppliedEquipment(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyDictionary<string, object?> payload,
        string summary,
        StateDelta delta,
        params (string Key, object? Value)[] fields)
    {
        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(consequence, targetId, messages, delta, fields);
    }

    private static string? FindEquipmentSlot(EquipmentComponent equipment, string? slotOrItem)
    {
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            return null;
        }

        return equipment.Slots.Keys.FirstOrDefault(slot =>
            slot.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[slot].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindInventoryKey(InventoryComponent inventory, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (inventory.Items.ContainsKey(item))
        {
            return item;
        }

        var normalized = NormalizeToken(item, item);
        return inventory.Items.Keys.FirstOrDefault(key =>
            NormalizeToken(key, key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static void AdjustInventory(Entity entity, InventoryComponent inventory, string item, int delta)
    {
        var key = FindInventoryKey(inventory, item) ?? item;
        inventory.Items.TryGetValue(key, out var current);
        var next = current + delta;
        if (next <= 0)
        {
            inventory.Items.Remove(key);
            inventory.TreasuredItems.Remove(key);
        }
        else
        {
            inventory.Items[key] = next;
        }

        entity.Set(inventory);
    }

    private static string? FindWareKey(MerchantComponent merchant, string item)
    {
        if (string.IsNullOrWhiteSpace(item))
        {
            return null;
        }

        if (merchant.Wares.ContainsKey(item))
        {
            return item;
        }

        var normalized = NormalizeToken(item, item);
        return merchant.Wares.Keys.FirstOrDefault(key =>
            key.Contains(item, StringComparison.OrdinalIgnoreCase)
            || item.Contains(key, StringComparison.OrdinalIgnoreCase)
            || NormalizeToken(key, key).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private WorldConsequenceApplyResult ApplyAddMerchantStock(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Merchant stock consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null || !merchant.TryGet<MerchantComponent>(out var stock))
        {
            return Reject(consequence, "Merchant stock consequence target is not a merchant.");
        }

        var itemName = ReadString(payload, "itemName")?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return Reject(consequence, "Merchant stock consequence did not include an item name.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        stock.Wares.TryGetValue(itemName, out var current);
        stock.Wares[itemName] = current + quantity;

        var operation = ReadString(payload, "operation") ?? "addMerchantStock";
        var summary = $"{merchant.Name}'s stock now includes {itemName}.";
        var delta = new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(consequence, ("item", itemName), ("quantity", stock.Wares[itemName])));
        return Applied(
            consequence,
            merchant.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("item", itemName),
            ("quantity", stock.Wares[itemName]));
    }

    private WorldConsequenceApplyResult ApplyOfferTrade(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Trade offer consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null)
        {
            return Reject(consequence, "Trade offer consequence target does not exist.");
        }

        if (!merchant.TryGet<MerchantComponent>(out var stock))
        {
            stock = new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), ReadInt(payload, "gold") ?? 30);
            merchant.Set(stock);
        }

        var itemName = ReadString(payload, "itemName")?.Trim();
        if (!string.IsNullOrWhiteSpace(itemName))
        {
            var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
            stock.Wares.TryGetValue(itemName, out var current);
            stock.Wares[itemName] = current + quantity;
        }

        EnsureInteractableVerbs(merchant, "wares", "buy", "sell", "talk");
        var operation = ReadString(payload, "operation") ?? "offerTrade";
        var summary = string.IsNullOrWhiteSpace(itemName)
            ? $"{merchant.Name} is ready to trade."
            : $"{merchant.Name} offers trade in {itemName}.";
        var delta = new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(consequence, ("item", itemName), ("quantity", string.IsNullOrWhiteSpace(itemName) ? 0 : stock.Wares[itemName])));
        return Applied(consequence, merchant.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("item", itemName));
    }

    private WorldConsequenceApplyResult ApplyExecuteTrade(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = FirstNonBlank(ReadString(payload, "merchantId"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Trade consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null || !merchant.TryGet<MerchantComponent>(out var stock))
        {
            return Reject(consequence, "Trade consequence target is not a merchant.");
        }

        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "buyerEntityId"),
            ReadString(payload, "sellerEntityId"),
            consequence.SourceEntityId);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Trade consequence did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Trade consequence actor does not exist.");
        }

        var mode = NormalizeToken(FirstNonBlank(ReadString(payload, "mode"), "buy")!, "buy");
        var itemName = FirstNonBlank(ReadString(payload, "itemName"), ReadString(payload, "item"), ReadString(payload, "wareKey"));
        var requestedWareKey = FirstNonBlank(ReadString(payload, "wareKey"), itemName);
        if (string.IsNullOrWhiteSpace(itemName) || string.IsNullOrWhiteSpace(requestedWareKey))
        {
            return Reject(consequence, "Trade consequence did not include an item.");
        }

        var price = Math.Max(0, ReadInt(payload, "price") ?? 1);
        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        var totalPrice = price * quantity;
        var inventory = actor.TryGet<InventoryComponent>(out var existingInventory)
            ? existingInventory
            : InventoryComponent.Empty();
        if (!actor.Has<InventoryComponent>())
        {
            actor.Set(inventory);
        }

        StateDelta delta;
        string summary;
        switch (mode)
        {
            case "buy":
            case "purchase":
                {
                    var wareKey = FindWareKey(stock, requestedWareKey);
                    if (wareKey is null || !stock.Wares.TryGetValue(wareKey, out var available) || available < quantity)
                    {
                        return Reject(consequence, $"{merchant.Name} is not selling {itemName}.");
                    }

                    inventory.Items.TryGetValue("gold", out var gold);
                    if (gold < totalPrice)
                    {
                        return Reject(consequence, $"Trade actor needs {totalPrice} gold for {itemName}.");
                    }

                    AdjustInventory(actor, inventory, "gold", -totalPrice);
                    AdjustInventory(actor, inventory, itemName, quantity);
                    stock.Wares[wareKey] = available - quantity;
                    merchant.Set(stock with { Gold = stock.Gold + totalPrice });
                    summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} buys {itemName} from {merchant.Name} for {totalPrice} gold.")!;
                    delta = TradeDelta(consequence, payload, merchant, actor, mode, itemName, wareKey, price, quantity, totalPrice, stock.Gold, summary);
                    break;
                }

            case "sell":
                {
                    var itemKey = FindInventoryKey(inventory, itemName);
                    if (itemKey is null || itemKey.Equals("gold", StringComparison.OrdinalIgnoreCase))
                    {
                        return Reject(consequence, $"Trade actor is not carrying {itemName}.");
                    }

                    inventory.Items.TryGetValue(itemKey, out var carrying);
                    if (carrying < quantity)
                    {
                        return Reject(consequence, $"Trade actor is not carrying enough {itemName}.");
                    }

                    if (stock.Gold < totalPrice)
                    {
                        return Reject(consequence, $"{merchant.Name} cannot afford {itemName}.");
                    }

                    // Resolve against an existing ware the same way buy does, so selling an item
                    // under a slightly different name or case than the merchant's stock key does
                    // not fragment one ware into two separate, unreconciled stock entries. Falls
                    // back to the requested key only when the merchant never carried this ware.
                    var wareKey = FindWareKey(stock, requestedWareKey) ?? requestedWareKey;
                    AdjustInventory(actor, inventory, itemKey, -quantity);
                    AdjustInventory(actor, inventory, "gold", totalPrice);
                    stock.Wares.TryGetValue(wareKey, out var current);
                    stock.Wares[wareKey] = current + quantity;
                    merchant.Set(stock with { Gold = stock.Gold - totalPrice });
                    summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"{actor.Name} sells {itemName} to {merchant.Name} for {totalPrice} gold.")!;
                    delta = TradeDelta(consequence, payload, merchant, actor, mode, itemName, wareKey, price, quantity, totalPrice, stock.Gold, summary);
                    break;
                }

            default:
                return Reject(consequence, $"Unknown trade mode: {mode}");
        }

        var messages = AddMessageIfAllowed(consequence, payload, summary, defaultEmitMessage: false)
            ? new[] { summary }
            : Array.Empty<string>();

        return Applied(
            consequence,
            merchant.Id.Value,
            messages,
            delta,
            ("mode", mode),
            ("item", itemName),
            ("price", price),
            ("quantity", quantity),
            ("totalPrice", totalPrice),
            ("merchantGold", stock.Gold));
    }

    private StateDelta TradeDelta(
        WorldConsequence consequence,
        IReadOnlyDictionary<string, object?> payload,
        Entity merchant,
        Entity actor,
        string mode,
        string itemName,
        string wareKey,
        int price,
        int quantity,
        int totalPrice,
        int merchantGold,
        string summary)
    {
        var operation = ReadString(payload, "operation") ?? "executeTrade";
        return new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(
                consequence,
                ("merchantId", merchant.Id.Value),
                ("actorEntityId", actor.Id.Value),
                ("mode", mode),
                ("item", itemName),
                ("wareKey", wareKey),
                ("price", price),
                ("quantity", quantity),
                ("totalPrice", totalPrice),
                ("merchantGold", merchantGold)));
    }

    private WorldConsequenceApplyResult ApplyOfferService(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var providerId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Reject(consequence, "Service offer consequence did not include a provider id.");
        }

        var provider = EntityById(providerId);
        if (provider is null)
        {
            return Reject(consequence, "Service offer consequence target does not exist.");
        }

        var serviceId = NormalizeToken(ReadString(payload, "serviceId") ?? ReadString(payload, "name") ?? "service", "service");
        var name = FirstNonBlank(ReadString(payload, "name"), serviceId) ?? serviceId;
        var service = new ServiceOffer(
            serviceId,
            name,
            ReadString(payload, "description") ?? consequence.Evidence ?? name,
            NormalizeToken(ReadString(payload, "effectKind") ?? "record_memory", "record_memory"),
            Math.Max(0, ReadInt(payload, "goldCost") ?? 0),
            FirstNonBlank(ReadString(payload, "itemCost")),
            FirstNonBlank(ReadString(payload, "targetHint")),
            ReadBool(payload, "revealed") ?? true,
            ReadStringList(payload, "tags"),
            FirstNonBlank(ReadString(payload, "wantStatusOnComplete"), ReadString(payload, "want_status_on_complete")),
            FirstNonBlank(ReadString(payload, "wantStakesOnComplete"), ReadString(payload, "want_stakes_on_complete")),
            ReadStringList(payload, "wantAddTagsOnComplete").Concat(ReadStringList(payload, "want_add_tags_on_complete")).ToArray(),
            ReadStringList(payload, "wantRemoveTagsOnComplete").Concat(ReadStringList(payload, "want_remove_tags_on_complete")).ToArray());
        var services = provider.TryGet<ServiceComponent>(out var existing)
            ? existing.Offers.ToList()
            : new List<ServiceOffer>();
        var existingIndex = services.FindIndex(offer => offer.Id.Equals(service.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            services[existingIndex] = service;
        }
        else
        {
            services.Add(service);
        }

        provider.Set(new ServiceComponent(services.OrderBy(offer => offer.Id, StringComparer.OrdinalIgnoreCase).ToArray()));
        EnsureInteractableVerbs(provider, "services", "request_service", "talk");
        var operation = ReadString(payload, "operation") ?? "offerService";
        var summary = $"{provider.Name} can offer {service.Name}.";
        var delta = new StateDelta(
            operation,
            provider.Id.Value,
            summary,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("targetHint", service.TargetHint),
                ("wantStatusOnComplete", service.WantStatusOnComplete),
                ("wantStakesOnComplete", service.WantStakesOnComplete),
                ("wantAddTagsOnComplete", service.WantAddTagsOnComplete?.ToArray()),
                ("wantRemoveTagsOnComplete", service.WantRemoveTagsOnComplete?.ToArray())));
        return Applied(consequence, provider.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("serviceId", service.Id));
    }

    private WorldConsequenceApplyResult ApplyRequestService(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var providerId = FirstNonBlank(
            ReadString(payload, "providerId"),
            ReadString(payload, "provider_id"),
            ReadString(payload, "serviceProviderId"),
            ReadString(payload, "service_provider_id"),
            consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Reject(consequence, "Service request consequence did not include a provider id.");
        }

        var provider = EntityById(providerId);
        if (provider is null || !provider.TryGet<ServiceComponent>(out var services))
        {
            return Reject(consequence, "Service request target is not a service provider.");
        }

        var serviceText = FirstNonBlank(
            ReadString(payload, "service"),
            ReadString(payload, "serviceId"),
            ReadString(payload, "service_id"),
            ReadString(payload, "serviceName"),
            ReadString(payload, "service_name"),
            ReadString(payload, "name"));
        if (string.IsNullOrWhiteSpace(serviceText))
        {
            return Reject(consequence, "Service request consequence did not name a service.");
        }

        var allowHidden = ReadBool(payload, "allowHidden") ?? ReadBool(payload, "allow_hidden") ?? false;
        var service = FindServiceOffer(allowHidden ? services.Offers : services.Offers.Where(offer => offer.Revealed), serviceText);
        if (service is null)
        {
            return Reject(consequence, $"{provider.Name} is not offering {serviceText}.");
        }

        var actorId = FirstNonBlank(
            ReadString(payload, "actorEntityId"),
            ReadString(payload, "actor_entity_id"),
            ReadString(payload, "requesterEntityId"),
            ReadString(payload, "requester_entity_id"),
            ReadString(payload, "actorId"),
            ReadString(payload, "actor_id"),
            consequence.SourceEntityId,
            _state.ControlledEntityId.Value);
        if (string.IsNullOrWhiteSpace(actorId))
        {
            return Reject(consequence, "Service request consequence did not include an actor id.");
        }

        var actor = EntityById(actorId);
        if (actor is null)
        {
            return Reject(consequence, "Service request actor does not exist.");
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost)
            && actor.TryGet<InventoryComponent>(out var inventory)
            && FindInventoryKey(inventory, service.ItemCost) is { } itemKey
            && inventory.TreasuredItems.Contains(itemKey))
        {
            return Reject(consequence, $"{itemKey} is protected; unprotect it before offering it.");
        }

        var transaction = GameTransaction.Begin(_state);
        var effect = ApplyServiceEffect(provider, service, actor);
        if (!effect.Applied)
        {
            return RollBackServiceRequestTransaction(
                transaction,
                consequence,
                provider,
                service,
                effect.Deltas,
                effect.Error ?? $"{provider.Name} cannot complete that service here.");
        }

        var payment = PayServiceCost(consequence, actor, provider, service);
        if (!payment.Applied)
        {
            return RollBackServiceRequestTransaction(
                transaction,
                consequence,
                provider,
                service,
                payment.Deltas,
                payment.Error ?? $"{provider.Name} cannot complete that payment.");
        }

        var wantCompletion = ApplyServiceWantCompletion(provider, service);
        if (ServiceHasWantCompletion(service) && !wantCompletion.Applied)
        {
            return RollBackServiceRequestTransaction(
                transaction,
                consequence,
                provider,
                service,
                wantCompletion.Deltas,
                wantCompletion.Error ?? "service_want_completion_failed");
        }

        var operation = ReadString(payload, "operation") ?? "requestService";
        var serviceMessage = FirstNonBlank(ReadString(payload, "message"), $"{provider.Name} provides {service.Name}.")!;
        var messages = new List<string>();
        messages.AddRange(effect.Messages);
        messages.AddRange(payment.Messages);
        messages.AddRange(wantCompletion.Messages);
        if (AddMessageIfAllowed(consequence, payload, serviceMessage, defaultEmitMessage: false))
        {
            messages.Add(serviceMessage);
        }

        var delta = new StateDelta(
            operation,
            provider.Id.Value,
            serviceMessage,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("actorEntityId", actor.Id.Value)));
        transaction.Commit();
        var deltas = new List<StateDelta> { delta };
        deltas.AddRange(payment.Deltas);
        deltas.AddRange(effect.Deltas);
        deltas.AddRange(wantCompletion.Deltas);
        return new WorldConsequenceApplyResult(
            true,
            provider.Id.Value,
            null,
            messages,
            deltas,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("actorEntityId", actor.Id.Value)));
    }

    private WorldConsequenceApplyResult RollBackServiceRequestTransaction(
        GameTransaction transaction,
        WorldConsequence consequence,
        Entity provider,
        ServiceOffer service,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        var skipped = ServiceRequestSkipped(consequence, provider, service, failure, failedDeltas);
        return new WorldConsequenceApplyResult(
            false,
            provider.Id.Value,
            failure,
            Array.Empty<string>(),
            failedDeltas.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("failure", failure),
                ("error", failure)));
    }

    private StateDelta ServiceRequestSkipped(
        WorldConsequence consequence,
        Entity provider,
        ServiceOffer service,
        string failure,
        IReadOnlyList<StateDelta> failedDeltas) =>
        new(
            "serviceRequestSkipped",
            provider.Id.Value,
            $"Service request rolled back: {failure}.",
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("failure", failure),
                ("rejectedCount", failedDeltas.Count(delta =>
                    delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))),
                ("auditOnly", true),
                ("playerVisible", false)));

    private WorldConsequenceApplyResult ApplyServiceEffect(Entity provider, ServiceOffer service, Entity requester)
    {
        var effect = NormalizeServiceEffect(service.EffectKind);
        if (effect is "open_or_unlock" or "unlock_or_open" or "ward_breaking")
        {
            var door = ResolveServiceDoor(provider, service);
            if (door is null)
            {
                return WorldConsequenceApplyResult.Empty("There is no nearby door for that service.");
            }

            return Apply(WorldConsequence.OpenOrUnlock(
                "service",
                door.Id.Value,
                actorId: provider.Id.Value,
                unlock: true,
                open: true,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: provider.Id.Value,
                evidence: service.Description,
                operation: "serviceOpenOrUnlock",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["beneficiaryId"] = requester.Id.Value,
                }));
        }

        if (effect is "create_route" or "escape_route" or "reveal_route")
        {
            return Apply(WorldConsequence.CreateRoute(
                "service",
                provider.Id.Value,
                string.IsNullOrWhiteSpace(service.TargetHint) ? service.Name : service.TargetHint,
                service.Description,
                effect,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: provider.Id.Value,
                evidence: service.Description,
                operation: "serviceCreateRoute",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                }));
        }

        return Apply(WorldConsequence.RecordMemory(
            "service",
            provider.Id.Value,
            $"{provider.Name} provided {service.Name}: {service.Description}",
            "service",
            2,
            shareable: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: provider.Id.Value,
            operation: "serviceMemory",
            details: new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
            }));
    }

    private ServicePaymentResult PayServiceCost(
        WorldConsequence consequence,
        Entity actor,
        Entity provider,
        ServiceOffer service)
    {
        var deltas = new List<StateDelta>();
        var messages = new List<string>();
        if (service.GoldCost > 0)
        {
            var applied = Apply(WorldConsequence.ModifyInventory(
                "service",
                actor.Id.Value,
                "gold",
                op: "consume",
                amount: service.GoldCost,
                sourceEntityId: actor.Id.Value,
                evidence: service.Description,
                operation: "serviceCost",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["providerId"] = provider.Id.Value,
                    ["costKind"] = "gold",
                    ["parentConsequenceType"] = consequence.Type,
                }));
            deltas.AddRange(applied.Deltas);
            messages.AddRange(applied.Messages);
            if (!applied.Applied)
            {
                return new ServicePaymentResult(false, applied.Error, deltas, messages);
            }
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost))
        {
            var applied = Apply(WorldConsequence.ModifyInventory(
                "service",
                actor.Id.Value,
                service.ItemCost,
                op: "consume",
                amount: 1,
                sourceEntityId: actor.Id.Value,
                evidence: service.Description,
                operation: "serviceCost",
                details: new Dictionary<string, object?>
                {
                    ["serviceId"] = service.Id,
                    ["serviceName"] = service.Name,
                    ["providerId"] = provider.Id.Value,
                    ["costKind"] = "item",
                    ["parentConsequenceType"] = consequence.Type,
                }));
            deltas.AddRange(applied.Deltas);
            messages.AddRange(applied.Messages);
            if (!applied.Applied)
            {
                return new ServicePaymentResult(false, applied.Error, deltas, messages);
            }
        }

        return new ServicePaymentResult(true, null, deltas, messages);
    }

    private WorldConsequenceApplyResult ApplyServiceWantCompletion(Entity provider, ServiceOffer service)
    {
        if (!ServiceHasWantCompletion(service))
        {
            return WorldConsequenceApplyResult.Empty();
        }

        if (!provider.Has<WantComponent>())
        {
            return ServiceWantSkipped(provider, service, "provider_has_no_want");
        }

        var applied = Apply(WorldConsequence.UpdateWant(
            "service",
            provider.Id.Value,
            status: service.WantStatusOnComplete,
            stakes: service.WantStakesOnComplete,
            addTags: service.WantAddTagsOnComplete,
            removeTags: service.WantRemoveTagsOnComplete,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: provider.Id.Value,
            evidence: $"{provider.Name} provided {service.Name}.",
            reason: "Completing this service updates the provider's active want through the shared consequence lifecycle.",
            operation: "serviceWantCompletion",
            details: new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
                ["effectKind"] = service.EffectKind,
                ["playerVisible"] = false,
            },
            recordMemory: true,
            memoryText: $"{provider.Name} provided {service.Name}, changing their active want.",
            memoryProvenance: $"service:{service.Id}",
            memoryShareable: false));
        return applied.Applied
            ? applied
            : applied with
            {
                Deltas = applied.Deltas
                    .Concat(ServiceWantSkipped(provider, service, applied.Error ?? "want_update_rejected").Deltas)
                    .ToArray(),
            };
    }

    private static bool ServiceHasWantCompletion(ServiceOffer service) =>
        !string.IsNullOrWhiteSpace(service.WantStatusOnComplete)
        || !string.IsNullOrWhiteSpace(service.WantStakesOnComplete)
        || (service.WantAddTagsOnComplete?.Count ?? 0) > 0
        || (service.WantRemoveTagsOnComplete?.Count ?? 0) > 0;

    private WorldConsequenceApplyResult ServiceWantSkipped(
        Entity provider,
        ServiceOffer service,
        string reason)
    {
        var delta = new StateDelta(
            "serviceWantSkipped",
            provider.Id.Value,
            "Service want completion skipped.",
            new Dictionary<string, object?>
            {
                ["serviceId"] = service.Id,
                ["serviceName"] = service.Name,
                ["reason"] = reason,
                ["effectKind"] = service.EffectKind,
                ["playerVisible"] = false,
                ["auditOnly"] = true,
            });
        return new WorldConsequenceApplyResult(
            false,
            provider.Id.Value,
            reason,
            Array.Empty<string>(),
            new[] { delta },
            new Dictionary<string, object?>());
    }

    private Entity? ResolveServiceDoor(Entity provider, ServiceOffer service)
    {
        var target = FirstNonBlank(service.TargetHint, service.Name);
        return ResolveNearbyEntity(provider, target, entity => entity.Has<DoorComponent>(), range: 2)
            ?? ResolveNearbyEntity(provider, null, entity => entity.Has<DoorComponent>(), range: 2);
    }

    private Entity? ResolveNearbyEntity(
        Entity origin,
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var candidates = NearbyEntities(origin, predicate, range);
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

        return candidates.FirstOrDefault();
    }

    private IReadOnlyList<Entity> NearbyEntities(
        Entity origin,
        Func<Entity, bool> predicate,
        int range)
    {
        if (!origin.TryGet<PositionComponent>(out var originPosition))
        {
            return Array.Empty<Entity>();
        }

        return _state.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && Distance(originPosition.Position, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? Distance(originPosition.Position, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static ServiceOffer? FindServiceOffer(IEnumerable<ServiceOffer> services, string serviceText)
    {
        var normalized = NormalizeToken(serviceText, "");
        return services
            .OrderBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(service =>
                service.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
                || service.Id.Equals(serviceText.Trim(), StringComparison.OrdinalIgnoreCase)
                || service.Name.Equals(serviceText.Trim(), StringComparison.OrdinalIgnoreCase)
                || service.Name.Contains(serviceText.Trim(), StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(NormalizeToken(service.Name, service.Name), StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeServiceEffect(string effect)
    {
        var normalized = string.Join(
            "_",
            effect.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? "record_memory" : normalized;
    }

    private sealed record ServicePaymentResult(
        bool Applied,
        string? Error,
        IReadOnlyList<StateDelta> Deltas,
        IReadOnlyList<string> Messages);
}
