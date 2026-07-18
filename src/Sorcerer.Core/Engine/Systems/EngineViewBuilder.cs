using Sorcerer.Core.Characters;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Status;
using Sorcerer.Core.Validation;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class EngineViewBuilder
{
    private readonly GameEngine _engine;
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
        _engine = engine;
        _generationSystem = generationSystem;
        _inventoryService = inventoryService;
        _loreCatalog = loreCatalog;
        _perceptionSystem = perceptionSystem;
        _statusRegistry = statusRegistry;
        _state = engine.State;
    }

    public MagicContextView MagicContext(
        OperationIndex operations,
        IReadOnlyCollection<string>? requiredContext = null,
        string? resolverQuery = null)
    {
        var caster = _state.ControlledEntity;
        var casterPosition = caster.Get<PositionComponent>().Position;
        var casterActor = caster.Get<ActorComponent>();
        var soulId = caster.TryGet<SoulComponent>(out var soul) ? soul.SoulId : caster.Id.Value;
        var soulRecord = CharacterMath.EnsureSoulRecord(_state, caster);
        var statuses = BuildStatusCards(caster);
        var perception = _perceptionSystem.RefreshControlled();
        var routedContext = requiredContext is not null;
        // By default the resolver only sees what the caster perceives (plus the caster). Entities the
        // player cannot perceive are included only when a routed capability asks for them via its
        // RequiredContext (e.g. a memory-edit spell reaching an unseen mind); otherwise they are
        // off-screen context the spell does not need. See docs/CAPABILITY_ROUTING.md Lever B.
        var hiddenContextRequested = requiredContext is not null && requiredContext.Contains("hidden_entities");
        var includeContextObjects = !routedContext || RequiresContext(
            requiredContext,
            "visible_entities",
            "spell_anchors",
            "selected_target",
            "conjurable_items");
        // Actors and mechanically important hooks use the full-card lane. Ordinary fixtures use a
        // separate compact scenery lane, so adding lush clutter cannot evict a hostile, resident,
        // readable, promise anchor, item, or selected object from resolver context.
        var perceivedCandidates = _state.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Where(entity => entity.Id == _state.ControlledEntityId
                || perception.VisibleEntityIds.Contains(entity.Id));
        var hiddenPool = hiddenContextRequested
            ? _state.Entities.Values
                .Where(entity => entity.TryGet<PositionComponent>(out _))
                .Where(entity => entity.Id != _state.ControlledEntityId
                    && !perception.VisibleEntityIds.Contains(entity.Id))
                .Where(entity => ContextEntityRouting.IsActor(entity) || ContextEntityRouting.IsHookBearing(entity))
                .OrderBy(entity => entity.Id.Value)
                .ToArray()
            : Array.Empty<Entity>();
        var namedHidden = hiddenPool.Where(entity => IsNamedInQuery(entity, resolverQuery)).ToArray();
        var hiddenCandidates = (namedHidden.Length > 0
                ? namedHidden
                : string.IsNullOrWhiteSpace(resolverQuery) || NamesRemoteIntent(resolverQuery)
                    ? hiddenPool
                    : Array.Empty<Entity>())
            .Take(MagicContextHiddenEntityCap)
            .ToArray();
        var includeHidden = hiddenCandidates.Length > 0;
        var candidates = perceivedCandidates
            .Concat(hiddenCandidates)
            .DistinctBy(entity => entity.Id)
            .ToArray();
        var orderedCandidates = candidates
            .OrderBy(entity => entity.Id == _state.ControlledEntityId
                ? -1
                : GameEngine.Distance(casterPosition, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
        var actorLane = orderedCandidates
            .Where(entity => entity.Id == _state.ControlledEntityId || ContextEntityRouting.IsActor(entity))
            .ToArray();
        var fullLaneQuery = actorLane
            .Concat(orderedCandidates
                .Where(entity => entity.Id != _state.ControlledEntityId)
                .Where(entity => !ContextEntityRouting.IsActor(entity))
                .Where(entity => IsSelectedEntity(entity)
                    || (includeContextObjects && ContextEntityRouting.IsHookBearing(entity))))
            .DistinctBy(entity => entity.Id)
            .OrderBy(entity => entity.Id == _state.ControlledEntityId
                ? -2
                : includeHidden && IsNamedInQuery(entity, resolverQuery)
                    ? -1
                    : GameEngine.Distance(casterPosition, entity.Get<PositionComponent>().Position))
            .ThenBy(entity => entity.Id.Value);
        var fullLane = (routedContext ? fullLaneQuery.Take(MagicContextEntityCap) : fullLaneQuery)
            .Select(entity => ToPerceivedEntity(entity, perception))
            .ToArray();
        var scenery = includeContextObjects
            ? orderedCandidates
                .Where(ContextEntityRouting.IsCompactScenery)
                .Where(entity => !IsSelectedEntity(entity))
                .Take(routedContext ? MagicContextRoutedSceneryCap : MagicContextSceneryCap)
                .Select(entity =>
                {
                    var position = entity.Get<PositionComponent>().Position;
                    var physical = entity.TryGet<PhysicalComponent>(out var component) ? component : null;
                    return new SceneryNote(
                        entity.Id.Value,
                        entity.Name,
                        position.X,
                        position.Y,
                        physical?.Material ?? "unknown",
                        TagsFor(entity));
                })
                .ToArray()
            : Array.Empty<SceneryNote>();

        var ordinaryFloor = _generationSystem.CurrentRegion.FloorTerrain;
        var includeTerrain = !routedContext || RequiresContext(
            requiredContext,
            "nearby_tiles",
            "visible_tiles",
            "selected_target");
        var terrain = includeTerrain
            ? BuildAllTiles(perception)
                .Where(tile => tile.BlocksMovement || IsInterestingTerrain(tile.Terrain, ordinaryFloor))
                .OrderBy(tile => GameEngine.Distance(casterPosition, new GridPoint(tile.X, tile.Y)))
                .Take(routedContext ? MagicContextTerrainCap : int.MaxValue)
                .Select(tile => new TileNote(
                    tile.X,
                    tile.Y,
                    tile.Terrain,
                    tile.BlocksMovement ? new[] { "blocking" } : Array.Empty<string>(),
                    _perceptionSystem.RelationToPlayer(new GridPoint(tile.X, tile.Y), perception)))
                .ToArray()
            : Array.Empty<TileNote>();

        var includePromises = !routedContext || RequiresContext(requiredContext, "promises");
        var includeLore = !routedContext || RequiresContext(requiredContext, "region", "lore");
        var reagents = _inventoryService.BuildReagentCards(caster);

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
            fullLane,
            terrain,
            _state.SelectedTarget,
            routedContext ? Array.Empty<string>() : _state.Messages.TakeLast(8).ToArray(),
            includePromises
                ? _state.PromiseLedger.Promises
                    .Where(promise => promise.PlayerVisible)
                    // A routed cleansing cast has a four-promise budget. Durable active curses
                    // must win that budget over unrelated old leads or the resolver can see the
                    // status chip but not the authoritative record that actually drives it.
                    .OrderByDescending(promise =>
                        promise.Kind.Equals("curse", StringComparison.OrdinalIgnoreCase)
                        && promise.Status is not "cleared" and not "fulfilled")
                    .ThenByDescending(promise => promise.Salience)
                    .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
                    .Take(routedContext ? MagicContextPromiseCap : int.MaxValue)
                    .Select(ToResolverPromiseCard)
                    .ToArray()
                : Array.Empty<PromiseCard>(),
            operations,
            BuildResolverLens(
                caster,
                soulRecord,
                _generationSystem.CurrentRegion,
                _generationSystem.CurrentRealm,
                _generationSystem.CurrentImperialPresence,
                _generationSystem.CurrentAffordances,
                _generationSystem.CurrentPlace,
                compact: routedContext),
            (routedContext ? reagents.Take(MagicContextReagentCap) : reagents).ToArray(),
            includeLore ? BuildLoreContext(fullLane).Take(routedContext ? 1 : int.MaxValue).ToArray() : Array.Empty<LoreCardView>(),
            scenery);
    }

    private static bool RequiresContext(IReadOnlyCollection<string>? requiredContext, params string[] keys) =>
        requiredContext is not null
        && keys.Any(requiredContext.Contains);

    private static bool IsNamedInQuery(Entity entity, string? resolverQuery)
    {
        if (string.IsNullOrWhiteSpace(resolverQuery))
        {
            return false;
        }

        var query = resolverQuery.ToLowerInvariant();
        if (query.Contains(entity.Name.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return true;
        }

        return $"{entity.Name} {entity.Id.Value.Replace('_', ' ')}"
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(part => part.Length >= 4 && query.Contains(part.ToLowerInvariant(), StringComparison.Ordinal));
    }

    private static bool NamesRemoteIntent(string? resolverQuery)
    {
        if (string.IsNullOrWhiteSpace(resolverQuery))
        {
            return false;
        }

        var query = resolverQuery.ToLowerInvariant();
        return new[] { "far away", "elsewhere", "not here", "absent", "offscreen", "off-screen", "towns away", "miles away", "beyond this place" }
            .Any(query.Contains);
    }

    private PerceivedEntity ToPerceivedEntity(Entity entity, PerceptionSnapshot perception)
    {
        var position = entity.Get<PositionComponent>().Position;
        var actor = entity.TryGet<ActorComponent>(out var entityActor) ? entityActor : null;
        var physical = entity.TryGet<PhysicalComponent>(out var component) ? component : null;
        return new PerceivedEntity(
            entity.Id.Value,
            entity.Name,
            entity.TryGet<RenderableComponent>(out var renderable) ? renderable.Glyph : '?',
            position.X,
            position.Y,
            RelativeX: null,
            RelativeY: null,
            actor?.Faction,
            physical?.Material ?? "unknown",
            TagsFor(entity),
            actor?.HitPoints,
            actor?.MaxHitPoints,
            _perceptionSystem.RelationToPlayer(position, perception));
    }

    private bool IsSelectedEntity(Entity entity) =>
        _state.SelectedTarget is { } selected
        && entity.TryGet<PositionComponent>(out var position)
        && position.Position == selected;

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
        var claims = _state.Claims.Records
            .Where(claim => claim.PlayerVisible)
            .Select(ToClaimCard)
            .ToArray();
        var rumors = RumorViewBuilder.Visible(_state, limit: 12)
            .Select(ToRumorCard)
            .ToArray();

        var tiles = BuildPlayerTiles(perception);
        var inventory = _inventoryService.BuildInventoryCards(_state.ControlledEntity);
        var reagents = _inventoryService.BuildReagentCards(_state.ControlledEntity);
        var statuses = BuildStatusCards(_state.ControlledEntity);
        var character = BuildCharacterSheet();

        // The player log is curated for the renderer (chaff dropped, near-duplicates removed, damage
        // classified). State.Messages stays the full raw record; only what the player sees is shaped.
        var messageCards = MessageLog.Curate(_state.Messages, limit: 500);
        var objectiveCards = ObjectiveCards();
        return new GameView(
            _state.Width,
            _state.Height,
            _state.Turn,
            _state.ControlledEntityId.Value,
            entities,
            promises,
            messageCards.Select(card => card.Text).ToArray(),
            tiles,
            inventory,
            reagents,
            statuses,
            _state.SelectedTarget,
            character,
            BuildWorldCard(),
            claims,
            rumors,
            JournalViewBuilder.Build(_state),
            messageCards,
            BuildRepertoire(),
            objectiveCards.FirstOrDefault(),
            objectiveCards.Skip(1).ToArray(),
            _engine.DescribeThreats(),
            JournalViewBuilder.BuildStructured(_state));
    }

    public IReadOnlyList<ObjectiveCard> ObjectiveCards() =>
        _state.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible)
            .Where(promise => !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase))
            .Select(promise => (Promise: promise, Contract: PromiseObjectiveContracts.For(_state, promise)))
            .Where(item => item.Contract is not null)
            .OrderByDescending(item => item.Promise.Status.Equals("ready_to_return", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(item => item.Promise.Salience)
            .ThenBy(item => item.Promise.Id, StringComparer.OrdinalIgnoreCase)
            .Select(item => new ObjectiveCard(
                item.Promise.Id,
                item.Contract!.Kind,
                item.Promise.Status,
                item.Promise.Text,
                ObjectiveNextStep(item.Promise, item.Contract),
                item.Promise.Subject,
                item.Contract.GiverName,
                item.Promise.ClaimedPlace,
                item.Promise.Status.Equals("ready_to_return", StringComparison.OrdinalIgnoreCase)))
            .ToArray();

    private static string ObjectiveNextStep(WorldPromise promise, PromiseObjectiveContract contract)
    {
        if (promise.Status.Equals("ready_to_return", StringComparison.OrdinalIgnoreCase))
        {
            return $"Return to {contract.GiverName} and talk.";
        }

        if (promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase))
        {
            return promise.Text;
        }

        return contract.Kind switch
        {
            "meet" => $"Talk to {promise.Subject}.",
            "fetch" => $"Pick up {contract.RequiredItem ?? promise.Subject}.",
            "delivery" => $"Give {contract.RequiredItem ?? "the parcel"} to {promise.Subject}.",
            "escort" => $"Recruit {promise.Subject} and bring them back to {contract.GiverName}.",
            "threat" => $"Resolve {promise.Subject}; force is only one option.",
            "folk_service" => $"Request {promise.Subject} from the promised practitioner.",
            "rumor_verification" => $"Talk to {promise.Subject} and hear their account.",
            "social_leverage" => $"Change {promise.Subject}'s bond, want, fear, or allegiance.",
            _ => promise.Text,
        };
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
                    _state.Triggers.Records.Count,
                    _state.Claims.Records.Count,
                    _state.Rumors.Records.Count,
                    _state.WorldTurns.Records.Count),
                validation.Issues.Select(issue => $"{issue.Code}: {issue.Message}").ToArray(),
                BuildBackgroundJobCards(),
                BuildFactionDebugCards(),
                BuildBondDebugCards(),
                BuildWantDebugCards(),
                _state.Claims.Records.Select(claim => claim.Id).ToArray(),
                _state.Rumors.Records.Select(rumor => rumor.Id).ToArray(),
                BuildRumorDebugCards(),
                _state.WorldTurns.Records.Select(record => record.Id).ToArray(),
                BuildWorldTurnDebugCards(),
                _state.RunStatus,
                _state.RunConclusion,
                BuildWitnessDebugCards(),
                BuildCapabilitySummary(),
                BuildEconomySummary())
            : null;

        return new AgentObservation(View(), debugState);
    }

    // Read-only capability portfolio (Phase 2.3): rolls up ordinary ledgers -- named echoes,
    // relationships, active promises, treasured items -- into one qualitative summary. No XP or
    // levels; it just reads what the run already accumulated.
    private CapabilitySummaryView BuildCapabilitySummary()
    {
        var echoes = _state.Echoes.Records.Count;
        var bonds = _state.Bonds.Bonds.Count;
        var promises = _state.PromiseLedger.Promises.Count;
        var treasured = _state.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
            ? inventory.TreasuredItems.Count
            : 0;
        var summary =
            $"Portfolio: {echoes} echo(es), {bonds} bond(s), {promises} promise(s), {treasured} treasured item(s).";
        return new CapabilitySummaryView(echoes, bonds, promises, treasured, summary);
    }

    // Measured economy (Phase 2.4): reads the sorcerer's gold and sellable stacks (a source measure)
    // and the paid services offered by providers present in the zone (the recurring sinks within
    // reach), so the working buy/sell/service economy is legible without adding any new system.
    private EconomySummaryView BuildEconomySummary()
    {
        var gold = 0;
        var sellable = 0;
        if (_state.ControlledEntity.TryGet<InventoryComponent>(out var inventory))
        {
            gold = inventory.Items.TryGetValue("gold", out var onHand) ? onHand : 0;
            sellable = inventory.Items.Count(pair => pair.Value > 0
                && !pair.Key.Equals("gold", StringComparison.OrdinalIgnoreCase)
                && !inventory.TreasuredItems.Contains(pair.Key));
        }

        var paidOffers = _state.Entities.Values
            .Where(entity => entity.Has<ServiceComponent>())
            .SelectMany(entity => entity.Get<ServiceComponent>().Offers)
            .Where(offer => offer.Revealed && offer.GoldCost > 0)
            .ToArray();
        var servicesInReach = paidOffers.Length;
        var serviceGold = paidOffers.Sum(offer => offer.GoldCost);

        var summary =
            $"Purse: {gold} gold, {sellable} sellable stack(s); {servicesInReach} paid service(s) in reach "
            + $"totaling {serviceGold} gold.";
        return new EconomySummaryView(gold, sellable, servicesInReach, serviceGold, summary);
    }

    // Debug projection of the one visibility policy: who could currently witness the controlled
    // body (as both actor and deed effect at its own tile), and how. Debug-only -- player-facing
    // views stay non-omniscient.
    private IReadOnlyList<WitnessDebugCard> BuildWitnessDebugCards()
    {
        var actor = _state.ControlledEntity;
        if (!actor.TryGet<PositionComponent>(out var position))
        {
            return Array.Empty<WitnessDebugCard>();
        }

        return _perceptionSystem
            .ClassifyEffectWitnesses(position.Position, position.Position, actor)
            .Select(observation => new WitnessDebugCard(
                observation.WitnessEntityId,
                observation.WitnessSoulId,
                observation.SawActor,
                observation.SawEffect,
                observation.Classification))
            .ToArray();
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

    private IReadOnlyList<WantDebugCard> BuildWantDebugCards() =>
        _state.Entities.Values
            .Where(entity => entity.TryGet<WantComponent>(out _))
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(entity =>
            {
                var want = entity.Get<WantComponent>();
                return new WantDebugCard(
                    entity.Id.Value,
                    entity.Name,
                    want.Id,
                    want.Text,
                    want.Salience,
                    want.Status,
                    want.Stakes,
                    want.Tags.ToArray());
            })
            .ToArray();

    private IReadOnlyList<RumorDebugCard> BuildRumorDebugCards() =>
        _state.Rumors.Records
            .OrderByDescending(rumor => rumor.Salience)
            .ThenByDescending(rumor => rumor.LastTurn)
            .ThenBy(rumor => rumor.Id, StringComparer.OrdinalIgnoreCase)
            .Select(rumor => new RumorDebugCard(
                rumor.Id,
                rumor.Text,
                rumor.OriginalText,
                rumor.SourceKind,
                rumor.SourceId,
                rumor.OriginRegionId,
                rumor.CurrentRegionId,
                rumor.Salience,
                rumor.Status,
                rumor.Hops,
                rumor.CreatedTurn,
                rumor.LastTurn,
                rumor.CarrierIds.ToArray(),
                rumor.Tags.ToArray(),
                rumor.DistortionHistory.ToArray()))
            .ToArray();

    private IReadOnlyList<WorldTurnDebugCard> BuildWorldTurnDebugCards() =>
        _state.WorldTurns.Records
            .TakeLast(16)
            .Select(record => new WorldTurnDebugCard(
                record.Id,
                record.Turn,
                record.Reason,
                record.Kind,
                record.SourceId,
                record.Summary,
                new Dictionary<string, object?>(record.Details, StringComparer.OrdinalIgnoreCase)))
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
        var place = _generationSystem.CurrentPlace;
        var nearest = _generationSystem.CurrentNearestSettlement;
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
            _generationSystem.CurrentAffordances.ToArray(),
            place.Kind,
            place.Settlement?.Name,
            place.District?.Name,
            place.Road?.Name,
            place.Landmark?.Name,
            place.Interior?.Name,
            nearest.Distance == 0
                ? $"{nearest.Settlement.Name} (here)"
                : $"{nearest.Settlement.Name} ({nearest.Distance} {nearest.Direction})",
            BuildCapitalApproach(region, place),
            BuildRunArc(region));
    }

    // Read-only run-arc projection (Phase 2.1): the current movement, derived furthest-progressed
    // first from ordinary facts (current region + imperial defenses vs max). A label for
    // debug/telemetry and chronicle structure -- it gates nothing. Tuning lives in docs/RUN_ARC.md.
    private RunArcView BuildRunArc(RegionDefinition region)
    {
        var empire = _state.Factions.FactionsByRole("empire_bloc").ToArray();
        var defenses = empire.Sum(faction => _state.Factions.ResourceValue(faction.Id, "defenses"));
        var maxDefenses = empire.Sum(faction => _state.Factions.ResourceValue(faction.Id, "max_defenses"));

        var movement =
            region.Id.Equals("vigovian_capital", StringComparison.OrdinalIgnoreCase) ? "reach"
            : region.Id.Equals("imperial_encounter", StringComparison.OrdinalIgnoreCase) ? "escape"
            : defenses < maxDefenses ? "war"
            : "foothold";

        var summary = movement switch
        {
            "escape" => "Escape: breaking out of imperial custody.",
            "foothold" => "Foothold: building strength on the frontier.",
            "war" => "War: the empire's defenses are being spent.",
            "reach" => "Reach: at the marble heart of the empire, the throne within reach.",
            _ => movement,
        };

        return new RunArcView(movement, region.Id, region.Name, defenses, maxDefenses, summary);
    }

    // Read-only capital-approach projection (Phase 1.2): derived entirely from the capital's own
    // district geography, the imperial defense resource, and the emperor actor. Null outside the
    // capital. No plot-access meter, no finale gate -- just legibility over ordinary facts.
    private static readonly string[] CapitalThresholdOrder = { "censor_gate", "archive_quarter", "inner_court" };

    private CapitalApproachView? BuildCapitalApproach(RegionDefinition region, WorldPlaceProfile place)
    {
        if (!region.Id.Equals("vigovian_capital", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var currentDistrictId = place.District?.Id;
        var thresholds = (region.Settlement?.Districts ?? Array.Empty<RegionDistrictDefinition>())
            .OrderBy(district => IndexOfThreshold(district.Id))
            .ThenBy(district => district.Id, StringComparer.Ordinal)
            .Select(district => new CapitalThresholdCard(
                district.Id,
                district.Name,
                district.Id.Equals(currentDistrictId, StringComparison.OrdinalIgnoreCase),
                district.Tags))
            .ToArray();

        var defenses = _state.Factions.FactionsByRole("empire_bloc")
            .Sum(faction => _state.Factions.ResourceValue(faction.Id, "defenses"));
        var emperor = _state.Entities.Values.FirstOrDefault(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("emperor", StringComparer.OrdinalIgnoreCase));
        var emperorAlive = emperor is not null
            && emperor.TryGet<ActorComponent>(out var actor)
            && actor.Alive;
        // Guard density is the imperial defense made physical (organic capital approach): spending
        // defenses -- by force or by allies at war -- thins the guard the player must get through.
        var throneGuards = _state.Entities.Values.Count(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("guard", StringComparer.OrdinalIgnoreCase)
            && entity.TryGet<ActorComponent>(out var guard)
            && guard.Alive);

        var where = place.District is null ? "at the outer approach" : $"in {place.District.Name}";
        var court = emperor is null ? "is not in view" : emperorAlive ? "still holds the Inner Court" : "has fallen";
        var guardLine = throneGuards == 0
            ? "No guard stands between you and the throne."
            : $"{throneGuards} Censorate {(throneGuards == 1 ? "guard holds" : "guards hold")} the approach.";
        var summary = $"Vigovian Capital, {where}. Imperial defenses stand at {defenses}. {guardLine} Emperor Odran {court}.";

        return new CapitalApproachView(
            InCapital: true,
            currentDistrictId,
            thresholds,
            defenses,
            throneGuards,
            emperor is not null,
            emperorAlive,
            summary);
    }

    private static int IndexOfThreshold(string districtId)
    {
        var index = Array.FindIndex(
            CapitalThresholdOrder,
            id => id.Equals(districtId, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : int.MaxValue;
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
            tags.Distinct().OrderBy(tag => tag).ToArray(),
            BuildContextActions(entity, position));
    }

    private IReadOnlyList<ContextActionCard> BuildContextActions(Entity entity, GridPoint position)
    {
        var actions = new List<ContextActionCard>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var actorPosition = _state.ControlledEntity.Get<PositionComponent>().Position;
        var isControlled = entity.Id == _state.ControlledEntityId;

        void Add(
            string id,
            string label,
            string command,
            int range,
            string presentation = "execute")
        {
            if (!seen.Add(id))
            {
                return;
            }

            var reachable = GameEngine.StepDistance(actorPosition, position) <= range;
            actions.Add(new ContextActionCard(
                id,
                label,
                command,
                reachable,
                reachable ? null : $"Too far away; move within {range}.",
                presentation));
        }

        if (!isControlled)
        {
            Add("target", "Target", $"target {position.X} {position.Y}", int.MaxValue);
            Add("examine", "Inspect", $"examine {entity.Id.Value}", 2);
        }

        if (entity.TryGet<ActorComponent>(out var actor) && actor.Alive && !isControlled)
        {
            Add("talk", "Talk...", $"talk {entity.Name}, ", 2, presentation: "compose");
            Add("bonds", "Bonds", $"bonds {entity.Id.Value}", 2);
            Add("recruit", "Recruit", $"recruit {entity.Id.Value}", 2);
            Add("give", "Give...", $"give  to {entity.Name}", 2, presentation: "compose");
            Add("possess", "Possess", $"possess {entity.Id.Value}", 1);
            var anchoredObligation = entity.TryGet<PromiseAnchorComponent>(out var anchor)
                ? anchor.PromiseIds.Select(id => _state.PromiseLedger.Promises.FirstOrDefault(promise =>
                        promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase)
                        && (promise.Kind.Equals("debt", StringComparison.OrdinalIgnoreCase)
                            || promise.Kind.Equals("bargain", StringComparison.OrdinalIgnoreCase))
                        && promise.Status is not "cleared" and not "fulfilled" and not "breached"))
                    .FirstOrDefault(promise => promise is not null)
                : null;
            if (anchoredObligation?.BargainOffer is { } offer)
            {
                Add("terms", "Review Terms", $"bargains {anchoredObligation.Id}", 2);
                Add(
                    "settle",
                    offer.Options.Count == 1 ? $"Accept {offer.Options[0].Label}" : "Choose Terms...",
                    offer.Options.Count == 1
                        ? $"settle {entity.Id.Value} with {offer.Options[0].Id}"
                        : $"settle {entity.Id.Value} with ",
                    2,
                    presentation: offer.Options.Count == 1 ? "execute" : "compose");
            }
            else if (anchoredObligation is not null)
            {
                Add("bargain", "Ask for Terms", $"bargain {entity.Id.Value}", 2);
            }
            else
            {
                Add("bargain", "Bargain", $"bargain {entity.Id.Value}", 2);
            }

            Add("intimidate", "Intimidate", $"intimidate {entity.Id.Value}", 4);
            Add("concede", "Concede", $"concede {entity.Id.Value}", 5);
            Add("exchange", "Exchange...", $"exchange  for  with {entity.Id.Value}", 5, presentation: "compose");
            Add("counter", "Counter Intent...", $"counter {entity.Id.Value} with ", 3, presentation: "compose");
        }

        if (entity.Has<DoorComponent>())
        {
            Add("open", "Open", $"open {entity.Id.Value}", 1);
        }

        if (entity.Has<InteriorEntranceComponent>())
        {
            Add("breach", "Force Entry", $"breach {entity.Id.Value}", 2);
        }

        if (entity.Has<ItemComponent>())
        {
            Add("pickup", "Pick Up", $"pickup {entity.Id.Value}", 1);
        }

        if (entity.Has<ReadableComponent>())
        {
            Add("read", "Read", $"read {entity.Id.Value}", 1);
        }

        if (entity.Has<MerchantComponent>())
        {
            Add("wares", "Wares", $"wares {entity.Id.Value}", 2);
        }

        if (entity.Has<ServiceComponent>())
        {
            Add("services", "Services", $"services {entity.Id.Value}", 2);
        }

        if (entity.TryGet<InteractableComponent>(out var interactable))
        {
            foreach (var verb in interactable.Verbs)
            {
                AddInteractableVerb(verb);
            }
        }

        return actions;

        void AddInteractableVerb(string verb)
        {
            var normalized = verb.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "talk":
                    Add("talk", "Talk...", $"talk {entity.Name}, ", 2, presentation: "compose");
                    break;
                case "open":
                    Add("open", "Open", $"open {entity.Id.Value}", 1);
                    break;
                case "read":
                    Add("read", "Read", $"read {entity.Id.Value}", 1);
                    break;
                case "pickup":
                case "get":
                    Add("pickup", "Pick Up", $"pickup {entity.Id.Value}", 1);
                    break;
                case "wares":
                case "browse":
                    Add("wares", "Wares", $"wares {entity.Id.Value}", 2);
                    break;
                case "services":
                    Add("services", "Services", $"services {entity.Id.Value}", 2);
                    break;
                case "examine":
                case "inspect":
                    Add("examine", "Inspect", $"examine {entity.Id.Value}", 2);
                    break;
                case "enter":
                    var entranceLabel = entity.TryGet<InteriorEntranceComponent>(out var entrance)
                        ? $"Enter {entrance.Name}"
                        : "Enter";
                    Add("enter", entranceLabel, $"enter {entity.Id.Value}", 1);
                    break;
                case "leave":
                    Add("leave", "Leave", "leave", 1);
                    break;
            }
        }
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

    /// <summary>
    /// The controlled soul's reliable magic — known charter forms and, when the experiment is
    /// on, the run's echo grimoire — so renderers can offer them as affordances instead of
    /// leaving them behind the typed `charter`/`echo` verbs.
    /// </summary>
    public RepertoireCard BuildRepertoire()
    {
        var body = _state.ControlledEntity;
        var soulId = body.TryGet<SoulComponent>(out var soul) ? soul.SoulId : body.Id.Value;
        var spells = _state.Souls.KnownCharterSpellsFor(soulId)
            .Select(id => CharterSpellbook.Default.Find(id))
            .OfType<CharterSpell>()
            .Select(spell => new CharterSpellCard(
                spell.Id,
                spell.Name,
                spell.Summary,
                spell.CostText,
                spell.Targeting))
            .ToArray();
        var echoesEnabled = GameSession.EchoesEnabledFor(_state);
        var echoes = echoesEnabled
            ? _state.Echoes.ForSoul(soulId)
                // Index is 1-based to match `echo <n>`; the next cast's fatigue surcharge
                // equals the current TimesCast (see GameSession.CastEcho).
                .Select((record, index) => new EchoCard(index + 1, record.Name, record.TimesCast, record.TimesCast))
                .ToArray()
            : Array.Empty<EchoCard>();
        return new RepertoireCard(spells, echoesEnabled, echoes);
    }

    private static ResolverLensView BuildResolverLens(
        Entity caster,
        SoulRecord soulRecord,
        RegionDefinition region,
        RealmProfile realm,
        int imperialPresence,
        IReadOnlyList<RegionAffordanceCard> affordances,
        WorldPlaceProfile place,
        bool compact = false)
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
        if (!compact && magnitudeDelta != 0)
        {
            notes.Add(magnitude);
        }

        var volatility = composure <= 2
            ? "low composure: wild magic answers more chaotically; costs and backfires can be stranger"
            : composure >= 5
                ? "high composure: wild magic answers cleanly with fewer messy flourishes"
                : "unchanged";
        if (!compact && !volatility.Equals("unchanged", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(volatility);
        }

        var costFraming = bodyStats.Vigor <= 2
            ? "low vigor: avoid leaning on raw HP or bodily punishment as the default cost"
            : bodyStats.Vigor >= 5
                ? "high vigor: physical costs are more plausible if the spell needs a price"
                : "unchanged";
        if (!compact && !costFraming.Equals("unchanged", StringComparison.OrdinalIgnoreCase))
        {
            notes.Add(costFraming);
        }

        if (!compact && !string.IsNullOrWhiteSpace(soulRecord.MagicalSignature))
        {
            notes.Add($"signature lens: {soulRecord.MagicalSignature}");
        }

        notes.Add(compact
            ? $"place: {place.DisplayName} in {region.Name}; {place.Kind}; voice: {region.VoiceSummary}"
            : $"region lens: {region.Name} in {realm.Name}; realm status {realm.Status}; ruler {realm.Ruler}; tradition {region.TraditionId}; wildness {region.WildnessBase}; imperial presence {imperialPresence}");
        if (!compact)
        {
            notes.Add(
                $"place lens: {place.DisplayName}; kind {place.Kind}; {place.Summary}; tags {string.Join(", ", place.Tags)}");
        }
        if (!compact && !string.IsNullOrWhiteSpace(region.VoiceSummary))
        {
            notes.Add($"region voice: {region.VoiceSummary}");
        }

        foreach (var affordance in compact ? Array.Empty<RegionAffordanceCard>() : affordances)
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

    private const int MagicContextEntityCap = 12;
    private const int MagicContextHiddenEntityCap = 4;
    private const int MagicContextTerrainCap = 16;
    private const int MagicContextPromiseCap = 4;
    private const int MagicContextReagentCap = 4;
    private const int MagicContextRoutedSceneryCap = 6;
    private const int MagicContextSceneryCap = 10;

    /// <summary>
    /// The resolver's view of a promise: what it says, about what, and how it might trigger.
    /// Eligibility-debug and provenance fields stay null so the provider wire format (which omits
    /// nulls) drops them; the player-facing GameView keeps the full card via
    /// <see cref="ToPromiseCard"/>. See docs/OPTIMIZATION_PLAN.md WS2.4.
    /// </summary>
    private static PromiseCard ToResolverPromiseCard(WorldPromise promise) =>
        new(
            promise.Id,
            promise.Kind,
            promise.Status,
            promise.Text,
            promise.PlayerVisible,
            promise.Source,
            promise.Subject,
            promise.ClaimedPlace,
            BoundTargetId: promise.BoundTargetId,
            TriggerHint: promise.TriggerHint,
            CostProfileId: promise.CostProfileId,
            Stacks: promise.Stacks);

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
            promise.RealizedIn,
            promise.LastEligibilityFailure,
            promise.LastEligibilityContext,
            promise.LastEligibilityTurn,
            promise.SourceClaimId,
            promise.SourceSpeakerId,
            promise.SourceListenerSoulId,
            promise.SourceConfidence,
            promise.CostProfileId,
            promise.Stacks);

    private static ClaimCard ToClaimCard(ClaimRecord claim) =>
        new(
            claim.Id,
            claim.Source,
            claim.SpeakerId,
            claim.Text,
            claim.Category,
            claim.Subject,
            claim.Salience,
            claim.Confidence,
            claim.Status,
            claim.PlayerVisible,
            claim.Tags,
            claim.BoundPromiseId,
            claim.AppliedTo);

    private static RumorCard ToRumorCard(RumorRecord rumor) =>
        new(
            rumor.Id,
            rumor.Text,
            rumor.SourceKind,
            rumor.SourceId,
            rumor.OriginRegionId,
            rumor.CurrentRegionId,
            rumor.Salience,
            rumor.Hops,
            rumor.Status,
            rumor.OriginalText,
            rumor.CreatedTurn,
            rumor.LastTurn,
            rumor.Tags,
            rumor.DistortionHistory);

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
