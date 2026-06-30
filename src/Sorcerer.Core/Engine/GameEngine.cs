using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Status;
using Sorcerer.Core.Validation;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine;

public sealed class GameEngine
{
    private readonly InventoryService _inventoryService = new(ItemCatalog.CreateMinimal());
    private readonly StatusRegistry _statusRegistry = StatusRegistry.CreateDefault();

    public GameEngine(GameState state)
    {
        State = state;
    }

    public GameState State { get; }

    public ActionResult MoveControlled(Direction direction)
    {
        var turnBefore = State.Turn;
        var actor = State.ControlledEntity;
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

    public ActionResult Unsupported(string action, bool free = true)
    {
        var message = $"{action} is part of the Sorcerer architecture stub, but is not implemented yet.";
        State.AddMessage(message);
        return ActionResult.Simple(
            action,
            success: false,
            consumedTurn: !free,
            State.Turn,
            State.Turn,
            message);
    }

    public void AdvanceTurn() => State.Turn += 1;

    public void AddMessage(string message) => State.AddMessage(message);

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

        var message = updated.Alive
            ? $"{target.Name} takes {actual} {damageType} damage."
            : $"{target.Name} falls.";
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

    public StateDelta HealEntity(Entity target, int amount)
    {
        var actor = target.Get<ActorComponent>();
        var healed = Math.Max(0, Math.Min(amount, actor.MaxHitPoints - actor.HitPoints));
        target.Set(actor with { HitPoints = actor.HitPoints + healed });
        var message = healed == 0
            ? $"{target.Name} is already whole."
            : $"{target.Name} heals {healed} HP.";
        State.AddMessage(message);

        return new StateDelta(
            "heal",
            target.Id.Value,
            message,
            new Dictionary<string, object?> { ["amount"] = healed });
    }

    public StateDelta AddPromise(string kind, string text)
    {
        var promise = State.PromiseLedger.Add(kind, text, playerVisible: true);
        var message = $"A promise enters the world: {promise.Text}";
        State.AddMessage(message);
        return new StateDelta(
            "createPromise",
            promise.Id,
            message,
            new Dictionary<string, object?>
            {
                ["kind"] = promise.Kind,
                ["status"] = promise.Status,
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

    public GameView View()
    {
        var entities = State.Entities.Values
            .OrderBy(entity => entity.Id.Value)
            .Select(ToEntityCard)
            .ToArray();

        var promises = State.PromiseLedger.Promises
            .Select(promise => new PromiseCard(
                promise.Id,
                promise.Kind,
                promise.Status,
                promise.Text,
                promise.PlayerVisible))
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
            statuses);
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
                validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}").ToArray())
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

    private ActionResult Attack(Entity attacker, Entity defender, int turnBefore)
    {
        var attackerActor = attacker.Get<ActorComponent>();
        var defenderActor = defender.Get<ActorComponent>();
        var damage = Math.Max(1, attackerActor.Attack - defenderActor.Defense);
        var delta = DamageEntity(defender, damage, "physical");
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

    private static bool IsHostile(Entity actor, Entity target)
    {
        if (!actor.TryGet<ActorComponent>(out var actorStats)
            || !target.TryGet<ActorComponent>(out var targetStats))
        {
            return false;
        }

        return actorStats.Faction != targetStats.Faction
            && targetStats.Faction != "neutral";
    }

    private static int Distance(GridPoint a, GridPoint b) =>
        Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private IReadOnlyList<MapTileCard> BuildTiles()
    {
        var tiles = new List<MapTileCard>(State.Width * State.Height);
        for (var y = 0; y < State.Height; y++)
        {
            for (var x = 0; x < State.Width; x++)
            {
                var point = new GridPoint(x, y);
                var blocks = State.BlockingTerrain.Contains(point);
                tiles.Add(new MapTileCard(
                    x,
                    y,
                    blocks ? "wall" : "floor",
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
}
