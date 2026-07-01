using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record ZoneSnapshot(
    string ZoneId,
    string RegionId,
    bool Generated,
    IReadOnlyDictionary<EntityId, Entity> Entities,
    IReadOnlySet<GridPoint> BlockingTerrain,
    IReadOnlyDictionary<GridPoint, string> Terrain,
    IReadOnlyDictionary<GridPoint, int> TerrainExpirations,
    IReadOnlyDictionary<GridPoint, TileFlow> TileFlows,
    IReadOnlyDictionary<string, IReadOnlySet<GridPoint>> ExploredBySoulId,
    IReadOnlyList<string> RoomProfiles,
    IReadOnlyList<string> PromiseHooks);

public sealed record RegionAffordanceCard(
    string Id,
    string Text,
    IReadOnlyList<string> Tags);
