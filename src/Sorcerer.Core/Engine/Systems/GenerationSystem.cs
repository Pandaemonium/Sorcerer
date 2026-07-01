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
    private readonly GameState _state;
    private readonly RegionRegistry _regions = RegionRegistry.CreateMinimal();

    public GenerationSystem(GameState state, ItemCatalog itemCatalog, LoreCatalog loreCatalog)
    {
        _itemCatalog = itemCatalog;
        _loreCatalog = loreCatalog;
        _state = state;
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
        LoadZone(target, travelers, direction);

        var message = $"You travel {direction.ToString().ToLowerInvariant()} into {CurrentRegion.Name}.";
        _state.AddMessage(message);
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
            }),
        };
        foreach (var delta in generatedDeltas)
        {
            _state.AddMessage(delta.Summary);
            deltas.Add(delta);
        }

        foreach (var rumor in NarrationSystem.ZoneEntryRumors(_state, CurrentRegion, CurrentRealm))
        {
            _state.AddMessage(rumor.Text);
            deltas.Add(new StateDelta(
                rumor.Kind,
                CurrentRegion.Id,
                rumor.Text,
                rumor.Details));
        }

        return deltas;
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
        lines.Add($"Capital reach: thin-slice reachable east of Hollowmere; imperial defenses tracked at {capitalDefenses}.");
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
        var terrain = new Dictionary<GridPoint, string>();
        var blocking = new HashSet<GridPoint>();
        for (var x = 0; x < _state.Width; x++)
        {
            blocking.Add(new GridPoint(x, 0));
            blocking.Add(new GridPoint(x, _state.Height - 1));
            terrain[new GridPoint(x, 0)] = "wall";
            terrain[new GridPoint(x, _state.Height - 1)] = "wall";
        }

        for (var y = 0; y < _state.Height; y++)
        {
            blocking.Add(new GridPoint(0, y));
            blocking.Add(new GridPoint(_state.Width - 1, y));
            terrain[new GridPoint(0, y)] = "wall";
            terrain[new GridPoint(_state.Width - 1, y)] = "wall";
        }

        for (var y = 1; y < _state.Height - 1; y++)
        {
            for (var x = 1; x < _state.Width - 1; x++)
            {
                terrain[new GridPoint(x, y)] = region.FloorTerrain;
            }
        }

        var entities = new Dictionary<EntityId, Entity>();
        var realm = CurrentWorld.RealmFor(region.RealmId);
        var texture = TextureGrammar.ZoneFeature(region, realm, _state.Rng);
        var propId = _state.NextEntityId("zone_prop");
        var propTags = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(texture.Subjects)
            .Append("generated")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var prop = new Entity(propId, texture.Name)
            .Set(new PositionComponent(new GridPoint(_state.Width / 2, _state.Height / 2)))
            .Set(new RenderableComponent('&', "fixture"))
            .Set(new TagsComponent(propTags))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: region.TerrainTags.FirstOrDefault() ?? "stone"))
            .Set(new DescriptionComponent(texture.Description))
            .Set(new FixtureComponent("zone_feature", propTags));
        entities[propId] = prop;

        var curio = CurioGenerator.Generate(region, realm, _state.Rng);
        _itemCatalog.Add(curio.ToDefinition());
        var itemId = _state.NextEntityId("zone_item");
        var item = new Entity(itemId, curio.Name)
            .Set(new PositionComponent(new GridPoint((_state.Width / 2) + 2, _state.Height / 2)))
            .Set(new RenderableComponent('*', "item"))
            .Set(new TagsComponent(curio.Tags))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: curio.Material))
            .Set(new DescriptionComponent(curio.Description))
            .Set(new ItemComponent(curio.Id, curio.Value, curio.Material, curio.Tags, StackPolicy: "unique"))
            .Set(new StackComponent(1));
        entities[itemId] = item;

        var resident = BuildResident(region, CurrentWorld.RealmFor(region.RealmId), entities, entryDirection);
        entities[resident.Id] = resident;
        if (region.Id.Equals("vigovian_capital", StringComparison.OrdinalIgnoreCase))
        {
            var emperor = BuildEmperor(region, entities);
            entities[emperor.Id] = emperor;
        }

        var promiseHooks = new List<string>();
        promiseHooks.AddRange(RealizeTravelPromisesForZone(zoneId, region, entities, deltas));

        return new ZoneSnapshot(
            zoneId,
            region.Id,
            Generated: true,
            entities,
            blocking,
            terrain,
            new Dictionary<GridPoint, int>(),
            new Dictionary<GridPoint, TileFlow>(),
            new Dictionary<string, IReadOnlySet<GridPoint>>(StringComparer.OrdinalIgnoreCase),
            new[] { region.Id },
            (region.Affordances ?? Array.Empty<RegionAffordanceCard>())
                .Select(affordance => affordance.Id)
                .Concat(promiseHooks)
                .ToArray());
    }

    private IReadOnlyList<string> RealizeTravelPromisesForZone(
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas)
    {
        if (zoneId.Equals("0,0", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var realizedIds = new List<string>();
        foreach (var promise in _state.PromiseLedger.Promises
            .Where(IsTravelSitePromise)
            .Take(2)
            .ToArray())
        {
            var realized = _state.PromiseLedger.SetStatus(promise.Id, "realized", zoneId);
            if (realized is null)
            {
                continue;
            }

            var siteId = _state.NextEntityId("promise_site");
            var position = FindGeneratedOpenPoint(entities, new GridPoint((_state.Width / 2) - 3, _state.Height / 2));
            var tags = new[] { "promise", "site", NormalizeToken(realized.Kind), NormalizeToken(realized.RealizationKind ?? "site") }
                .Concat(region.TerrainTags)
                .Concat(region.VoiceTags)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var site = new Entity(siteId, PromiseSiteName(realized, region))
                .Set(new PositionComponent(position))
                .Set(new RenderableComponent('?', "promise"))
                .Set(new TagsComponent(tags))
                .Set(new DescriptionComponent(realized.Text))
                .Set(new PhysicalComponent(BlocksMovement: true, Material: region.TerrainTags.FirstOrDefault() ?? "stone"))
                .Set(new FixtureComponent("promise_site", tags))
                .Set(new PromiseAnchorComponent(new[] { realized.Id }));
            entities[siteId] = site;
            var canon = _state.Canon.Add(
                "site",
                siteId.Value,
                realized.Text,
                $"{site.Name}: {realized.Text}",
                tags,
                $"promise:{realized.Id}:travel",
                _state.Turn);
            var summary = $"A promised place takes shape: {site.Name}.";
            deltas.Add(new StateDelta(
                "promiseSite",
                siteId.Value,
                summary,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = realized.Id,
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["canonId"] = canon.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                }));
            realizedIds.Add(realized.Id);
        }

        return realizedIds;
    }

    private Entity BuildResident(
        RegionDefinition region,
        RealmProfile realm,
        IReadOnlyDictionary<EntityId, Entity> entities,
        Direction entryDirection)
    {
        var residentId = _state.NextEntityId("resident");
        var faction = ResidentFaction(region);
        var tags = new[] { "resident", "generated", NormalizeToken(region.RealmId), NormalizeToken(realm.Status) }
            .Concat(region.TerrainTags)
            .Concat(region.VoiceTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var position = FindGeneratedOpenPoint(entities, ResidentEntryPoint(entryDirection));
        return new Entity(residentId, ResidentName(region))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('p', faction))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent($"{realm.Name} is {realm.Status} under {realm.Ruler}. {ResidentDescription(region)}"))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(8, 8, 0, 0, 1, 0, faction))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("resident"))
            .Set(new SoulComponent($"{residentId.Value}_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new FactionComponent(faction, new[] { "resident" }))
            .Set(new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["red tincture"] = 1,
                ["grave salt"] = 2,
            }, Gold: 35))
            .Set(new InteractableComponent(new[] { "talk", "give", "recruit", "wares", "buy", "sell" }))
            .Set(new ProfileComponent(ResidentName(region), ResidentDescription(region)));
    }

    private Entity BuildEmperor(
        RegionDefinition region,
        IReadOnlyDictionary<EntityId, Entity> entities)
    {
        var emperorId = EntityId.Create("emperor_odran");
        var tags = new[] { "emperor", "odran", "vigovia", "imperial", "ruler", "win_condition" }
            .Concat(region.TerrainTags)
            .Concat(region.VoiceTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var position = FindGeneratedOpenPoint(entities, new GridPoint(_state.Width / 2, (_state.Height / 2) - 2));
        return new Entity(emperorId, "Emperor Odran of Vigovia")
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('E', "imperial"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent("The reasonable marble center of the empire, very mortal despite the room's opinion."))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new BodyStatsComponent(2))
            .Set(new ActorComponent(12, 12, 0, 0, 2, 0, "empire"))
            .Set(new FactionComponent("empire", new[] { "empire", "ruler", "emperor" }))
            .Set(new ControllerComponent(ControllerKind.None))
            .Set(new AiComponent("emperor"))
            .Set(new SoulComponent("emperor_odran_soul"))
            .Set(new ProfileComponent(
                "Emperor Odran of Vigovia",
                "a calm human sovereign under too much marble",
                Origin: "Vigovia",
                MagicalSignature: "law polished until it believes itself kind"))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new InteractableComponent(new[] { "talk", "examine" }));
    }

    private static bool IsTravelSitePromise(WorldPromise promise)
    {
        if (!promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            || !PromiseTriggerMatches(promise.TriggerHint, "travel"))
        {
            return false;
        }

        var realization = NormalizeToken(promise.RealizationKind ?? promise.Kind);
        return realization is "site" or "quest" or "prophecy";
    }

    private GridPoint FindGeneratedOpenPoint(IReadOnlyDictionary<EntityId, Entity> entities, GridPoint origin)
    {
        var occupied = entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _)
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement)
            .Select(entity => entity.Get<PositionComponent>().Position)
            .ToHashSet();
        return FindOpenNear(origin, occupied) ?? origin;
    }

    private void LoadZone(ZoneSnapshot snapshot, IReadOnlyList<Entity> travelers, Direction entryDirection)
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

        var entry = EntryPoint(entryDirection);
        var occupied = OccupiedPoints();
        for (var index = 0; index < travelers.Count; index++)
        {
            var traveler = travelers[index].Clone();
            var point = FindOpenNear(entry, occupied) ?? entry;
            traveler.Set(new PositionComponent(point));
            occupied.Add(point);
            _state.Entities[traveler.Id] = traveler;
        }

        _state.SelectedTarget = null;
    }

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

    private GridPoint? FindOpenNear(GridPoint origin, HashSet<GridPoint> occupied)
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
            if (CanEnter(point, occupied))
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
                    if (CanEnter(point, occupied))
                    {
                        return point;
                    }
                }
            }
        }

        return null;
    }

    private bool CanEnter(GridPoint point, HashSet<GridPoint> occupied) =>
        point.X > 0
        && point.Y > 0
        && point.X < _state.Width - 1
        && point.Y < _state.Height - 1
        && !_state.BlockingTerrain.Contains(point)
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

    private static string ResidentFaction(RegionDefinition region) =>
        region.RealmId switch
        {
            "empire" => "empire",
            "hollowmere" => "hollowmere",
            _ => "neutral",
        };

    private static string PromiseSiteName(WorldPromise promise, RegionDefinition region)
    {
        if (!string.IsNullOrWhiteSpace(promise.ClaimedPlace)
            && !promise.ClaimedPlace.Equals(region.Id, StringComparison.OrdinalIgnoreCase))
        {
            return promise.ClaimedPlace;
        }

        return region.Id switch
        {
            "hollowmere_margin" => "folded-road checkpoint",
            "wild_border" => "promise-touched border stone",
            _ => "promised waymark",
        };
    }

    private static bool PromiseTriggerMatches(string? hint, string trigger) =>
        string.IsNullOrWhiteSpace(hint)
        || hint.Equals(trigger, StringComparison.OrdinalIgnoreCase)
        || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase);

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
