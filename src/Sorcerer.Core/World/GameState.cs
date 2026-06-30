using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed class GameState
{
    public GameState(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public int Turn { get; set; }

    public EntityId ControlledEntityId { get; set; } = EntityId.Create("player");

    public GridPoint? SelectedTarget { get; set; }

    public Dictionary<EntityId, Entity> Entities { get; } = new();

    public HashSet<GridPoint> BlockingTerrain { get; } = new();

    public List<string> Messages { get; } = new();

    public PromiseLedger PromiseLedger { get; } = new();

    public Entity ControlledEntity => Entities[ControlledEntityId];

    public void AddMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Messages.Add(message.Trim());
        }

        if (Messages.Count > 80)
        {
            Messages.RemoveRange(0, Messages.Count - 80);
        }
    }
}

