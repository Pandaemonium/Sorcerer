using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed record InteriorTransitionResult(
    bool Success,
    string? Error,
    IReadOnlyList<StateDelta> Deltas);

public sealed class GenerationSystem
{
    private readonly ItemCatalog _itemCatalog;
    private readonly LoreCatalog _loreCatalog;
    private readonly QuestTemplateCatalog _quests;
    private readonly PromiseRealizationSystem _promiseRealizationSystem;
    private readonly WorldTurnSystem _worldTurnSystem = new();
    private readonly Func<WorldConsequence, WorldConsequenceApplyResult> _applyConsequence;
    private readonly Func<GameState, WorldConsequence, WorldConsequenceApplyResult> _applyGeneratedConsequence;
    private readonly GameState _state;
    private readonly RegionRegistry _regions;
    private readonly WorldPlaceGraph _places;

    public GenerationSystem(
        GameState state,
        ItemCatalog itemCatalog,
        LoreCatalog loreCatalog,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null,
        Func<GameState, WorldConsequence, WorldConsequenceApplyResult>? applyGeneratedConsequence = null,
        RegionRegistry? regions = null)
    {
        _itemCatalog = itemCatalog;
        _loreCatalog = loreCatalog;
        _quests = QuestTemplateCatalog.LoadDefault();
        _state = state;
        _regions = regions ?? RegionCatalog.LoadDefault();
        _places = WorldPlaceGraph.Create(state.Seed, _regions);
        _applyConsequence = applyConsequence ?? (consequence => WorldConsequenceGuard.ApplyWithNewApplier(state, consequence));
        _applyGeneratedConsequence = applyGeneratedConsequence ?? ApplyGeneratedConsequenceToDetached;
        _promiseRealizationSystem = new PromiseRealizationSystem(state, applyConsequence: _applyConsequence, regions: _regions);
    }

    public RegionDefinition CurrentRegion =>
        _regions.Region(_state.RegionId) ?? _regions.Region("imperial_encounter")!;

    public IReadOnlyList<RegionAffordanceCard> CurrentAffordances =>
        CurrentRegion.Affordances ?? Array.Empty<RegionAffordanceCard>();

    public RealmProfile CurrentRealm => CurrentWorld.RealmFor(CurrentRegion.RealmId);

    public WorldPlaceProfile CurrentPlace
    {
        get
        {
            var exit = CurrentInteriorExit();
            var definition = exit is null ? null : InteriorForId(CurrentRegion, exit.InteriorId);
            if (exit is not null && definition is not null)
            {
                return new WorldPlaceProfile(
                    _state.CurrentZoneId,
                    CurrentRegion.Id,
                    WorldPlaceKinds.Interior,
                    Interior: new WorldInteriorProfile(
                        definition.Id,
                        definition.Name,
                        definition.Kind,
                        definition.Summary,
                        exit.ExteriorZoneId,
                        definition.AccessPolicy,
                        definition.Tags));
            }

            return _places.Profile(_state.CurrentZoneId, CurrentRegion.Id);
        }
    }

    public NearestSettlement CurrentNearestSettlement
    {
        get
        {
            var exteriorZoneId = CurrentInteriorExit()?.ExteriorZoneId;
            return _places.Nearest(exteriorZoneId ?? _state.CurrentZoneId, CurrentRegion.Id);
        }
    }

    public WorldPlaceGraph PlaceGraph => _places;

    public int CurrentImperialPresence =>
        Math.Clamp(CurrentRegion.ImperialPresence + CurrentRealm.ImperialGripDelta, 0, 100);

    private WorldRoll CurrentWorld => WorldRoll.Create(_state.Seed);

