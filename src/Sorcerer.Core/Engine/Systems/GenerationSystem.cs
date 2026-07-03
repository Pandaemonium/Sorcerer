using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class GenerationSystem
{
    private readonly ItemCatalog _itemCatalog;
    private readonly LoreCatalog _loreCatalog;
    private readonly PromiseRealizationSystem _promiseRealizationSystem;
    private readonly WorldTurnSystem _worldTurnSystem = new();
    private readonly Func<WorldConsequence, WorldConsequenceApplyResult> _applyConsequence;
    private readonly Func<GameState, WorldConsequence, WorldConsequenceApplyResult> _applyGeneratedConsequence;
    private readonly GameState _state;
    private readonly RegionRegistry _regions = RegionRegistry.CreateMinimal();

    public GenerationSystem(
        GameState state,
        ItemCatalog itemCatalog,
        LoreCatalog loreCatalog,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null,
        Func<GameState, WorldConsequence, WorldConsequenceApplyResult>? applyGeneratedConsequence = null)
    {
        _itemCatalog = itemCatalog;
        _loreCatalog = loreCatalog;
        _state = state;
        _applyConsequence = applyConsequence ?? (consequence => WorldConsequenceGuard.ApplyWithNewApplier(state, consequence));
        _applyGeneratedConsequence = applyGeneratedConsequence ?? ApplyGeneratedConsequenceToDetached;
        _promiseRealizationSystem = new PromiseRealizationSystem(state, applyConsequence: _applyConsequence);
    }

    public RegionDefinition CurrentRegion =>
        _regions.Region(_state.RegionId) ?? _regions.Region("imperial_encounter")!;

    public IReadOnlyList<RegionAffordanceCard> CurrentAffordances =>
        CurrentRegion.Affordances ?? Array.Empty<RegionAffordanceCard>();

    public RealmProfile CurrentRealm => CurrentWorld.RealmFor(CurrentRegion.RealmId);

    public int CurrentImperialPresence =>
        Math.Clamp(CurrentRegion.ImperialPresence + CurrentRealm.ImperialGripDelta, 0, 100);

    private WorldRoll CurrentWorld => WorldRoll.Create(_state.Seed);

    public IReadOnlyList<StateDelta> Travel(Direction direction)
    {
        var fromZone = _state.CurrentZoneId;
        var targetZone = NeighborZoneId(fromZone, direction);
        var travelers = TravelingEntities().Select(entity => entity.Clone()).ToArray();
        _state.Zones[fromZone] = CaptureZone(fromZone, exclude: travelers.Select(entity => entity.Id).ToHashSet());

        var generatedDeltas = new List<StateDelta>();
        var target = _state.Zones.TryGetValue(targetZone, out var saved)
            ? CloneZone(saved)
            : GenerateZone(targetZone, direction, generatedDeltas);
        var loadDeltas = LoadZone(target, travelers, direction);

        var message = $"You travel {direction.ToString().ToLowerInvariant()} into {CurrentRegion.Name}.";
        var deltas = new List<StateDelta>
        {
            new(
            "travel",
            targetZone,
            message,
            new Dictionary<string, object?>
            {
                ["fromZone"] = fromZone,
                ["toZone"] = targetZone,
                ["regionId"] = _state.RegionId,
                ["direction"] = direction.ToString(),
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }),
        };
        deltas.AddRange(loadDeltas);
        var travelMessage = _applyConsequence(WorldConsequence.Message(
            "travel",
            message,
            targetEntityId: CurrentRegion.Id,
            visibility: WorldConsequenceVisibility.Message,
            evidence: "The controlled soul crossed a zone boundary.",
            reason: "Travel loaded a destination zone and region.",
            operation: "travelMessage",
            details: new Dictionary<string, object?>
            {
                ["fromZone"] = fromZone,
                ["toZone"] = targetZone,
                ["regionId"] = _state.RegionId,
                ["direction"] = direction.ToString(),
                ["playerVisible"] = true,
            }));
        deltas.AddRange(travelMessage.Deltas);
        foreach (var delta in generatedDeltas)
        {
            deltas.AddRange(PersistVisibleDeltaMessage(delta, "generated_zone"));
            deltas.Add(SuppressGeneratedPlayerMessage(delta));
        }

        foreach (var delta in _worldTurnSystem.Apply(
            _state,
            "travel",
            budget: 2,
            announce: false,
            applyConsequence: _applyConsequence))
        {
            deltas.AddRange(PersistVisibleDeltaMessage(delta, "world_turn"));
            deltas.Add(SuppressGeneratedPlayerMessage(delta));
        }

        foreach (var rumor in NarrationSystem.ZoneEntryRumors(_state, CurrentRegion, CurrentRealm))
        {
            var applied = _applyConsequence(WorldConsequence.Message(
                "zone_entry",
                rumor.Text,
                targetEntityId: CurrentRegion.Id,
                visibility: WorldConsequenceVisibility.Message,
                evidence: "Zone-entry narration derived from legend and faction standing.",
                operation: rumor.Kind,
                details: rumor.Details));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> PersistVisibleDeltaMessage(StateDelta delta, string source)
    {
        if (IsAuditFailureDelta(delta)
            || !delta.IsPlayerVisible()
            || !DeltaVisibilityIsVisible(delta)
            || (delta.Details.TryGetValue("consequenceType", out var consequenceType)
                && string.Equals(Convert.ToString(consequenceType), WorldConsequenceTypes.Message, StringComparison.OrdinalIgnoreCase)))
        {
            return Array.Empty<StateDelta>();
        }

        var applied = _applyConsequence(WorldConsequence.Message(
            source,
            delta.Summary,
            targetEntityId: delta.Target,
            visibility: WorldConsequenceVisibility.Message,
            evidence: delta.Summary,
            reason: "A generated or world-turn delta was player-visible but not itself a message consequence.",
            operation: "generatedDeltaMessage",
            details: new Dictionary<string, object?>
            {
                ["sourceOperation"] = delta.Operation,
            }));
        return applied.Deltas;
    }

    private static bool IsAuditFailureDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase)
        || delta.Operation.Equals("generationConsequenceSkipped", StringComparison.OrdinalIgnoreCase);

    private static StateDelta SuppressGeneratedPlayerMessage(StateDelta delta)
    {
        if (delta.Details.TryGetValue("consequenceType", out var consequenceType)
            && string.Equals(Convert.ToString(consequenceType), WorldConsequenceTypes.Message, StringComparison.OrdinalIgnoreCase))
        {
            return delta;
        }

        var details = new Dictionary<string, object?>(delta.Details, StringComparer.OrdinalIgnoreCase)
        {
            ["playerVisible"] = false,
        };
        return new StateDelta(delta.Operation, delta.Target, delta.Summary, details);
    }

    private static bool DeltaVisibilityIsVisible(StateDelta delta)
    {
        if (!delta.Details.TryGetValue("visibility", out var raw))
        {
            return true;
        }

        var visibility = NormalizeToken(Convert.ToString(raw) ?? "");
        if (string.IsNullOrWhiteSpace(visibility))
        {
            visibility = WorldConsequenceVisibility.Hidden;
        }

        return visibility is
            WorldConsequenceVisibility.Message or WorldConsequenceVisibility.Journal or WorldConsequenceVisibility.Lead or "visible";
    }

    public IReadOnlyList<string> AtlasLines()
    {
        var current = CurrentRegion;
        var realm = CurrentRealm;
        var lines = new List<string>
        {
            $"Current zone { _state.CurrentZoneId }: {current.Name} ({realm.Name}, {realm.Status}); ruler {realm.Ruler}; tradition {current.TraditionId}; imperial grip {CurrentImperialPresence}; wildness {current.WildnessBase}.",
        };
        lines.AddRange(CurrentAffordances.Select(affordance => $"{affordance.Id}: {affordance.Text}"));
        lines.AddRange(LoreRouter
            .Select(_loreCatalog, LoreQueryForRegion(current, limit: 2))
            .Select(lore => $"Local lore - {lore.Title}: {OneLine(lore.Body)}"));
        var capitalDefenses = _state.Factions.FactionsByRole("empire_bloc")
            .Sum(faction => _state.Factions.ResourceValue(faction.Id, "defenses"));
        lines.Add($"Capital reach: reachable east of Hollowmere; imperial defenses tracked at {capitalDefenses}.");
        var known = _state.Zones.Keys
            .Concat(new[] { _state.CurrentZoneId })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
        lines.Add($"Known zones: {string.Join(", ", known)}");
        return lines;
    }

    private ZoneSnapshot CaptureZone(string zoneId, IReadOnlySet<EntityId> exclude)
    {
        return new ZoneSnapshot(
            zoneId,
            _state.RegionId,
            Generated: true,
            _state.Entities
                .Where(pair => !exclude.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            new HashSet<GridPoint>(_state.BlockingTerrain),
            new Dictionary<GridPoint, string>(_state.Terrain),
            new Dictionary<GridPoint, int>(_state.TerrainExpirations),
            new Dictionary<GridPoint, TileFlow>(_state.TileFlows),
            _state.ExploredBySoulId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<GridPoint>)new HashSet<GridPoint>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            new[] { CurrentRegion.Id },
            CurrentAffordances.Select(affordance => affordance.Id).ToArray());
    }

    private ZoneSnapshot GenerateZone(string zoneId, Direction entryDirection, List<StateDelta> deltas)
    {
        var region = RegionFor(zoneId);
        var generatedState = DetachedGeneratedZoneState(zoneId, region);
        var realm = CurrentWorld.RealmFor(region.RealmId);
        InitializeGeneratedTerrain(generatedState, region);
        ApplyGeneratedTerrainDetails(generatedState, region, realm, entryDirection, deltas);
        var texture = TextureGrammar.ZoneFeature(region, realm, _state.Rng);
        var propTags = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(texture.Subjects)
            .Append("generated")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        TryApplyGeneratedZoneConsequence(
            generatedState,
            WorldConsequence.SpawnFixture(
                "generation",
                texture.Name,
                _state.Width / 2,
                _state.Height / 2,
                prefix: "zone_prop",
                glyph: '&',
                palette: "fixture",
                fixtureType: "zone_feature",
                material: region.TerrainTags.FirstOrDefault() ?? "stone",
                tags: propTags,
                blocksMovement: true,
                description: texture.Description,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: texture.Description,
                reason: "Procedural zone generation created an ordinary fixture through the shared spawn lifecycle.",
                operation: "generateZoneFeature",
                emitMessage: false,
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                }),
            deltas,
            "zone feature");

        var curio = CurioGenerator.Generate(region, realm, _state.Rng);
        var curioDefinition = curio.ToDefinition();
        var curioApplied = TryApplyGeneratedZoneConsequence(
            generatedState,
            WorldConsequence.SpawnItem(
                "generation",
                curio.Name,
                (_state.Width / 2) + 2,
                _state.Height / 2,
                prefix: "zone_item",
                glyph: curioDefinition.Glyph,
                itemType: curio.Id,
                material: curio.Material,
                tags: curio.Tags,
                quantity: 1,
                value: curio.Value,
                stackPolicy: curioDefinition.StackPolicy,
                useProfile: curioDefinition.UseProfile,
                equipmentSlot: curioDefinition.EquipmentSlot,
                description: curio.Description,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: curio.Description,
                reason: "Procedural zone generation created an ordinary item through the shared spawn lifecycle.",
                operation: "generateZoneItem",
                emitMessage: false,
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                }),
            deltas,
            "zone item");
        if (curioApplied.Applied)
        {
            _itemCatalog.Add(curioDefinition);
        }

        SpawnGeneratedResident(generatedState, region, realm, entryDirection, deltas);
        if (region.Id.Equals("vigovian_capital", StringComparison.OrdinalIgnoreCase))
        {
            SpawnGeneratedEmperor(generatedState, region, deltas);
        }

        CommitGeneratedZoneState(generatedState);
        var entities = generatedState.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        var terrain = new Dictionary<GridPoint, string>(generatedState.Terrain);
        var blocking = new HashSet<GridPoint>(generatedState.BlockingTerrain);
        var promiseHooks = new List<string>();
        promiseHooks.AddRange(_promiseRealizationSystem.RealizeTravelPromises(
            zoneId,
            region,
            entities,
            deltas,
            EntryPoint(entryDirection),
            entryDirection));

        return new ZoneSnapshot(
            zoneId,
            region.Id,
            Generated: true,
            entities,
            blocking,
            terrain,
            new Dictionary<GridPoint, int>(generatedState.TerrainExpirations),
            new Dictionary<GridPoint, TileFlow>(generatedState.TileFlows),
            new Dictionary<string, IReadOnlySet<GridPoint>>(StringComparer.OrdinalIgnoreCase),
            new[] { region.Id },
            (region.Affordances ?? Array.Empty<RegionAffordanceCard>())
                .Select(affordance => affordance.Id)
                .Concat(promiseHooks)
                .ToArray());
    }

    private GameState DetachedGeneratedZoneState(string zoneId, RegionDefinition region) =>
        new(_state.Width, _state.Height)
        {
            Turn = _state.Turn,
            Seed = _state.Seed,
            Rng = new DeterministicRng(_state.Rng.State),
            RegionId = region.Id,
            CurrentZoneId = zoneId,
            RunStatus = _state.RunStatus,
            RunConclusion = _state.RunConclusion,
            NextEntitySerial = _state.NextEntitySerial,
            ControlledEntityId = _state.ControlledEntityId,
            BackgroundSettings = _state.BackgroundSettings,
        };

    private void CommitGeneratedZoneState(GameState generatedState)
    {
        _state.NextEntitySerial = generatedState.NextEntitySerial;
        _state.Rng = new DeterministicRng(generatedState.Rng.State);
    }

    private void InitializeGeneratedTerrain(GameState generatedState, RegionDefinition region)
    {
        for (var y = 0; y < generatedState.Height; y++)
        {
            for (var x = 0; x < generatedState.Width; x++)
            {
                generatedState.Terrain[new GridPoint(x, y)] = region.FloorTerrain;
            }
        }
    }

    private void ApplyGeneratedTerrainDetails(
        GameState generatedState,
        RegionDefinition region,
        RealmProfile realm,
        Direction entryDirection,
        List<StateDelta> deltas)
    {
        var terrain = TerrainDetailFor(region, realm);
        foreach (var point in TerrainDetailPoints(
            new GridPoint(generatedState.Width / 2, generatedState.Height / 2),
            entryDirection,
            generatedState.Width,
            generatedState.Height))
        {
            TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.SetTerrain(
                    "generation",
                    point.X,
                    point.Y,
                    terrain,
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: $"Region {region.Id} laid down {terrain} during zone generation.",
                    reason: "Procedural zone generation created ordinary terrain texture through the shared terrain lifecycle.",
                    operation: "generateZoneTerrain",
                    emitMessage: false,
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = generatedState.CurrentZoneId,
                        ["regionId"] = region.Id,
                        ["realmId"] = region.RealmId,
                        ["terrainSource"] = "region_generation",
                    }),
                deltas,
                "zone terrain");
        }
    }

    private static IReadOnlyList<GridPoint> TerrainDetailPoints(
        GridPoint center,
        Direction entryDirection,
        int width,
        int height)
    {
        var forward = entryDirection.Offset();
        var side = new GridPoint(-forward.Y, forward.X);
        if (forward.X == 0 && forward.Y == 0)
        {
            forward = new GridPoint(1, 0);
            side = new GridPoint(0, 1);
        }

        return new[]
            {
                center.Translate(side.X * 3, side.Y * 3),
                center.Translate((side.X * 2) + forward.X, (side.Y * 2) + forward.Y),
                center.Translate(side.X * -2, side.Y * -2),
                center.Translate((side.X * -3) + forward.X, (side.Y * -3) + forward.Y),
                center.Translate(forward.X * -3, forward.Y * -3),
            }
            .Where(point => point.X > 0 && point.Y > 0 && point.X < width - 1 && point.Y < height - 1)
            .Distinct()
            .ToArray();
    }

    private static string TerrainDetailFor(RegionDefinition region, RealmProfile realm) =>
        region.Id switch
        {
            "hollowmere_margin" => "shallow_water",
            "vigovian_capital" => "petition_marble",
            "wild_border" => realm.Status.Equals("wild", StringComparison.OrdinalIgnoreCase)
                ? "flowering_grass"
                : "bright_moss",
            _ => region.TerrainTags.Contains("marble", StringComparer.OrdinalIgnoreCase)
                ? "law_chalk"
                : "trodden_mud",
        };

    private Entity? TryApplyGeneratedZoneEntityConsequence(
        GameState generatedState,
        WorldConsequence consequence,
        List<StateDelta> deltas,
        string label)
    {
        var applied = TryApplyGeneratedZoneConsequence(generatedState, consequence, deltas, label);
        if (!string.IsNullOrWhiteSpace(applied.TargetId)
            && generatedState.Entities.TryGetValue(EntityId.Create(applied.TargetId), out var entity))
        {
            return entity;
        }

        if (applied.Applied)
        {
            AddGeneratedConsequenceSkipped(consequence, deltas, label, $"Generated {label} consequence did not produce an entity.");
        }

        return null;
    }

    private WorldConsequenceApplyResult TryApplyGeneratedZoneConsequence(
        GameState generatedState,
        WorldConsequence consequence,
        List<StateDelta> deltas,
        string label)
    {
        WorldConsequenceApplyResult applied;
        try
        {
            applied = _applyGeneratedConsequence(generatedState, consequence);
        }
        catch (Exception ex)
        {
            AddGeneratedConsequenceSkipped(consequence, deltas, label, ex.Message);
            return WorldConsequenceApplyResult.Empty(ex.Message);
        }

        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddGeneratedConsequenceSkipped(consequence, deltas, label, applied.Error ?? $"Generated {label} consequence was rejected.");
        }

        return applied;
    }

    private static WorldConsequenceApplyResult ApplyGeneratedConsequenceToDetached(
        GameState generatedState,
        WorldConsequence consequence) =>
        WorldConsequenceGuard.ApplyWithNewApplier(generatedState, consequence);

    private static void AddGeneratedConsequenceSkipped(
        WorldConsequence consequence,
        List<StateDelta> deltas,
        string label,
        string reason)
    {
        deltas.Add(new StateDelta(
            "generationConsequenceSkipped",
            consequence.TargetEntityId ?? consequence.Source,
            $"Generated {label} skipped: {reason}",
            new Dictionary<string, object?>
            {
                ["consequenceType"] = consequence.Type,
                ["source"] = consequence.Source,
                ["skipReason"] = reason,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private void SpawnGeneratedResident(
        GameState generatedState,
        RegionDefinition region,
        RealmProfile realm,
        Direction entryDirection,
        List<StateDelta> deltas)
    {
        var faction = ResidentFaction(region);
        var tags = new[] { "resident", "generated", NormalizeToken(region.RealmId), NormalizeToken(realm.Status) }
            .Concat(region.TerrainTags)
            .Concat(region.VoiceTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var position = FindGeneratedOpenPoint(generatedState, ResidentEntryPoint(entryDirection));
        var residentName = ResidentName(region);
        var residentDescription = ResidentDescription(region);
        var residentWant = ResidentWant(region);
        var resident = TryApplyGeneratedZoneEntityConsequence(
            generatedState,
            WorldConsequence.SpawnEntity(
                "generation",
                residentName,
                position.X,
                position.Y,
                prefix: "resident",
                glyph: 'p',
                faction: faction,
                hp: 8,
                attack: 1,
                tags: tags,
                material: "flesh",
                roles: new[] { "resident" },
                controllerKind: "ai",
                aiPolicyId: "resident",
                summoned: false,
                description: $"{realm.Name} is {realm.Status} under {realm.Ruler}. {residentDescription}",
                interactableVerbs: new[] { "talk", "give", "recruit", "wares", "buy", "sell" },
                bodyVigor: 3,
                includeMemory: true,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: residentDescription,
                reason: "Procedural zone generation created an ordinary resident through the shared spawn lifecycle.",
                operation: "generateResident",
                emitMessage: false,
                autoWant: false,
                wantText: residentWant.Text,
                wantId: residentWant.Id,
                wantStatus: residentWant.Status,
                wantStakes: residentWant.Stakes,
                wantSalience: residentWant.Salience,
                wantTags: residentWant.Tags,
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                    ["profileName"] = residentName,
                    ["profileAppearance"] = residentDescription,
                    ["profileOrigin"] = region.Name,
                }),
            deltas,
            "resident");
        if (resident is null)
        {
            return;
        }

        TryApplyGeneratedZoneConsequence(
            generatedState,
            WorldConsequence.OfferTrade(
                "generation",
                resident.Id.Value,
                "red tincture",
                quantity: 1,
                gold: 35,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: "Generated residents carry ordinary stock through the trade consequence lifecycle.",
                reason: "Procedural resident generation attached stock through offer_trade.",
                operation: "generateResidentWares",
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["stockSource"] = "resident_generation",
                }),
            deltas,
            "resident red tincture");
        TryApplyGeneratedZoneConsequence(
            generatedState,
            WorldConsequence.OfferTrade(
                "generation",
                resident.Id.Value,
                "grave salt",
                quantity: 2,
                gold: 35,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: "Generated residents carry ordinary stock through the trade consequence lifecycle.",
                reason: "Procedural resident generation attached stock through offer_trade.",
                operation: "generateResidentWares",
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["stockSource"] = "resident_generation",
                }),
            deltas,
            "resident grave salt");
    }

    private void SpawnGeneratedEmperor(
        GameState generatedState,
        RegionDefinition region,
        List<StateDelta> deltas)
    {
        var tags = new[] { "emperor", "odran", "vigovia", "imperial", "ruler", "win_condition" }
            .Concat(region.TerrainTags)
            .Concat(region.VoiceTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var position = FindGeneratedOpenPoint(generatedState, new GridPoint(_state.Width / 2, (_state.Height / 2) - 2));
        TryApplyGeneratedZoneEntityConsequence(
            generatedState,
            WorldConsequence.SpawnEntity(
                "generation",
                "Emperor Odran of Vigovia",
                position.X,
                position.Y,
                prefix: "emperor",
                glyph: 'E',
                faction: "empire",
                hp: 12,
                attack: 2,
                tags: tags,
                material: "body",
                roles: new[] { "empire", "ruler", "emperor" },
                controllerKind: "none",
                aiPolicyId: "emperor",
                summoned: false,
                description: "The reasonable marble center of the empire, very mortal despite the room's opinion.",
                interactableVerbs: new[] { "talk", "examine" },
                bodyVigor: 2,
                includeMemory: true,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: "The capital generated its ordinary win-condition actor.",
                reason: "The capital population generator spawned the emperor through the shared entity lifecycle.",
                operation: "generateEmperor",
                emitMessage: false,
                autoWant: false,
                entityId: "emperor_odran",
                wantText: "Preserve Vigovia's marble peace by proving wild magic can be named, filed, contained, and made unnecessary.",
                wantId: "want_emperor_odran_order",
                wantStatus: "active",
                wantStakes: "Threats, bargains, or impossible mercy can reveal containment doctrine, faction fractures, imperial procedures, or the private cost of keeping the empire reasonable.",
                wantSalience: 5,
                wantTags: new[] { "empire", "order", "containment", "late_game", "promise_source" },
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["profileName"] = "Emperor Odran of Vigovia",
                    ["profileAppearance"] = "a calm human sovereign under too much marble",
                    ["profileOrigin"] = "Vigovia",
                    ["profileMagicalSignature"] = "law polished until it believes itself kind",
                }),
            deltas,
            "emperor");
    }

    private GridPoint FindGeneratedOpenPoint(GameState generatedState, GridPoint origin)
    {
        var occupied = generatedState.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _)
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement)
            .Select(entity => entity.Get<PositionComponent>().Position)
            .ToHashSet();
        return FindOpenNear(origin, occupied, generatedState.BlockingTerrain) ?? origin;
    }

    private IReadOnlyList<StateDelta> LoadZone(ZoneSnapshot snapshot, IReadOnlyList<Entity> travelers, Direction entryDirection)
    {
        var deltas = new List<StateDelta>();
        _state.CurrentZoneId = snapshot.ZoneId;
        _state.RegionId = snapshot.RegionId;
        _state.Entities.Clear();
        foreach (var pair in snapshot.Entities)
        {
            _state.Entities[pair.Key] = pair.Value.Clone();
        }

        _state.BlockingTerrain.Clear();
        foreach (var point in snapshot.BlockingTerrain)
        {
            _state.BlockingTerrain.Add(point);
        }

        _state.Terrain.Clear();
        foreach (var pair in snapshot.Terrain)
        {
            _state.Terrain[pair.Key] = pair.Value;
        }

        _state.TerrainExpirations.Clear();
        foreach (var pair in snapshot.TerrainExpirations)
        {
            _state.TerrainExpirations[pair.Key] = pair.Value;
        }

        _state.TileFlows.Clear();
        foreach (var pair in snapshot.TileFlows)
        {
            _state.TileFlows[pair.Key] = pair.Value;
        }

        _state.ExploredBySoulId.Clear();
        foreach (var pair in snapshot.ExploredBySoulId)
        {
            _state.ExploredBySoulId[pair.Key] = new HashSet<GridPoint>(pair.Value);
        }

        var entry = EntryPoint(entryDirection);
        var occupied = OccupiedPoints();
        for (var index = 0; index < travelers.Count; index++)
        {
            var traveler = travelers[index].Clone();
            var point = FindOpenNear(entry, occupied, _state.BlockingTerrain) ?? entry;
            traveler.Set(new PositionComponent(point));
            _state.Entities[traveler.Id] = traveler;
            var placement = WorldConsequence.MoveEntity(
                "travel",
                traveler.Id.Value,
                point.X,
                point.Y,
                operation: "travelPlaceTraveler",
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: traveler.Id.Value,
                evidence: $"Traveler entered zone {snapshot.ZoneId} from {entryDirection}.",
                reason: "Zone travel places carried bodies at the destination entry edge.",
                emitMessage: false,
                message: EntryMessage(traveler),
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = snapshot.ZoneId,
                    ["regionId"] = snapshot.RegionId,
                    ["direction"] = entryDirection.ToString(),
                    ["travelerIndex"] = index,
                    ["playerVisible"] = false,
                });
            TryApplyTravelPlacementConsequence(placement, deltas);
            occupied.Add(point);
        }

        var selectedTarget = _applyConsequence(WorldConsequence.SetSelectedTarget(
            "travel",
            clear: true,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: "Zone loading clears selected tactical coordinates from the previous zone.",
            operation: "travelClearTarget"));
        deltas.AddRange(selectedTarget.Deltas);
        return deltas;
    }

    private WorldConsequenceApplyResult TryApplyTravelPlacementConsequence(
        WorldConsequence consequence,
        List<StateDelta> deltas)
    {
        WorldConsequenceApplyResult applied;
        try
        {
            applied = _applyConsequence(consequence);
        }
        catch (Exception ex)
        {
            AddTravelPlacementSkipped(consequence, deltas, ex.Message);
            return WorldConsequenceApplyResult.Empty(ex.Message);
        }

        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddTravelPlacementSkipped(
                consequence,
                deltas,
                applied.Error ?? "Travel placement consequence was rejected.");
        }

        return applied;
    }

    private static void AddTravelPlacementSkipped(
        WorldConsequence consequence,
        List<StateDelta> deltas,
        string reason)
    {
        deltas.Add(new StateDelta(
            "travelPlacementSkipped",
            consequence.TargetEntityId ?? consequence.Source,
            $"Travel placement skipped: {reason}",
            new Dictionary<string, object?>
            {
                ["consequenceType"] = consequence.Type,
                ["source"] = consequence.Source,
                ["skipReason"] = reason,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private string EntryMessage(Entity traveler) =>
        traveler.Id == _state.ControlledEntityId
            ? "You enter the destination zone."
            : $"{traveler.Name} enters the destination zone.";

    private IReadOnlyList<Entity> TravelingEntities()
    {
        var playerSoulId = SoulIdFor(_state.ControlledEntity);
        return _state.Entities.Values
            .Where(entity => entity.Id == _state.ControlledEntityId
                || (_state.Bonds.TryGet(SoulIdFor(entity), playerSoulId, out var bond)
                    && (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase) || bond.Loyalty >= 5)))
            .OrderBy(entity => entity.Id == _state.ControlledEntityId ? 0 : 1)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
    }

    private RegionDefinition RegionFor(string zoneId)
    {
        var (x, y) = ParseZoneId(zoneId);
        if (x >= 2 && y == 0)
        {
            return _regions.Region("vigovian_capital")!;
        }

        if (Math.Abs(x) + Math.Abs(y) >= 2)
        {
            return _regions.Region("wild_border")!;
        }

        if (x > 0 || y > 0)
        {
            return _regions.Region("hollowmere_margin")!;
        }

        return _regions.Region("imperial_encounter")!;
    }

    private GridPoint EntryPoint(Direction direction) =>
        direction switch
        {
            Direction.East => new GridPoint(1, _state.Height / 2),
            Direction.West => new GridPoint(_state.Width - 2, _state.Height / 2),
            Direction.South => new GridPoint(_state.Width / 2, 1),
            Direction.North => new GridPoint(_state.Width / 2, _state.Height - 2),
            _ => new GridPoint(_state.Width / 2, _state.Height / 2),
        };

    private GridPoint ResidentEntryPoint(Direction direction) =>
        direction switch
        {
            Direction.East => new GridPoint(3, _state.Height / 2),
            Direction.West => new GridPoint(_state.Width - 4, _state.Height / 2),
            Direction.South => new GridPoint(_state.Width / 2, 3),
            Direction.North => new GridPoint(_state.Width / 2, _state.Height - 4),
            _ => new GridPoint((_state.Width / 2) - 1, (_state.Height / 2) - 2),
        };

    private HashSet<GridPoint> OccupiedPoints() =>
        _state.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _)
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement
                && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive))
            .Select(entity => entity.Get<PositionComponent>().Position)
            .ToHashSet();

    private GridPoint? FindOpenNear(GridPoint origin, HashSet<GridPoint> occupied, IReadOnlySet<GridPoint> blockingTerrain)
    {
        foreach (var offset in new[]
        {
            new GridPoint(0, 0),
            new GridPoint(0, -1),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, 0),
            new GridPoint(-1, -1),
            new GridPoint(1, -1),
            new GridPoint(-1, 1),
            new GridPoint(1, 1),
        })
        {
            var point = origin.Translate(offset.X, offset.Y);
            if (CanEnter(point, occupied, blockingTerrain))
            {
                return point;
            }
        }

        for (var radius = 2; radius <= 5; radius++)
        {
            for (var dy = -radius; dy <= radius; dy++)
            {
                for (var dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) + Math.Abs(dy) != radius)
                    {
                        continue;
                    }

                    var point = origin.Translate(dx, dy);
                    if (CanEnter(point, occupied, blockingTerrain))
                    {
                        return point;
                    }
                }
            }
        }

        return null;
    }

    private bool CanEnter(GridPoint point, HashSet<GridPoint> occupied, IReadOnlySet<GridPoint> blockingTerrain) =>
        point.X > 0
        && point.Y > 0
        && point.X < _state.Width - 1
        && point.Y < _state.Height - 1
        && !blockingTerrain.Contains(point)
        && !occupied.Contains(point);

    private static string NeighborZoneId(string zoneId, Direction direction)
    {
        var (x, y) = ParseZoneId(zoneId);
        var offset = direction.Offset();
        return $"{x + offset.X},{y + offset.Y}";
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private LoreQuery LoreQueryForRegion(RegionDefinition region, int limit) =>
        new(
            region.TerrainTags
                .Concat(region.VoiceTags)
                .Concat(new[] { region.Id, region.RealmId, region.TraditionId })
                .Concat((region.Affordances ?? Array.Empty<RegionAffordanceCard>()).SelectMany(AffordanceSubjects))
                .ToArray(),
            new[] { "atlas", "background", "magic_context", region.Id }
                .Concat((region.Affordances ?? Array.Empty<RegionAffordanceCard>()).Select(affordance => affordance.Id))
                .ToArray(),
            LoreAccessLevel(),
            limit);

    private int LoreAccessLevel()
    {
        var canonDepth = _state.Canon.Records.Count(record =>
            record.Kind.Equals("readable", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("canon_detail", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("entity_detail", StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(1 + (canonDepth / 2), 1, 3);
    }

    private static IEnumerable<string> AffordanceSubjects(RegionAffordanceCard affordance) =>
        affordance.Tags.Append(affordance.Id);

    private static string OneLine(string text)
    {
        var normalized = text.Replace('\n', ' ').Trim();
        return normalized.Length <= 140 ? normalized : $"{normalized[..137]}...";
    }

    private static string PropName(RegionDefinition region) =>
        region.Id switch
        {
            "hollowmere_margin" => "reed-wrapped memory shrine",
            "vigovian_capital" => "petition-scarred throne dais",
            "wild_border" => "wildflower law-stone",
            _ => "marble survey marker",
        };

    private static string ItemName(RegionDefinition region) =>
        region.Id switch
        {
            "hollowmere_margin" => "river reed charm",
            "vigovian_capital" => "sealed imperial writ",
            "wild_border" => "bright bone seed",
            _ => "numbered chalk",
        };

    private static string ResidentName(RegionDefinition region) =>
        region.Id switch
        {
            "hollowmere_margin" => "Hollowmere reed-keeper",
            "vigovian_capital" => "imperial court functionary",
            "wild_border" => "wild border waykeeper",
            _ => "imperial survey clerk",
        };

    private static string ResidentDescription(RegionDefinition region) =>
        region.Id switch
        {
            "hollowmere_margin" => "A local watcher with wet hems and an eye for fugitives.",
            "vigovian_capital" => "A careful palace functionary who can explain why every cruelty was procedurally necessary.",
            "wild_border" => "A border wanderer who treats impossible flowers as weather.",
            _ => "A clerk of marble roads and numbered trespasses.",
        };

    private static WantComponent ResidentWant(RegionDefinition region) =>
        region.Id switch
        {
            "hollowmere_margin" => new WantComponent(
                $"want_{NormalizeToken(region.Id)}_shelter",
                "Keep Hollowmere's quiet shelters unburned while deciding whether this fugitive sorcerer is worth the risk.",
                salience: 4,
                stakes: "Trust, gifts, or shared enemies can draw out refuge, route, healer, or kinship leads.",
                tags: new[] { "shelter", "hollowmere", "refuge", "promise_source" }),
            "vigovian_capital" => new WantComponent(
                $"want_{NormalizeToken(region.Id)}_procedure",
                "Protect their office and reputation by making the empire's machinery look inevitable.",
                salience: 3,
                stakes: "Pressure or procedural curiosity can reveal ledgers, offices, warrants, or guarded routes.",
                tags: new[] { "procedure", "office", "empire", "promise_source" }),
            "wild_border" => new WantComponent(
                $"want_{NormalizeToken(region.Id)}_crossing",
                "Guide dangerous travelers toward crossings that anger the land least.",
                salience: 4,
                stakes: "Respectful questions can reveal landmarks, hazards, folk services, or strange costs.",
                tags: new[] { "crossing", "landmark", "folk_magic", "promise_source" }),
            _ => new WantComponent(
                $"want_{NormalizeToken(region.Id)}_survive",
                "Finish their local duty without becoming the next example in an imperial notice.",
                salience: 2,
                stakes: "Practical negotiation can reveal small routes, stock, or local names.",
                tags: new[] { "duty", "survival", "promise_source" }),
        };

    private static string ResidentFaction(RegionDefinition region) =>
        region.RealmId switch
        {
            "empire" => "empire",
            "hollowmere" => "hollowmere",
            _ => "neutral",
        };

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static ZoneSnapshot CloneZone(ZoneSnapshot zone) =>
        zone with
        {
            Entities = zone.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone()),
            BlockingTerrain = new HashSet<GridPoint>(zone.BlockingTerrain),
            Terrain = new Dictionary<GridPoint, string>(zone.Terrain),
            TerrainExpirations = new Dictionary<GridPoint, int>(zone.TerrainExpirations),
            TileFlows = new Dictionary<GridPoint, TileFlow>(zone.TileFlows),
            ExploredBySoulId = zone.ExploredBySoulId.ToDictionary(
                pair => pair.Key,
                pair => (IReadOnlySet<GridPoint>)new HashSet<GridPoint>(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            RoomProfiles = zone.RoomProfiles.ToArray(),
            PromiseHooks = zone.PromiseHooks.ToArray(),
        };
}
