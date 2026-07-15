using System.Text.Json;

namespace Sorcerer.Core.World;

public static class RegionCatalog
{
    private static readonly Lazy<RegionRegistry> DefaultRegistry = new(LoadDefault);

    /// <summary>Cached authored region catalog. Authored content is immutable at runtime
    /// (generated regions live on GameState, not here), so a process-wide cache is safe and
    /// keeps hot lookups — like resolving a place key to a display name — cheap.</summary>
    public static RegionRegistry Default => DefaultRegistry.Value;

    /// <summary>
    /// Turns a deed/rumor place key ("imperial_encounter:13,5") into a player-facing place name.
    /// Prefers the authored region's display name ("Marble Containment Yard") over the raw id,
    /// so scenario ids never leak into the log; falls back to a title-cased id for generated
    /// regions (whose underscore ids read fine title-cased, e.g. "Hollowmere Margin").
    /// </summary>
    public static string ReadablePlace(string? placeKey, string fallback = "the frontier")
    {
        var regionId = (placeKey ?? string.Empty).Split(':', 2, StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var authored = Default.Region(regionId)?.Name;
        if (!string.IsNullOrWhiteSpace(authored))
        {
            return authored;
        }

        var readable = regionId.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(readable)
            ? fallback
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(readable);
    }

    public static RegionRegistry LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "regions");
            var registry = LoadFrom(directory);
            if (registry.Regions.Count > 0)
            {
                return registry;
            }
        }

        var builtIn = LoadBuiltIn();
        if (builtIn.Regions.Count > 0)
        {
            return builtIn;
        }

        return RegionRegistry.CreateMinimal();
    }

    public static RegionRegistry LoadFrom(string directory)
    {
        var registry = new RegionRegistry();
        if (!Directory.Exists(directory))
        {
            return registry;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            ReadDocument(document.RootElement, registry);
        }

        return registry;
    }

    public static RegionRegistry LoadBuiltIn()
    {
        var registry = new RegionRegistry();
        var assembly = typeof(RegionCatalog).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Content.Regions.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(stream);
            ReadDocument(document.RootElement, registry);
        }

        return registry;
    }

    private static void ReadDocument(JsonElement root, RegionRegistry registry)
    {
        if (root.TryGetProperty("traditions", out var traditions)
            && traditions.ValueKind == JsonValueKind.Array)
        {
            foreach (var tradition in traditions.EnumerateArray().Select(ReadTradition))
            {
                if (!string.IsNullOrWhiteSpace(tradition.Id))
                {
                    registry.AddTradition(tradition);
                }
            }
        }

        if (root.TryGetProperty("regions", out var regions)
            && regions.ValueKind == JsonValueKind.Array)
        {
            foreach (var region in regions.EnumerateArray().Select(ReadRegion))
            {
                if (!string.IsNullOrWhiteSpace(region.Id))
                {
                    registry.AddRegion(region);
                }
            }
        }

        if (root.TryGetProperty("populations", out var populations)
            && populations.ValueKind == JsonValueKind.Array)
        {
            foreach (var populationRoot in populations.EnumerateArray())
            {
                var regionId = ReadString(populationRoot, "regionId", ReadString(populationRoot, "region_id", ""));
                var population = ReadPopulationGrammar(populationRoot);
                if (!string.IsNullOrWhiteSpace(regionId) && population is not null)
                {
                    registry.AddPopulation(regionId, population);
                }
            }
        }

        if (root.TryGetProperty("settlements", out var settlements)
            && settlements.ValueKind == JsonValueKind.Array)
        {
            foreach (var settlementRoot in settlements.EnumerateArray())
            {
                var regionId = ReadString(settlementRoot, "regionId", ReadString(settlementRoot, "region_id", ""));
                var settlement = ReadSettlementGrammar(settlementRoot);
                if (!string.IsNullOrWhiteSpace(regionId) && settlement is not null)
                {
                    registry.AddSettlement(regionId, settlement);
                }
            }
        }

        if (root.TryGetProperty("interiors", out var interiors)
            && interiors.ValueKind == JsonValueKind.Array)
        {
            foreach (var interiorRoot in interiors.EnumerateArray())
            {
                var regionId = ReadString(interiorRoot, "regionId", ReadString(interiorRoot, "region_id", ""));
                var grammar = ReadInteriorGrammar(interiorRoot);
                if (!string.IsNullOrWhiteSpace(regionId) && grammar is not null)
                {
                    registry.AddInteriors(regionId, grammar);
                }
            }
        }
    }

    private static TraditionDefinition ReadTradition(JsonElement root) =>
        new(
            ReadString(root, "id", ""),
            ReadString(root, "name", ReadString(root, "id", "")),
            ReadStringList(root, "tags"),
            ReadString(root, "magicTexture", ReadString(root, "magic_texture", "")),
            ReadString(root, "costTexture", ReadString(root, "cost_texture", "")));

    private static RegionDefinition ReadRegion(JsonElement root) =>
        new(
            ReadString(root, "id", ""),
            ReadString(root, "name", ReadString(root, "id", "")),
            ReadString(root, "realmId", ReadString(root, "realm_id", "unruled")),
            ReadString(root, "traditionId", ReadString(root, "tradition_id", "wild_color")),
            ReadInt(root, "imperialPresence", ReadInt(root, "imperial_presence", 0)),
            ReadStringList(root, "terrainTags", "terrain_tags"),
            ReadStringList(root, "voiceTags", "voice_tags"),
            ReadInt(root, "wildnessBase", ReadInt(root, "wildness_base", 0)),
            ReadString(root, "floorTerrain", ReadString(root, "floor_terrain", "floor")),
            ReadAffordances(root),
            ReadString(root, "voiceSummary", ReadString(root, "voice_summary", "")),
            ReadStringList(root, "ambientLines", "ambient_lines"),
            ReadPlacement(root),
            ReadPropGrammar(root),
            ReadGroundLoot(root));

    private static IReadOnlyList<RegionAffordanceCard> ReadAffordances(JsonElement root)
    {
        if (!root.TryGetProperty("affordances", out var value) || value.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<RegionAffordanceCard>();
        }

        return value.EnumerateArray()
            .Select(item => new RegionAffordanceCard(
                ReadString(item, "id", ""),
                ReadString(item, "text", ""),
                ReadStringList(item, "tags")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
    }

    private static RegionMapPlacement? ReadPlacement(JsonElement root)
    {
        if (!root.TryGetProperty("placement", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new RegionMapPlacement(
            ReadInt(value, "anchorX", ReadInt(value, "anchor_x", 0)),
            ReadInt(value, "anchorY", ReadInt(value, "anchor_y", 0)),
            Math.Max(0, ReadInt(value, "seedJitter", ReadInt(value, "seed_jitter", 0))));
    }

    private static RegionGroundLootDefinition? ReadGroundLoot(JsonElement root)
    {
        var value = root.TryGetProperty("groundLoot", out var camelValue)
            ? camelValue
            : root.TryGetProperty("ground_loot", out var snakeValue)
                ? snakeValue
                : default;
        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var min = Math.Clamp(ReadInt(value, "min", 0), 0, 8);
        var max = Math.Clamp(ReadInt(value, "max", Math.Max(2, min)), min, 8);
        var goldMin = Math.Max(1, ReadInt(value, "goldMin", ReadInt(value, "gold_min", 3)));
        var goldMax = Math.Max(goldMin, ReadInt(value, "goldMax", ReadInt(value, "gold_max", 10)));
        return new RegionGroundLootDefinition(
            min,
            max,
            Math.Clamp(ReadInt(value, "emptyChance", ReadInt(value, "empty_chance", 25)), 0, 100),
            Math.Clamp(ReadInt(value, "usefulChance", ReadInt(value, "useful_chance", 20)), 0, 100),
            goldMin,
            goldMax,
            Math.Clamp(ReadInt(value, "nearPropBias", ReadInt(value, "near_prop_bias", 70)), 0, 100));
    }

    private static RegionPropGrammarDefinition? ReadPropGrammar(JsonElement root)
    {
        if (!root.TryGetProperty("props", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var bases = ReadArray(value, "bases")
            .Select(item => new RegionPropBaseDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "name", ReadString(item, "id", "feature")),
                ReadChar(item, "glyph", '&'),
                ReadString(item, "fixtureType", ReadString(item, "fixture_type", "regional_prop")),
                ReadString(item, "description", ""),
                ReadStringList(item, "tags"),
                ReadBool(item, "blocksMovement", ReadBool(item, "blocks_movement", true)),
                ReadBool(item, "blocksSight", ReadBool(item, "blocks_sight", false)),
                Math.Max(1, ReadInt(item, "weight", 1))))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
        var materials = ReadArray(value, "materials")
            .Select(ReadPropPart)
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
        var conditions = ReadArray(value, "conditions")
            .Select(ReadPropPart)
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToArray();
        if (bases.Length == 0 || materials.Length == 0 || conditions.Length == 0)
        {
            return null;
        }

        var hooks = ReadArray(value, "hooks")
            .Select(item => new RegionPropHookDefinition(
                ReadString(item, "kind", ""),
                Math.Max(1, ReadInt(item, "weight", 1)),
                ReadNullableString(item, "title"),
                ReadNullableString(item, "text"),
                ReadStringList(item, "tags")))
            .Where(item => !string.IsNullOrWhiteSpace(item.Kind))
            .ToArray();
        var ensembles = ReadArray(value, "ensembles")
            .Select(item => new RegionPropEnsembleDefinition(
                ReadString(item, "id", ""),
                Math.Max(1, ReadInt(item, "weight", 1)),
                ReadArray(item, "members")
                    .Select(member => new RegionPropEnsembleMemberDefinition(
                        ReadString(member, "base", ReadString(member, "baseId", "")),
                        ReadString(member, "material", ReadString(member, "materialId", "")),
                        ReadString(member, "condition", ReadString(member, "conditionId", "")),
                        ReadInt(member, "x", ReadInt(member, "offsetX", 0)),
                        ReadInt(member, "y", ReadInt(member, "offsetY", 0))))
                    .ToArray()))
            .Where(item => !string.IsNullOrWhiteSpace(item.Id) && item.Members.Count > 0)
            .ToArray();

        var min = Math.Clamp(ReadInt(value, "min", 3), 0, 16);
        var max = Math.Clamp(ReadInt(value, "max", Math.Max(5, min)), min, 16);
        return new RegionPropGrammarDefinition(
            min,
            max,
            Math.Clamp(ReadInt(value, "emptyChance", 8), 0, 100),
            Math.Clamp(ReadInt(value, "denseChance", 20), 0, 100),
            Math.Clamp(ReadInt(value, "denseBonus", 4), 0, 12),
            Math.Clamp(ReadInt(value, "ensembleChance", 35), 0, 100),
            Math.Clamp(ReadInt(value, "hookChance", 15), 0, 100),
            bases,
            materials,
            conditions,
            hooks,
            ensembles);
    }

    private static RegionPropPartDefinition ReadPropPart(JsonElement root) =>
        new(
            ReadString(root, "id", ""),
            ReadString(root, "text", ""),
            ReadString(root, "description", ""),
            ReadStringList(root, "tags"),
            ReadNullableString(root, "material"),
            Math.Max(1, ReadInt(root, "weight", 1)));

    private static RegionPopulationGrammarDefinition? ReadPopulationGrammar(JsonElement root)
    {
        var namesRoot = root.TryGetProperty("names", out var namesValue) && namesValue.ValueKind == JsonValueKind.Object
            ? namesValue
            : default;
        var names = new RegionNameForgeDefinition(
            ReadStringList(namesRoot, "given"),
            ReadStringList(namesRoot, "bynames"));
        var archetypes = ReadArray(root, "archetypes")
            .Select(item =>
            {
                var hitPoints = ReadIntRange(item, "hitPoints", "hit_points", 7, 10);
                var attack = ReadIntRange(item, "attack", "attack", 0, 2);
                return new RegionResidentArchetypeDefinition(
                    ReadString(item, "id", ""),
                    ReadString(item, "title", ReadString(item, "id", "resident")),
                    ReadChar(item, "glyph", 'p'),
                    ReadNullableString(item, "factionId") ?? ReadNullableString(item, "faction_id"),
                    ReadStringList(item, "tags"),
                    ReadStringList(item, "roles"),
                    ReadStringList(item, "descriptions"),
                    ReadStringList(item, "wants"),
                    ReadArray(item, "wares")
                        .Select(ware => new RegionWareDefinition(
                            ReadString(ware, "item", ""),
                            Math.Max(1, ReadInt(ware, "min", 1)),
                            Math.Max(1, ReadInt(ware, "max", Math.Max(1, ReadInt(ware, "min", 1))))))
                        .Where(ware => !string.IsNullOrWhiteSpace(ware.Item))
                        .ToArray(),
                    ReadArray(item, "services")
                        .Select(service => new RegionServiceDefinition(
                            ReadString(service, "id", ""),
                            ReadString(service, "name", ReadString(service, "id", "service")),
                            ReadString(service, "description", ""),
                            ReadString(service, "effectKind", ReadString(service, "effect_kind", "record_memory")),
                            Math.Max(0, ReadInt(service, "goldCost", ReadInt(service, "gold_cost", 0))),
                            ReadNullableString(service, "itemCost") ?? ReadNullableString(service, "item_cost"),
                            ReadNullableString(service, "targetHint") ?? ReadNullableString(service, "target_hint"),
                            ReadStringList(service, "tags")))
                        .Where(service => !string.IsNullOrWhiteSpace(service.Id))
                        .ToArray(),
                    Math.Max(1, ReadInt(item, "weight", 1)),
                    Math.Max(0, ReadInt(item, "centerWeight", ReadInt(item, "center_weight", 1))),
                    Math.Max(0, ReadInt(item, "nearWeight", ReadInt(item, "near_weight", 1))),
                    Math.Max(0, ReadInt(item, "wildWeight", ReadInt(item, "wild_weight", 1))),
                    hitPoints.Min,
                    hitPoints.Max,
                    attack.Min,
                    attack.Max,
                    Math.Clamp(ReadInt(item, "knowledgeTier", ReadInt(item, "knowledge_tier", 1)), 1, 3),
                    ReadBool(item, "requiredAtCenter", ReadBool(item, "required_at_center", false)));
            })
            .Where(archetype => !string.IsNullOrWhiteSpace(archetype.Id)
                && archetype.Descriptions.Count > 0
                && archetype.Wants.Count > 0)
            .ToArray();
        if (names.GivenNames.Count == 0 || names.ByNames.Count == 0 || archetypes.Length == 0)
        {
            return null;
        }

        return new RegionPopulationGrammarDefinition(
            Math.Clamp(ReadDouble(root, "centerMean", ReadDouble(root, "center_mean", 4.0)), 0, 12),
            Math.Clamp(ReadDouble(root, "nearMean", ReadDouble(root, "near_mean", 1.5)), 0, 12),
            Math.Clamp(ReadDouble(root, "wildMean", ReadDouble(root, "wild_mean", 0.25)), 0, 12),
            Math.Max(0, ReadInt(root, "centerRadius", ReadInt(root, "center_radius", 0))),
            Math.Max(0, ReadInt(root, "nearRadius", ReadInt(root, "near_radius", 2))),
            Math.Clamp(ReadInt(root, "maxResidents", ReadInt(root, "max_residents", 8)), 0, 12),
            Math.Clamp(ReadInt(root, "hermitChance", ReadInt(root, "hermit_chance", 4)), 0, 100),
            names,
            archetypes,
            ReadArray(root, "centers")
                .Select(center => new RegionPopulationCenterDefinition(
                    ReadInt(center, "x", 0),
                    ReadInt(center, "y", 0)))
                .ToArray());
    }

    private static (int Min, int Max) ReadIntRange(
        JsonElement root,
        string camel,
        string snake,
        int fallbackMin,
        int fallbackMax)
    {
        var value = root.TryGetProperty(camel, out var camelValue)
            ? camelValue
            : root.TryGetProperty(snake, out var snakeValue)
                ? snakeValue
                : default;
        if (value.ValueKind != JsonValueKind.Array)
        {
            return (fallbackMin, fallbackMax);
        }

        var values = value.EnumerateArray()
            .Where(item => item.TryGetInt32(out _))
            .Select(item => item.GetInt32())
            .Take(2)
            .ToArray();
        var min = values.Length > 0 ? Math.Max(0, values[0]) : fallbackMin;
        var max = values.Length > 1 ? Math.Max(min, values[1]) : Math.Max(min, fallbackMax);
        return (min, max);
    }

    private static RegionSettlementGrammarDefinition? ReadSettlementGrammar(JsonElement root)
    {
        var districts = ReadArray(root, "districts")
            .Select(item => new RegionDistrictDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "name", ReadString(item, "id", "district")),
                ReadString(item, "summary", ""),
                ReadString(item, "terrain", "district_floor"),
                ReadString(item, "featureName", ReadString(item, "feature_name", "district marker")),
                ReadString(item, "featureDescription", ReadString(item, "feature_description", "")),
                ReadChar(item, "featureGlyph", ReadChar(item, "feature_glyph", '&')),
                ReadString(item, "featureMaterial", ReadString(item, "feature_material", "stone")),
                ReadStringList(item, "tags"),
                Math.Clamp(ReadInt(item, "populationPercent", ReadInt(item, "population_percent", 100)), 20, 250)))
            .Where(district => !string.IsNullOrWhiteSpace(district.Id))
            .ToArray();
        var landmarks = ReadArray(root, "landmarks")
            .Select(item => new RegionLandmarkDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "name", ReadString(item, "id", "landmark")),
                ReadString(item, "description", ""),
                ReadChar(item, "glyph", '^'),
                ReadString(item, "material", "stone"),
                ReadStringList(item, "tags")))
            .Where(landmark => !string.IsNullOrWhiteSpace(landmark.Id))
            .ToArray();
        var settlementNames = ReadStringList(root, "settlementNames", "settlement_names");
        if (settlementNames.Count == 0 || districts.Length < 3)
        {
            return null;
        }

        return new RegionSettlementGrammarDefinition(
            settlementNames,
            ReadStringList(root, "hamletNames", "hamlet_names"),
            Math.Clamp(ReadInt(root, "primaryRadius", ReadInt(root, "primary_radius", 1)), 1, 2),
            Math.Clamp(ReadInt(root, "hamletCount", ReadInt(root, "hamlet_count", 1)), 0, 3),
            ReadString(root, "roadName", ReadString(root, "road_name", "regional road")),
            ReadString(root, "roadTerrain", ReadString(root, "road_terrain", "road")),
            districts,
            landmarks);
    }

    private static RegionInteriorGrammarDefinition? ReadInteriorGrammar(JsonElement root)
    {
        var definitions = ReadArray(root, "definitions")
            .Select(item => new RegionInteriorDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "name", ReadString(item, "id", "interior")),
                ReadString(item, "kind", "significant_interior"),
                ReadString(item, "summary", ""),
                ReadString(item, "floorTerrain", ReadString(item, "floor_terrain", "interior_floor")),
                ReadString(item, "wallMaterial", ReadString(item, "wall_material", "stone")),
                ReadString(item, "accessPolicy", ReadString(item, "access_policy", "public")),
                ReadNullableString(item, "requiredItem") ?? ReadNullableString(item, "required_item"),
                ReadStringList(item, "tags"),
                ReadArray(item, "features")
                    .Select(feature => new RegionInteriorFeatureDefinition(
                        ReadString(feature, "name", "interior feature"),
                        ReadString(feature, "description", ""),
                        ReadChar(feature, "glyph", '&'),
                        ReadString(feature, "material", "stone"),
                        ReadStringList(feature, "tags"),
                        ReadBool(feature, "blocksMovement", ReadBool(feature, "blocks_movement", false)),
                        ReadNullableString(feature, "readable"),
                        ReadClaimSeeds(feature)))
                    .ToArray()))
            .Where(definition => !string.IsNullOrWhiteSpace(definition.Id)
                && !string.IsNullOrWhiteSpace(definition.Summary)
                && definition.Features.Count > 0)
            .ToArray();
        var bindings = ReadArray(root, "bindings")
            .Select(item => new RegionInteriorBinding(
                ReadString(item, "districtId", ReadString(item, "district_id", "")),
                ReadString(item, "interiorId", ReadString(item, "interior_id", "")),
                ReadNullableString(item, "siteTag") ?? ReadNullableString(item, "site_tag")))
            .Where(binding => (!string.IsNullOrWhiteSpace(binding.DistrictId) || !string.IsNullOrWhiteSpace(binding.SiteTag))
                && definitions.Any(definition => definition.Id.Equals(binding.InteriorId, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
        return definitions.Length == 0 || bindings.Length == 0
            ? null
            : new RegionInteriorGrammarDefinition(definitions, bindings);
    }

    private static IReadOnlyList<Entities.ClaimSeed>? ReadClaimSeeds(JsonElement root)
    {
        var claims = ReadArray(root, "claims")
            .Select(item => new Entities.ClaimSeed(
                ReadString(item, "text", ""),
                ReadString(item, "category", "document"),
                ReadString(item, "subject", ""),
                Salience: ReadInt(item, "salience", 3),
                Confidence: ReadInt(item, "confidence", 80),
                PlayerVisible: ReadBool(item, "playerVisible", ReadBool(item, "player_visible", true)),
                BindAsPromise: ReadBool(item, "bindAsPromise", ReadBool(item, "bind_as_promise", false)),
                PromiseKind: ReadNullableString(item, "promiseKind") ?? ReadNullableString(item, "promise_kind") ?? "rumor",
                RealizationKind: ReadNullableString(item, "realizationKind") ?? ReadNullableString(item, "realization_kind"),
                TriggerHint: ReadNullableString(item, "triggerHint") ?? ReadNullableString(item, "trigger_hint"),
                ClaimedPlace: ReadNullableString(item, "claimedPlace") ?? ReadNullableString(item, "claimed_place"),
                Tags: ReadStringList(item, "tags")))
            .Where(seed => !string.IsNullOrWhiteSpace(seed.Text))
            .ToArray();
        return claims.Length == 0 ? null : claims;
    }

    private static IReadOnlyList<JsonElement> ReadArray(JsonElement root, string property) =>
        root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray()
            : Array.Empty<JsonElement>();

    private static IReadOnlyList<string> ReadStringList(JsonElement root, string camel, string? snake = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<string>();
        }

        var property = root.TryGetProperty(camel, out var camelValue)
            ? camelValue
            : snake is not null && root.TryGetProperty(snake, out var snakeValue)
                ? snakeValue
                : default;
        if (property.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        return property.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString() ?? "")
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static char ReadChar(JsonElement root, string property, char fallback)
    {
        var value = ReadNullableString(root, property);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[0];
    }

    private static bool ReadBool(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var value)
            && (value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
                ? value.GetBoolean()
                : fallback;

    private static int ReadInt(JsonElement root, string property, int fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed)
            ? parsed
            : fallback;

    private static double ReadDouble(JsonElement root, string property, double fallback) =>
        root.TryGetProperty(property, out var value) && value.TryGetDouble(out var parsed)
            ? parsed
            : fallback;

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }
}
