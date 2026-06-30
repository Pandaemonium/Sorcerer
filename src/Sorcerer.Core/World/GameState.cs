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

    public int Seed { get; set; }

    public string RegionId { get; set; } = "imperial_encounter";

    public EntityId ControlledEntityId { get; set; } = EntityId.Create("player");

    public GridPoint? SelectedTarget { get; set; }

    public Dictionary<EntityId, Entity> Entities { get; } = new();

    public HashSet<GridPoint> BlockingTerrain { get; } = new();

    public List<string> Messages { get; } = new();

    public PromiseLedger PromiseLedger { get; } = new();

    public DeedLedger Deeds { get; } = new();

    public FactionLedger Factions { get; } = new();

    public LegendLedger Legend { get; } = new();

    public MemoryLedger Memories { get; } = new();

    public CanonLedger Canon { get; } = new();

    public BondLedger Bonds { get; } = new();

    public ScheduledEventLedger ScheduledEvents { get; } = new();

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
