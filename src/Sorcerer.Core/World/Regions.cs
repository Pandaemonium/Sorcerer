namespace Sorcerer.Core.World;

public sealed record TraditionDefinition(
    string Id,
    string Name,
    IReadOnlyList<string> Tags,
    string MagicTexture,
    string CostTexture);

public sealed record RegionMapPlacement(
    int AnchorX,
    int AnchorY,
    int SeedJitter = 0);

public sealed record RegionPropBaseDefinition(
    string Id,
    string Name,
    char Glyph,
    string FixtureType,
    string Description,
    IReadOnlyList<string> Tags,
    bool BlocksMovement = true,
    bool BlocksSight = false,
    int Weight = 1);

public sealed record RegionPropPartDefinition(
    string Id,
    string Text,
    string Description,
    IReadOnlyList<string> Tags,
    string? Material = null,
    int Weight = 1);

public sealed record RegionPropHookDefinition(
    string Kind,
    int Weight = 1,
    string? Title = null,
    string? Text = null,
    IReadOnlyList<string>? Tags = null);

public sealed record RegionPropEnsembleMemberDefinition(
    string BaseId,
    string MaterialId,
    string ConditionId,
    int OffsetX,
    int OffsetY);

public sealed record RegionPropEnsembleDefinition(
    string Id,
    int Weight,
    IReadOnlyList<RegionPropEnsembleMemberDefinition> Members);

public sealed record RegionPropGrammarDefinition(
    int MinProps,
    int MaxProps,
    int EmptyChancePercent,
    int DenseChancePercent,
    int DenseBonus,
    int EnsembleChancePercent,
    int HookChancePercent,
    IReadOnlyList<RegionPropBaseDefinition> Bases,
    IReadOnlyList<RegionPropPartDefinition> Materials,
    IReadOnlyList<RegionPropPartDefinition> Conditions,
    IReadOnlyList<RegionPropHookDefinition>? Hooks = null,
    IReadOnlyList<RegionPropEnsembleDefinition>? Ensembles = null);

public sealed record RegionGroundLootDefinition(
    int Min = 0,
    int Max = 2,
    int EmptyChancePercent = 25,
    int UsefulChancePercent = 20,
    int GoldMin = 3,
    int GoldMax = 10,
    int NearPropBiasPercent = 70);

public sealed record RegionEncounterDefinition(
    int AmbientChancePercent = 0);

public sealed record RegionPopulationCenterDefinition(int X, int Y);

public sealed record RegionNameForgeDefinition(
    IReadOnlyList<string> GivenNames,
    IReadOnlyList<string> ByNames);

public sealed record RegionWareDefinition(
    string Item,
    int MinQuantity = 1,
    int MaxQuantity = 1);

public sealed record RegionServiceDefinition(
    string Id,
    string Name,
    string Description,
    string EffectKind,
    int GoldCost = 0,
    string? ItemCost = null,
    string? TargetHint = null,
    IReadOnlyList<string>? Tags = null);

public sealed record RegionResidentArchetypeDefinition(
    string Id,
    string Title,
    char Glyph,
    string? FactionId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Descriptions,
    IReadOnlyList<string> Wants,
    IReadOnlyList<RegionWareDefinition> Wares,
    IReadOnlyList<RegionServiceDefinition> Services,
    int Weight = 1,
    int CenterWeight = 1,
    int NearWeight = 1,
    int WildWeight = 1,
    int MinHitPoints = 7,
    int MaxHitPoints = 10,
    int MinAttack = 0,
    int MaxAttack = 2,
    int KnowledgeTier = 1,
    bool RequiredAtCenter = false);

public sealed record RegionPopulationGrammarDefinition(
    double CenterMean,
    double NearMean,
    double WildMean,
    int CenterRadius,
    int NearRadius,
    int MaxResidents,
    int HermitChancePercent,
    RegionNameForgeDefinition Names,
    IReadOnlyList<RegionResidentArchetypeDefinition> Archetypes,
    IReadOnlyList<RegionPopulationCenterDefinition>? Centers = null);

public sealed record RegionDistrictDefinition(
    string Id,
    string Name,
    string Summary,
    string Terrain,
    string FeatureName,
    string FeatureDescription,
    char FeatureGlyph,
    string FeatureMaterial,
    IReadOnlyList<string> Tags,
    int PopulationPercent = 100);

public sealed record RegionLandmarkDefinition(
    string Id,
    string Name,
    string Description,
    char Glyph,
    string Material,
    IReadOnlyList<string> Tags);

public sealed record RegionSettlementGrammarDefinition(
    IReadOnlyList<string> SettlementNames,
    IReadOnlyList<string> HamletNames,
    int PrimaryRadius,
    int HamletCount,
    string RoadName,
    string RoadTerrain,
    IReadOnlyList<RegionDistrictDefinition> Districts,
    IReadOnlyList<RegionLandmarkDefinition> Landmarks);

