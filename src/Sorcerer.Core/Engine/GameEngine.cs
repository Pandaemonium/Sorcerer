using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Status;
using Sorcerer.Core.Validation;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine;

public sealed class GameEngine
{
    private readonly ItemCatalog _itemCatalog = ItemCatalog.CreateMinimal();
    private readonly InventoryService _inventoryService;
    private readonly StatusRegistry _statusRegistry = StatusRegistry.CreateDefault();

    public GameEngine(GameState state)
    {
        State = state;
        _inventoryService = new InventoryService(_itemCatalog);
    }

    public GameState State { get; }

    public ActionResult MoveControlled(Direction direction)
    {
        var turnBefore = State.Turn;
        var actor = State.ControlledEntity;
        if (IsUnableToMove(actor))
        {
            var blockedByStatus = $"{Subject(actor)} {Verb(actor, "struggle", "struggles")} against binding magic.";
            State.AddMessage(blockedByStatus);
            AdvanceTurn();
            return ActionResult.Simple(
                "move",
                success: false,
                consumedTurn: true,
                turnBefore,
                State.Turn,
                blockedByStatus);
        }

        var position = actor.Get<PositionComponent>();
        var offset = direction.Offset();
        var destination = position.Position.Translate(offset.X, offset.Y);

        if (!InBounds(destination) || State.BlockingTerrain.Contains(destination))
        {
            return ActionResult.Simple(
                "move",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                "Something solid refuses you.");
        }

        var blocker = BlockingEntityAt(destination);
        if (blocker is not null)
        {
            if (IsHostile(actor, blocker))
            {
                return Attack(actor, blocker, turnBefore);
            }

            return ActionResult.Simple(
                "move",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                $"{blocker.Name} blocks the way.");
        }

        actor.Set(new PositionComponent(destination));
        AdvanceTurn();
        State.AddMessage("You move.");
        return ActionResult.Simple(
            "move",
            success: true,
            consumedTurn: true,
            turnBefore,
            State.Turn,
            "You move.");
    }

    public ActionResult Wait()
    {
        var turnBefore = State.Turn;
        AdvanceTurn();
        State.AddMessage("You wait.");
        return ActionResult.Simple(
            "wait",
            success: true,
            consumedTurn: true,
            turnBefore,
            State.Turn,
            "You wait.");
    }

    public IReadOnlyList<StateDelta> RunActorTurns()
    {
        var player = State.ControlledEntity;
        if (!player.TryGet<ActorComponent>(out var playerActor) || !playerActor.Alive)
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var actor in State.Entities.Values
            .Where(entity => entity.Id != player.Id)
            .Where(entity => entity.TryGet<ControllerComponent>(out var controller)
                && controller.Kind == ControllerKind.Ai)
            .Where(entity => entity.TryGet<ActorComponent>(out var stats)
                && stats.Alive
                && IsHostile(entity, player))
            .OrderBy(entity => entity.Id.Value))
        {
            if (!player.Get<ActorComponent>().Alive)
            {
                break;
            }

            if (IsUnableToAct(actor))
            {
                continue;
            }

            if (!actor.TryGet<PositionComponent>(out var actorPosition)
                || !player.TryGet<PositionComponent>(out var playerPosition))
            {
                continue;
            }

            var distance = Distance(actorPosition.Position, playerPosition.Position);
            if (distance <= 1)
            {
                deltas.Add(AttackEntity(actor, player));
                continue;
            }

            if (distance <= 8)
            {
                var destination = StepToward(actorPosition.Position, playerPosition.Position);
                if (CanEnter(destination))
                {
                    deltas.Add(MoveEntity(actor, destination, "aiMove"));
                }
            }
        }

