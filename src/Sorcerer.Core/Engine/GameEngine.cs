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

    public StateDelta RestoreMana(Entity target, int amount)
    {
        var actor = target.Get<ActorComponent>();
        var restored = Math.Max(0, Math.Min(amount, actor.MaxMana - actor.Mana));
        target.Set(actor with { Mana = actor.Mana + restored });
        var message = restored == 0
            ? $"{target.Name} is already bright with mana."
            : $"{target.Name} regains {restored} mana.";
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
            ? $"{target.Name} is already whole."
            : $"{target.Name} heals {healed} HP.";
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
        var message = $"{entity.Name} moves to {destination.X},{destination.Y}.";
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
        var current = target.TryGet<StatusContainerComponent>(out var container)
            ? container.Statuses.ToList()
            : new List<StatusInstance>();
        current.Add(new StatusInstance(status, string.IsNullOrWhiteSpace(displayName) ? status : displayName, State.Turn + duration));
        target.Set(new StatusContainerComponent(current));
        var message = $"{target.Name} is {status.Replace('_', ' ')}.";
        State.AddMessage(message);
        return new StateDelta(
            "addStatus",
            target.Id.Value,
            message,
            new Dictionary<string, object?> { ["status"] = status, ["duration"] = duration });
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
                .Select(promise => new PromiseCard(
                    promise.Id,
                    promise.Kind,
                    promise.Status,
                    promise.Text,
                    promise.PlayerVisible))
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
