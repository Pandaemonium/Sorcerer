using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class ItemSystem
{
    private readonly GameEngine _engine;
    private readonly InventoryService _inventoryService;
    private readonly ItemCatalog _itemCatalog;
    private readonly PromiseRealizationSystem _promiseRealizationSystem;
    private readonly GameState _state;

    public ItemSystem(
        GameEngine engine,
        ItemCatalog itemCatalog,
        InventoryService inventoryService)
    {
        _engine = engine;
        _itemCatalog = itemCatalog;
        _inventoryService = inventoryService;
        _promiseRealizationSystem = new PromiseRealizationSystem(engine.State, engine);
        _state = engine.State;
    }

    public ActionResult Pickup(string? target)
    {
        var turnBefore = _state.Turn;
        var item = ResolveNearbyEntity(target, entity => entity.Has<ItemComponent>(), range: 1);
        if (item is null)
        {
            if (TryLootCorpse(target, turnBefore) is { } looted)
            {
                return looted;
            }

            var hint = InteractionSystem.OutOfReachHint(_engine, _state, target, entity => entity.Has<ItemComponent>());
            return ActionResult.Simple(
                "pickup",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                hint ?? "There is nothing here you can pick up.");
        }

        var itemComponent = item.Get<ItemComponent>();
        var quantity = item.TryGet<StackComponent>(out var stack) ? Math.Max(1, stack.Quantity) : 1;
        var key = InventoryKey(item, itemComponent);
        var message = quantity == 1
            ? $"You pick up {key}."
            : $"You pick up {quantity} {key}.";
        var applied = _engine.ApplyConsequence(WorldConsequence.TransferItem(
            "item",
            _state.ControlledEntityId.Value,
            "pickup",
            key,
            quantity,
            itemEntityId: item.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: item.Name,
            operation: "pickup",
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You cannot pick up {key}.";
            return new ActionResult
            {
                Action = "pickup",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "pickup",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
        };
    }

    /// <summary>
    /// Recovering a named item from an adjacent corpse: death voids inventory protection, so
    /// a keeper's treasured objective is lootable once they are dead. Requires an explicit
    /// item or corpse name — a bare "pickup" never rifles the dead.
    /// </summary>
    private ActionResult? TryLootCorpse(string? target, int turnBefore)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var origin = _state.ControlledEntity.Get<PositionComponent>().Position;
        var corpses = _state.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && !actor.Alive)
            .Where(entity => entity.TryGet<InventoryComponent>(out var held)
                && held.Items.Any(pair => pair.Value > 0))
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= 1)
            .OrderBy(entity => entity.Id.Value)
            .ToArray();
        foreach (var corpse in corpses)
        {
            var inventory = corpse.Get<InventoryComponent>();
            var key = FindInventoryKey(inventory, target)
                ?? FindInventoryKeyByToken(inventory, target)
                ?? (corpse.Name.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? inventory.Items.First(pair => pair.Value > 0).Key
                    : null);
            if (key is null)
            {
                continue;
            }

            var quantity = Math.Max(1, inventory.Items.TryGetValue(key, out var carried) ? carried : 1);
            var message = $"You take {key.Replace('_', ' ')} from {corpse.Name}'s corpse.";
            var applied = _engine.ApplyConsequence(WorldConsequence.TransferItem(
                "item",
                corpse.Id.Value,
                "give",
                key,
                quantity: quantity,
                recipientEntityId: _state.ControlledEntityId.Value,
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: _state.ControlledEntityId.Value,
                evidence: corpse.Name,
                operation: "lootCorpse",
                message: message,
                details: new Dictionary<string, object?> { ["allowProtected"] = true }));
            if (!applied.Applied)
            {
                continue;
            }

            var turnDeltas = _engine.AdvanceTurn();
            return new ActionResult
            {
                Action = "pickup",
                Success = true,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
                Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
            };
        }

        return null;
    }

    // NPC inventories key items by normalized token ("witness_parcel"); the player types the
    // spoken name ("witness parcel"). Compare in normalized space so both resolve.
    private static string? FindInventoryKeyByToken(InventoryComponent inventory, string item)
    {
        var expected = NormalizeId(item, "item");
        return inventory.Items.Keys.FirstOrDefault(key =>
        {
            var normalized = NormalizeId(key, "item");
            return normalized.Equals(expected, StringComparison.OrdinalIgnoreCase)
                || normalized.Contains(expected, StringComparison.OrdinalIgnoreCase)
                || expected.Contains(normalized, StringComparison.OrdinalIgnoreCase);
        });
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

        var position = _state.ControlledEntity.Get<PositionComponent>().Position;
        var definition = _itemCatalog.Find(key);
        var message = $"You drop {key}.";
        var applied = _engine.ApplyConsequence(WorldConsequence.TransferItem(
            "item",
            _state.ControlledEntityId.Value,
            "drop",
            key,
            quantity: 1,
            x: position.X,
            y: position.Y,
            prefix: NormalizeId(definition?.Id ?? key, "item"),
            glyph: definition?.Glyph ?? '*',
            itemType: definition?.Id ?? NormalizeId(key, "item"),
            material: definition?.Material ?? "unknown",
            tags: definition?.Tags ?? new[] { "item" },
            value: definition?.Value ?? 1,
            stackPolicy: definition?.StackPolicy ?? "commodity",
            useProfile: definition?.UseProfile ?? "inert",
            equipmentSlot: definition?.EquipmentSlot,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: key,
            operation: "drop",
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You cannot drop {key}.";
            return new ActionResult
            {
                Action = "drop",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "drop",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
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
        var effect = profile.StartsWith("heal:", StringComparison.OrdinalIgnoreCase)
            ? WorldConsequence.Heal(
                "use",
                actor.Id.Value,
                ParseProfileAmount(profile, fallback: 4),
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: actor.Id.Value,
                evidence: key,
                operation: "useHeal")
            : profile.StartsWith("mana:", StringComparison.OrdinalIgnoreCase)
                ? WorldConsequence.RestoreMana(
                    "use",
                    actor.Id.Value,
                    ParseProfileAmount(profile, fallback: 4),
                    visibility: WorldConsequenceVisibility.Message,
                    sourceEntityId: actor.Id.Value,
                    evidence: key,
                    operation: "useRestoreMana")
                : null;
        if (effect is null)
        {
            return ActionResult.Simple(
                "use",
                success: false,
                consumedTurn: false,
                turnBefore,
                _state.Turn,
                $"{key} resists ordinary use.");
        }

        var transaction = GameTransaction.Begin(_state);
        var deltas = new List<StateDelta>();
        var narration = _engine.ApplyConsequence(WorldConsequence.Message(
            "use",
            useMessage,
            targetEntityId: actor.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: key,
            reason: "A carried item was intentionally used.",
            operation: "useItemMessage",
            details: new Dictionary<string, object?>
            {
                ["item"] = key,
                ["useProfile"] = profile,
                ["playerVisible"] = true,
            }));
        if (!narration.Applied)
        {
            var failure = narration.Error ?? $"{key} could not be used.";
            RollBackUseItemTransaction(transaction, deltas, 0, actor, key, narration.Deltas, failure);
            return new ActionResult
            {
                Action = "use",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(narration.Deltas);
        var effectResult = _engine.ApplyConsequence(effect);
        if (!effectResult.Applied)
        {
            var failure = effectResult.Error ?? $"{key} failed to take effect.";
            RollBackUseItemTransaction(transaction, deltas, 0, actor, key, effectResult.Deltas, failure);
            return new ActionResult
            {
                Action = "use",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(effectResult.Deltas);
        var consume = _engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "use",
            actor.Id.Value,
            key,
            op: "consume",
            amount: 1,
            sourceEntityId: actor.Id.Value,
            evidence: key,
            operation: "useItemSpent",
            details: new Dictionary<string, object?>
            {
                ["item"] = key,
            }));
        if (!consume.Applied)
        {
            var failure = consume.Error ?? $"{key} could not be consumed.";
            RollBackUseItemTransaction(transaction, deltas, 0, actor, key, consume.Deltas, failure);
            return new ActionResult
            {
                Action = "use",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = deltas,
            };
        }

        deltas.AddRange(consume.Deltas);
        transaction.Commit();
        var turnDeltas = _engine.AdvanceTurn();
        deltas.AddRange(turnDeltas);
        return new ActionResult
        {
            Action = "use",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = deltas.PlayerMessages().ToArray(),
            Deltas = deltas,
        };
    }

    private static void RollBackUseItemTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        Entity actor,
        string item,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        deltas.AddRange(FailureDiagnostics(failedDeltas));
        var rejectedCount = FailureDiagnostics(failedDeltas).Count;
        deltas.Add(new StateDelta(
            "useItemSkipped",
            actor.Id.Value,
            $"Item use rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["actorEntityId"] = actor.Id.Value,
                ["item"] = item,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas
            .Where(delta => delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static void RemoveRangeFrom<T>(List<T> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
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

        var message = $"You equip {key} in your {slot}.";
        var applied = _engine.ApplyConsequence(WorldConsequence.UpdateEquipment(
            "item",
            actor.Id.Value,
            "equip",
            item: key,
            slot: slot,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: key,
            operation: "equip",
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You cannot equip {key}.";
            return new ActionResult
            {
                Action = "equip",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "equip",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
        };
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
        var message = $"You unequip {item}.";
        var applied = _engine.ApplyConsequence(WorldConsequence.UpdateEquipment(
            "item",
            _state.ControlledEntityId.Value,
            "unequip",
            item: item,
            slot: slot,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: slotOrItem,
            operation: "unequip",
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You cannot unequip {item}.";
            return new ActionResult
            {
                Action = "unequip",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "unequip",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
        };
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

        var message = $"{equipment.Slots[slot]} is now your magical focus.";
        var applied = _engine.ApplyConsequence(WorldConsequence.UpdateEquipment(
            "item",
            _state.ControlledEntityId.Value,
            "focus",
            item: equipment.Slots[slot],
            slot: slot,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: slotOrItem,
            operation: "focus",
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You cannot focus {slotOrItem}.";
            return new ActionResult
            {
                Action = "focus",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "focus",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = applied.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = applied.Deltas.Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult UnfocusItem(string? slotOrItem)
    {
        var turnBefore = _state.Turn;
        var equipment = EnsureEquipment(_state.ControlledEntity);
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            var cleared = "You release your magical focus.";
            var applied = _engine.ApplyConsequence(WorldConsequence.UpdateEquipment(
                "item",
                _state.ControlledEntityId.Value,
                "unfocus",
                visibility: WorldConsequenceVisibility.Message,
                sourceEntityId: _state.ControlledEntityId.Value,
                operation: "unfocus",
                message: cleared));
            if (!applied.Applied)
            {
                var failure = applied.Error ?? "You cannot release your magical focus.";
                return new ActionResult
                {
                    Action = "unfocus",
                    Success = false,
                    ConsumedTurn = false,
                    TurnBefore = turnBefore,
                    TurnAfter = _state.Turn,
                    Messages = new[] { failure },
                    Deltas = applied.Deltas,
                };
            }

            return new ActionResult
            {
                Action = "unfocus",
                Success = true,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = applied.Messages.ToArray(),
                Deltas = applied.Deltas,
            };
        }

        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null || !equipment.FocusSlots.Contains(slot))
        {
            return ActionResult.Simple("unfocus", false, false, turnBefore, _state.Turn, $"{slotOrItem} is not focused.");
        }

        var message = $"{equipment.Slots[slot]} is no longer your focus.";
        var appliedUnfocus = _engine.ApplyConsequence(WorldConsequence.UpdateEquipment(
            "item",
            _state.ControlledEntityId.Value,
            "unfocus",
            item: equipment.Slots[slot],
            slot: slot,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: slotOrItem,
            operation: "unfocus",
            message: message));
        if (!appliedUnfocus.Applied)
        {
            var failure = appliedUnfocus.Error ?? $"You cannot unfocus {slotOrItem}.";
            return new ActionResult
            {
                Action = "unfocus",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = new[] { failure },
                Deltas = appliedUnfocus.Deltas,
            };
        }

        return new ActionResult
        {
            Action = "unfocus",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = appliedUnfocus.Messages.ToArray(),
            Deltas = appliedUnfocus.Deltas,
        };
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
        var turn = _state.Turn;
        var merchant = ResolveNearbyCommerceProvider(target, "wares");
        if (merchant is null)
        {
            return ActionResult.Simple("wares", false, false, turn, turn, "No nearby merchant is ready to trade.");
        }

        var messages = new List<string>();
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(
            merchant,
            "wares",
            messages,
            alreadyPersistedMessages: messages.ToList()).ToList();
        if (!merchant.TryGet<MerchantComponent>(out var merchantComponent))
        {
            return new ActionResult
            {
                Action = "wares",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turn,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { "No nearby merchant is ready to trade." }).ToArray(),
                Deltas = deltas,
            };
        }

        var wares = merchantComponent.Wares
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
        messages.AddRange(wares.Length == 0 ? new[] { $"{merchant.Name} has nothing for sale." } : wares);
        return new ActionResult
        {
            Action = "wares",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = _state.Turn,
            Messages = messages.ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult Buy(string item, string? target)
    {
        var turnBefore = _state.Turn;
        var merchant = ResolveNearbyCommerceProvider(target, "buy");
        if (merchant is null)
        {
            return ActionResult.Simple("buy", false, false, turnBefore, _state.Turn, "No nearby merchant is ready to trade.");
        }

        var messages = new List<string>();
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(
            merchant,
            "buy",
            messages,
            alreadyPersistedMessages: messages.ToList()).ToList();
        if (!merchant.TryGet<MerchantComponent>(out var merchantComponent))
        {
            return new ActionResult
            {
                Action = "buy",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { "No nearby merchant is ready to trade." }).ToArray(),
                Deltas = deltas,
            };
        }

        var wareKey = FindWareKey(merchantComponent, item);
        if (wareKey is null)
        {
            return new ActionResult
            {
                Action = "buy",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { $"{merchant.Name} is not selling {item}." }).ToArray(),
                Deltas = deltas,
            };
        }

        var definition = _itemCatalog.Find(wareKey);
        var price = Math.Max(1, definition?.Value ?? 1);
        var inventory = EnsureInventory(_state.ControlledEntity);
        inventory.Items.TryGetValue("gold", out var gold);
        if (gold < price)
        {
            return new ActionResult
            {
                Action = "buy",
                Success = false,
                ConsumedTurn = false,
                FailureCode = Sorcerer.Core.Results.FailureCode.UnpaidCost,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { $"You need {price} gold for {definition?.Name ?? wareKey}." }).ToArray(),
                Deltas = deltas,
            };
        }

        var itemName = definition?.Name ?? wareKey;
        var message = $"You buy {itemName} from {merchant.Name} for {price} gold.";
        var applied = _engine.ApplyConsequence(WorldConsequence.ExecuteTrade(
            "trade",
            merchant.Id.Value,
            _state.ControlledEntityId.Value,
            "buy",
            itemName,
            wareKey,
            price,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: message,
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"{merchant.Name} cannot complete that trade.";
            return new ActionResult
            {
                Action = "buy",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { failure }).ToArray(),
                Deltas = deltas.Concat(applied.Deltas).ToArray(),
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "buy",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = messages.Concat(applied.Messages).Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(applied.Deltas).Concat(turnDeltas).ToArray(),
        };
    }

    public ActionResult Sell(string item, string? target)
    {
        var turnBefore = _state.Turn;
        var merchant = ResolveNearbyCommerceProvider(target, "sell");
        if (merchant is null)
        {
            return ActionResult.Simple("sell", false, false, turnBefore, _state.Turn, "No nearby merchant is ready to trade.");
        }

        var messages = new List<string>();
        var deltas = _promiseRealizationSystem.RealizeAnchoredPromises(
            merchant,
            "sell",
            messages,
            alreadyPersistedMessages: messages.ToList()).ToList();
        if (!merchant.TryGet<MerchantComponent>(out var merchantComponent))
        {
            return new ActionResult
            {
                Action = "sell",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { "No nearby merchant is ready to trade." }).ToArray(),
                Deltas = deltas,
            };
        }

        var inventory = EnsureInventory(_state.ControlledEntity);
        var key = FindInventoryKey(inventory, item);
        if (key is null || key.Equals("gold", StringComparison.OrdinalIgnoreCase))
        {
            return new ActionResult
            {
                Action = "sell",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { $"You are not carrying {item}." }).ToArray(),
                Deltas = deltas,
            };
        }

        var definition = _itemCatalog.Find(key);
        var price = Math.Max(1, definition?.Value ?? 1);
        if (merchantComponent.Gold < price)
        {
            return new ActionResult
            {
                Action = "sell",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { $"{merchant.Name} cannot afford {key}." }).ToArray(),
                Deltas = deltas,
            };
        }

        var wareKey = definition?.Name ?? key;
        var message = $"You sell {wareKey} to {merchant.Name} for {price} gold.";
        var applied = _engine.ApplyConsequence(WorldConsequence.ExecuteTrade(
            "trade",
            merchant.Id.Value,
            _state.ControlledEntityId.Value,
            "sell",
            wareKey,
            wareKey,
            price,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: message,
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"{merchant.Name} cannot complete that trade.";
            return new ActionResult
            {
                Action = "sell",
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turnBefore,
                TurnAfter = _state.Turn,
                Messages = messages.Concat(new[] { failure }).ToArray(),
                Deltas = deltas.Concat(applied.Deltas).ToArray(),
            };
        }

        var turnDeltas = _engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "sell",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = _state.Turn,
            Messages = messages.Concat(applied.Messages).Concat(turnDeltas.PlayerMessages()).ToArray(),
            Deltas = deltas.Concat(applied.Deltas).Concat(turnDeltas).ToArray(),
        };
    }

    public bool IsCarrying(string item)
    {
        var inventory = EnsureInventory(_state.ControlledEntity);
        return FindInventoryKey(inventory, item) is not null;
    }

    public bool IsCarrying(Entity entity, string item) =>
        entity.TryGet<InventoryComponent>(out var inventory)
        && FindInventoryKey(inventory, item) is not null;

    private InventoryComponent EnsureInventory(Entity entity)
    {
        if (entity.TryGet<InventoryComponent>(out var inventory))
        {
            return inventory;
        }

        return InventoryComponent.Empty();
    }

    private static EquipmentComponent EnsureEquipment(Entity entity)
    {
        if (entity.TryGet<EquipmentComponent>(out var equipment))
        {
            return equipment;
        }

        return EquipmentComponent.Empty();
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

    private Entity? ResolveNearbyCommerceProvider(string? target, string trigger)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return ResolveNearbyMerchant(null)
                ?? ResolveNearbyEntity(null, entity => HasCommercePromise(entity, trigger), range: 2);
        }

        return ResolveNearbyEntity(
            target,
            entity => entity.Has<MerchantComponent>() || HasCommercePromise(entity, trigger),
            range: 2);
    }

    private bool HasCommercePromise(Entity entity, string trigger)
    {
        if (!entity.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return false;
        }

        return anchor.PromiseIds.Any(promiseId =>
            _state.PromiseLedger.Promises.Any(promise =>
                promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase)
                && promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
                && CommerceRealizationKind(promise)
                && CommerceTriggerMatches(promise.TriggerHint, trigger)));
    }

    private static bool CommerceRealizationKind(WorldPromise promise)
    {
        var text = NormalizeToken(promise.RealizationKind ?? promise.Kind);
        return text is "merchant_stock" or "stock" or "trade";
    }

    private static bool CommerceTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = NormalizeToken(trigger);
        var parts = triggerHint
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeToken)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parts.Contains(normalizedTrigger)
            || parts.Contains("encounter")
            || (normalizedTrigger is "buy" or "sell" or "wares" or "trade"
                && parts.Overlaps(new[] { "buy", "wares", "trade", "sell", "merchant", "market", "stock" }));
    }

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

    private static string NormalizeToken(string value) => NormalizeId(value, "");
}