        return deltas;
    }

    public ActionResult Inspect()
    {
        var player = State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        var actor = player.Get<ActorComponent>();
        var messages = new List<string>
        {
            $"Turn {State.Turn}. You are at {position.X},{position.Y}.",
            $"HP {actor.HitPoints}/{actor.MaxHitPoints}; MP {actor.Mana}/{actor.MaxMana}.",
        };

        foreach (var entity in State.Entities.Values.OrderBy(e => e.Id.Value))
        {
            if (entity.Id == State.ControlledEntityId)
            {
                continue;
            }

            if (!entity.TryGet<PositionComponent>(out var entityPosition))
            {
                continue;
            }

            var distance = Distance(position, entityPosition.Position);
            if (distance <= 8)
            {
                messages.Add($"{entity.Name} at {entityPosition.Position.X},{entityPosition.Position.Y}.");
            }
        }

        return ActionResult.Simple(
            "inspect",
            success: true,
            consumedTurn: false,
            State.Turn,
            State.Turn,
            messages.ToArray());
    }

    public ActionResult Pickup(string? target)
    {
        var turnBefore = State.Turn;
        var item = ResolveNearbyEntity(target, entity => entity.Has<ItemComponent>(), range: 1);
        if (item is null)
        {
            return ActionResult.Simple(
                "pickup",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                "There is nothing here you can pick up.");
        }

        var itemComponent = item.Get<ItemComponent>();
        var quantity = item.TryGet<StackComponent>(out var stack) ? Math.Max(1, stack.Quantity) : 1;
        var key = InventoryKey(item, itemComponent);
        var inventory = EnsureInventory(State.ControlledEntity);
        inventory.Items.TryGetValue(key, out var current);
        inventory.Items[key] = current + quantity;
        State.Entities.Remove(item.Id);

        var message = quantity == 1
            ? $"You pick up {key}."
            : $"You pick up {quantity} {key}.";
        State.AddMessage(message);
        AdvanceTurn();
        return ActionResult.Simple("pickup", true, true, turnBefore, State.Turn, message);
    }

    public ActionResult DropItem(string item)
    {
        var turnBefore = State.Turn;
        var inventory = EnsureInventory(State.ControlledEntity);
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple(
                "drop",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                $"You are not carrying {item}.");
        }

        ChangeInventory(inventory, key, -1);
        var position = State.ControlledEntity.Get<PositionComponent>().Position;
        var dropped = BuildItemEntity(key, position, quantity: 1);
        State.Entities[dropped.Id] = dropped;

        var message = $"You drop {key}.";
        State.AddMessage(message);
        AdvanceTurn();
        return new ActionResult
        {
            Action = "drop",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
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
        var turnBefore = State.Turn;
        var actor = State.ControlledEntity;
        var inventory = EnsureInventory(actor);
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple(
                "use",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
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
                State.Turn,
                $"{key} has no immediate use.");
        }

        var useMessage = $"You use {key}.";
        State.AddMessage(useMessage);
        var deltas = new List<StateDelta>();
        if (profile.StartsWith("heal:", StringComparison.OrdinalIgnoreCase))
        {
            deltas.Add(HealEntity(actor, ParseProfileAmount(profile, fallback: 4)));
        }
        else if (profile.StartsWith("mana:", StringComparison.OrdinalIgnoreCase))
        {
            deltas.Add(RestoreMana(actor, ParseProfileAmount(profile, fallback: 4)));
        }
        else
        {
            return ActionResult.Simple(
                "use",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                $"{key} resists ordinary use.");
        }

        ChangeInventory(inventory, key, -1);
        AdvanceTurn();
        return new ActionResult
        {
            Action = "use",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = new[] { useMessage }.Concat(deltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = deltas,
        };
    }

    public ActionResult EquipItem(string item)
    {
        var turnBefore = State.Turn;
        var actor = State.ControlledEntity;
        var inventory = EnsureInventory(actor);
        var key = FindInventoryKey(inventory, item);
        if (key is null)
        {
            return ActionResult.Simple("equip", false, false, turnBefore, State.Turn, $"You are not carrying {item}.");
        }

        var definition = _itemCatalog.Find(key);
        var slot = definition?.EquipmentSlot;
        if (string.IsNullOrWhiteSpace(slot))
        {
            return ActionResult.Simple("equip", false, false, turnBefore, State.Turn, $"{key} cannot be equipped.");
        }

        var equipment = EnsureEquipment(actor);
        equipment.Slots[slot] = key;
        var message = $"You equip {key} in your {slot}.";
        State.AddMessage(message);
        AdvanceTurn();
        return ActionResult.Simple("equip", true, true, turnBefore, State.Turn, message);
    }

    public ActionResult UnequipItem(string slotOrItem)
    {
        var turnBefore = State.Turn;
        var equipment = EnsureEquipment(State.ControlledEntity);
        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            return ActionResult.Simple("unequip", false, false, turnBefore, State.Turn, $"{slotOrItem} is not equipped.");
        }

        var item = equipment.Slots[slot];
        equipment.Slots.Remove(slot);
        equipment.FocusSlots.Remove(slot);
        var message = $"You unequip {item}.";
        State.AddMessage(message);
        AdvanceTurn();
        return ActionResult.Simple("unequip", true, true, turnBefore, State.Turn, message);
    }

    public ActionResult FocusItem(string slotOrItem)
    {
        var turnBefore = State.Turn;
        var equipment = EnsureEquipment(State.ControlledEntity);
        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null)
        {
            return ActionResult.Simple("focus", false, false, turnBefore, State.Turn, $"{slotOrItem} is not equipped.");
        }

        equipment.FocusSlots.Add(slot);
        var message = $"{equipment.Slots[slot]} is now your magical focus.";
        State.AddMessage(message);
        AdvanceTurn();
        return ActionResult.Simple("focus", true, true, turnBefore, State.Turn, message);
    }

    public ActionResult UnfocusItem(string? slotOrItem)
    {
        var turnBefore = State.Turn;
        var equipment = EnsureEquipment(State.ControlledEntity);
        if (string.IsNullOrWhiteSpace(slotOrItem))
        {
            equipment.FocusSlots.Clear();
            var cleared = "You release your magical focus.";
            State.AddMessage(cleared);
            return ActionResult.Simple("unfocus", true, false, turnBefore, State.Turn, cleared);
        }

        var slot = equipment.Slots.Keys.FirstOrDefault(key =>
            key.Equals(slotOrItem, StringComparison.OrdinalIgnoreCase)
            || equipment.Slots[key].Equals(slotOrItem, StringComparison.OrdinalIgnoreCase));
        if (slot is null || !equipment.FocusSlots.Remove(slot))
        {
            return ActionResult.Simple("unfocus", false, false, turnBefore, State.Turn, $"{slotOrItem} is not focused.");
        }

        var message = $"{equipment.Slots[slot]} is no longer your focus.";
        State.AddMessage(message);
        return ActionResult.Simple("unfocus", true, false, turnBefore, State.Turn, message);
    }

    public ActionResult Reagents()
    {
        var cards = _inventoryService.BuildReagentCards(State.ControlledEntity);
        var messages = cards.Count == 0
            ? new[] { "You have no unprotected reagents." }
            : cards.Select(card => $"{card.Quantity}x {card.Name} ({card.Material}, value {card.TotalValue})").ToArray();
        return ActionResult.Simple("reagents", true, false, State.Turn, State.Turn, messages);
    }

    public ActionResult Journal()
    {
        var promises = State.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible)
            .Select(promise => $"{promise.Id} [{promise.Status}] {promise.Text}")
            .ToArray();
        return ActionResult.Simple(
            "journal",
            true,
            false,
            State.Turn,
            State.Turn,
            promises.Length == 0 ? new[] { "No promises are visible yet." } : promises);
    }

    public ActionResult Talk(string text)
    {
        var turnBefore = State.Turn;
        var target = ResolveNearbyEntity(
            text,
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 1);
        if (target is null)
        {
            return ActionResult.Simple("talk", false, false, turnBefore, State.Turn, "No one nearby is ready to talk.");
        }

        var message = DialogueLine(target);
        var messages = new List<string> { message };
        var deltas = RealizePromisesForEntity(target, "talk", messages);
        foreach (var line in messages)
        {
            State.AddMessage(line);
        }

        AdvanceTurn();
        return new ActionResult
        {
            Action = "talk",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages,
            Deltas = deltas,
        };
    }

    public ActionResult Read(string? target)
    {
        var turnBefore = State.Turn;
        var entity = ResolveNearbyEntity(target, candidate => candidate.Has<ReadableComponent>(), range: 1);
        if (entity is null)
        {
            return ActionResult.Simple("read", false, false, turnBefore, State.Turn, "There is nothing readable within reach.");
        }

        var readable = entity.Get<ReadableComponent>();
        var body = string.IsNullOrWhiteSpace(readable.TextKey)
            ? $"{readable.Title}: the words hold still just long enough to be understood."
            : readable.TextKey;
        State.Canon.Add(
            "readable",
            entity.Id.Value,
            body,
            readable.Title,
            TagsFor(entity),
            "read",
            State.Turn);
        EnqueueBackgroundJob("canon_detail", entity, priority: 3);
        var messages = new List<string> { body };
        var deltas = RealizePromisesForEntity(entity, "read", messages);
        foreach (var line in messages)
        {
            State.AddMessage(line);
        }

        AdvanceTurn();
        return new ActionResult
        {
            Action = "read",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages,
            Deltas = deltas,
        };
    }

    public ActionResult Examine(string? target)
    {
        var entity = ResolveNearbyEntity(target, entity => entity.Id != State.ControlledEntityId, range: 1);
        if (entity is null)
        {
            return ActionResult.Simple("examine", false, false, State.Turn, State.Turn, "There is nothing close enough to examine.");
        }

        var messages = DescribeEntity(entity);
        EnqueueBackgroundJob("entity_detail", entity, priority: 2);
        return ActionResult.Simple("examine", true, false, State.Turn, State.Turn, messages.ToArray());
    }

    public ActionResult Open(string? target)
    {
        var turnBefore = State.Turn;
        var door = ResolveNearbyEntity(target, entity => entity.Has<DoorComponent>(), range: 1);
        if (door is null)
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, "There is nothing here you can open.");
        }

        var doorComponent = door.Get<DoorComponent>();
        if (doorComponent.IsOpen)
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, $"{door.Name} is already open.");
        }

        if (!string.IsNullOrWhiteSpace(doorComponent.KeyId)
            && !IsCarrying(doorComponent.KeyId))
        {
            return ActionResult.Simple("open", false, false, turnBefore, State.Turn, $"{door.Name} is locked.");
        }

        door.Set(doorComponent with { IsOpen = true });
        if (door.TryGet<PhysicalComponent>(out var physical))
        {
            door.Set(physical with { BlocksMovement = false });
        }

        if (door.TryGet<RenderableComponent>(out var renderable))
        {
            door.Set(renderable with { Glyph = '/', Palette = "open" });
        }

        var messages = new List<string> { $"You open {door.Name}." };
        var deltas = new List<StateDelta>
        {
            new(
                "open",
                door.Id.Value,
                messages[0],
                new Dictionary<string, object?> { ["open"] = true }),
        };

        ResolveDoorConsequences(door, messages, deltas);
        foreach (var message in messages)
        {
            State.AddMessage(message);
        }

        AdvanceTurn();
        return new ActionResult
        {
            Action = "open",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = messages,
            Deltas = deltas,
        };
    }

    public ActionResult Possess(string? target)
    {
        var turnBefore = State.Turn;
        var newBody = ResolveNearbyEntity(
            target,
            entity => entity.Id != State.ControlledEntityId
                && entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive,
            range: 1);
        if (newBody is null)
        {
            return ActionResult.Simple(
                "possess",
                success: false,
                consumedTurn: false,
                turnBefore,
                State.Turn,
                "No nearby living body is close enough to possess.");
        }

        if (!CanPossess(newBody, out var reason))
        {
            var resisted = reason ?? $"{newBody.Name} braces against your soul and refuses the door.";
            State.AddMessage(resisted);
            AdvanceTurn();
            return ActionResult.Simple("possess", false, true, turnBefore, State.Turn, resisted);
        }

        var deltas = PossessEntity(newBody);
        AdvanceTurn();
        return new ActionResult
        {
            Action = "possess",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = deltas.Select(delta => delta.Summary).ToArray(),
            Deltas = deltas,
        };
    }

    public bool CanPossess(Entity newBody, out string? reason)
    {
        reason = null;
        if (newBody.Id == State.ControlledEntityId)
        {
            reason = "You are already in that body.";
            return false;
        }

        if (!newBody.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            reason = $"{newBody.Name} is not a living body.";
            return false;
        }

        if (IsHostile(State.ControlledEntity, newBody) && !IsUnableToAct(newBody))
        {
            reason = $"{newBody.Name} braces against your soul and refuses the door.";
            return false;
        }

        return true;
    }

    public IReadOnlyList<StateDelta> PossessEntity(Entity newBody)
    {
        if (!CanPossess(newBody, out var reason))
        {
            throw new InvalidOperationException(reason ?? "Possession target rejected.");
        }

        var oldBody = State.ControlledEntity;
        var newActor = newBody.Get<ActorComponent>();
        var oldActor = oldBody.Get<ActorComponent>();
        var targetIsHostile = IsHostile(oldBody, newBody);
        var oldSoul = oldBody.TryGet<SoulComponent>(out var oldSoulComponent)
            ? oldSoulComponent
            : new SoulComponent($"{oldBody.Id.Value}_soul");
        var newSoul = newBody.TryGet<SoulComponent>(out var newSoulComponent)
            ? newSoulComponent
            : new SoulComponent($"{newBody.Id.Value}_soul");

        oldBody.Set(newSoul);
        newBody.Set(oldSoul);
        oldBody.Set(new ControllerComponent(ControllerKind.Ai));
        newBody.Set(new ControllerComponent(ControllerKind.Player));
        oldBody.Set(new AiComponent(targetIsHostile ? "displaced_hostile_soul" : "displaced_soul"));
        newBody.Set(new AiComponent("player_controlled"));
        oldBody.Set(oldActor with { Faction = newActor.Faction });
        newBody.Set(newActor with { Faction = oldActor.Faction });
        oldBody.Set(new FactionComponent(newActor.Faction, new[] { newActor.Faction, "displaced" }));
        newBody.Set(new FactionComponent(oldActor.Faction, new[] { oldActor.Faction, "possessed_body" }));

        var statusDeltas = new[]
        {
            ApplyStatus(oldBody, "disoriented", duration: 2, displayName: "disoriented soul"),
            ApplyStatus(newBody, "soul_swapped", duration: 8, displayName: "borrowed body"),
        };
        State.ControlledEntityId = newBody.Id;
        State.SelectedTarget = null;
        State.Deeds.Append(
            State.Turn,
            oldSoul.SoulId,
            "body_swap",
            targetIsHostile ? 5 : 3,
            State.RegionId,
            "witnessed",
            new[] { oldBody.Id.Value, newBody.Id.Value },
            new[] { "wild_magic", "body_swap", targetIsHostile ? "violation" : "consent" });

        var message = $"Your soul crosses into {newBody.Name}; {oldBody.Name} staggers with someone else behind the eyes.";
        State.AddMessage(message);
        return statusDeltas.Concat(new[]
            {
                new StateDelta(
                    "possess",
                    newBody.Id.Value,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["oldBody"] = oldBody.Id.Value,
                        ["newBody"] = newBody.Id.Value,
                        ["playerSoul"] = oldSoul.SoulId,
                        ["displacedSoul"] = newSoul.SoulId,
                    }),
            }).ToArray();
    }

    public ActionResult Standing()
    {
        var messages = State.Factions.Factions
            .OrderBy(faction => faction.Id)
            .Select(faction =>
            {
                var standing = faction.Standing.Count == 0
                    ? "unchanged"
                    : string.Join(", ", faction.Standing.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}:{pair.Value}"));
                return $"{faction.Name} ({faction.Role}): {standing}";
            })
            .ToArray();
        return ActionResult.Simple("standing", true, false, State.Turn, State.Turn, messages.Length == 0 ? new[] { "No faction standing is known." } : messages);
    }

    public ActionResult Followers()
    {
        var followers = State.Entities.Values
            .Where(entity => entity.Id != State.ControlledEntityId)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor)
                && actor.Alive
                && actor.Faction == State.ControlledEntity.Get<ActorComponent>().Faction)
            .OrderBy(entity => entity.Id.Value)
            .Select(entity => $"{entity.Name} ({entity.Id.Value})")
            .ToArray();
        return ActionResult.Simple("followers", true, false, State.Turn, State.Turn, followers.Length == 0 ? new[] { "No one is following you." } : followers);
    }

    public ActionResult Jobs()
    {
        var jobs = BuildBackgroundJobCards()
            .OrderBy(job => job.State)
            .ThenBy(job => job.Id)
            .Select(job =>
            {
                var timing = job.State switch
                {
                    "Queued" => $"queued on turn {job.CreatedTurn}",
                    "Running" => $"started on turn {job.StartedTurn}",
                    "Completed" => $"completed on turn {job.CompletedTurn}",
                    "Applied" => $"applied on turn {job.AppliedTurn}",
                    "Failed" => $"failed: {job.Error}",
                    _ => job.State,
                };
                return $"{job.Id} [{job.State}] {job.Purpose} -> {job.TargetId} ({timing})";
            })
            .ToArray();
        return ActionResult.Simple(
            "jobs",
            true,
            false,
            State.Turn,
            State.Turn,
            jobs.Length == 0 ? new[] { "No background jobs are queued." } : jobs);
    }

    public ActionResult Unsupported(string action, bool free = true)
    {
        var turnBefore = State.Turn;
        var message = $"{action} is part of the Sorcerer architecture stub, but is not implemented yet.";
        State.AddMessage(message);
        if (!free)
        {
            AdvanceTurn();
        }

        return ActionResult.Simple(
            action,
            success: false,
            consumedTurn: !free,
            turnBefore,
            State.Turn,
            message);
    }

    public void AdvanceTurn()
    {
        State.Turn += 1;
        ExpireStatuses();
        ExpireTerrain();
        ResolveScheduledEvents();
        PumpBackgroundJobs();
    }

    public void AddMessage(string message) => State.AddMessage(message);

    public Entity? EntityAt(GridPoint point) =>
        State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point);

    public Entity? FindNearestHostile()
    {
        var actor = State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        return State.Entities.Values
            .Where(entity => entity.Id != actor.Id)
            .Where(entity => entity.TryGet<ActorComponent>(out var targetActor)
                && targetActor.Alive
                && IsHostile(actor, entity))
            .OrderBy(entity => Distance(origin, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value)
            .FirstOrDefault();
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
        State.AddMessage(message);

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
        State.AddMessage(message);
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
        var restored = Math.Max(0, Math.Min(amount, actor.MaxMana - actor.Mana));
        target.Set(actor with { Mana = actor.Mana + restored });
        var message = restored == 0
            ? $"{Subject(target)} {Verb(target, "are", "is")} already bright with mana."
            : $"{Subject(target)} {Verb(target, "regain", "regains")} {restored} mana.";
        State.AddMessage(message);
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
        State.AddMessage(message);

        return new StateDelta(
            "heal",
            target.Id.Value,
            message,
            new Dictionary<string, object?> { ["amount"] = healed });
    }

    public StateDelta MoveEntity(Entity entity, GridPoint destination, string operation)
    {
        var before = entity.Get<PositionComponent>().Position;
        if (!InBounds(destination)
            || State.BlockingTerrain.Contains(destination)
            || BlockingEntityAt(destination) is not null)
        {
            var blocked = $"{entity.Name} cannot move to {destination.X},{destination.Y}.";
            State.AddMessage(blocked);
            return new StateDelta(
                operation,
                entity.Id.Value,
                blocked,
                new Dictionary<string, object?>
                {
                    ["fromX"] = before.X,
                    ["fromY"] = before.Y,
                    ["blocked"] = true,
                });
        }

        entity.Set(new PositionComponent(destination));
        var message = $"{Subject(entity)} {Verb(entity, "move", "moves")} to {destination.X},{destination.Y}.";
        State.AddMessage(message);
        return new StateDelta(
            operation,
            entity.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["fromX"] = before.X,
                ["fromY"] = before.Y,
                ["toX"] = destination.X,
                ["toY"] = destination.Y,
            });
    }

    public StateDelta SetTerrain(GridPoint point, string terrain, int? duration = null)
    {
        State.Terrain[point] = terrain;
        if (duration is > 0)
        {
            State.TerrainExpirations[point] = State.Turn + duration.Value;
        }
        else
        {
            State.TerrainExpirations.Remove(point);
        }

        if (TerrainBlocksMovement(terrain))
        {
            State.BlockingTerrain.Add(point);
        }
        else if (!IsBoundaryWall(point))
        {
            State.BlockingTerrain.Remove(point);
        }

        var message = $"The tile at {point.X},{point.Y} becomes {terrain.Replace('_', ' ')}.";
        State.AddMessage(message);
        return new StateDelta(
            "createTile",
            $"tile:{point.X},{point.Y}",
            message,
            new Dictionary<string, object?>
            {
                ["x"] = point.X,
                ["y"] = point.Y,
                ["terrain"] = terrain,
                ["duration"] = duration,
            });
    }

    public StateDelta ApplyStatus(Entity target, string status, int duration, string displayName = "")
    {
        var canonicalStatus = _statusRegistry.Canonicalize(status);
        var label = string.IsNullOrWhiteSpace(displayName) ? status : displayName;
        var current = target.TryGet<StatusContainerComponent>(out var container)
            ? container.Statuses.ToList()
            : new List<StatusInstance>();
        current.Add(new StatusInstance(canonicalStatus, label, State.Turn + duration));
        target.Set(new StatusContainerComponent(current));
        var message = $"{target.Name} is {label.Replace('_', ' ')}.";
        State.AddMessage(message);
        return new StateDelta(
            "addStatus",
            target.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["status"] = canonicalStatus,
                ["displayName"] = label,
                ["duration"] = duration,
            });
    }

    public StateDelta RemoveStatus(Entity target, string status)
    {
        if (!target.TryGet<StatusContainerComponent>(out var container))
        {
            var unchanged = $"{target.Name} has no {status} to remove.";
            State.AddMessage(unchanged);
            return new StateDelta("removeStatus", target.Id.Value, unchanged, new Dictionary<string, object?> { ["status"] = status });
        }

        var remaining = container.Statuses
            .Where(instance => !instance.Id.Equals(status, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        target.Set(new StatusContainerComponent(remaining));
        var message = $"{status.Replace('_', ' ')} leaves {target.Name}.";
        State.AddMessage(message);
        return new StateDelta("removeStatus", target.Id.Value, message, new Dictionary<string, object?> { ["status"] = status });
    }

    public Entity SpawnEntity(string prefix, string name, char glyph, GridPoint position, string faction, int hp, int attack, IReadOnlyList<string> tags)
    {
        var entity = new Entity(State.NextEntityId(prefix), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent(glyph, faction))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "summoned"))
            .Set(new ActorComponent(hp, hp, 0, 0, attack, 0, faction))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent(faction == "player" ? "ally" : "hostile_guard"))
            .Set(StatusContainerComponent.Empty())
            .Set(new SoulComponent($"{prefix}_{State.NextEntitySerial}_soul"));
        State.Entities.Add(entity.Id, entity);
        return entity;
    }

    public StateDelta AddPromise(string kind, string text, Entity? anchor = null, string triggerHint = "", string source = "wild_magic")
    {
        var subject = State.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : State.ControlledEntityId.Value;
        var promise = State.PromiseLedger.Add(
            kind,
            text,
            playerVisible: true,
            source: source,
            salience: 2,
            subject: subject,
            claimedPlace: State.RegionId,
            triggerHint: triggerHint,
            realizationKind: InferRealizationKind(kind, text));

        var bound = BindPromiseIfPossible(promise, anchor, triggerHint);
        var finalPromise = bound ?? promise;
        var message = finalPromise.Status == "bound"
            ? $"A promise binds to {finalPromise.BoundTargetId ?? finalPromise.BoundPlace}: {finalPromise.Text}"
            : $"A promise enters the world: {finalPromise.Text}";
        State.AddMessage(message);
        return new StateDelta(
            "createPromise",
            finalPromise.Id,
            message,
            new Dictionary<string, object?>
            {
                ["kind"] = finalPromise.Kind,
                ["status"] = finalPromise.Status,
                ["subject"] = finalPromise.Subject,
                ["boundPlace"] = finalPromise.BoundPlace,
                ["boundTargetId"] = finalPromise.BoundTargetId,
                ["triggerHint"] = finalPromise.TriggerHint,
                ["realizationKind"] = finalPromise.RealizationKind,
            });
    }

    public StateValidationReport ValidateState() => StateValidator.Validate(State);

    public Entity? EntityById(string id) =>
        State.Entities.TryGetValue(EntityId.Create(id), out var entity) ? entity : null;

    public Entity? ResolveEntity(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)
            || target.Equals("player", StringComparison.OrdinalIgnoreCase)
            || target.Equals("self", StringComparison.OrdinalIgnoreCase))
        {
            return State.ControlledEntity;
        }

        if (target.Equals("nearest_enemy", StringComparison.OrdinalIgnoreCase)
            || target.Equals("nearest", StringComparison.OrdinalIgnoreCase)
            || target.Equals("enemy", StringComparison.OrdinalIgnoreCase))
        {
            return FindNearestHostile();
        }

        return EntityById(target);
    }

    public MagicContextView MagicContext(OperationIndex operations)
    {
        var caster = State.ControlledEntity;
        var casterPosition = caster.Get<PositionComponent>().Position;
        var casterActor = caster.Get<ActorComponent>();
        var soulId = caster.TryGet<SoulComponent>(out var soul) ? soul.SoulId : caster.Id.Value;
        var statuses = BuildStatusCards(caster);
        var visible = State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .OrderBy(entity => entity.Id.Value)
            .Select(entity =>
            {
                var pos = entity.Get<PositionComponent>().Position;
                var actor = entity.TryGet<ActorComponent>(out var entityActor) ? entityActor : null;
                var physical = entity.TryGet<PhysicalComponent>(out var phys) ? phys : null;
                var tags = TagsFor(entity);
                return new PerceivedEntity(
                    entity.Id.Value,
                    entity.Name,
                    entity.TryGet<RenderableComponent>(out var renderable) ? renderable.Glyph : '?',
                    pos.X,
                    pos.Y,
                    pos.X - casterPosition.X,
                    pos.Y - casterPosition.Y,
                    actor?.Faction,
                    physical?.Material ?? "unknown",
                    tags,
                    actor?.HitPoints,
                    actor?.MaxHitPoints);
            })
            .ToArray();

        var terrain = BuildTiles()
            .Where(tile => tile.BlocksMovement || !tile.Terrain.Equals("floor", StringComparison.OrdinalIgnoreCase))
            .Select(tile => new TileNote(
                tile.X,
                tile.Y,
                tile.Terrain,
                tile.BlocksMovement ? new[] { "blocking" } : Array.Empty<string>()))
            .ToArray();

        return new MagicContextView(
            new CasterView(
                caster.Id.Value,
                caster.Name,
                casterPosition.X,
                casterPosition.Y,
                casterActor.HitPoints,
                casterActor.MaxHitPoints,
                casterActor.Mana,
                casterActor.MaxMana,
                soulId,
                statuses),
            visible,
            terrain,
            State.SelectedTarget,
            State.Messages.TakeLast(8).ToArray(),
            State.PromiseLedger.Promises
                .Where(promise => promise.PlayerVisible)
                .Select(ToPromiseCard)
                .ToArray(),
            operations);
    }

    public GameView View()
    {
        var entities = State.Entities.Values
            .OrderBy(entity => entity.Id.Value)
            .Select(ToEntityCard)
            .ToArray();

        var promises = State.PromiseLedger.Promises
            .Select(ToPromiseCard)
            .ToArray();

        var tiles = BuildTiles();
        var inventory = _inventoryService.BuildInventoryCards(State.ControlledEntity);
        var reagents = _inventoryService.BuildReagentCards(State.ControlledEntity);
        var statuses = BuildStatusCards(State.ControlledEntity);

        return new GameView(
            State.Width,
            State.Height,
            State.Turn,
            State.ControlledEntityId.Value,
            entities,
            promises,
            State.Messages.ToArray(),
            tiles,
            inventory,
            reagents,
            statuses,
            State.SelectedTarget);
    }

    public AgentObservation Observation(bool debug)
    {
        var validation = ValidateState();
        var debugState = debug
            ? new DebugStateView(
                State.Entities.Count,
                State.Entities.Keys.Select(id => id.Value).OrderBy(id => id).ToArray(),
                State.PromiseLedger.Promises.Select(p => p.Id).ToArray(),
                State.SelectedTarget,
                new LedgerSummary(
                    State.Deeds.Records.Count,
                    State.Factions.Factions.Count,
                    State.Legend.Tags.Count,
                    State.Memories.Records.Count,
                    State.Canon.Records.Count,
                    State.Bonds.Bonds.Count,
                    State.ScheduledEvents.Events.Count),
                validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}").ToArray(),
                BuildBackgroundJobCards())
            : null;

        return new AgentObservation(View(), debugState);
    }

    public bool InBounds(GridPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X < State.Width && point.Y < State.Height;

    public Entity? BlockingEntityAt(GridPoint point) =>
        State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement
            && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive));

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

    private EquipmentComponent EnsureEquipment(Entity entity)
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

    private void ChangeInventory(InventoryComponent inventory, string item, int delta)
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

    private Entity BuildItemEntity(string item, GridPoint position, int quantity)
    {
        var definition = _itemCatalog.Find(item);
        var idPrefix = NormalizeId(definition?.Id ?? item, "item");
        var tags = definition?.Tags ?? new[] { "item" };
        var name = definition?.Name ?? item;
        return new Entity(State.NextEntityId(idPrefix), name)
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

    private string InventoryKey(Entity entity, ItemComponent item)
    {
        var definition = _itemCatalog.Find(item.ItemType);
        if (definition is not null)
        {
            return definition.Name;
        }

        return string.IsNullOrWhiteSpace(entity.Name) ? item.ItemType : entity.Name;
    }

    private bool IsCarrying(string item)
    {
        var inventory = EnsureInventory(State.ControlledEntity);
        return FindInventoryKey(inventory, item) is not null;
    }

    private static int ParseProfileAmount(string profile, int fallback)
    {
        var parts = profile.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[1], out var parsed)
            ? Math.Clamp(parsed, 1, 99)
            : fallback;
    }

    private static string NormalizeId(string value, string fallback)
    {
        var cleaned = string.Join(
            '_',
            value.Trim().ToLowerInvariant()
                .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\' }, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(cleaned) ? fallback : cleaned;
    }

    private Entity? ResolveNearbyEntity(
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var origin = State.ControlledEntity.Get<PositionComponent>().Position;
        var candidates = State.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && Distance(origin, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position) ? Distance(origin, position.Position) : int.MaxValue)
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

        if (State.SelectedTarget is { } selected)
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

    private string DialogueLine(Entity target)
    {
        if (IsHostile(target, State.ControlledEntity))
        {
            return $"{target.Name} answers with trained imperial silence.";
        }

        if (target.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase))
        {
            State.Bonds.Set(new BondRecord(
                target.TryGet<SoulComponent>(out var targetSoul) ? targetSoul.SoulId : target.Id.Value,
                State.ControlledEntity.TryGet<SoulComponent>(out var playerSoul) ? playerSoul.SoulId : State.ControlledEntityId.Value,
                Loyalty: 2,
                Fear: 1,
                Admiration: 1,
                Resentment: 0,
                Posture: "grateful"));
            return $"{target.Name} whispers, \"If you get me out, Hollowmere will remember the color of your magic.\"";
        }

        if (target.TryGet<ProfileComponent>(out var profile))
        {
            return $"{profile.PublicName}: {profile.Appearance}";
        }

        return $"{target.Name} has nothing urgent to say.";
    }

    private IReadOnlyList<string> DescribeEntity(Entity entity)
    {
        var lines = new List<string> { $"{entity.Name} ({entity.Id.Value})." };
        if (entity.TryGet<DescriptionComponent>(out var description))
        {
            lines.Add(description.Text);
        }

        if (entity.TryGet<PhysicalComponent>(out var physical))
        {
            lines.Add($"Material: {physical.Material}; blocks movement: {physical.BlocksMovement}.");
        }

        if (entity.TryGet<ActorComponent>(out var actor))
        {
            lines.Add($"HP {actor.HitPoints}/{actor.MaxHitPoints}; faction {actor.Faction}.");
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            lines.Add($"Readable: {readable.Title}.");
        }

        if (entity.TryGet<DoorComponent>(out var door))
        {
            lines.Add(door.IsOpen ? "It is open." : string.IsNullOrWhiteSpace(door.KeyId) ? "It is closed." : "It is locked.");
        }

        if (entity.TryGet<TagsComponent>(out var tags) && tags.Tags.Count > 0)
        {
            lines.Add($"Tags: {string.Join(", ", tags.Tags)}.");
        }

        if (entity.TryGet<StatusContainerComponent>(out var statuses))
        {
            var active = statuses.Statuses.Where(IsStatusActive).Select(status => status.DisplayName).ToArray();
            if (active.Length > 0)
            {
                lines.Add($"Statuses: {string.Join(", ", active)}.");
            }
        }

        return lines;
    }

    private void ResolveDoorConsequences(Entity door, List<string> messages, List<StateDelta> deltas)
    {
        deltas.AddRange(RealizePromisesForEntity(door, "open", messages));

        if (!door.Name.Contains("cell", StringComparison.OrdinalIgnoreCase)
            && !door.Id.Value.Contains("cell", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var doorPosition = door.Get<PositionComponent>().Position;
        var prisoner = State.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("prisoner", StringComparer.OrdinalIgnoreCase)
            && entity.TryGet<PositionComponent>(out var position)
            && Distance(position.Position, doorPosition) <= 2);
        if (prisoner is null || !prisoner.TryGet<ActorComponent>(out var actor))
        {
            return;
        }

        prisoner.Set(actor with { Faction = "player" });
        prisoner.Set(new ControllerComponent(ControllerKind.Ai));
        prisoner.Set(new AiComponent("follower"));
        prisoner.Set(new FactionComponent("player", new[] { "rescued", "hollowmere" }));
        var soulId = State.ControlledEntity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : State.ControlledEntityId.Value;
        State.Deeds.Append(
            State.Turn,
            soulId,
            "freed_prisoner",
            3,
            State.RegionId,
            "witnessed",
            new[] { prisoner.Id.Value },
            new[] { "mercy", "anti_empire", "hollowmere" });
        State.Factions.AdjustStanding("empire", "suspicion", 2);
        State.Factions.AdjustStanding("hollowmere", "gratitude", 2);

        var rescue = $"{prisoner.Name} is free enough to choose you, for now.";
        messages.Add(rescue);
        deltas.Add(new StateDelta(
            "freePrisoner",
            prisoner.Id.Value,
            rescue,
            new Dictionary<string, object?>
            {
                ["faction"] = "player",
                ["deed"] = "freed_prisoner",
            }));
    }

    private void ExpireStatuses()
    {
        foreach (var entity in State.Entities.Values)
        {
            if (!entity.TryGet<StatusContainerComponent>(out var container))
            {
                continue;
            }

            var active = container.Statuses.Where(IsStatusActive).ToArray();
            if (active.Length == container.Statuses.Count)
            {
                continue;
            }

            entity.Set(new StatusContainerComponent(active));
        }
    }

    private void ExpireTerrain()
    {
        var expired = State.TerrainExpirations
            .Where(pair => pair.Value <= State.Turn)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var point in expired)
        {
            var terrain = State.Terrain.TryGetValue(point, out var existing)
                ? existing
                : "terrain";
            State.TerrainExpirations.Remove(point);
            State.Terrain.Remove(point);
            if (!IsBoundaryWall(point))
            {
                State.BlockingTerrain.Remove(point);
            }

            State.AddMessage($"The {terrain.Replace('_', ' ')} at {point.X},{point.Y} fades.");
        }
    }

    private void ResolveScheduledEvents()
    {
        foreach (var scheduled in State.ScheduledEvents.PopDue(State.Turn))
        {
            var text = scheduled.Payload.TryGetValue("text", out var rawText)
                ? Convert.ToString(rawText)
                : null;
            var description = scheduled.Payload.TryGetValue("description", out var rawDescription)
                ? Convert.ToString(rawDescription)
                : null;
            var message = !string.IsNullOrWhiteSpace(text)
                ? text!
                : !string.IsNullOrWhiteSpace(description)
                    ? description!
                    : $"Delayed magic comes due: {scheduled.Kind}.";
            State.AddMessage(message);
        }
    }

    private void EnqueueBackgroundJob(string purpose, Entity target, int priority)
    {
        if (!State.BackgroundSettings.Enabled)
        {
            return;
        }

        var activeCount = State.BackgroundJobs.Jobs.Count(job =>
            job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.Completed);
        if (activeCount >= State.BackgroundSettings.MaxQueuedJobs
            || State.BackgroundJobs.HasActiveJob(purpose, target.Id.Value)
            || State.Canon.Records.Any(record =>
                record.AttachedTo.Equals(target.Id.Value, StringComparison.OrdinalIgnoreCase)
                && record.Kind.Equals(purpose, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var job = State.BackgroundJobs.Enqueue(purpose, target.Id.Value, priority, State.Turn);
        State.AddMessage($"Background job queued: {job.Purpose} for {target.Name}.");
    }

    private void PumpBackgroundJobs()
    {
        if (!State.BackgroundSettings.Enabled)
        {
            return;
        }

        for (var count = 0; count < State.BackgroundSettings.JobsPerTurn; count++)
        {
            var queued = State.BackgroundJobs.NextQueued();
            if (queued is null)
            {
                return;
            }

            var running = queued with
            {
                State = BackgroundJobState.Running,
                StartedTurn = State.Turn,
            };
            State.BackgroundJobs.Replace(running);

            try
            {
                var text = GenerateBackgroundText(running);
                var completed = running with
                {
                    State = BackgroundJobState.Completed,
                    CompletedTurn = State.Turn,
                    ResultText = text,
                };
                State.BackgroundJobs.Replace(completed);
                ApplyBackgroundJob(completed);
            }
            catch (Exception ex)
            {
                State.BackgroundJobs.Replace(running with
                {
                    State = BackgroundJobState.Failed,
                    Error = ex.Message,
                });
            }
        }
    }

    private string GenerateBackgroundText(BackgroundJob job)
    {
        var target = EntityById(job.TargetId);
        if (target is null)
        {
            return $"Background detail for missing target {job.TargetId}.";
        }

        var tags = TagsFor(target);
        var material = target.TryGet<PhysicalComponent>(out var physical) ? physical.Material : "unknown matter";
        return job.Purpose switch
        {
            "canon_detail" => $"{target.Name} gains a quiet margin note: {DescribeTradition(tags)}",
            "entity_detail" => $"{target.Name} is {material}, tagged by {DescribeTags(tags)}, and waiting for a spell to make that matter.",
            _ => $"{target.Name} gains background detail for {job.Purpose}.",
        };
    }

    private void ApplyBackgroundJob(BackgroundJob job)
    {
        if (string.IsNullOrWhiteSpace(job.ResultText))
        {
            State.BackgroundJobs.Replace(job with
            {
                State = BackgroundJobState.Failed,
                Error = "Background job produced no text.",
            });
            return;
        }

        State.Canon.Add(
            job.Purpose,
            job.TargetId,
            job.ResultText,
            job.ResultText.Length <= 80 ? job.ResultText : $"{job.ResultText[..77]}...",
            Array.Empty<string>(),
            "background",
            State.Turn);
        State.BackgroundJobs.Replace(job with
        {
            State = BackgroundJobState.Applied,
            AppliedTurn = State.Turn,
        });
        State.AddMessage($"Background detail settles onto {job.TargetId}.");
    }

    private IReadOnlyList<BackgroundJobCard> BuildBackgroundJobCards() =>
        State.BackgroundJobs.Jobs
            .Select(job => new BackgroundJobCard(
                job.Id,
                job.Purpose,
                job.TargetId,
                job.State.ToString(),
                job.Priority,
                job.CreatedTurn,
                job.StartedTurn,
                job.CompletedTurn,
                job.AppliedTurn,
                job.ResultText,
                job.Error))
            .ToArray();

    private static string DescribeTradition(IReadOnlyList<string> tags)
    {
        if (tags.Contains("law", StringComparer.OrdinalIgnoreCase)
            || tags.Contains("imperial", StringComparer.OrdinalIgnoreCase))
        {
            return "marble law tries to make the world hold still.";
        }

        if (tags.Contains("hollowmere", StringComparer.OrdinalIgnoreCase)
            || tags.Contains("water", StringComparer.OrdinalIgnoreCase))
        {
            return "water remembers names the empire tried to flatten.";
        }

        if (tags.Contains("fire", StringComparer.OrdinalIgnoreCase))
        {
            return "fire is treated here as witness, appetite, and warning.";
        }

        return "the world keeps a little color in reserve.";
    }

    private static string DescribeTags(IReadOnlyList<string> tags) =>
        tags.Count == 0 ? "no obvious tradition" : string.Join(", ", tags.Take(6));

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > State.Turn;

    private ActionResult Attack(Entity attacker, Entity defender, int turnBefore)
    {
        var delta = AttackEntity(attacker, defender);
        AdvanceTurn();
        return new ActionResult
        {
            Action = "attack",
            Success = true,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = State.Turn,
            Messages = new[] { delta.Summary },
            Deltas = new[] { delta },
        };
    }

    public bool IsHostile(Entity actor, Entity target)
    {
        if (!actor.TryGet<ActorComponent>(out var actorStats)
            || !target.TryGet<ActorComponent>(out var targetStats))
        {
            return false;
        }

        return actorStats.Faction != targetStats.Faction
            && targetStats.Faction != "neutral";
    }

    public static int Distance(GridPoint a, GridPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private string Subject(Entity entity) =>
        entity.Id == State.ControlledEntityId ? "You" : entity.Name;

    private string ObjectName(Entity entity) =>
        entity.Id == State.ControlledEntityId ? "you" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == State.ControlledEntityId ? secondPerson : thirdPerson;

    private static void MarkDefeated(Entity entity)
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

    private bool CanEnter(GridPoint point) =>
        InBounds(point)
        && !State.BlockingTerrain.Contains(point)
        && BlockingEntityAt(point) is null;

    private static GridPoint StepToward(GridPoint from, GridPoint to) =>
        from.Translate(Math.Sign(to.X - from.X), Math.Sign(to.Y - from.Y));

    private bool IsUnableToAct(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return false;
        }

        return container.Statuses.Any(status => IsStatusActive(status) && _statusRegistry.BlocksAction(status.Id));
    }

    private bool IsUnableToMove(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return false;
        }

        return container.Statuses.Any(status => IsStatusActive(status) && _statusRegistry.BlocksMovement(status.Id));
    }

    private IReadOnlyList<MapTileCard> BuildTiles()
    {
        var tiles = new List<MapTileCard>(State.Width * State.Height);
        for (var y = 0; y < State.Height; y++)
        {
            for (var x = 0; x < State.Width; x++)
            {
                var point = new GridPoint(x, y);
                var terrain = State.Terrain.TryGetValue(point, out var tile) ? tile : State.BlockingTerrain.Contains(point) ? "wall" : "floor";
                var blocks = State.BlockingTerrain.Contains(point);
                tiles.Add(new MapTileCard(
                    x,
                    y,
                    terrain,
                    blocks,
                    blocks));
            }
        }

        return tiles;
    }

    private IReadOnlyList<StatusCard> BuildStatusCards(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return Array.Empty<StatusCard>();
        }

        return container.Statuses
            .Where(IsStatusActive)
            .Select(status =>
            {
                var definition = _statusRegistry.Find(status.Id);
                return new StatusCard(
                    status.Id,
                    status.DisplayName.Length > 0 ? status.DisplayName : definition?.DisplayName ?? status.Id,
                    status.ExpiresTurn,
                    status.Intensity);
            })
            .ToArray();
    }

    private static EntityCard ToEntityCard(Entity entity)
    {
        var position = entity.TryGet<PositionComponent>(out var pos)
            ? pos.Position
            : new GridPoint(-1, -1);
        var glyph = entity.TryGet<RenderableComponent>(out var renderable)
            ? renderable.Glyph
            : '?';
        var blocks = entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement;
        var faction = entity.TryGet<ActorComponent>(out var actor)
            ? actor.Faction
            : null;
        var tags = new List<string>();
        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        return new EntityCard(
            entity.Id.Value,
            entity.Name,
            position.X,
            position.Y,
            glyph,
            blocks,
            faction,
            actor?.HitPoints,
            actor?.MaxHitPoints,
            tags.Distinct().OrderBy(tag => tag).ToArray());
    }

    private static PromiseCard ToPromiseCard(WorldPromise promise) =>
        new(
            promise.Id,
            promise.Kind,
            promise.Status,
            promise.Text,
            promise.PlayerVisible,
            promise.Source,
            promise.Subject,
            promise.ClaimedPlace,
            promise.BoundPlace,
            promise.BoundTargetId,
            promise.TriggerHint,
            promise.RealizationKind,
            promise.RealizedIn);

    private WorldPromise? BindPromiseIfPossible(WorldPromise promise, Entity? anchor, string triggerHint)
    {
        anchor ??= ResolvePromiseAnchorFromSelectionOrText(promise.Text);
        if (anchor is not null)
        {
            AttachPromiseAnchor(anchor, promise.Id);
            return State.PromiseLedger.Bind(
                promise.Id,
                boundPlace: State.RegionId,
                boundTargetId: anchor.Id.Value,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferTriggerHint(promise.Text, anchor) : triggerHint,
                realizationKind: promise.RealizationKind);
        }

        if (CanBindToRegion(promise))
        {
            return State.PromiseLedger.Bind(
                promise.Id,
                boundPlace: State.RegionId,
                boundTargetId: null,
                triggerHint: string.IsNullOrWhiteSpace(triggerHint) ? InferTriggerHint(promise.Text, null) : triggerHint,
                realizationKind: promise.RealizationKind);
        }

        return null;
    }

    private void AttachPromiseAnchor(Entity anchor, string promiseId)
    {
        var ids = anchor.TryGet<PromiseAnchorComponent>(out var existing)
            ? existing.PromiseIds.ToList()
            : new List<string>();
        if (!ids.Contains(promiseId, StringComparer.OrdinalIgnoreCase))
        {
            ids.Add(promiseId);
        }

        anchor.Set(new PromiseAnchorComponent(ids));
    }

    private IReadOnlyList<StateDelta> RealizePromisesForEntity(Entity entity, string trigger, List<string> messages)
    {
        if (!entity.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var promiseId in anchor.PromiseIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = State.PromiseLedger.Promises.FirstOrDefault(promise =>
                promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
            if (existing is null
                || existing.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                || !PromiseTriggerMatches(existing.TriggerHint, trigger))
            {
                continue;
            }

            var realizedIn = $"{trigger}:{entity.Id.Value}";
            var realized = State.PromiseLedger.SetStatus(existing.Id, "realized", realizedIn);
            if (realized is null)
            {
                continue;
            }

            var message = $"A promise stirs awake: {realized.Text}";
            messages.Add(message);
            deltas.Add(new StateDelta(
                "realizePromise",
                realized.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["status"] = realized.Status,
                    ["trigger"] = trigger,
                    ["target"] = entity.Id.Value,
                    ["realizedIn"] = realized.RealizedIn,
                    ["realizationKind"] = realized.RealizationKind,
                }));
            deltas.AddRange(ApplyPromiseRealization(realized, entity, trigger, messages));
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyPromiseRealization(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var kind = NormalizeId(promise.RealizationKind ?? promise.Kind, "omen");
        return kind switch
        {
            "memory" => RealizePromiseMemory(promise, anchor, trigger, messages),
            "threat" => RealizePromiseThreat(promise, anchor, trigger, messages),
            "item" => RealizePromiseItem(promise, anchor, trigger, messages),
            "quest" => RealizePromiseCanon(promise, anchor, trigger, messages, "quest", "A quest takes shape"),
            "site" => RealizePromiseCanon(promise, anchor, trigger, messages, "site", "A distant place answers"),
            _ => RealizePromiseCanon(promise, anchor, trigger, messages, "omen", "The omen settles into the world"),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseMemory(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var worldMemory = State.Memories.Append(
            anchor.Id.Value,
            promise.Text,
            $"promise:{promise.Id}:{trigger}",
            Math.Max(2, promise.Salience + 1),
            shareable: true);
        var existing = anchor.TryGet<MemoryComponent>(out var memory)
            ? memory.Records.ToList()
            : new List<EntityMemoryRecord>();
        existing.Add(new EntityMemoryRecord(
            $"memory_{promise.Id}",
            promise.Text,
            promise.Id,
            trigger,
            Math.Max(2, promise.Salience + 1),
            Shareable: true));
        anchor.Set(new MemoryComponent(existing));

        var message = $"{anchor.Name} remembers something that was not there before.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseMemory",
                worldMemory.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["anchor"] = anchor.Id.Value,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseThreat(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : State.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin)
            ?? FindOpenAdjacent(State.ControlledEntity.Get<PositionComponent>().Position)
            ?? origin;
        var threatName = PromiseThreatName(promise);
        var threat = SpawnEntity(
            "promise_threat",
            threatName,
            'D',
            position,
            "empire",
            hp: 8,
            attack: 3,
            tags: new[] { "promise", "threat", "omen" });
        var message = $"{threat.Name} arrives to collect on the promise.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseThreat",
                threat.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseItem(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : State.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin) ?? origin;
        var itemName = PromiseItemName(promise);
        var item = BuildItemEntity(itemName, position, quantity: 1);
        item.Name = itemName;
        item.Set(new DescriptionComponent($"This object exists because a promise became concrete: {promise.Text}"));
        State.Entities[item.Id] = item;

        var message = $"{item.Name} appears where the promise can reach it.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseItem",
                item.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizePromiseCanon(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        string canonKind,
        string messagePrefix)
    {
        var canon = State.Canon.Add(
            canonKind,
            anchor.Id.Value,
            promise.Text,
            promise.Text,
            new[] { "promise", promise.Kind, canonKind },
            $"promise:{promise.Id}:{trigger}",
            State.Turn);
        var message = $"{messagePrefix}: {promise.Text}";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseCanon",
                canon.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["anchor"] = anchor.Id.Value,
                    ["kind"] = canonKind,
                    ["trigger"] = trigger,
                }),
        };
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        var offsets = new[]
        {
            new GridPoint(0, -1),
            new GridPoint(1, 0),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, -1),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(-1, -1),
        };

        foreach (var offset in offsets)
        {
            var candidate = origin.Translate(offset.X, offset.Y);
            if (CanEnter(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string PromiseThreatName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("collector"))
        {
            return "debt collector";
        }

        if (lower.Contains("soldier") || lower.Contains("empire") || lower.Contains("imperial"))
        {
            return "promised imperial claimant";
        }

        return "promised threat";
    }

    private static string PromiseItemName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("key"))
        {
            return "promised key";
        }

        if (lower.Contains("blade") || lower.Contains("knife") || lower.Contains("sword"))
        {
            return "promised blade";
        }

        if (lower.Contains("pearl"))
        {
            return "promised pearl";
        }

        return "promise token";
    }

    private static bool PromiseTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        var hints = triggerHint.ToLowerInvariant()
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hints.Any(hint =>
            hint == normalizedTrigger
            || (normalizedTrigger == "open" && hint is "door" or "opened" or "unlock")
            || (normalizedTrigger == "talk" && hint is "speak" or "name" or "dialogue")
            || (normalizedTrigger == "read" && hint is "notice" or "sign" or "book"));
    }

    private Entity? ResolvePromiseAnchorFromSelectionOrText(string text)
    {
        if (State.SelectedTarget is { } selected)
        {
            var selectedEntity = EntityAt(selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        var tokens = text.ToLowerInvariant()
            .Split(new[] { ' ', '-', '.', ',', ':', ';', '/', '\\', '\'' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return State.Entities.Values
            .Where(entity => entity.Id != State.ControlledEntityId)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Score = PromiseAnchorScore(entity, tokens),
                Distance = entity.TryGet<PositionComponent>(out var position)
                    ? Distance(State.ControlledEntity.Get<PositionComponent>().Position, position.Position)
                    : int.MaxValue,
            })
            .Where(candidate => candidate.Score > 0)
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Distance)
            .ThenBy(candidate => candidate.Entity.Id.Value)
            .Select(candidate => candidate.Entity)
            .FirstOrDefault();
    }

    private static int PromiseAnchorScore(Entity entity, HashSet<string> tokens)
    {
        var score = 0;
        foreach (var token in entity.Name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (tokens.Contains(token))
            {
                score += 3;
            }
        }

        if (entity.TryGet<TagsComponent>(out var tags))
        {
            score += tags.Tags.Count(tag => tokens.Contains(tag));
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            score += fixture.Tags.Count(tag => tokens.Contains(tag));
            if (tokens.Contains(fixture.FixtureType))
            {
                score += 2;
            }
        }

        if (entity.TryGet<ReadableComponent>(out var readable))
        {
            foreach (var token in readable.Title.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (tokens.Contains(token))
                {
                    score += 2;
                }
            }
        }

        return score;
    }

    private static bool CanBindToRegion(WorldPromise promise) =>
        promise.Kind.Equals("prophecy", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("quest", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("threat", StringComparison.OrdinalIgnoreCase)
        || promise.Kind.Equals("debt", StringComparison.OrdinalIgnoreCase);

    private static string InferTriggerHint(string text, Entity? anchor)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("read") || anchor?.Has<ReadableComponent>() == true)
        {
            return "read";
        }

        if (lower.Contains("open") || lower.Contains("door") || anchor?.Has<DoorComponent>() == true)
        {
            return "open";
        }

        if (lower.Contains("speak") || lower.Contains("talk") || lower.Contains("name"))
        {
            return "talk";
        }

        return "encounter";
    }

    private static string InferRealizationKind(string kind, string text)
    {
        var lower = $"{kind} {text}".ToLowerInvariant();
        if (lower.Contains("item") || lower.Contains("blade") || lower.Contains("key"))
        {
            return "item";
        }

        if (lower.Contains("enemy") || lower.Contains("collector") || lower.Contains("threat"))
        {
            return "threat";
        }

        if (lower.Contains("quest") || lower.Contains("reward"))
        {
            return "quest";
        }

        if (lower.Contains("remember") || lower.Contains("name"))
        {
            return "memory";
        }

        return kind.Equals("debt", StringComparison.OrdinalIgnoreCase) ? "threat" : "omen";
    }

    private bool IsBoundaryWall(GridPoint point) =>
        point.X == 0 || point.Y == 0 || point.X == State.Width - 1 || point.Y == State.Height - 1;

    private static bool TerrainBlocksMovement(string terrain) =>
        terrain is "wall" or "ice_wall" or "rubble" or "vines";

    private static IReadOnlyList<string> TagsFor(Entity entity)
    {
        var tags = new List<string>();
        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
            tags.Add(item.Material);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
    }
}
