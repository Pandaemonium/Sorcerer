using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.Entities;

public enum ControllerKind
{
    None,
    Player,
    Ai,
}

public sealed record PositionComponent(GridPoint Position) : IEntityComponent;

public sealed record RenderableComponent(char Glyph, string Palette = "default") : IEntityComponent;

public sealed record TagsComponent(IReadOnlyList<string> Tags) : IEntityComponent;

public sealed record DescriptionComponent(string Text) : IEntityComponent;

public sealed record PhysicalComponent(
    bool BlocksMovement = true,
    bool BlocksSight = false,
    string Material = "flesh",
    int Size = 1,
    int Durability = 0) : IEntityComponent;

public sealed record ActorComponent(
    int HitPoints,
    int MaxHitPoints,
    int Mana,
    int MaxMana,
    int Attack,
    int Defense,
    string Faction) : IEntityComponent
{
    public bool Alive => HitPoints > 0;
}

public sealed record ControllerComponent(ControllerKind Kind) : IEntityComponent;

public sealed record InventoryComponent(
    Dictionary<string, int> Items,
    HashSet<string> TreasuredItems) : IEntityComponent
{
    public static InventoryComponent Empty() => new(new Dictionary<string, int>(), new HashSet<string>());
}

public sealed record EquipmentComponent(
    Dictionary<string, string> Slots,
    HashSet<string> FocusSlots) : IEntityComponent
{
    public static EquipmentComponent Empty() => new(new Dictionary<string, string>(), new HashSet<string>());
}

public sealed record ItemComponent(
    string ItemType,
    int Value,
    string Material,
    IReadOnlyList<string> Tags,
    string StackPolicy = "commodity",
    string UseProfile = "inert",
    string? EquipmentSlot = null) : IEntityComponent;

public sealed record StackComponent(int Quantity = 1) : IEntityComponent;

public sealed record FixtureComponent(
    string FixtureType,
    IReadOnlyList<string> Tags,
    bool CanAnchorMagic = true) : IEntityComponent;

public sealed record ReadableComponent(string Title, string TextKey = "") : IEntityComponent;

public sealed record InteractableComponent(IReadOnlyList<string> Verbs) : IEntityComponent;

public sealed record SoulComponent(string SoulId) : IEntityComponent;

public sealed record ProfileComponent(
    string PublicName,
    string Appearance,
    string Origin = "",
    string MagicalSignature = "",
    string Backstory = "") : IEntityComponent;

public sealed record StatusInstance(
    string Id,
    string DisplayName,
    int? ExpiresTurn,
    int Intensity = 1,
    IReadOnlyDictionary<string, object?>? Details = null);

public sealed record StatusContainerComponent(IReadOnlyList<StatusInstance> Statuses) : IEntityComponent
{
    public static StatusContainerComponent Empty() => new(Array.Empty<StatusInstance>());
}

public sealed record EntityMemoryRecord(
    string Id,
    string Text,
    string Source,
    string Provenance,
    int Salience,
    bool Shareable);

public sealed record MemoryComponent(IReadOnlyList<EntityMemoryRecord> Records) : IEntityComponent
{
    public static MemoryComponent Empty() => new(Array.Empty<EntityMemoryRecord>());
}

public sealed record FactionComponent(
    string FactionId,
    IReadOnlyList<string> Roles) : IEntityComponent;

public sealed record DoorComponent(bool IsOpen, string? KeyId = null) : IEntityComponent;

public sealed record AiComponent(string PolicyId, IReadOnlyDictionary<string, object?>? Parameters = null) : IEntityComponent;

public sealed record SummonedComponent(string Source, int? ExpiresTurn = null) : IEntityComponent;

public sealed record PromiseAnchorComponent(IReadOnlyList<string> PromiseIds) : IEntityComponent
{
    public PromiseAnchorComponent(string promiseId)
        : this(new[] { promiseId })
    {
    }
}
