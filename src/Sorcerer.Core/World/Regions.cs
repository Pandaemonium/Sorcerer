namespace Sorcerer.Core.World;

public sealed record TraditionDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Tags,
    string MagicTexture,
    string CostTexture);

public sealed record RegionDefinition(
    string Id,
    string Name,
    string RealmId,
    string TraditionId,
    int ImperialPresence,
    IReadOnlyList<string> TerrainTags,
    IReadOnlyList<string> VoiceTags);

public sealed class RegionRegistry
{
    private readonly Dictionary<string, RegionDefinition> _regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TraditionDefinition> _traditions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<RegionDefinition> Regions => _regions.Values;

    public IReadOnlyCollection<TraditionDefinition> Traditions => _traditions.Values;

    public void AddRegion(RegionDefinition region) => _regions[region.Id] = region;

    public void AddTradition(TraditionDefinition tradition) => _traditions[tradition.Id] = tradition;

    public RegionDefinition? Region(string id) =>
        _regions.TryGetValue(id, out var region) ? region : null;

    public TraditionDefinition? Tradition(string id) =>
        _traditions.TryGetValue(id, out var tradition) ? tradition : null;

    public static RegionRegistry CreateMinimal()
    {
        var registry = new RegionRegistry();
        registry.AddTradition(new TraditionDefinition(
            "wild_color",
            "Wild Color",
            new[] { "wild", "color", "names", "omens" },
            "bright, unruly, personal",
            "debt, stain, altered names"));
        registry.AddRegion(new RegionDefinition(
            "imperial_encounter",
            "Marble Containment Yard",
            "empire",
            "wild_color",
            90,
            new[] { "marble", "law", "containment" },
            new[] { "cold", "bureaucratic", "threatening" }));
        return registry;
    }
}
