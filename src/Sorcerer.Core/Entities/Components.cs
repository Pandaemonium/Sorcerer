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

public sealed record BodyStatsComponent(int Vigor) : IEntityComponent;

public sealed record SoulStatsComponent(int Attunement, int Composure) : IEntityComponent;

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

/// <summary>
/// The derived combat/resolver payload of everything an entity currently has equipped (WP2,
/// docs/CONTENT_SPRINT_PLAN.md). It is a cache recomputed by EquipmentEffectService whenever
/// equipment changes and re-stamped at combat time; base ActorComponent stats are never mutated by
/// equip/unequip. Readers add these on top of base stats: attack at the strike site, defense and
/// resistance/weakness at the damage site, focus bias in resolver context.
/// </summary>
public sealed record EquipmentEffectComponent(
    int Attack,
    int Defense,
    IReadOnlyDictionary<string, int> Resistances,
    IReadOnlyDictionary<string, int> Weaknesses,
    IReadOnlyList<string> FocusBias) : IEntityComponent
{
    public static EquipmentEffectComponent Empty { get; } = new(
        0,
        0,
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
        Array.Empty<string>());

    public bool IsMeaningful =>
        Attack != 0 || Defense != 0 || Resistances.Count > 0 || Weaknesses.Count > 0 || FocusBias.Count > 0;
}

/// <summary>Per-inventory-key magical provenance. The item keeps its ordinary identity.</summary>
public sealed record ItemAlterationComponent(Dictionary<string, string> Profiles) : IEntityComponent
{
    public static ItemAlterationComponent Empty() =>
        new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
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

public sealed record MerchantComponent(
    Dictionary<string, int> Wares,
    int Gold = 30) : IEntityComponent;

public sealed record ServiceOffer(
    string Id,
    string Name,
    string Description,
    string EffectKind,
    int GoldCost = 0,
    string? ItemCost = null,
    string? TargetHint = null,
    bool Revealed = true,
    IReadOnlyList<string>? Tags = null,
    string? WantStatusOnComplete = null,
    string? WantStakesOnComplete = null,
    IReadOnlyList<string>? WantAddTagsOnComplete = null,
    IReadOnlyList<string>? WantRemoveTagsOnComplete = null);

public sealed record ServiceComponent(IReadOnlyList<ServiceOffer> Offers) : IEntityComponent
{
    public static ServiceComponent Empty() => new(Array.Empty<ServiceOffer>());
}

public sealed record FixtureComponent(
    string FixtureType,
    IReadOnlyList<string> Tags,
    bool CanAnchorMagic = true) : IEntityComponent;

public sealed record ReadableComponent(string Title, string TextKey = "") : IEntityComponent;

public sealed record ClaimSeed(
    string Text,
    string Category,
    string Subject,
    int Salience = 3,
    int Confidence = 75,
    bool PlayerVisible = true,
    bool BindAsPromise = false,
    string PromiseKind = "rumor",
    string? RealizationKind = null,
    string? TriggerHint = null,
    string? ClaimedPlace = null,
    IReadOnlyList<string>? Tags = null,
    string? SpokenText = null,
    string? ObjectiveText = null);

public sealed record ClaimSourceComponent(IReadOnlyList<ClaimSeed> Claims) : IEntityComponent;

public sealed record InteractableComponent(IReadOnlyList<string> Verbs) : IEntityComponent;

public sealed record InteriorEntranceComponent(
    string InteriorZoneId,
    string InteriorId,
    string Name,
    string Kind,
    string Summary,
    string AccessPolicy,
    string? RequiredItem,
    string ExteriorZoneId,
    int ExteriorX,
    int ExteriorY) : IEntityComponent;

public sealed record InteriorExitComponent(
    string ExteriorZoneId,
    string InteriorId,
    string InteriorName,
    int ExteriorX,
    int ExteriorY) : IEntityComponent;

public sealed record SoulComponent(string SoulId) : IEntityComponent;

public sealed record ProfileComponent(
    string PublicName,
    string Appearance,
    string Origin = "",
    string MagicalSignature = "",
    string Backstory = "",
    string PortraitPath = "") : IEntityComponent;

public sealed record WantComponent : IEntityComponent
{
    public WantComponent(
        string id,
        string text,
        int salience = 2,
        string status = "active",
        string stakes = "",
        IReadOnlyList<string>? tags = null)
    {
        Id = id;
        Text = text;
        Salience = salience;
        Status = status;
        Stakes = stakes;
        Tags = tags?.ToArray() ?? Array.Empty<string>();
    }

    public string Id { get; init; }

    public string Text { get; init; }

    public int Salience { get; init; }

    public string Status { get; init; }

    public string Stakes { get; init; }

    public IReadOnlyList<string> Tags { get; init; }
}

public sealed record KnowledgeComponent(Dictionary<string, int> TopicTiers) : IEntityComponent
{
    public static KnowledgeComponent Empty() =>
        new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

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

/// <summary>
/// Damage-type-keyed resistance (0-95, percent reduction) and weakness (0-200, percent
/// amplification) bands, read by the damage consequence applier before the flat Defense
/// reduction already applied to every hit.
/// </summary>
public sealed record ResistanceComponent(
    Dictionary<string, int> Resistances,
    Dictionary<string, int> Weaknesses) : IEntityComponent
{
    public static ResistanceComponent Empty() => new(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
}

public sealed record DelayedDamageComponent(int Buffered, int ReleaseTurn) : IEntityComponent;

/// <summary>
/// Behavior tags that reshape how <see cref="Sorcerer.Core.Engine.Systems.AiSystem"/> decides an
/// actor's turn, keyed to an optional expiry turn (null means permanent).
/// </summary>
public sealed record BehaviorTagsComponent(Dictionary<string, int?> Tags) : IEntityComponent
{
    public static BehaviorTagsComponent Empty() => new(new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase));
}
