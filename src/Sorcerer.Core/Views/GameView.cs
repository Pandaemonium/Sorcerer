using Sorcerer.Core.Primitives;

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
    IReadOnlyList<string> Tags);

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
    int? MaxHitPoints);

public sealed record TileNote(
    int X,
    int Y,
    string Terrain,
    IReadOnlyList<string> Tags);

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
    OperationIndex Operations);

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
    GridPoint? SelectedTarget = null);

public sealed record LedgerSummary(
    int Deeds,
    int Factions,
    int LegendTags,
    int Memories,
    int CanonRecords,
    int Bonds,
    int ScheduledEvents);

public sealed record DebugStateView(
    int EntityCount,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<string> PromiseIds,
    GridPoint? SelectedTarget,
    LedgerSummary? Ledgers = null,
    IReadOnlyList<string>? ValidationIssues = null,
    IReadOnlyList<BackgroundJobCard>? BackgroundJobs = null);

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
