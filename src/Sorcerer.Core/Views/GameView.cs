using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Views;

public sealed record EntityCard(
    string Id,
    string Name,
    int X,
    int Y,
    char Glyph,
    bool BlocksMovement,
    string? Faction,
    int? HitPoints,
    int? MaxHitPoints,
    IReadOnlyList<string> Tags);

public sealed record MapTileCard(
    int X,
    int Y,
    string Terrain,
    bool BlocksMovement,
    bool BlocksSight,
    bool Visible = true,
    bool Explored = true);

public sealed record ItemCard(
    string Id,
    string Name,
    int Quantity,
    int Value,
    string Material,
    IReadOnlyList<string> Tags,
    bool Protected,
    bool Equipped,
    bool Focused);

public sealed record ReagentCard(
    string Name,
    int Quantity,
    int UnitValue,
    int TotalValue,
    string Material,
    IReadOnlyList<string> Tags,
    string SpellBias = "");

public sealed record LoreCardView(
    string Id,
    string Title,
    int Level,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Triggers,
    string Body);

public sealed record CasterView(
    string Id,
    string Name,
    int X,
    int Y,
    int HitPoints,
    int MaxHitPoints,
    int Mana,
    int MaxMana,
    string SoulId,
    IReadOnlyList<StatusCard> Statuses);

public sealed record ResolverLensView(
    int Vigor,
    int Attunement,
    int Composure,
    int EffectMagnitudeDelta,
    string Magnitude,
    string Volatility,
    string CostFraming,
    string Signature,
    IReadOnlyList<string> Notes);

public sealed record PerceivedEntity(
    string Id,
    string Name,
    char Glyph,
    int X,
    int Y,
    int RelativeX,
    int RelativeY,
    string? Faction,
    string Material,
    IReadOnlyList<string> Tags,
    int? HitPoints,
    int? MaxHitPoints,
    string Visibility = "visible");

public sealed record TileNote(
    int X,
    int Y,
    string Terrain,
    IReadOnlyList<string> Tags,
    string Visibility = "visible");

public sealed record OperationCardView(
    string Name,
    IReadOnlyList<string> Aliases,
    string Summary,
    string PromptGuidance,
    IReadOnlyDictionary<string, string> Fields,
    IReadOnlyList<object> Examples);

public sealed record OperationIndex(
    IReadOnlyList<string> Names,
    IReadOnlyList<OperationCardView> Cards);

public sealed record MagicContextView(
    CasterView Caster,
    IReadOnlyList<PerceivedEntity> Visible,
    IReadOnlyList<TileNote> Terrain,
    GridPoint? SelectedTarget,
    IReadOnlyList<string> RecentEvents,
    IReadOnlyList<PromiseCard> KnownPromises,
    OperationIndex Operations,
    ResolverLensView? ResolverLens = null,
    IReadOnlyList<ReagentCard>? Reagents = null,
    IReadOnlyList<LoreCardView>? Lore = null);

public sealed record StatusCard(
    string Id,
    string DisplayName,
    int? ExpiresTurn,
    int Intensity);

public sealed record PromiseCard(
    string Id,
    string Kind,
    string Status,
    string Text,
    bool PlayerVisible,
    string Source = "unknown",
    string Subject = "",
    string? ClaimedPlace = null,
    string? BoundPlace = null,
    string? BoundTargetId = null,
    string? TriggerHint = null,
    string? RealizationKind = null,
    string? RealizedIn = null);

public sealed record GameView(
    int Width,
    int Height,
    int Turn,
    string ControlledEntityId,
    IReadOnlyList<EntityCard> Entities,
    IReadOnlyList<PromiseCard> Promises,
    IReadOnlyList<string> Messages,
    IReadOnlyList<MapTileCard>? Tiles = null,
    IReadOnlyList<ItemCard>? Inventory = null,
    IReadOnlyList<ReagentCard>? Reagents = null,
    IReadOnlyList<StatusCard>? Statuses = null,
    GridPoint? SelectedTarget = null,
    CharacterSheetCard? Character = null,
    WorldCard? World = null);

public sealed record WorldCard(
    string CurrentZoneId,
    string RegionId,
    string RegionName,
    string RealmId,
    string RealmName,
    string RealmStatus,
    string RealmRuler,
    string TraditionId,
    int ImperialPresence,
    int Wildness,
    IReadOnlyList<RegionAffordanceCard> Affordances);

public sealed record CharacterSheetCard(
    string BodyEntityId,
    string SoulId,
    string PublicName,
    string Appearance,
    string OriginId,
    string OriginName,
    string Tradition,
    int Vigor,
    int Attunement,
    int Composure,
    int HitPoints,
    int MaxHitPoints,
    int Mana,
    int MaxMana,
    string MagicalSignature,
    string Backstory);

public sealed record LedgerSummary(
    int Deeds,
    int Factions,
    int LegendTags,
    int Memories,
    int CanonRecords,
    int Bonds,
    int ScheduledEvents,
    int Suspicions = 0,
    int Souls = 0,
    int Triggers = 0);

public sealed record DebugStateView(
    int EntityCount,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<string> PromiseIds,
    GridPoint? SelectedTarget,
    LedgerSummary? Ledgers = null,
    IReadOnlyList<string>? ValidationIssues = null,
    IReadOnlyList<BackgroundJobCard>? BackgroundJobs = null,
    IReadOnlyList<FactionDebugCard>? Factions = null,
    IReadOnlyList<BondDebugCard>? Bonds = null,
    string RunStatus = "running",
    string? RunConclusion = null);

public sealed record FactionDebugCard(
    string Id,
    string Name,
    string Role,
    IReadOnlyDictionary<string, int> Standing,
    IReadOnlyDictionary<string, int> Resources,
    IReadOnlyList<string> HostileRoles);

public sealed record BondDebugCard(
    string SubjectSoulId,
    string TargetSoulId,
    int Loyalty,
    int Fear,
    int Admiration,
    int Resentment,
    string Posture);

public sealed record BackgroundJobCard(
    string Id,
    string Purpose,
    string TargetId,
    string State,
    int Priority,
    int CreatedTurn,
    int? StartedTurn,
    int? CompletedTurn,
    int? AppliedTurn,
    string? ResultText,
    string? Error);

public sealed record PendingCastView(
    string Id,
    string Text,
    string State);

public sealed record AgentObservation(
    GameView View,
    DebugStateView? Debug,
    PendingCastView? PendingCast = null);
