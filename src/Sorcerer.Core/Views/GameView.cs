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

public sealed record PromiseCard(
    string Id,
    string Kind,
    string Status,
    string Text,
    bool PlayerVisible);

public sealed record GameView(
    int Width,
    int Height,
    int Turn,
    string ControlledEntityId,
    IReadOnlyList<EntityCard> Entities,
    IReadOnlyList<PromiseCard> Promises,
    IReadOnlyList<string> Messages);

public sealed record DebugStateView(
    int EntityCount,
    IReadOnlyList<string> EntityIds,
    IReadOnlyList<string> PromiseIds,
    GridPoint? SelectedTarget);

public sealed record AgentObservation(
    GameView View,
    DebugStateView? Debug);