    public IReadOnlyList<StateDelta> Travel(Direction direction)
    {
        var fromZone = _state.CurrentZoneId;
        var fromRegion = CurrentRegion;
        var fromRealm = CurrentWorld.RealmFor(fromRegion.RealmId);
        var targetZone = NeighborZoneId(fromZone, direction);
        var travelers = TravelingEntities().Select(entity => entity.Clone()).ToArray();
        _state.Zones[fromZone] = CaptureZone(fromZone, exclude: travelers.Select(entity => entity.Id).ToHashSet());

        var generatedDeltas = new List<StateDelta>();
        var target = _state.Zones.TryGetValue(targetZone, out var saved)
            ? CloneZone(saved)
            : GenerateZone(targetZone, direction, generatedDeltas);
        var loadDeltas = LoadZone(target, travelers, direction);

        var message = TravelMessage(direction, fromRegion, fromRealm, CurrentRegion, CurrentRealm, CurrentPlace);
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
                ["fromRegionId"] = fromRegion.Id,
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

    public InteriorTransitionResult EnterInterior(string? targetText)
    {
        var entranceEntity = ResolveNearbyInteriorEntrance(targetText);
        if (entranceEntity is null || !entranceEntity.TryGet<InteriorEntranceComponent>(out var entrance))
        {
            return new InteriorTransitionResult(
                false,
                "There is no entrance within reach.",
                Array.Empty<StateDelta>());
        }

        if (!CanEnter(entranceEntity, entrance))
        {
            var requirement = string.IsNullOrWhiteSpace(entrance.RequiredItem)
                ? "permission, force, or magic"
                : $"{entrance.RequiredItem}, permission, force, or magic";
            return new InteriorTransitionResult(
                false,
                $"{entrance.Name} is restricted. You need {requirement}; the threshold is a problem, not an absolute lock.",
                Array.Empty<StateDelta>());
        }

        if (!entrance.ExteriorZoneId.Equals(_state.CurrentZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return new InteriorTransitionResult(
                false,
                "That entrance no longer belongs to this place.",
                Array.Empty<StateDelta>());
        }

        var region = CurrentRegion;
        var definition = InteriorForId(region, entrance.InteriorId);
        if (definition is null)
        {
            return new InteriorTransitionResult(
                false,
                $"{entrance.Name} has no interior definition.",
                Array.Empty<StateDelta>());
        }

        var travelers = TravelingEntities().Select(entity => entity.Clone()).ToArray();
        var generatedDeltas = new List<StateDelta>();
        var target = _state.Zones.TryGetValue(entrance.InteriorZoneId, out var saved)
            ? CloneZone(saved)
            : GenerateInterior(entrance, definition, region, generatedDeltas);
        _state.Zones[_state.CurrentZoneId] = CaptureZone(
            _state.CurrentZoneId,
            travelers.Select(entity => entity.Id).ToHashSet());

        var deltas = new List<StateDelta>
        {
            new(
                "enterInterior",
                entrance.InteriorZoneId,
                $"Entered {entrance.Name}.",
                new Dictionary<string, object?>
                {
                    ["fromZone"] = entrance.ExteriorZoneId,
                    ["toZone"] = entrance.InteriorZoneId,
                    ["interiorId"] = entrance.InteriorId,
                    ["accessPolicy"] = entrance.AccessPolicy,
                    ["auditOnly"] = true,
                    ["playerVisible"] = false,
                }),
        };
        deltas.AddRange(LoadLinkedZone(
            target,
            travelers,
            new GridPoint(3, _state.Height / 2),
            "enterInterior",
            entrance.InteriorId));
        foreach (var generated in generatedDeltas)
        {
            deltas.AddRange(PersistVisibleDeltaMessage(generated, "generated_interior"));
            deltas.Add(SuppressGeneratedPlayerMessage(generated));
        }

        var message = _applyConsequence(WorldConsequence.Message(
            "interior",
            $"You enter {entrance.Name}. {entrance.Summary}",
            targetEntityId: entrance.InteriorId,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: $"The controlled soul crossed the threshold from {entrance.ExteriorZoneId}.",
            reason: "A linked interior zone became the active place.",
            operation: "enterInteriorMessage",
            details: new Dictionary<string, object?>
            {
                ["fromZone"] = entrance.ExteriorZoneId,
                ["toZone"] = entrance.InteriorZoneId,
                ["interiorId"] = entrance.InteriorId,
                ["playerVisible"] = true,
            }));
        deltas.AddRange(message.Deltas);
        return new InteriorTransitionResult(true, null, deltas);
    }

    public InteriorTransitionResult LeaveInterior()
    {
        var exitEntity = ResolveNearbyInteriorExit();
        if (exitEntity is null || !exitEntity.TryGet<InteriorExitComponent>(out var exit))
        {
            return new InteriorTransitionResult(
                false,
                "There is no way out within reach.",
                Array.Empty<StateDelta>());
        }

        if (!_state.Zones.TryGetValue(exit.ExteriorZoneId, out var savedExterior))
        {
            return new InteriorTransitionResult(
                false,
                "The exterior threshold has lost its destination.",
                Array.Empty<StateDelta>());
        }

        var interiorZoneId = _state.CurrentZoneId;
        var travelers = TravelingEntities().Select(entity => entity.Clone()).ToArray();
        _state.Zones[interiorZoneId] = CaptureZone(
            interiorZoneId,
            travelers.Select(entity => entity.Id).ToHashSet());

        var deltas = new List<StateDelta>
        {
            new(
                "leaveInterior",
                exit.ExteriorZoneId,
                $"Left {exit.InteriorName}.",
                new Dictionary<string, object?>
                {
                    ["fromZone"] = interiorZoneId,
                    ["toZone"] = exit.ExteriorZoneId,
                    ["interiorId"] = exit.InteriorId,
                    ["auditOnly"] = true,
                    ["playerVisible"] = false,
                }),
        };
        deltas.AddRange(LoadLinkedZone(
            CloneZone(savedExterior),
            travelers,
            new GridPoint(exit.ExteriorX, exit.ExteriorY),
            "leaveInterior",
            exit.InteriorId));
        var message = _applyConsequence(WorldConsequence.Message(
            "interior",
            $"You leave {exit.InteriorName} and return outside.",
            targetEntityId: exit.InteriorId,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: $"The controlled soul crossed the threshold back to {exit.ExteriorZoneId}.",
            reason: "The linked exterior zone became the active place.",
            operation: "leaveInteriorMessage",
            details: new Dictionary<string, object?>
            {
                ["fromZone"] = interiorZoneId,
                ["toZone"] = exit.ExteriorZoneId,
                ["interiorId"] = exit.InteriorId,
                ["playerVisible"] = true,
            }));
        deltas.AddRange(message.Deltas);
        return new InteriorTransitionResult(true, null, deltas);
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
        var place = CurrentPlace;
        var nearest = CurrentNearestSettlement;
        var lines = new List<string>
        {
            $"Current zone {_state.CurrentZoneId}: {place.DisplayName}; {current.Name} ({realm.Name}, {realm.Status}); ruler {realm.Ruler}; tradition {current.TraditionId}; imperial grip {CurrentImperialPresence}; wildness {current.WildnessBase}.",
        };
        if (place.Settlement is not null && place.District is not null)
        {
            lines.Add($"Settlement: {place.Settlement.Name}; district: {place.District.Name}. {place.District.Summary}");
        }
        else if (place.Interior is not null)
        {
            lines.Add($"Interior: {place.Interior.Name} ({place.Interior.Kind}); threshold to {place.Interior.ExteriorZoneId}. {place.Interior.Summary}");
        }
        else if (place.Road is not null)
        {
            lines.Add($"Road: {place.Road.Name}; connects {_places.Settlements.First(settlement => settlement.Id == place.Road.FromSettlementId).Name} and {_places.Settlements.First(settlement => settlement.Id == place.Road.ToSettlementId).Name}.");
        }
        else if (place.Landmark is not null)
        {
            lines.Add($"Landmark: {place.Landmark.Name}. {place.Landmark.Definition.Description}");
        }

        var localThresholds = _state.Entities.Values
            .Where(entity => entity.TryGet<InteriorEntranceComponent>(out _))
            .Select(entity => entity.Get<InteriorEntranceComponent>())
            .OrderBy(entrance => entrance.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var threshold in localThresholds)
        {
            lines.Add($"Threshold: {threshold.Name} ({threshold.AccessPolicy}). {threshold.Summary}");
        }

        lines.Add(nearest.Distance == 0
            ? $"Nearest settlement: {nearest.Settlement.Name}, here."
            : $"Nearest settlement: {nearest.Settlement.Name}, {nearest.Distance} zone(s) {nearest.Direction}.");
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
            new[]
            {
                CurrentRegion.Id,
                CurrentPlace.Kind,
                CurrentPlace.Settlement?.Id,
                CurrentPlace.District?.Id,
                CurrentPlace.Road?.Id,
                CurrentPlace.Landmark?.Id,
                CurrentPlace.Interior?.Id,
                CurrentPlace.Interior?.Kind,
            }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray(),
            CurrentAffordances.Select(affordance => affordance.Id).ToArray());
    }

    private ZoneSnapshot GenerateZone(string zoneId, Direction entryDirection, List<StateDelta> deltas)
    {
        var region = RegionFor(zoneId);
        var place = _places.Profile(zoneId, region.Id);
        var generatedState = DetachedGeneratedZoneState(zoneId, region);
        var realm = CurrentWorld.RealmFor(region.RealmId);
        InitializeGeneratedTerrain(generatedState, region);
        ApplyGeneratedTerrainDetails(generatedState, region, realm, entryDirection, deltas);
        ApplyGeneratedPlaceTerrain(generatedState, region, place, deltas);
        SpawnPlaceFeature(generatedState, region, place, deltas);
        SpawnGeneratedProps(generatedState, region, realm, place, entryDirection, deltas);

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

        PopulateZone(generatedState, region, realm, place, entryDirection, deltas);
        if (region.Id.Equals("vigovian_capital", StringComparison.OrdinalIgnoreCase)
            && place.District?.Id.Equals("inner_court", StringComparison.OrdinalIgnoreCase) == true
            && place.Settlement is { } capital
            && capital.CenterX == ParseZoneId(zoneId).X
            && capital.CenterY == ParseZoneId(zoneId).Y)
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
            new[] { region.Id, place.Kind, place.Settlement?.Id, place.District?.Id, place.Road?.Id, place.Landmark?.Id }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray(),
            (region.Affordances ?? Array.Empty<RegionAffordanceCard>())
                .Select(affordance => affordance.Id)
                .Concat(promiseHooks)
                .ToArray());
    }

