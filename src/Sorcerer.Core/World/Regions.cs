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
    IReadOnlyList<string> VoiceTags,
    int WildnessBase = 0,
    string FloorTerrain = "floor",
    IReadOnlyList<RegionAffordanceCard>? Affordances = null,
    string VoiceSummary = "",
    IReadOnlyList<string>? AmbientLines = null);

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
            new[] { "cold", "bureaucratic", "threatening" },
            WildnessBase: 0,
            FloorTerrain: "floor",
            Affordances: new[]
            {
                new RegionAffordanceCard("imperial_cover", "Marble walls create hard lines of sight and official blind spots.", new[] { "stealth", "law" }),
            },
            VoiceSummary: "Imperial Vigovia under marble law: censors measure and label every wonder, and boots move in unison. Keep outcomes cold, precise, and civic.",
            AmbientLines: new[]
            {
                "You hear the rhythmic stamp of boots in unison.",
                "A horn sounds three precise notes, then falls silent.",
                "Iron scrapes against iron in perfect time.",
            }));
        registry.AddRegion(new RegionDefinition(
            "vigovian_capital",
            "Vigovian Capital",
            "empire",
            "wild_color",
            100,
            new[] { "marble", "capital", "throne", "law" },
            new[] { "cold", "ceremonial", "reasonable", "afraid" },
            WildnessBase: 0,
            FloorTerrain: "polished_marble",
            Affordances: new[]
            {
                new RegionAffordanceCard("imperial_defenses", "Capital defenses still matter, but this sprint exposes a thin reachable emperor encounter.", new[] { "empire", "defenses", "capital" }),
                new RegionAffordanceCard("emperor_present", "Emperor Odran exists here as a killable ordinary actor, not a cutscene.", new[] { "emperor", "win_condition" }),
            },
            VoiceSummary: "The Vigovian capital: ceremonial marble, reasonable and afraid, where even the throne obeys the law it wrote. Keep outcomes formal, watched, and precise.",
            AmbientLines: new[]
            {
                "Somewhere a censor's bell marks the hour, exactly.",
                "Boots move in unison down a far corridor.",
                "A horn sounds three precise notes, then falls silent.",
            }));
        registry.AddRegion(new RegionDefinition(
            "hollowmere_margin",
            "Hollowmere Margin",
            "hollowmere",
            "wild_color",
            45,
            new[] { "reeds", "water", "mud", "memory" },
            new[] { "wet", "folk", "watchful" },
            WildnessBase: 2,
            FloorTerrain: "reed_floor",
            Affordances: new[]
            {
                new RegionAffordanceCard("reed_cover", "Reed beds give fugitives soft cover and water-flavored spell hooks.", new[] { "cover", "water" }),
                new RegionAffordanceCard("hollowmere_sympathy", "Hollowmere folk are more willing to hear anti-imperial stories.", new[] { "recruitment", "hollowmere" }),
            },
            VoiceSummary: "Frontier country under imperial eyes: hedgerows, market roads, old shrines, and buried strata of older magic below. Keep outcomes earthy and vivid, wonder with mud on its boots.",
            AmbientLines: new[]
            {
                "Something unseen brushes the rushes, curious.",
                "The deep places are listening, politely.",
                "Far off, water finds a new way down.",
            }));
        registry.AddRegion(new RegionDefinition(
            "wild_border",
            "Wild Border",
            "unruled",
            "wild_color",
            15,
            new[] { "flowers", "bone", "rain", "broken-law" },
            new[] { "lush", "feral", "dreamlike" },
            WildnessBase: 5,
            FloorTerrain: "wild_grass",
            Affordances: new[]
            {
                new RegionAffordanceCard("loose_reality", "Wild terrain makes transformations feel easier but attention harder to predict.", new[] { "wild_magic", "risk" }),
            },
            VoiceSummary: "Deep wild country: dreamlike, gently impossible, jewel-bright. Light lingers, glass grows, and distances disagree. Describe outcomes with strange, vivid beauty.",
            AmbientLines: new[]
            {
                "A voice hums a lullaby with no breath behind it.",
                "Your shadow arrives half a step late.",
                "Somewhere near, a festival is being remembered by the stones.",
            }));
        return registry;
    }
}
