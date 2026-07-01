using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Status;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class EffectSystem
{
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;

    public EffectSystem(GameState state, StatusRegistry statusRegistry)
    {
        _state = state;
        _statusRegistry = statusRegistry;
    }

    public StateDelta SetTerrain(GridPoint point, string terrain, int? duration = null)
    {
        _state.Terrain[point] = terrain;
        if (duration is > 0)
        {
            _state.TerrainExpirations[point] = _state.Turn + duration.Value;
        }
        else
        {
            _state.TerrainExpirations.Remove(point);
        }

        if (TerrainBlocksMovement(terrain))
        {
            _state.BlockingTerrain.Add(point);
        }
        else if (!IsBoundaryWall(point))
        {
            _state.BlockingTerrain.Remove(point);
        }

        var message = $"The tile at {point.X},{point.Y} becomes {terrain.Replace('_', ' ')}.";
        _state.AddMessage(message);
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
        var actualDuration = duration > 0
            ? duration
            : _statusRegistry.Find(canonicalStatus)?.DefaultDuration ?? 3;
        var label = string.IsNullOrWhiteSpace(displayName) ? status : displayName;
        var current = target.TryGet<StatusContainerComponent>(out var container)
            ? container.Statuses.ToList()
            : new List<StatusInstance>();
        current.Add(new StatusInstance(canonicalStatus, label, _state.Turn + actualDuration));
        target.Set(new StatusContainerComponent(current));
        var message = $"{Subject(target)} {Verb(target, "are", "is")} {label.Replace('_', ' ')}.";
        _state.AddMessage(message);
        return new StateDelta(
            "addStatus",
            target.Id.Value,
            message,
            new Dictionary<string, object?>
            {
                ["status"] = canonicalStatus,
                ["displayName"] = label,
                ["duration"] = actualDuration,
            });
    }

    public StateDelta RemoveStatus(Entity target, string status)
    {
        if (!target.TryGet<StatusContainerComponent>(out var container))
        {
            var unchanged = $"{target.Name} has no {status} to remove.";
            _state.AddMessage(unchanged);
            return new StateDelta("removeStatus", target.Id.Value, unchanged, new Dictionary<string, object?> { ["status"] = status });
        }

        var remaining = container.Statuses
            .Where(instance => !instance.Id.Equals(status, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        target.Set(new StatusContainerComponent(remaining));
        var message = $"{SentenceCase(status.Replace('_', ' '))} leaves {ObjectName(target)}.";
        _state.AddMessage(message);
        return new StateDelta("removeStatus", target.Id.Value, message, new Dictionary<string, object?> { ["status"] = status });
    }

    public Entity SpawnEntity(string prefix, string name, char glyph, GridPoint position, string faction, int hp, int attack, IReadOnlyList<string> tags)
    {
        var entity = new Entity(_state.NextEntityId(prefix), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent(glyph, faction))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "summoned"))
            .Set(new ActorComponent(hp, hp, 0, 0, attack, 0, faction))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent(faction == "player" ? "ally" : "hostile_guard"))
            .Set(StatusContainerComponent.Empty())
            .Set(new SoulComponent($"{prefix}_{_state.NextEntitySerial}_soul"));
        _state.Entities.Add(entity.Id, entity);
        return entity;
    }

    private bool IsBoundaryWall(GridPoint point) =>
        point.X == 0 || point.Y == 0 || point.X == _state.Width - 1 || point.Y == _state.Height - 1;

    private static bool TerrainBlocksMovement(string terrain) =>
        terrain is "wall" or "ice_wall" or "rubble" or "vines";

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string ObjectName(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "you" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;

    private static string SentenceCase(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? text
            : $"{char.ToUpperInvariant(text[0])}{text[1..]}";
}