    private ZoneSnapshot GenerateInterior(
        InteriorEntranceComponent entrance,
        RegionInteriorDefinition interior,
        RegionDefinition region,
        List<StateDelta> deltas)
    {
        var generatedState = DetachedGeneratedZoneState(entrance.InteriorZoneId, region);
        for (var y = 0; y < generatedState.Height; y++)
        {
            for (var x = 0; x < generatedState.Width; x++)
            {
                generatedState.Terrain[new GridPoint(x, y)] = interior.FloorTerrain;
            }
        }

        var perimeter = Enumerable.Range(0, generatedState.Width)
            .SelectMany(x => new[] { new GridPoint(x, 0), new GridPoint(x, generatedState.Height - 1) })
            .Concat(Enumerable.Range(1, Math.Max(0, generatedState.Height - 2))
                .SelectMany(y => new[] { new GridPoint(0, y), new GridPoint(generatedState.Width - 1, y) }))
            .Distinct()
            .ToArray();
        foreach (var point in perimeter)
        {
            TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.SetTerrain(
                    "generation",
                    point.X,
                    point.Y,
                    "wall",
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: $"{interior.Name} is enclosed by {interior.WallMaterial}.",
                    reason: "Linked interior generation enclosed the room through the shared terrain lifecycle.",
                    operation: "generateInteriorWall",
                    emitMessage: false,
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = entrance.InteriorZoneId,
                        ["regionId"] = region.Id,
                        ["interiorId"] = interior.Id,
                        ["wallMaterial"] = interior.WallMaterial,
                    }),
                deltas,
                "interior wall");
        }

        var exitPoint = new GridPoint(2, generatedState.Height / 2);
        TryApplyGeneratedZoneConsequence(
            generatedState,
            WorldConsequence.SpawnFixture(
                "generation",
                $"threshold of {interior.Name}",
                exitPoint.X,
                exitPoint.Y,
                prefix: "interior_exit",
                glyph: '<',
                palette: "threshold",
                fixtureType: "interior_exit",
                material: interior.WallMaterial,
                tags: interior.Tags.Concat(new[] { "threshold", "interior_exit" }).ToArray(),
                blocksMovement: false,
                description: $"The threshold leads back to {entrance.ExteriorZoneId}.",
                interactableVerbs: new[] { "examine", "leave" },
                canAnchorMagic: true,
                interiorExit: new InteriorExitComponent(
                    entrance.ExteriorZoneId,
                    interior.Id,
                    interior.Name,
                    entrance.ExteriorX,
                    entrance.ExteriorY),
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: $"{interior.Name} retains a two-way threshold to its exterior.",
                reason: "Linked interior generation created an explicit exit entity.",
                operation: "generateInteriorExit",
                emitMessage: false,
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = entrance.InteriorZoneId,
                    ["regionId"] = region.Id,
                    ["interiorId"] = interior.Id,
                    ["exteriorZoneId"] = entrance.ExteriorZoneId,
                }),
            deltas,
            "interior exit");

        var featurePoints = new[]
        {
            new GridPoint(generatedState.Width / 2, generatedState.Height / 2),
            new GridPoint((generatedState.Width / 2) + 5, (generatedState.Height / 2) - 3),
            new GridPoint((generatedState.Width / 2) - 5, (generatedState.Height / 2) + 3),
            new GridPoint((generatedState.Width / 2) + 6, (generatedState.Height / 2) + 4),
        };
        for (var index = 0; index < interior.Features.Count; index++)
        {
            var feature = interior.Features[index];
            var position = FindGeneratedOpenPoint(generatedState, featurePoints[index % featurePoints.Length]);
            TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.SpawnFixture(
                    "generation",
                    feature.Name,
                    position.X,
                    position.Y,
                    prefix: $"interior_feature_{NormalizeToken(interior.Id)}",
                    glyph: feature.Glyph,
                    palette: "interior_feature",
                    fixtureType: $"interior_{NormalizeToken(interior.Kind)}",
                    material: feature.Material,
                    tags: feature.Tags
                        .Concat(interior.Tags)
                        .Concat(new[] { "interior_feature", interior.Id, region.Id })
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray(),
                    blocksMovement: feature.BlocksMovement,
                    description: feature.Description,
                    interactableVerbs: new[] { "examine" },
                    canAnchorMagic: true,
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: feature.Description,
                    reason: "Authored regional interior grammar instantiated a semantic fixture.",
                    operation: "generateInteriorFeature",
                    emitMessage: false,
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = entrance.InteriorZoneId,
                        ["regionId"] = region.Id,
                        ["interiorId"] = interior.Id,
                        ["interiorKind"] = interior.Kind,
                        ["featureIndex"] = index,
                    }),
                deltas,
                "interior feature");
        }

        var place = new WorldPlaceProfile(
            entrance.InteriorZoneId,
            region.Id,
            WorldPlaceKinds.Interior,
            Interior: new WorldInteriorProfile(
                interior.Id,
                interior.Name,
                interior.Kind,
                interior.Summary,
                entrance.ExteriorZoneId,
                interior.AccessPolicy,
                interior.Tags));
        PopulateZone(
            generatedState,
            region,
            CurrentWorld.RealmFor(region.RealmId),
            place,
            Direction.East,
            deltas);

        CommitGeneratedZoneState(generatedState);
        var entities = generatedState.Entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        var promiseHooks = _promiseRealizationSystem.RealizeTravelPromises(
            entrance.InteriorZoneId,
            region,
            entities,
            deltas,
            new GridPoint(3, generatedState.Height / 2),
            Direction.East);
        return new ZoneSnapshot(
            entrance.InteriorZoneId,
            region.Id,
            Generated: true,
            entities,
            new HashSet<GridPoint>(generatedState.BlockingTerrain),
            new Dictionary<GridPoint, string>(generatedState.Terrain),
            new Dictionary<GridPoint, int>(generatedState.TerrainExpirations),
            new Dictionary<GridPoint, TileFlow>(generatedState.TileFlows),
            new Dictionary<string, IReadOnlySet<GridPoint>>(StringComparer.OrdinalIgnoreCase),
            new[] { region.Id, WorldPlaceKinds.Interior, interior.Id, interior.Kind },
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

    private void SpawnGeneratedProps(
        GameState generatedState,
        RegionDefinition region,
        RealmProfile realm,
        WorldPlaceProfile place,
        Direction entryDirection,
        List<StateDelta> deltas)
    {
        var batch = RegionPropGenerator.Generate(region, realm, _state.Seed, generatedState.CurrentZoneId);
        if (region.Props is null)
        {
            SpawnFallbackGeneratedProp(generatedState, region, realm, deltas);
            return;
        }

        var positionRng = new DeterministicRng(WorldRoll.StableSeed(
            _state.Seed,
            generatedState.CurrentZoneId,
            region.Id,
            "prop_positions"));
        var reserved = PropReservedPoints(entryDirection);
        var ensembleAnchors = new Dictionary<string, GridPoint>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in batch.Props)
        {
            GridPoint desired;
            if (!string.IsNullOrWhiteSpace(prop.EnsembleId))
            {
                if (!ensembleAnchors.TryGetValue(prop.EnsembleId, out var anchor))
                {
                    anchor = RandomInteriorPoint(generatedState, positionRng);
                    ensembleAnchors[prop.EnsembleId] = anchor;
                }

                desired = anchor.Translate(prop.OffsetX, prop.OffsetY);
            }
            else
            {
                desired = RandomInteriorPoint(generatedState, positionRng);
            }

            var point = FindOpenNear(desired, reserved, generatedState.BlockingTerrain);
            if (point is null)
            {
                continue;
            }

            var applied = TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.SpawnFixture(
                    "generation",
                    prop.Name,
                    point.Value.X,
                    point.Value.Y,
                    prefix: "zone_prop",
                    glyph: prop.Glyph,
                    palette: "regional_prop",
                    fixtureType: prop.FixtureType,
                    material: prop.Material,
                    tags: prop.Tags.Concat(place.Tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                    blocksMovement: prop.BlocksMovement,
                    blocksSight: prop.BlocksSight,
                    description: prop.Description,
                    interactableVerbs: string.IsNullOrWhiteSpace(prop.ReadableTitle)
                        ? Array.Empty<string>()
                        : new[] { "read" },
                    canAnchorMagic: prop.CanAnchorMagic,
                    readableTitle: prop.ReadableTitle,
                    readableText: prop.ReadableText,
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: prop.Description,
                    reason: "Procedural generation composed a regional semantic prop through the shared fixture lifecycle.",
                    operation: "generateZoneFeature",
                    emitMessage: false,
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = generatedState.CurrentZoneId,
                        ["regionId"] = region.Id,
                        ["realmId"] = region.RealmId,
                        ["propGrammar"] = true,
                        ["propDense"] = batch.Dense,
                        ["ensembleId"] = prop.EnsembleId,
                        ["semanticScenery"] = true,
                        ["placeKind"] = place.Kind,
                        ["settlementId"] = place.Settlement?.Id,
                        ["districtId"] = place.District?.Id,
                        ["roadId"] = place.Road?.Id,
                        ["landmarkId"] = place.Landmark?.Id,
                    }),
                deltas,
                "regional prop");
            if (applied.Applied)
            {
                reserved.Add(point.Value);
            }
        }
    }

    private void ApplyGeneratedPlaceTerrain(
        GameState generatedState,
        RegionDefinition region,
        WorldPlaceProfile place,
        List<StateDelta> deltas)
    {
        if (string.IsNullOrWhiteSpace(place.Terrain) || place.Kind == WorldPlaceKinds.Wilderness)
        {
            return;
        }

        foreach (var point in PlaceTerrainPoints(generatedState, place))
        {
            TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.SetTerrain(
                    "generation",
                    point.X,
                    point.Y,
                    place.Terrain,
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: $"{place.DisplayName} laid down {place.Terrain}.",
                    reason: "The shared world place graph textured a generated district, road, or landmark.",
                    operation: "generatePlaceTerrain",
                    emitMessage: false,
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = generatedState.CurrentZoneId,
                        ["regionId"] = region.Id,
                        ["placeKind"] = place.Kind,
                        ["settlementId"] = place.Settlement?.Id,
                        ["districtId"] = place.District?.Id,
                        ["roadId"] = place.Road?.Id,
                        ["landmarkId"] = place.Landmark?.Id,
                    }),
                deltas,
                "place terrain");
        }
    }

    private static IReadOnlyList<GridPoint> PlaceTerrainPoints(GameState state, WorldPlaceProfile place)
    {
        var points = new HashSet<GridPoint>();
        var centerX = state.Width / 2;
        var centerY = state.Height / 2;
        if (place.District is not null)
        {
            for (var y = centerY - 2; y <= centerY + 2; y++)
            {
                for (var x = centerX - 4; x <= centerX + 4; x++)
                {
                    points.Add(new GridPoint(x, y));
                }
            }
        }
        else if (place.Road is not null)
        {
            var (zoneX, zoneY) = ParseZoneId(place.ZoneId);
            var roadZones = place.Road.ZoneIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var horizontal = roadZones.Contains($"{zoneX - 1},{zoneY}") || roadZones.Contains($"{zoneX + 1},{zoneY}");
            var vertical = roadZones.Contains($"{zoneX},{zoneY - 1}") || roadZones.Contains($"{zoneX},{zoneY + 1}");
            if (horizontal)
            {
                for (var x = 0; x < state.Width; x++)
                {
                    points.Add(new GridPoint(x, centerY));
                    points.Add(new GridPoint(x, centerY + 1));
                }
            }

            if (vertical || !horizontal)
            {
                for (var y = 0; y < state.Height; y++)
                {
                    points.Add(new GridPoint(centerX, y));
                    points.Add(new GridPoint(centerX + 1, y));
                }
            }
        }
        else if (place.Landmark is not null)
        {
            for (var y = centerY - 1; y <= centerY + 1; y++)
            {
                for (var x = centerX - 1; x <= centerX + 1; x++)
                {
                    points.Add(new GridPoint(x, y));
                }
            }
        }

        return points.Where(point => point.X >= 0 && point.Y >= 0 && point.X < state.Width && point.Y < state.Height).ToArray();
    }

    private void SpawnPlaceFeature(
        GameState generatedState,
        RegionDefinition region,
        WorldPlaceProfile place,
        List<StateDelta> deltas)
    {
        var district = place.District;
        var landmark = place.Landmark?.Definition;
        if (district is null && landmark is null)
        {
            return;
        }

        var name = district?.FeatureName ?? landmark!.Name;
        var description = district?.FeatureDescription ?? landmark!.Description;
        var glyph = district?.FeatureGlyph ?? landmark!.Glyph;
        var material = district?.FeatureMaterial ?? landmark!.Material;
        var tags = (district?.Tags ?? landmark!.Tags)
            .Concat(place.Tags)
            .Concat(new[] { "place_feature", "significant_site" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var position = FindGeneratedOpenPoint(
            generatedState,
            new GridPoint(generatedState.Width / 2, generatedState.Height / 2));
        var interior = district is not null
            && place.Settlement?.IsPrimary == true
            ? InteriorForDistrict(region, district.Id)
            : null;
        var interiorZoneId = interior is null
            ? null
            : $"interior:{NormalizeToken(region.Id)}:{NormalizeToken(interior.Id)}:{generatedState.CurrentZoneId}";
        var entrance = interior is null
            ? null
            : new InteriorEntranceComponent(
                interiorZoneId!,
                interior.Id,
                interior.Name,
                interior.Kind,
                interior.Summary,
                interior.AccessPolicy,
                interior.RequiredItem,
                generatedState.CurrentZoneId,
                position.X,
                position.Y);
        var fixtureTags = interior is null
            ? tags
            : tags
                .Concat(new[]
                {
                    "interior_entrance",
                    interior.Id,
                    interior.Kind,
                    interior.AccessPolicy,
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        var fixtureDescription = interior is null
            ? description
            : $"{description} Beyond this threshold is {interior.Name}. {interior.Summary} "
                + (interior.AccessPolicy.Equals("public", StringComparison.OrdinalIgnoreCase)
                    ? "The threshold is public."
                    : $"The threshold is restricted; {interior.RequiredItem ?? "permission, force, or magic"} is one ordinary way through.");
        TryApplyGeneratedZoneConsequence(
            generatedState,
            WorldConsequence.SpawnFixture(
                "generation",
                name,
                position.X,
                position.Y,
                prefix: district is null ? "landmark" : "district_site",
                glyph: glyph,
                palette: district is null ? "landmark" : "district",
                fixtureType: district is null ? "regional_landmark" : "district_site",
                material: material,
                tags: fixtureTags,
                blocksMovement: false,
                description: fixtureDescription,
                interactableVerbs: entrance is null ? new[] { "examine" } : new[] { "examine", "enter" },
                canAnchorMagic: true,
                interiorEntrance: entrance,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: description,
                reason: "The shared world place graph instantiated a district or landmark signature site.",
                operation: "generatePlaceFeature",
                emitMessage: false,
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["placeKind"] = place.Kind,
                    ["settlementId"] = place.Settlement?.Id,
                    ["districtId"] = district?.Id,
                    ["roadId"] = place.Road?.Id,
                    ["landmarkId"] = place.Landmark?.Id,
                    ["interiorId"] = interior?.Id,
                    ["interiorZoneId"] = interiorZoneId,
                }),
            deltas,
            "place feature");
    }

    private static RegionInteriorDefinition? InteriorForDistrict(RegionDefinition region, string districtId)
    {
        var grammar = region.Interiors;
        var binding = grammar?.Bindings.FirstOrDefault(candidate =>
            candidate.DistrictId.Equals(districtId, StringComparison.OrdinalIgnoreCase));
        return binding is null
            ? null
            : grammar!.Definitions.FirstOrDefault(definition =>
                definition.Id.Equals(binding.InteriorId, StringComparison.OrdinalIgnoreCase));
    }

    private static RegionInteriorDefinition? InteriorForId(RegionDefinition region, string interiorId) =>
        region.Interiors?.Definitions.FirstOrDefault(definition =>
            definition.Id.Equals(interiorId, StringComparison.OrdinalIgnoreCase));

    private void SpawnFallbackGeneratedProp(
        GameState generatedState,
        RegionDefinition region,
        RealmProfile realm,
        List<StateDelta> deltas)
    {
        var texture = TextureGrammar.ZoneFeature(region, realm, generatedState.Rng);
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
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                    ["propGrammar"] = false,
                }),
            deltas,
            "zone feature");
    }

    private HashSet<GridPoint> PropReservedPoints(Direction entryDirection)
    {
        var points = new HashSet<GridPoint>
        {
            EntryPoint(entryDirection),
            ResidentEntryPoint(entryDirection),
            new(_state.Width / 2, _state.Height / 2),
            new((_state.Width / 2) + 2, _state.Height / 2),
            new(_state.Width / 2, (_state.Height / 2) - 2),
        };
        foreach (var origin in points.ToArray())
        {
            points.Add(origin.Translate(-1, 0));
            points.Add(origin.Translate(1, 0));
            points.Add(origin.Translate(0, -1));
            points.Add(origin.Translate(0, 1));
        }

        return points;
    }

    private static GridPoint RandomInteriorPoint(GameState state, IRng rng) =>
        new(
            rng.NextInt(2, Math.Max(3, state.Width - 2)),
            rng.NextInt(2, Math.Max(3, state.Height - 2)));

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
            _ when region.TerrainTags.Contains("marble", StringComparer.OrdinalIgnoreCase) => "law_chalk",
            _ when region.TerrainTags.Contains("desert", StringComparer.OrdinalIgnoreCase) => "wind_rippled_sand",
            _ when region.TerrainTags.Contains("crystal", StringComparer.OrdinalIgnoreCase) => "crystal_scree",
            _ when region.TerrainTags.Contains("whalebone", StringComparer.OrdinalIgnoreCase) => "carved_bone_chip",
            _ when region.TerrainTags.Contains("red_earth", StringComparer.OrdinalIgnoreCase) => "chariot_rut",
            _ when region.TerrainTags.Contains("cloth", StringComparer.OrdinalIgnoreCase) => "thread_scrap",
            _ when region.TerrainTags.Contains("canals", StringComparer.OrdinalIgnoreCase) => "canal_water",
            _ when region.TerrainTags.Contains("grass", StringComparer.OrdinalIgnoreCase) => "long_grass",
            _ when region.TerrainTags.Contains("high_pasture", StringComparer.OrdinalIgnoreCase) => "herb_patch",
            _ when region.TerrainTags.Contains("crag", StringComparer.OrdinalIgnoreCase) => "loose_shale",
            _ when region.TerrainTags.Contains("harbor", StringComparer.OrdinalIgnoreCase) => "tide_pool",
            _ => region.FloorTerrain,
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

    private void PopulateZone(
        GameState generatedState,
        RegionDefinition region,
        RealmProfile realm,
        WorldPlaceProfile place,
        Direction entryDirection,
        List<StateDelta> deltas)
    {
        if (region.Population is null)
        {
            return;
        }

        var anchor = region.Placement is null
            ? ParseZoneId(generatedState.CurrentZoneId)
            : PlacedAnchor(region);
        var batch = RegionPopulationGenerator.Generate(
            _state.Seed,
            generatedState.CurrentZoneId,
            region,
            realm,
            anchor,
            habitatOverride: place.Settlement is not null ? RegionPopulationGenerator.CenterHabitat : null,
            expectedMultiplier: place.PopulationMultiplier);
        for (var index = 0; index < batch.Residents.Count; index++)
        {
            var journey = index == 0
                ? GeneratedJourneyFactory.Create(
                    _state.Seed,
                    place,
                    _places,
                    region,
                    batch.Residents[index],
                    _quests)
                : null;
            SpawnPopulationResident(
                generatedState,
                region,
                realm,
                place,
                entryDirection,
                batch,
                batch.Residents[index],
                journey,
                index,
                deltas);
        }
    }

    private void SpawnPopulationResident(
        GameState generatedState,
        RegionDefinition region,
        RealmProfile realm,
        WorldPlaceProfile place,
        Direction entryDirection,
        RegionPopulationBatch batch,
        GeneratedResidentProfile profile,
        GeneratedJourney? journey,
        int index,
        List<StateDelta> deltas)
    {
        var position = FindGeneratedOpenPoint(
            generatedState,
            PopulationSpawnOrigin(generatedState, entryDirection, index));
        var verbs = new List<string> { "talk", "give", "recruit" };
        if (journey is not null)
        {
            verbs.Add("examine");
        }
        if (profile.Wares.Count > 0)
        {
            verbs.AddRange(new[] { "wares", "buy", "sell" });
        }

        if (profile.Services.Count > 0)
        {
            verbs.AddRange(new[] { "services", "request_service" });
        }

        var resident = TryApplyGeneratedZoneEntityConsequence(
            generatedState,
            WorldConsequence.SpawnEntity(
                "generation",
                profile.Name,
                position.X,
                position.Y,
                prefix: $"resident_{NormalizeToken(profile.ArchetypeId)}",
                glyph: profile.Glyph,
                faction: profile.FactionId,
                hp: profile.HitPoints,
                attack: profile.Attack,
                tags: profile.Tags,
                material: "flesh",
                roles: profile.Roles,
                controllerKind: "ai",
                aiPolicyId: "resident",
                summoned: false,
                description: $"{realm.Name} is {realm.Status} under {realm.Ruler}. {profile.Description}",
                interactableVerbs: verbs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                bodyVigor: Math.Clamp(2 + (profile.HitPoints / 4), 2, 5),
                includeMemory: true,
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: profile.Description,
                reason: "Regional population generation created a resident through the shared spawn lifecycle.",
                operation: "generateResident",
                emitMessage: false,
                autoWant: false,
                wantText: journey is null ? profile.WantText : $"{profile.WantText} {journey.WantText}",
                wantId: $"want_{NormalizeToken(region.Id)}_{NormalizeToken(profile.ArchetypeId)}_{NormalizeToken(profile.Name)}",
                wantStatus: "active",
                wantStakes: "Help, trade, promises, witnessed deeds, or trouble can change this want and expose new routes, people, or obligations.",
                wantSalience: Math.Clamp(2 + profile.KnowledgeTier, 2, 5),
                wantTags: profile.Tags
                    .Take(4)
                    .Concat(new[] { "regional_population", "promise_source" })
                    .Concat(journey is null ? Array.Empty<string>() : new[] { "generated_journey", journey.TemplateId })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                claimSeeds: journey is null ? null : new[] { journey.Claim },
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = generatedState.CurrentZoneId,
                    ["regionId"] = region.Id,
                    ["realmId"] = region.RealmId,
                    ["profileName"] = profile.Name,
                    ["profileAppearance"] = profile.Description,
                    ["profileOrigin"] = region.Name,
                    ["populationArchetype"] = profile.ArchetypeId,
                    ["populationHabitat"] = batch.Habitat,
                    ["populationDistance"] = batch.DistanceToCenter,
                    ["populationExpected"] = Math.Round(batch.ExpectedPopulation, 2),
                    ["populationIndex"] = index,
                    ["placeKind"] = place.Kind,
                    ["settlementId"] = place.Settlement?.Id,
                    ["districtId"] = place.District?.Id,
                    ["journeyTemplateId"] = journey?.TemplateId,
                    ["journeyDestinationZoneId"] = journey?.DestinationZoneId,
                    ["journeyDestinationName"] = journey?.DestinationName,
                }),
            deltas,
            $"{profile.ArchetypeId} resident");
        if (resident is null)
        {
            return;
        }

        foreach (var ware in profile.Wares)
        {
            TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.OfferTrade(
                    "generation",
                    resident.Id.Value,
                    ware.Item,
                    quantity: ware.Quantity,
                    gold: 20 + (profile.KnowledgeTier * 10),
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: $"{profile.Title} stock came from the regional population grammar.",
                    reason: "Regional population generation attached authored stock through offer_trade.",
                    operation: "generateResidentWares",
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = generatedState.CurrentZoneId,
                        ["regionId"] = region.Id,
                        ["populationArchetype"] = profile.ArchetypeId,
                        ["stockSource"] = "regional_population",
                    }),
                deltas,
                $"{profile.ArchetypeId} {ware.Item}");
        }

        foreach (var service in profile.Services)
        {
            TryApplyGeneratedZoneConsequence(
                generatedState,
                WorldConsequence.OfferService(
                    "generation",
                    resident.Id.Value,
                    service.Id,
                    service.Name,
                    service.Description,
                    service.EffectKind,
                    service.GoldCost,
                    service.ItemCost,
                    service.TargetHint,
                    tags: service.Tags,
                    visibility: WorldConsequenceVisibility.Hidden,
                    evidence: service.Description,
                    reason: "Regional population generation attached an authored service through offer_service.",
                    operation: "generateResidentService",
                    details: new Dictionary<string, object?>
                    {
                        ["zoneId"] = generatedState.CurrentZoneId,
                        ["regionId"] = region.Id,
                        ["populationArchetype"] = profile.ArchetypeId,
                        ["serviceSource"] = "regional_population",
                    }),
                deltas,
                $"{profile.ArchetypeId} {service.Id} service");
        }
    }

    private static GridPoint PopulationSpawnOrigin(
        GameState generatedState,
        Direction entryDirection,
        int index)
    {
        if (index == 0)
        {
            return entryDirection switch
            {
                Direction.East => new GridPoint(3, generatedState.Height / 2),
                Direction.West => new GridPoint(generatedState.Width - 4, generatedState.Height / 2),
                Direction.South => new GridPoint(generatedState.Width / 2, 3),
                Direction.North => new GridPoint(generatedState.Width / 2, generatedState.Height - 4),
                _ => new GridPoint((generatedState.Width / 2) - 1, (generatedState.Height / 2) - 2),
            };
        }

        var offsets = new[]
        {
            new GridPoint(-3, -2),
            new GridPoint(3, -2),
            new GridPoint(-3, 2),
            new GridPoint(3, 2),
            new GridPoint(0, -3),
            new GridPoint(0, 3),
            new GridPoint(-4, 0),
            new GridPoint(4, 0),
        };
        var offset = offsets[(index - 1) % offsets.Length];
        return new GridPoint(
            Math.Clamp((generatedState.Width / 2) + offset.X, 1, generatedState.Width - 2),
            Math.Clamp((generatedState.Height / 2) + offset.Y, 1, generatedState.Height - 2));
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
        RestoreZone(snapshot);

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

    private void RestoreZone(ZoneSnapshot snapshot)
    {
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
    }

    private IReadOnlyList<StateDelta> LoadLinkedZone(
        ZoneSnapshot snapshot,
        IReadOnlyList<Entity> travelers,
        GridPoint entry,
        string operation,
        string interiorId)
    {
        var deltas = new List<StateDelta>();
        RestoreZone(snapshot);
        var occupied = OccupiedPoints();
        for (var index = 0; index < travelers.Count; index++)
        {
            var traveler = travelers[index].Clone();
            var point = FindOpenNear(entry, occupied, _state.BlockingTerrain) ?? entry;
            traveler.Set(new PositionComponent(point));
            _state.Entities[traveler.Id] = traveler;
            var placement = WorldConsequence.MoveEntity(
                "interior",
                traveler.Id.Value,
                point.X,
                point.Y,
                operation: $"{operation}PlaceTraveler",
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: traveler.Id.Value,
                evidence: $"Traveler crossed the linked threshold into {snapshot.ZoneId}.",
                reason: "Interior transitions carry the controlled body and followers through one shared zone boundary.",
                emitMessage: false,
                message: EntryMessage(traveler),
                details: new Dictionary<string, object?>
                {
                    ["zoneId"] = snapshot.ZoneId,
                    ["regionId"] = snapshot.RegionId,
                    ["interiorId"] = interiorId,
                    ["travelerIndex"] = index,
                    ["playerVisible"] = false,
                });
            TryApplyTravelPlacementConsequence(placement, deltas);
            occupied.Add(point);
        }

        var selectedTarget = _applyConsequence(WorldConsequence.SetSelectedTarget(
            "interior",
            clear: true,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: "Crossing an interior threshold clears tactical coordinates from the previous zone.",
            operation: $"{operation}ClearTarget"));
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

    private InteriorExitComponent? CurrentInteriorExit() =>
        _state.Entities.Values
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(entity => entity.TryGet<InteriorExitComponent>(out var exit) ? exit : null)
            .FirstOrDefault(exit => exit is not null);

    private Entity? ResolveNearbyInteriorEntrance(string? targetText)
    {
        var candidates = NearbyThresholds(entity => entity.Has<InteriorEntranceComponent>());
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            var token = NormalizeToken(targetText);
            return candidates.FirstOrDefault(entity =>
                    NormalizeToken(entity.Id.Value).Equals(token, StringComparison.OrdinalIgnoreCase)
                    || NormalizeToken(entity.Name).Equals(token, StringComparison.OrdinalIgnoreCase)
                    || (entity.TryGet<InteriorEntranceComponent>(out var entrance)
                        && NormalizeToken(entrance.Name).Equals(token, StringComparison.OrdinalIgnoreCase)))
                ?? candidates.FirstOrDefault(entity =>
                    NormalizeToken(entity.Name).Contains(token, StringComparison.OrdinalIgnoreCase)
                    || (entity.TryGet<InteriorEntranceComponent>(out var entrance)
                        && NormalizeToken(entrance.Name).Contains(token, StringComparison.OrdinalIgnoreCase)));
        }

        if (_state.SelectedTarget is { } selected)
        {
            var selectedEntity = candidates.FirstOrDefault(entity =>
                entity.Get<PositionComponent>().Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        return candidates.FirstOrDefault();
    }

    private Entity? ResolveNearbyInteriorExit() =>
        NearbyThresholds(entity => entity.Has<InteriorExitComponent>()).FirstOrDefault();

    private IReadOnlyList<Entity> NearbyThresholds(Func<Entity, bool> predicate)
    {
        var origin = _state.ControlledEntity.Get<PositionComponent>().Position;
        return _state.Entities.Values
            .Where(entity => entity.Id != _state.ControlledEntityId
                && predicate(entity)
                && entity.TryGet<PositionComponent>(out var position)
                && Math.Max(
                    Math.Abs(position.Position.X - origin.X),
                    Math.Abs(position.Position.Y - origin.Y)) <= 1)
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private bool CanEnter(Entity entranceEntity, InteriorEntranceComponent entrance)
    {
        var policy = NormalizeToken(entrance.AccessPolicy);
        if (policy is "" or "public" or "open")
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(entrance.RequiredItem)
            && _state.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
            && inventory.Items.Any(item => item.Value > 0
                && NormalizeToken(item.Key).Equals(NormalizeToken(entrance.RequiredItem), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var interiorToken = NormalizeToken(entrance.InteriorId);
        if (entranceEntity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Select(NormalizeToken).Any(tag => tag is "open" or "unlocked" or "forced_open" or "access_granted" or "permission_granted"
                || tag.Equals($"access_{interiorToken}", StringComparison.OrdinalIgnoreCase)
                || tag.Equals($"permission_{interiorToken}", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        var permissionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"interior_access_{interiorToken}",
            $"access_{interiorToken}",
            $"permission_{interiorToken}",
        };
        return _state.WorldFlags.Any(pair =>
            permissionKeys.Contains(NormalizeToken(pair.Key)) && IsTruthy(pair.Value));
    }

    private static bool IsTruthy(object? value) => value switch
    {
        bool boolean => boolean,
        int integer => integer != 0,
        long integer => integer != 0,
        string text => text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("granted", StringComparison.OrdinalIgnoreCase)
            || text.Equals("open", StringComparison.OrdinalIgnoreCase)
            || (int.TryParse(text, out var number) && number != 0),
        _ => false,
    };

    public RegionDefinition RegionForZone(string zoneId)
    {
        var (x, y) = ParseZoneId(zoneId);
        if (x == 0 && y == 0)
        {
            return _regions.Region("imperial_encounter")!;
        }

        if (x is >= 2 and <= 4 && Math.Abs(y) <= 1)
        {
            return _regions.Region("vigovian_capital")!;
        }

        if ((x == 1 && y == 0) || (x == 0 && y == 1) || (x == 1 && y == 1))
        {
            return _regions.Region("hollowmere_margin")!;
        }

        var placed = _regions.Regions
            .Where(region => region.Placement is not null && !region.Id.Equals("imperial_encounter", StringComparison.OrdinalIgnoreCase))
            .Select(region => (Region: region, Point: PlacedAnchor(region)))
            .OrderBy(item => SquaredDistance(x, y, item.Point.X, item.Point.Y))
            .ThenBy(item => item.Region.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Region)
            .FirstOrDefault();
        if (placed is not null)
        {
            return placed;
        }

        if (Math.Abs(x) + Math.Abs(y) >= 2)
        {
            return _regions.Region("wild_border")!;
        }

        return x > 0 || y > 0
            ? _regions.Region("hollowmere_margin")!
            : _regions.Region("imperial_encounter")!;
    }

    private RegionDefinition RegionFor(string zoneId) => RegionForZone(zoneId);

    private (int X, int Y) PlacedAnchor(RegionDefinition region) =>
        WorldPlaceGraph.AnchorFor(_state.Seed, region);

    private static long SquaredDistance(int x1, int y1, int x2, int y2)
    {
        var dx = (long)x1 - x2;
        var dy = (long)y1 - y2;
        return (dx * dx) + (dy * dy);
    }

    private static string TravelMessage(
        Direction direction,
        RegionDefinition fromRegion,
        RealmProfile fromRealm,
        RegionDefinition toRegion,
        RealmProfile toRealm,
        WorldPlaceProfile destination)
    {
        var placeText = destination.Kind switch
        {
            WorldPlaceKinds.Settlement => $"{destination.DisplayName} in {toRegion.Name}",
            WorldPlaceKinds.Road => $"{destination.Road!.Name} in {toRegion.Name}",
            WorldPlaceKinds.Landmark => $"{destination.Landmark!.Name} in {toRegion.Name}",
            _ => toRegion.Name,
        };
        var message = $"You travel {direction.ToString().ToLowerInvariant()} into {placeText}.";
        return fromRegion.RealmId.Equals(toRegion.RealmId, StringComparison.OrdinalIgnoreCase)
            ? message
            : $"{message} Behind you, {fromRealm.Name}; ahead, {toRealm.Name}.";
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
