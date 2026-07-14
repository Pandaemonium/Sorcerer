namespace Sorcerer.Core.World;

public static class WorldPlaceKinds
{
    public const string Settlement = "settlement";
    public const string Road = "road";
    public const string Landmark = "landmark";
    public const string Interior = "interior";
    public const string Wilderness = "wilderness";
}

public sealed record WorldSettlement(
    string Id,
    string Name,
    string RegionId,
    int CenterX,
    int CenterY,
    int Radius,
    bool IsPrimary);

public sealed record WorldRoad(
    string Id,
    string Name,
    string FromSettlementId,
    string ToSettlementId,
    string Terrain,
    IReadOnlyList<string> ZoneIds);

public sealed record WorldLandmark(
    string Id,
    string Name,
    string RegionId,
    string ZoneId,
    RegionLandmarkDefinition Definition);

public sealed record WorldInteriorProfile(
    string Id,
    string Name,
    string Kind,
    string Summary,
    string ExteriorZoneId,
    string AccessPolicy,
    IReadOnlyList<string> Tags);

public sealed record WorldPlaceProfile(
    string ZoneId,
    string RegionId,
    string Kind,
    WorldSettlement? Settlement = null,
    RegionDistrictDefinition? District = null,
    WorldRoad? Road = null,
    WorldLandmark? Landmark = null,
    WorldInteriorProfile? Interior = null)
{
    public string DisplayName =>
        Interior?.Name
        ?? (District is not null && Settlement is not null
            ? $"{District.Name}, {Settlement.Name}"
            : Settlement?.Name ?? Landmark?.Name ?? Road?.Name ?? "open country");

    public string Summary =>
        Interior?.Summary
        ?? District?.Summary
        ?? Landmark?.Definition.Description
        ?? (Road is not null ? $"You are on {Road.Name}." : "The country here lies between named places.");

    public string Terrain =>
        Interior is not null ? "interior" : District?.Terrain
        ?? Landmark?.Definition.Material
        ?? Road?.Terrain
        ?? "";

    public double PopulationMultiplier =>
        Interior is not null ? 0.35 : District is null ? 1.0 : District.PopulationPercent / 100.0;

    public IReadOnlyList<string> Tags =>
        (Interior?.Tags
            ?? District?.Tags
            ?? Landmark?.Definition.Tags
            ?? (Road is not null ? new[] { "road", "travel" } : new[] { "wilderness" }))
        .Append(Kind)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record NearestSettlement(
    WorldSettlement Settlement,
    int Distance,
    string Direction);

public sealed class WorldPlaceGraph
{
    private readonly IReadOnlyDictionary<string, RegionDefinition> _regions;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<WorldRoad>> _roadsByZone;
    private readonly IReadOnlyDictionary<string, WorldLandmark> _landmarksByZone;

    private WorldPlaceGraph(
        int seed,
        IReadOnlyDictionary<string, RegionDefinition> regions,
        IReadOnlyList<WorldSettlement> settlements,
        IReadOnlyList<WorldRoad> roads,
        IReadOnlyList<WorldLandmark> landmarks)
    {
        Seed = seed;
        _regions = regions;
        Settlements = settlements;
        Roads = roads;
        Landmarks = landmarks;
        _roadsByZone = roads
            .SelectMany(road => road.ZoneIds.Select(zoneId => (ZoneId: zoneId, Road: road)))
            .GroupBy(item => item.ZoneId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<WorldRoad>)group.Select(item => item.Road).OrderBy(road => road.Id).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        _landmarksByZone = landmarks.ToDictionary(landmark => landmark.ZoneId, StringComparer.OrdinalIgnoreCase);
    }

    public int Seed { get; }

    public IReadOnlyList<WorldSettlement> Settlements { get; }

    public IReadOnlyList<WorldRoad> Roads { get; }

    public IReadOnlyList<WorldLandmark> Landmarks { get; }

    public static WorldPlaceGraph Create(int seed, RegionRegistry registry)
    {
        var regions = registry.Regions.ToDictionary(region => region.Id, StringComparer.OrdinalIgnoreCase);
        var primaries = regions.Values
            .Where(region => region.Placement is not null && region.Settlement is not null)
            .OrderBy(region => region.Id, StringComparer.OrdinalIgnoreCase)
            .Select(region => CreatePrimary(seed, region))
            .ToArray();
        var settlements = primaries.ToList();
        foreach (var primary in primaries)
        {
            var region = regions[primary.RegionId];
            settlements.AddRange(CreateHamlets(seed, region, primary, primaries));
        }

        var roads = BuildRoads(seed, settlements, primaries, regions);
        var landmarks = BuildLandmarks(seed, regions.Values, primaries, settlements, roads);
        return new WorldPlaceGraph(seed, regions, settlements, roads, landmarks);
    }

    public WorldPlaceProfile Profile(string zoneId, string regionId)
    {
        var (x, y) = ParseZoneId(zoneId);
        var settlement = Settlements
            .Where(candidate => candidate.RegionId.Equals(regionId, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => Math.Max(Math.Abs(x - candidate.CenterX), Math.Abs(y - candidate.CenterY)) <= candidate.Radius)
            .OrderByDescending(candidate => candidate.IsPrimary)
            .ThenBy(candidate => candidate.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (settlement is not null && _regions.TryGetValue(regionId, out var region) && region.Settlement is { } grammar)
        {
            var district = DistrictFor(grammar, settlement, x, y);
            return new WorldPlaceProfile(zoneId, regionId, WorldPlaceKinds.Settlement, settlement, district);
        }

        if (_landmarksByZone.TryGetValue(zoneId, out var landmark)
            && landmark.RegionId.Equals(regionId, StringComparison.OrdinalIgnoreCase))
        {
            return new WorldPlaceProfile(zoneId, regionId, WorldPlaceKinds.Landmark, Landmark: landmark);
        }

        if (_roadsByZone.TryGetValue(zoneId, out var roads) && roads.Count > 0)
        {
            return new WorldPlaceProfile(zoneId, regionId, WorldPlaceKinds.Road, Road: roads[0]);
        }

        return new WorldPlaceProfile(zoneId, regionId, WorldPlaceKinds.Wilderness);
    }

    public NearestSettlement Nearest(string zoneId, string? regionId = null)
    {
        var (x, y) = ParseZoneId(zoneId);
        var candidates = string.IsNullOrWhiteSpace(regionId)
            ? Settlements
            : Settlements.Where(settlement => settlement.RegionId.Equals(regionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Count == 0)
        {
            var fallback = new WorldSettlement(
                "settlement_unknown",
                "no mapped settlement",
                regionId ?? "unknown",
                x,
                y,
                Radius: 0,
                IsPrimary: false);
            return new NearestSettlement(fallback, 0, "here");
        }

        var nearest = candidates
            .OrderBy(settlement => Manhattan(x, y, settlement.CenterX, settlement.CenterY))
            .ThenByDescending(settlement => settlement.IsPrimary)
            .ThenBy(settlement => settlement.Id, StringComparer.OrdinalIgnoreCase)
            .First();
        var dx = nearest.CenterX - x;
        var dy = nearest.CenterY - y;
        return new NearestSettlement(nearest, Math.Abs(dx) + Math.Abs(dy), DirectionText(dx, dy));
    }

    public static (int X, int Y) AnchorFor(int seed, RegionDefinition region)
    {
        var placement = region.Placement ?? new RegionMapPlacement(0, 0);
        if (placement.SeedJitter == 0)
        {
            return (placement.AnchorX, placement.AnchorY);
        }

        var span = (placement.SeedJitter * 2) + 1;
        var xJitter = (WorldRoll.StableSeed(seed, region.Id, "map_x") % span) - placement.SeedJitter;
        var yJitter = (WorldRoll.StableSeed(seed, region.Id, "map_y") % span) - placement.SeedJitter;
        return (placement.AnchorX + xJitter, placement.AnchorY + yJitter);
    }

    private static WorldSettlement CreatePrimary(int seed, RegionDefinition region)
    {
        var grammar = region.Settlement!;
        var anchor = AnchorFor(seed, region);
        var name = Pick(grammar.SettlementNames, WorldRoll.StableSeed(seed, region.Id, "settlement_name"));
        return new WorldSettlement(
            $"settlement_{NormalizeToken(region.Id)}_primary",
            name,
            region.Id,
            anchor.X,
            anchor.Y,
            grammar.PrimaryRadius,
            IsPrimary: true);
    }

    private static IReadOnlyList<WorldSettlement> CreateHamlets(
        int seed,
        RegionDefinition region,
        WorldSettlement primary,
        IReadOnlyList<WorldSettlement> primaries)
    {
        var grammar = region.Settlement!;
        var explicitCenters = (region.Population?.Centers ?? Array.Empty<RegionPopulationCenterDefinition>())
            .Select(center => (center.X, center.Y))
            .Where(center => Manhattan(center.X, center.Y, primary.CenterX, primary.CenterY) > primary.Radius)
            .Distinct()
            .Take(grammar.HamletCount)
            .ToList();
        var candidates = explicitCenters
            .Concat(RingCandidates(primary.CenterX, primary.CenterY, 4)
            .Distinct()
            .Where(point => BelongsToPrimary(point.X, point.Y, primary, primaries))
            .OrderBy(point => WorldRoll.StableSeed(seed, region.Id, point.X.ToString(), point.Y.ToString(), "hamlet"))
            .Take(Math.Max(0, grammar.HamletCount - explicitCenters.Count)))
            .ToArray();
        return candidates.Select((point, index) =>
        {
            var name = grammar.HamletNames.Count > 0
                ? Pick(grammar.HamletNames, WorldRoll.StableSeed(seed, region.Id, index.ToString(), "hamlet_name"))
                : $"{primary.Name} Wayside";
            return new WorldSettlement(
                $"settlement_{NormalizeToken(region.Id)}_hamlet_{index + 1}",
                name,
                region.Id,
                point.X,
                point.Y,
                Radius: 0,
                IsPrimary: false);
        }).ToArray();
    }

    private static IReadOnlyList<WorldRoad> BuildRoads(
        int seed,
        IReadOnlyList<WorldSettlement> settlements,
        IReadOnlyList<WorldSettlement> primaries,
        IReadOnlyDictionary<string, RegionDefinition> regions)
    {
        var edges = PrimarySpanningEdges(primaries).ToList();
        edges.AddRange(settlements
            .Where(settlement => !settlement.IsPrimary)
            .Select(hamlet => (From: primaries.Single(primary => primary.RegionId == hamlet.RegionId), To: hamlet)));
        return edges.Select((edge, index) =>
        {
            var grammar = regions[edge.From.RegionId].Settlement!;
            var roadName = edge.From.RegionId.Equals(edge.To.RegionId, StringComparison.OrdinalIgnoreCase)
                ? grammar.RoadName
                : $"{edge.From.Name}–{edge.To.Name} road";
            return new WorldRoad(
                $"road_{index + 1}_{NormalizeToken(edge.From.Id)}_{NormalizeToken(edge.To.Id)}",
                roadName,
                edge.From.Id,
                edge.To.Id,
                grammar.RoadTerrain,
                ManhattanPath(seed, edge.From, edge.To));
        }).ToArray();
    }

    private static IReadOnlyList<(WorldSettlement From, WorldSettlement To)> PrimarySpanningEdges(
        IReadOnlyList<WorldSettlement> primaries)
    {
        if (primaries.Count < 2)
        {
            return Array.Empty<(WorldSettlement, WorldSettlement)>();
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { primaries[0].Id };
        var edges = new List<(WorldSettlement From, WorldSettlement To)>();
        while (visited.Count < primaries.Count)
        {
            var edge = primaries
                .Where(from => visited.Contains(from.Id))
                .SelectMany(from => primaries
                    .Where(to => !visited.Contains(to.Id))
                    .Select(to => (From: from, To: to, Distance: Manhattan(from.CenterX, from.CenterY, to.CenterX, to.CenterY))))
                .OrderBy(candidate => candidate.Distance)
                .ThenBy(candidate => candidate.From.Id, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.To.Id, StringComparer.OrdinalIgnoreCase)
                .First();
            edges.Add((edge.From, edge.To));
            visited.Add(edge.To.Id);
        }

        return edges;
    }

    private static IReadOnlyList<string> ManhattanPath(int seed, WorldSettlement from, WorldSettlement to)
    {
        var horizontalFirst = WorldRoll.StableSeed(seed, from.Id, to.Id, "road_bend") % 2 == 0;
        var points = new List<(int X, int Y)> { (from.CenterX, from.CenterY) };
        var current = points[0];
        if (horizontalFirst)
        {
            WalkAxis(points, ref current, to.CenterX, horizontal: true);
            WalkAxis(points, ref current, to.CenterY, horizontal: false);
        }
        else
        {
            WalkAxis(points, ref current, to.CenterY, horizontal: false);
            WalkAxis(points, ref current, to.CenterX, horizontal: true);
        }

        return points.Select(point => $"{point.X},{point.Y}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void WalkAxis(List<(int X, int Y)> points, ref (int X, int Y) current, int target, bool horizontal)
    {
        while ((horizontal ? current.X : current.Y) != target)
        {
            var value = horizontal ? current.X : current.Y;
            var step = Math.Sign(target - value);
            current = horizontal ? (current.X + step, current.Y) : (current.X, current.Y + step);
            points.Add(current);
        }
    }

    private static IReadOnlyList<WorldLandmark> BuildLandmarks(
        int seed,
        IEnumerable<RegionDefinition> regions,
        IReadOnlyList<WorldSettlement> primaries,
        IReadOnlyList<WorldSettlement> settlements,
        IReadOnlyList<WorldRoad> roads)
    {
        var occupied = settlements.Select(settlement => $"{settlement.CenterX},{settlement.CenterY}")
            .Concat(roads.SelectMany(road => road.ZoneIds))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var landmarks = new List<WorldLandmark>();
        foreach (var region in regions.Where(region => region.Settlement?.Landmarks.Count > 0))
        {
            var primary = primaries.Single(settlement => settlement.RegionId == region.Id);
            var candidates = RingCandidates(primary.CenterX, primary.CenterY, 3)
                .Where(point => BelongsToPrimary(point.X, point.Y, primary, primaries))
                .Where(point => !occupied.Contains($"{point.X},{point.Y}"))
                .OrderBy(point => WorldRoll.StableSeed(seed, region.Id, point.X.ToString(), point.Y.ToString(), "landmark"))
                .ToArray();
            if (candidates.Length == 0)
            {
                continue;
            }

            var definition = Pick(region.Settlement!.Landmarks, WorldRoll.StableSeed(seed, region.Id, "landmark_kind"));
            var point = candidates[0];
            var zoneId = $"{point.X},{point.Y}";
            occupied.Add(zoneId);
            landmarks.Add(new WorldLandmark(
                $"landmark_{NormalizeToken(region.Id)}_{NormalizeToken(definition.Id)}",
                definition.Name,
                region.Id,
                zoneId,
                definition));
        }

        return landmarks;
    }

    private static RegionDistrictDefinition DistrictFor(
        RegionSettlementGrammarDefinition grammar,
        WorldSettlement settlement,
        int x,
        int y)
    {
        if (settlement.Radius == 0)
        {
            return grammar.Districts[0];
        }

        var width = (settlement.Radius * 2) + 1;
        var dx = x - settlement.CenterX + settlement.Radius;
        var dy = y - settlement.CenterY + settlement.Radius;
        var ordinal = (dy * width) + dx;
        return grammar.Districts[Math.Abs(ordinal) % grammar.Districts.Count];
    }

    private static bool BelongsToPrimary(
        int x,
        int y,
        WorldSettlement expected,
        IReadOnlyList<WorldSettlement> primaries) =>
        primaries
            .OrderBy(primary => Manhattan(x, y, primary.CenterX, primary.CenterY))
            .ThenBy(primary => primary.Id, StringComparer.OrdinalIgnoreCase)
            .First()
            .Id.Equals(expected.Id, StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<(int X, int Y)> RingCandidates(int centerX, int centerY, int radius)
    {
        for (var y = -radius; y <= radius; y++)
        {
            for (var x = -radius; x <= radius; x++)
            {
                if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius)
                {
                    yield return (centerX + x, centerY + y);
                }
            }
        }
    }

    public static string DirectionText(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
        {
            return "here";
        }

        var vertical = dy > 0 ? "south" : dy < 0 ? "north" : "";
        var horizontal = dx > 0 ? "east" : dx < 0 ? "west" : "";
        return string.IsNullOrWhiteSpace(vertical)
            ? horizontal
            : string.IsNullOrWhiteSpace(horizontal)
                ? vertical
                : $"{vertical}-{horizontal}";
    }

    private static T Pick<T>(IReadOnlyList<T> values, int seed) =>
        values[Math.Abs(seed) % values.Count];

    private static int Manhattan(int x1, int y1, int x2, int y2) =>
        Math.Abs(x1 - x2) + Math.Abs(y1 - y2);

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }
}
