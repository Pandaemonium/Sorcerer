using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;

namespace Sorcerer.Core.World;

public sealed record TileFlow(int Dx, int Dy, int? ExpiresTurn);

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

    public DeterministicRng Rng { get; set; } = new(1);

    public string RegionId { get; set; } = "imperial_encounter";

    public string CurrentZoneId { get; set; } = "0,0";

    public string RunStatus { get; set; } = "running";

    public string? RunConclusion { get; set; }

    public int NextEntitySerial { get; set; } = 1;

    public EntityId ControlledEntityId { get; set; } = EntityId.Create("player");

    public GridPoint? SelectedTarget { get; set; }

    /// <summary>The controlled entity's last movement offset, read by the "mimic" behavior tag.</summary>
    public GridPoint? LastControlledMoveDelta { get; set; }

    public Dictionary<EntityId, Entity> Entities { get; } = new();

    public Dictionary<string, ZoneSnapshot> Zones { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<GridPoint> BlockingTerrain { get; } = new();

    public Dictionary<GridPoint, string> Terrain { get; } = new();

    public Dictionary<GridPoint, int> TerrainExpirations { get; } = new();

    /// <summary>
    /// Standing tile fields (conveyors, gravity wells, wind) that translate whoever stands on
    /// them by <see cref="TileFlow.Dx"/>/<see cref="TileFlow.Dy"/> each turn, applied by
    /// <see cref="Sorcerer.Core.Engine.Systems.TurnSystem"/>.
    /// </summary>
    public Dictionary<GridPoint, TileFlow> TileFlows { get; } = new();

    public Dictionary<string, HashSet<GridPoint>> ExploredBySoulId { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Messages { get; } = new();

    public SoulLedger Souls { get; } = new();

    public PromiseLedger PromiseLedger { get; } = new();

    public DeedLedger Deeds { get; } = new();

    public FactionLedger Factions { get; } = new();

    public LegendLedger Legend { get; } = new();

    public MemoryLedger Memories { get; } = new();

    public ClaimLedger Claims { get; } = new();

    public RumorLedger Rumors { get; } = new();

    public WorldTurnLedger WorldTurns { get; } = new();

    public CanonLedger Canon { get; } = new();

    public BondLedger Bonds { get; } = new();

    public ScheduledEventLedger ScheduledEvents { get; } = new();

    public TriggerLedger Triggers { get; } = new();

    public PersistentEffectLedger PersistentEffects { get; } = new();

    public EchoLedger Echoes { get; } = new();

    public SuspicionLedger Suspicions { get; } = new();

    public BackgroundJobSettings BackgroundSettings { get; set; } = new();

    public BackgroundJobQueue BackgroundJobs { get; } = new();

    public Dictionary<string, object?> WorldFlags { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Entity ControlledEntity => Entities[ControlledEntityId];

    public EntityId NextEntityId(string prefix) =>
        EntityId.Create($"{prefix}_{NextEntitySerial++}");

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
