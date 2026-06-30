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

public sealed record PhysicalComponent(
    bool BlocksMovement = true,
    bool BlocksSight = false,
    string Material = "flesh") : IEntityComponent;

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

public sealed record ItemComponent(
    string ItemType,
    int Value,
    string Material,
    IReadOnlyList<string> Tags) : IEntityComponent;

public sealed record FixtureComponent(
    string FixtureType,
    IReadOnlyList<string> Tags,
    bool CanAnchorMagic = true) : IEntityComponent;

public sealed record ReadableComponent(string Title, string TextKey = "") : IEntityComponent;

public sealed record InteractableComponent(IReadOnlyList<string> Verbs) : IEntityComponent;

public sealed record SoulComponent(string SoulId) : IEntityComponent;

public sealed record PromiseAnchorComponent(string PromiseId) : IEntityComponent;

