using Sorcerer.Core.Characters;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Status;
using Sorcerer.Core.Validation;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class EngineViewBuilder
{
    private readonly GenerationSystem _generationSystem;
    private readonly InventoryService _inventoryService;
    private readonly LoreCatalog _loreCatalog;
    private readonly PerceptionSystem _perceptionSystem;
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;

    public EngineViewBuilder(
        GameEngine engine,
        InventoryService inventoryService,
        StatusRegistry statusRegistry,
        PerceptionSystem perceptionSystem,
        GenerationSystem generationSystem,
        LoreCatalog loreCatalog)
    {
        _generationSystem = generationSystem;
        _inventoryService = inventoryService;
        _loreCatalog = loreCatalog;
        _perceptionSystem = perceptionSystem;
        _statusRegistry = statusRegistry;
        _state = engine.State;
    }

    public MagicContextView MagicContext(OperationIndex operations)
    {
        var caster = _state.ControlledEntity;
        var casterPosition = caster.Get<PositionComponent>().Position;
        var casterActor = caster.Get<ActorComponent>();
        var soulId = caster.TryGet<SoulComponent>(out var soul) ? soul.SoulId : caster.Id.Value;
        var soulRecord = CharacterMath.EnsureSoulRecord(_state, caster);
        var statuses = BuildStatusCards(caster);
        var perception = _perceptionSystem.RefreshControlled();
        var visible = _state.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .OrderBy(entity => entity.Id.Value)
            .Select(entity =>
            {
                var pos = entity.Get<PositionComponent>().Position;
                var actor = entity.TryGet<ActorComponent>(out var entityActor) ? entityActor : null;
                var physical = entity.TryGet<PhysicalComponent>(out var phys) ? phys : null;
                var tags = TagsFor(entity);
                return new PerceivedEntity(
                    entity.Id.Value,
                    entity.Name,
                    entity.TryGet<RenderableComponent>(out var renderable) ? renderable.Glyph : '?',
                    pos.X,
                    pos.Y,
                    pos.X - casterPosition.X,
                    pos.Y - casterPosition.Y,
                    actor?.Faction,
                    physical?.Material ?? "unknown",
                    tags,
                    actor?.HitPoints,
                    actor?.MaxHitPoints,
                    _perceptionSystem.RelationToPlayer(pos, perception));
            })
            .ToArray();

        var ordinaryFloor = _generationSystem.CurrentRegion.FloorTerrain;
        var terrain = BuildAllTiles(perception)
            .Where(tile => tile.BlocksMovement || IsInterestingTerrain(tile.Terrain, ordinaryFloor))
            .Select(tile => new TileNote(
                tile.X,
                tile.Y,
                tile.Terrain,
                tile.BlocksMovement ? new[] { "blocking" } : Array.Empty<string>(),
                _perceptionSystem.RelationToPlayer(new GridPoint(tile.X, tile.Y), perception)))
            .ToArray();

        return new MagicContextView(
            new CasterView(
                caster.Id.Value,
                caster.Name,
                casterPosition.X,
                casterPosition.Y,
                casterActor.HitPoints,
                casterActor.MaxHitPoints,
                casterActor.Mana,
                casterActor.MaxMana,
                soulId,
                statuses),
            visible,
            terrain,
            _state.SelectedTarget,
            _state.Messages.TakeLast(8).ToArray(),
            _state.PromiseLedger.Promises
                .Where(promise => promise.PlayerVisible)
                .Select(ToPromiseCard)
                .ToArray(),
            operations,
            BuildResolverLens(
                caster,
                soulRecord,
                _generationSystem.CurrentRegion,
                _generationSystem.CurrentRealm,
                _generationSystem.CurrentImperialPresence,
                _generationSystem.CurrentAffordances),
            _inventoryService.BuildReagentCards(caster),
            BuildLoreContext(visible));
    }

    private static bool IsInterestingTerrain(string terrain, string ordinaryFloor) =>
        !terrain.Equals("floor", StringComparison.OrdinalIgnoreCase)
        && !terrain.Equals(ordinaryFloor, StringComparison.OrdinalIgnoreCase);

    public GameView View()
    {
        var perception = _perceptionSystem.RefreshControlled();
        var entities = _state.Entities.Values
            .OrderBy(entity => entity.Id.Value)
            .Where(entity => entity.Id == _state.ControlledEntityId || perception.VisibleEntityIds.Contains(entity.Id))
            .Select(ToEntityCard)
            .ToArray();

        var promises = _state.PromiseLedger.Promises
            .Select(ToPromiseCard)
            .ToArray();

        var tiles = BuildPlayerTiles(perception);
        var inventory = _inventoryService.BuildInventoryCards(_state.ControlledEntity);
        var reagents = _inventoryService.BuildReagentCards(_state.ControlledEntity);
        var statuses = BuildStatusCards(_state.ControlledEntity);
        var character = BuildCharacterSheet();

        return new GameView(
            _state.Width,
            _state.Height,
            _state.Turn,
            _state.ControlledEntityId.Value,
            entities,
            promises,
            _state.Messages.ToArray(),
            tiles,
            inventory,
            reagents,
            statuses,
            _state.SelectedTarget,
            character,
            BuildWorldCard());
    }

    public AgentObservation Observation(bool debug)
    {
        var validation = StateValidator.Validate(_state);
        var debugState = debug
            ? new DebugStateView(
                _state.Entities.Count,
                _state.Entities.Keys.Select(id => id.Value).OrderBy(id => id).ToArray(),
                _state.PromiseLedger.Promises.Select(p => p.Id).ToArray(),
                _state.SelectedTarget,
                new LedgerSummary(
                    _state.Deeds.Records.Count,
                    _state.Factions.Factions.Count,
                    _state.Legend.Tags.Count,
                    _state.Memories.Records.Count,
                    _state.Canon.Records.Count,
                    _state.Bonds.Bonds.Count,
                    _state.ScheduledEvents.Events.Count,
                    _state.Suspicions.Records.Count,
                    _state.Souls.Records.Count,
                    _state.Triggers.Records.Count),
                validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}").ToArray(),
                BuildBackgroundJobCards(),
                BuildFactionDebugCards(),
                BuildBondDebugCards(),
                _state.RunStatus,
                _state.RunConclusion)
            : null;

        return new AgentObservation(View(), debugState);
    }

    private IReadOnlyList<FactionDebugCard> BuildFactionDebugCards() =>
        _state.Factions.Factions
            .OrderBy(faction => faction.Id)
            .Select(faction => new FactionDebugCard(
                faction.Id,
                faction.Name,
                faction.Role,
                new Dictionary<string, int>(faction.Standing, StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, int>(faction.Resources, StringComparer.OrdinalIgnoreCase),
                faction.HostileRoles.ToArray()))
            .ToArray();

    private IReadOnlyList<BondDebugCard> BuildBondDebugCards() =>
        _state.Bonds.Bonds
            .OrderBy(bond => bond.SubjectSoulId)
            .ThenBy(bond => bond.TargetSoulId)
            .Select(bond => new BondDebugCard(
                bond.SubjectSoulId,
                bond.TargetSoulId,
                bond.Loyalty,
                bond.Fear,
                bond.Admiration,
                bond.Resentment,
                bond.Posture))
            .ToArray();

    private WorldCard BuildWorldCard()
    {
        var region = _generationSystem.CurrentRegion;
        var realm = _generationSystem.CurrentRealm;
        return new WorldCard(
            _state.CurrentZoneId,
            region.Id,
            region.Name,
            region.RealmId,
            realm.Name,
            realm.Status,
            realm.Ruler,
            region.TraditionId,
            _generationSystem.CurrentImperialPresence,
            region.WildnessBase,
            _generationSystem.CurrentAffordances.ToArray());
    }

    public IReadOnlyList<BackgroundJobCard> BuildBackgroundJobCards() =>
        _state.BackgroundJobs.Jobs
            .Select(job => new BackgroundJobCard(
                job.Id,
                job.Purpose,
                job.TargetId,
                job.State.ToString(),
                job.Priority,
                job.CreatedTurn,
                job.StartedTurn,
                job.CompletedTurn,
                job.AppliedTurn,
                job.ResultText,
                job.Error))
            .ToArray();

    private IReadOnlyList<LoreCardView> BuildLoreContext(IReadOnlyList<PerceivedEntity> visible)
    {
        var region = _generationSystem.CurrentRegion;
        var affordances = _generationSystem.CurrentAffordances;
        var subjects = region.TerrainTags
            .Concat(region.VoiceTags)
            .Concat(new[] { region.Id, region.RealmId, region.TraditionId })
            .Concat(affordances.SelectMany(AffordanceSubjects))
            .Concat(visible.SelectMany(entity => entity.Tags))
            .Concat(visible.Select(entity => entity.Faction ?? ""))
            .Concat(visible.Select(entity => entity.Material))
            .Concat(_state.PromiseLedger.Promises.Where(promise => promise.PlayerVisible).Select(PromiseSubject))
            .ToArray();
        var triggers = new[] { "magic_context", region.Id, region.RealmId, region.TraditionId }
            .Concat(affordances.Select(affordance => affordance.Id))
            .Concat(_state.Messages.TakeLast(4).SelectMany(MessageTokens))
            .ToArray();

        return LoreRouter
            .Select(_loreCatalog, new LoreQuery(subjects, triggers, LoreAccessLevel(), Limit: 3))
            .Select(lore => new LoreCardView(
                lore.Id,
                lore.Title,
                lore.Level,
                lore.Subjects,
                lore.Triggers,
                lore.Body))
            .ToArray();
    }

    private int LoreAccessLevel()
    {
        var canonDepth = _state.Canon.Records.Count(record =>
            record.Kind.Equals("readable", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("canon_detail", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("entity_detail", StringComparison.OrdinalIgnoreCase));
        var legendWeight = _state.Legend.Tags.Sum(tag => tag.Weight);
        return Math.Clamp(1 + (canonDepth / 2) + (legendWeight >= 4 ? 1 : 0), 1, 3);
    }

    private static IEnumerable<string> AffordanceSubjects(RegionAffordanceCard affordance) =>
        affordance.Tags.Append(affordance.Id);

    private static string PromiseSubject(WorldPromise promise) =>
        string.IsNullOrWhiteSpace(promise.Subject) ? promise.Kind : promise.Subject;

    private static IEnumerable<string> MessageTokens(string message) =>
        message
            .Split(new[] { ' ', ',', '.', ':', ';', '!', '?', '-', '_' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length > 3);

    private IReadOnlyList<MapTileCard> BuildPlayerTiles(PerceptionSnapshot perception)
    {
        var tiles = new List<MapTileCard>(_state.Width * _state.Height);
        for (var y = 0; y < _state.Height; y++)
        {
            for (var x = 0; x < _state.Width; x++)
            {
                var point = new GridPoint(x, y);
                var visible = perception.VisibleTiles.Contains(point);
                var explored = visible || perception.ExploredTiles.Contains(point);
                if (!explored)
                {
                    tiles.Add(new MapTileCard(
                        x,
                        y,
                        "unknown",
                        BlocksMovement: false,
                        BlocksSight: false,
                        Visible: false,
                        Explored: false));
                    continue;
                }

                tiles.Add(BuildTile(point, visible, explored));
            }
        }

        return tiles;
    }

    private IReadOnlyList<MapTileCard> BuildAllTiles(PerceptionSnapshot perception)
    {
        var tiles = new List<MapTileCard>(_state.Width * _state.Height);
        for (var y = 0; y < _state.Height; y++)
        {
            for (var x = 0; x < _state.Width; x++)
            {
                var point = new GridPoint(x, y);
                tiles.Add(BuildTile(
                    point,
                    perception.VisibleTiles.Contains(point),
                    perception.VisibleTiles.Contains(point) || perception.ExploredTiles.Contains(point)));
            }
        }

        return tiles;
    }

    private MapTileCard BuildTile(GridPoint point, bool visible, bool explored)
    {
        var terrain = _state.Terrain.TryGetValue(point, out var tile)
            ? tile
            : _state.BlockingTerrain.Contains(point) ? "wall" : "floor";
        var blocksMovement = _state.BlockingTerrain.Contains(point);
        return new MapTileCard(
            point.X,
            point.Y,
            terrain,
            blocksMovement,
            _perceptionSystem.BlocksSight(point),
            visible,
            explored);
    }

    private IReadOnlyList<StatusCard> BuildStatusCards(Entity entity)
    {
        if (!entity.TryGet<StatusContainerComponent>(out var container))
        {
            return Array.Empty<StatusCard>();
        }

        return container.Statuses
            .Where(IsStatusActive)
            .Select(status =>
            {
                var definition = _statusRegistry.Find(status.Id);
                return new StatusCard(
                    status.Id,
                    status.DisplayName.Length > 0 ? status.DisplayName : definition?.DisplayName ?? status.Id,
                    status.ExpiresTurn,
                    status.Intensity);
            })
            .ToArray();
    }

    private EntityCard ToEntityCard(Entity entity)
    {
        var position = entity.TryGet<PositionComponent>(out var pos)
            ? pos.Position
            : new GridPoint(-1, -1);
        var glyph = entity.TryGet<RenderableComponent>(out var renderable)
            ? renderable.Glyph
            : '?';
        var blocks = entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement;
        var faction = entity.TryGet<ActorComponent>(out var actor)
            ? actor.Faction
            : null;
        var tags = new List<string>();
        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        return new EntityCard(
            entity.Id.Value,
            entity.Name,
            position.X,
            position.Y,
            glyph,
            blocks,
            faction,
            actor?.HitPoints,
            actor?.MaxHitPoints,
            tags.Distinct().OrderBy(tag => tag).ToArray());
    }

    public CharacterSheetCard BuildCharacterSheet()
    {
        var body = _state.ControlledEntity;
        var actor = body.Get<ActorComponent>();
        var bodyStats = body.TryGet<BodyStatsComponent>(out var stats)
            ? stats
            : CharacterMath.InferBodyStats(actor);
        var soulId = body.TryGet<SoulComponent>(out var soul) ? soul.SoulId : body.Id.Value;
        var soulRecord = CharacterMath.EnsureSoulRecord(_state, body);
        var profile = body.TryGet<ProfileComponent>(out var bodyProfile)
            ? bodyProfile
            : new ProfileComponent(body.Name, "");

        return new CharacterSheetCard(
            body.Id.Value,
            soulId,
            string.IsNullOrWhiteSpace(profile.PublicName) ? body.Name : profile.PublicName,
            profile.Appearance,
            soulRecord.OriginId,
            soulRecord.OriginName,
            soulRecord.Tradition,
            bodyStats.Vigor,
            soulRecord.Stats.Attunement,
            soulRecord.Stats.Composure,
            actor.HitPoints,
            actor.MaxHitPoints,
            actor.Mana,
            actor.MaxMana,
            soulRecord.MagicalSignature,
            soulRecord.Backstory);
    }

    private static ResolverLensView BuildResolverLens(
        Entity caster,
        SoulRecord soulRecord,
        RegionDefinition region,
        RealmProfile realm,
        int imperialPresence,
        IReadOnlyList<RegionAffordanceCard> affordances)
    {
        var bodyStats = caster.TryGet<BodyStatsComponent>(out var stats)
            ? stats
            : caster.TryGet<ActorComponent>(out var actor)
                ? CharacterMath.InferBodyStats(actor)
                : new BodyStatsComponent(1);
        var attunement = soulRecord.Stats.Attunement;
        var composure = soulRecord.Stats.Composure;
        var magnitudeDelta = attunement <= 2 ? -1 : attunement >= 5 ? 1 : 0;
        var notes = new List<string>();
        var magnitude = magnitudeDelta switch
        {
            < 0 => "low attunement: prefer the floor of each effect band",
            > 0 => "high attunement: effects may lean toward the top of their band",
            _ => "unchanged",
        };
        if (magnitudeDelta != 0)
        {
            notes.Add(magnitude);
        }

        var volatility = composure <= 2
            ? "low composure: wild magic answers more chaotically; costs and backfires can be stranger"
            : composure >= 5
                ? "high composure: wild magic answers cleanly with fewer messy flourishes"
                : "unchanged";
        if (!volatility.Equals("unchanged", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(volatility);
        }

        var costFraming = bodyStats.Vigor <= 2
            ? "low vigor: avoid leaning on raw HP or bodily punishment as the default cost"
            : bodyStats.Vigor >= 5
                ? "high vigor: physical costs are more plausible if the spell needs a price"
                : "unchanged";
        if (!costFraming.Equals("unchanged", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(costFraming);
        }

        if (!string.IsNullOrWhiteSpace(soulRecord.MagicalSignature))
        {
            notes.Add($"signature lens: {soulRecord.MagicalSignature}");
        }

        notes.Add(
            $"region lens: {region.Name} in {realm.Name}; realm status {realm.Status}; ruler {realm.Ruler}; tradition {region.TraditionId}; wildness {region.WildnessBase}; imperial presence {imperialPresence}");
        foreach (var affordance in affordances)
        {
            notes.Add($"region affordance {affordance.Id}: {affordance.Text}");
        }

        return new ResolverLensView(
            bodyStats.Vigor,
            attunement,
            composure,
            magnitudeDelta,
            magnitude,
            volatility,
            costFraming,
            soulRecord.MagicalSignature,
            notes);
    }

    private static PromiseCard ToPromiseCard(WorldPromise promise) =>
        new(
            promise.Id,
            promise.Kind,
            promise.Status,
            promise.Text,
            promise.PlayerVisible,
            promise.Source,
            promise.Subject,
            promise.ClaimedPlace,
            promise.BoundPlace,
            promise.BoundTargetId,
            promise.TriggerHint,
            promise.RealizationKind,
            promise.RealizedIn);

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    private static IReadOnlyList<string> TagsFor(Entity entity)
    {
        var tags = new List<string>();
        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
            tags.Add(item.Material);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
    }
}