public sealed record RegionInteriorFeatureDefinition(
    string Name,
    string Description,
    char Glyph,
    string Material,
    IReadOnlyList<string> Tags,
    bool BlocksMovement = false,
    // A feature may be a document: readable text plus data-authored claim seeds, so interiors
    // can hold plans, ledgers, and schedules without object-id-specific handlers
    // (docs/OPENING_SEQUENCE.md "promise offering from documents and props").
    string? Readable = null,
    IReadOnlyList<Entities.ClaimSeed>? Claims = null);

public sealed record RegionInteriorDefinition(
    string Id,
    string Name,
    string Kind,
    string Summary,
    string FloorTerrain,
    string WallMaterial,
    string AccessPolicy,
    string? RequiredItem,
    IReadOnlyList<string> Tags,
    IReadOnlyList<RegionInteriorFeatureDefinition> Features);

// A binding attaches an interior either to a settlement district (DistrictId) or to any
// promise-site fixture carrying SiteTag - so a promised place ("imperial relay waystation")
// can realize as an enterable interior through region data rather than bespoke code.
public sealed record RegionInteriorBinding(string DistrictId, string InteriorId, string? SiteTag = null);

public sealed record RegionInteriorGrammarDefinition(
    IReadOnlyList<RegionInteriorDefinition> Definitions,
    IReadOnlyList<RegionInteriorBinding> Bindings);

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
    IReadOnlyList<string>? AmbientLines = null,
    RegionMapPlacement? Placement = null,
    RegionPropGrammarDefinition? Props = null,
    RegionGroundLootDefinition? GroundLoot = null,
    RegionEncounterDefinition? Encounters = null,
    RegionPopulationGrammarDefinition? Population = null,
    RegionSettlementGrammarDefinition? Settlement = null,
    RegionInteriorGrammarDefinition? Interiors = null,
    RegionVocabulary? Vocabulary = null);

/// <summary>
/// WP1 (docs/CONTENT_SPRINT_PLAN.md): the cultural word-pools that generation used to select with
/// hard-coded <c>region.Id switch</c> arms in TextureGrammar, ThreatArchetypeGenerator, and the
/// promise helpers. Moving them onto region data means adding a region's flavour is pure authoring:
/// no priority region needs an id branch for content selection. Every field is optional; an absent
/// pool falls back to the shared realm/default behaviour, so unauthored regions are unchanged.
/// Threat pools must avoid promise/oath/omen/prophecy tokens (see ThreatArchetypeGenerator).
/// </summary>
public sealed record RegionVocabulary(
    IReadOnlyList<string> FixtureAdjectives,
    IReadOnlyList<string> FixtureNouns,
    string? TextureSubject,
    string? TextureDescriptionTemplate,
    IReadOnlyList<string> ThreatAdjectives,
    IReadOnlyList<string> ThreatNouns,
    string? ThreatEntryProse,
    string? PromisedSiteName)
{
    public static readonly RegionVocabulary Empty = new(
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null,
        Array.Empty<string>(),
        Array.Empty<string>(),
        null,
        null);
}

public sealed class RegionRegistry
{
    private readonly Dictionary<string, RegionDefinition> _regions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TraditionDefinition> _traditions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegionPopulationGrammarDefinition> _populations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegionSettlementGrammarDefinition> _settlements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RegionInteriorGrammarDefinition> _interiors = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<RegionDefinition> Regions => _regions.Values;

    public IReadOnlyCollection<TraditionDefinition> Traditions => _traditions.Values;

    public void AddRegion(RegionDefinition region)
    {
        var withPopulation = _populations.TryGetValue(region.Id, out var population)
            ? region with { Population = population }
            : region;
        _regions[region.Id] = _settlements.TryGetValue(region.Id, out var settlement)
            ? withPopulation with { Settlement = settlement }
            : withPopulation;
        if (_interiors.TryGetValue(region.Id, out var interiors))
        {
            _regions[region.Id] = _regions[region.Id] with { Interiors = interiors };
        }
    }

    public void AddPopulation(string regionId, RegionPopulationGrammarDefinition population)
    {
        _populations[regionId] = population;
        if (_regions.TryGetValue(regionId, out var region))
        {
            _regions[regionId] = region with { Population = population };
        }
    }

    public void AddSettlement(string regionId, RegionSettlementGrammarDefinition settlement)
    {
        _settlements[regionId] = settlement;
        if (_regions.TryGetValue(regionId, out var region))
        {
            _regions[regionId] = region with { Settlement = settlement };
        }
    }

    public void AddInteriors(string regionId, RegionInteriorGrammarDefinition interiors)
    {
        _interiors[regionId] = interiors;
        if (_regions.TryGetValue(regionId, out var region))
        {
            _regions[regionId] = region with { Interiors = interiors };
        }
    }

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
                "A heron stands in the shallows, watching the water, not you.",
                "By the path an old shrine-stone wears fresh offerings: bread, a coin, a knot of reeds.",
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
                "Somewhere near, festival music is playing, and no one is playing it.",
            }));
        return registry;
    }
}
