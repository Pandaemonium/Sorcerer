using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed record PromiseRealizationContext(
    string Trigger,
    string RegionId,
    string? ZoneId = null,
    string? Direction = null,
    string? AnchorEntityId = null,
    GridPoint? PlacementOrigin = null)
{
    public static PromiseRealizationContext Travel(
        string zoneId,
        string regionId,
        Direction direction,
        GridPoint placementOrigin) =>
        new("travel", regionId, ZoneId: zoneId, Direction: direction.ToString(), AnchorEntityId: null, PlacementOrigin: placementOrigin);

    public static PromiseRealizationContext Anchored(
        string trigger,
        string regionId,
        string anchorEntityId) =>
        new(trigger, regionId, ZoneId: null, Direction: null, AnchorEntityId: anchorEntityId);

    public static PromiseRealizationContext Ambient(
        string trigger,
        string regionId,
        string zoneId,
        string anchorEntityId,
        GridPoint placementOrigin) =>
        new(trigger, regionId, ZoneId: zoneId, Direction: null, AnchorEntityId: anchorEntityId, PlacementOrigin: placementOrigin);
}

public sealed partial class PromiseRealizationSystem
{
    // A bound "threat" promise never expires to control volume (always-honor), but it also
    // doesn't need to realize the instant it's eligible. This cooldown paces threat
    // realization globally across the whole world (not per-region) so threats don't cluster
    // one travel after another; a threat promise on cooldown simply isn't selected this
    // travel and remains fully bound/eligible until the cooldown lapses.
    private const int ThreatRealizationCooldownTurns = 10;
    private const string ThreatRealizationCooldownKind = "threat_realized";
    private const string ThreatRealizationCooldownSourceId = "global";

    private readonly GameState _state;
    private readonly GameEngine? _engine;
    private readonly Func<WorldConsequence, WorldConsequenceApplyResult>? _applyConsequence;
    private readonly IReadOnlyDictionary<string, TravelPromiseHandler> _travelHandlers;
    private readonly IReadOnlyDictionary<string, AnchoredPromiseHandler> _anchoredHandlers;
    private readonly RegionRegistry _regions;

    public PromiseRealizationSystem(
        GameState state,
        GameEngine? engine = null,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null,
        RegionRegistry? regions = null)
    {
        _state = state;
        _engine = engine;
        _applyConsequence = applyConsequence;
        _regions = regions ?? RegionCatalog.LoadDefault();
        _travelHandlers = CreateTravelHandlers();
        _anchoredHandlers = CreateAnchoredHandlers();
    }

    public IReadOnlyList<string> RealizeTravelPromises(
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin,
        Direction direction)
    {
        if (zoneId.Equals("0,0", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var context = PromiseRealizationContext.Travel(zoneId, region.Id, direction, placementOrigin);
        RecordTravelEligibilityFailures(context, deltas);
        var ranked = SelectTravelPromises(context);
        var selected = DiversifyByKind(ranked, 2);
        var selectedIds = new HashSet<string>(
            selected.Select(candidate => candidate.Promise.Id),
            StringComparer.OrdinalIgnoreCase);
        foreach (var skipped in ranked.Where(candidate => !selectedIds.Contains(candidate.Promise.Id)))
        {
            RecordPromiseEligibilityFailure(skipped.Promise, context, "kind_diversity_budgeted_out", deltas);
        }

        var realizedIds = new List<string>();
        foreach (var candidate in selected)
        {
            var plan = BuildTravelPlan(candidate, context, zoneId);
            deltas.Add(PromiseRealizationPlanDelta(plan));
            if (PreflightTravelPlan(plan, entities, placementOrigin) is { } preflightFailure)
            {
                RecordPromiseEligibilityFailure(plan.Promise, context, preflightFailure, deltas);
                continue;
            }

            var snapshot = GameStateSnapshot.Capture(_state);
            var entitySnapshot = CloneEntityMap(entities);
            var deltaStart = deltas.Count;
            var outcome = ApplyTravelRealization(plan, plan.Promise, zoneId, region, entities, deltas, placementOrigin);
            if (!outcome.Applied)
            {
                RollBackPromiseRealization(snapshot, entities, entitySnapshot, deltas, deltaStart);
                deltas.AddRange(FailureDiagnostics(outcome.Deltas));
                deltas.Add(PromiseRealizationSkippedDelta(plan, outcome.Deltas, outcome.Failure ?? "handler_rejected"));
                RecordPromiseEligibilityFailure(plan.Promise, context, outcome.Failure ?? "handler_rejected", deltas);
                continue;
            }

            var realized = UpdatePromiseStatus(plan.Promise, "realized", plan.RealizedIn, plan.Context.Trigger, deltas);
            if (realized is null)
            {
                var failedDeltas = deltas.Skip(deltaStart).ToArray();
                RollBackPromiseRealization(snapshot, entities, entitySnapshot, deltas, deltaStart);
                deltas.AddRange(FailureDiagnostics(failedDeltas));
                deltas.Add(PromiseRealizationSkippedDelta(plan, failedDeltas, "promise_status_rejected"));
                RecordPromiseEligibilityFailure(plan.Promise, context, "promise_status_rejected", deltas);
                continue;
            }

            deltas.Add(RealizePromiseDelta(realized, plan));
            realizedIds.Add(realized.Id);
        }

        return realizedIds;
    }

    private IReadOnlyList<ScoredPromise> SelectTravelPromises(PromiseRealizationContext context) =>
        _state.PromiseLedger.Promises
            .Where(promise => IsTravelPromise(promise, context))
            .Select(promise => new ScoredPromise(promise, TravelPromiseScore(promise, context)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Promise.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    /// <summary>
    /// Picks the top-scored candidate of each distinct realization kind first, so one travel
    /// never realizes two of the same kind (e.g. two "threat" promises) just because both
    /// happened to outscore everything else. Only falls back to a second candidate of an
    /// already-picked kind if there aren't enough distinct kinds to fill the budget. A
    /// same-kind candidate that loses out here is not dropped -- it stays bound and fully
    /// eligible for a later travel; see the "kind_diversity_budgeted_out" eligibility record
    /// this produces at the call site.
    /// </summary>
    private static IReadOnlyList<ScoredPromise> DiversifyByKind(IReadOnlyList<ScoredPromise> ranked, int budget)
    {
        var picked = new List<ScoredPromise>();
        var pickedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var usedKinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in ranked)
        {
            if (picked.Count >= budget)
            {
                break;
            }

            var kind = NormalizeToken(candidate.Promise.RealizationKind ?? candidate.Promise.Kind);
            if (usedKinds.Add(kind))
            {
                picked.Add(candidate);
                pickedIds.Add(candidate.Promise.Id);
            }
        }

        if (picked.Count < budget)
        {
            foreach (var candidate in ranked)
            {
                if (picked.Count >= budget)
                {
                    break;
                }

                if (pickedIds.Add(candidate.Promise.Id))
                {
                    picked.Add(candidate);
                }
            }
        }

        return picked;
    }

    public IReadOnlyList<StateDelta> RealizeAnchoredPromises(
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages = null,
        int budget = 2)
    {
        if (!anchor.TryGet<PromiseAnchorComponent>(out var promiseAnchor))
        {
            return Array.Empty<StateDelta>();
        }

        if (budget <= 0)
        {
            return Array.Empty<StateDelta>();
        }

        var context = PromiseRealizationContext.Anchored(trigger, _state.RegionId, anchor.Id.Value);
        var deltas = new List<StateDelta>();
        var candidates = SelectAnchoredPromises(anchor, promiseAnchor, context);
        foreach (var skipped in candidates.Skip(budget))
        {
            RecordPromiseEligibilityFailure(skipped.Promise, context, "budgeted_out", deltas);
        }

        foreach (var candidate in candidates.Take(budget).ToArray())
        {
            var plan = BuildAnchoredPlan(candidate, context, anchor);
            deltas.Add(PromiseRealizationPlanDelta(plan));
            if (PreflightAnchoredPlan(plan, anchor) is { } preflightFailure)
            {
                RecordPromiseEligibilityFailure(plan.Promise, context, preflightFailure, deltas);
                continue;
            }

            var snapshot = GameStateSnapshot.Capture(_state);
            var messageStart = messages.Count;
            var persistedStart = alreadyPersistedMessages?.Count;
            var deltaStart = deltas.Count;
            var outcome = ApplyAnchoredRealization(plan, plan.Promise, anchor, trigger, messages, alreadyPersistedMessages);
            deltas.AddRange(outcome.Deltas);
            if (!outcome.Applied)
            {
                var failedDeltas = deltas.Skip(deltaStart).ToArray();
                RollBackAnchoredPromiseRealization(
                    snapshot,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    alreadyPersistedMessages,
                    persistedStart);
                deltas.AddRange(FailureDiagnostics(failedDeltas));
                deltas.Add(PromiseRealizationSkippedDelta(plan, outcome.Deltas, outcome.Failure ?? "handler_rejected"));
                RecordPromiseEligibilityFailure(plan.Promise, context, outcome.Failure ?? "handler_rejected", deltas);
                continue;
            }

            var realized = UpdatePromiseStatus(plan.Promise, "realized", plan.RealizedIn, trigger, deltas);
            if (realized is null)
            {
                var failedDeltas = deltas.Skip(deltaStart).ToArray();
                RollBackAnchoredPromiseRealization(
                    snapshot,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    alreadyPersistedMessages,
                    persistedStart);
                deltas.AddRange(FailureDiagnostics(failedDeltas));
                deltas.Add(PromiseRealizationSkippedDelta(plan, failedDeltas, "promise_status_rejected"));
                RecordPromiseEligibilityFailure(plan.Promise, context, "promise_status_rejected", deltas);
                continue;
            }

            var message = $"A promise stirs awake: {realized.Text}";
            deltas.AddRange(AddVisiblePromiseMessage(
                realized,
                anchor,
                trigger,
                message,
                "promiseAwakened",
                messages,
                alreadyPersistedMessages,
                ("realizedIn", plan.RealizedIn)));
            deltas.Add(RealizePromiseDelta(realized, plan));
        }

        return deltas;
    }

    private IReadOnlyList<ScoredPromise> SelectAnchoredPromises(
        Entity anchor,
        PromiseAnchorComponent promiseAnchor,
        PromiseRealizationContext context) =>
        promiseAnchor.PromiseIds
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(id => _state.PromiseLedger.Promises.FirstOrDefault(promise =>
                promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
            .Where(promise => promise is not null)
            .Cast<WorldPromise>()
            .Where(promise => IsAnchoredPromise(promise, context))
            .Select(promise => new ScoredPromise(promise, AnchoredPromiseScore(promise, anchor, context)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Promise.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<StateDelta> RealizeAmbientPromises(
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages = null,
        int budget = 1)
    {
        if (budget <= 0)
        {
            return Array.Empty<StateDelta>();
        }

        var anchor = _state.ControlledEntity;
        var origin = anchor.TryGet<PositionComponent>(out var position)
            ? position.Position
            : new GridPoint(_state.Width / 2, _state.Height / 2);
        var context = PromiseRealizationContext.Ambient(
            trigger,
            _state.RegionId,
            _state.CurrentZoneId,
            anchor.Id.Value,
            origin);
        var deltas = new List<StateDelta>();
        RecordAmbientEligibilityFailures(context, deltas);
        foreach (var candidate in SelectAmbientPromises(context).Take(budget).ToArray())
        {
            var plan = BuildAmbientPlan(candidate, context, anchor);
            deltas.Add(PromiseRealizationPlanDelta(plan));
            if (PreflightAnchoredPlan(plan, anchor) is { } preflightFailure)
            {
                RecordPromiseEligibilityFailure(plan.Promise, context, preflightFailure, deltas);
                continue;
            }

            var snapshot = GameStateSnapshot.Capture(_state);
            var messageStart = messages.Count;
            var persistedStart = alreadyPersistedMessages?.Count;
            var deltaStart = deltas.Count;
            var outcome = ApplyAnchoredRealization(plan, plan.Promise, anchor, trigger, messages, alreadyPersistedMessages);
            deltas.AddRange(outcome.Deltas);
            if (!outcome.Applied)
            {
                var failedDeltas = deltas.Skip(deltaStart).ToArray();
                RollBackAnchoredPromiseRealization(
                    snapshot,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    alreadyPersistedMessages,
                    persistedStart);
                deltas.AddRange(FailureDiagnostics(failedDeltas));
                deltas.Add(PromiseRealizationSkippedDelta(plan, outcome.Deltas, outcome.Failure ?? "handler_rejected"));
                RecordPromiseEligibilityFailure(plan.Promise, context, outcome.Failure ?? "handler_rejected", deltas);
                continue;
            }

            var realized = UpdatePromiseStatus(plan.Promise, "realized", plan.RealizedIn, trigger, deltas);
            if (realized is null)
            {
                var failedDeltas = deltas.Skip(deltaStart).ToArray();
                RollBackAnchoredPromiseRealization(
                    snapshot,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    alreadyPersistedMessages,
                    persistedStart);
                deltas.AddRange(FailureDiagnostics(failedDeltas));
                deltas.Add(PromiseRealizationSkippedDelta(plan, failedDeltas, "promise_status_rejected"));
                RecordPromiseEligibilityFailure(plan.Promise, context, "promise_status_rejected", deltas);
                continue;
            }

            deltas.Add(RealizePromiseDelta(realized, plan));
        }

        return deltas;
    }

    private WorldPromise? UpdatePromiseStatus(
        WorldPromise promise,
        string status,
        string realizedIn,
        string trigger,
        List<StateDelta> deltas)
    {
        var applied = ApplyConsequence(WorldConsequence.UpdatePromise(
            $"promise:{promise.Id}:{trigger}",
            promise.Id,
            status: status,
            realizedIn: realizedIn,
            operation: "promiseStatus",
            clearEligibilityFailure: true,
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["realizationTrigger"] = trigger,
            }));
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            return null;
        }

        return _state.PromiseLedger.Promises.FirstOrDefault(item =>
            item.Id.Equals(promise.Id, StringComparison.OrdinalIgnoreCase));
    }

    private void RecordTravelEligibilityFailures(
        PromiseRealizationContext context,
        List<StateDelta> deltas)
    {
        foreach (var promise in _state.PromiseLedger.Promises
            .Where(promise => promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase))
            .Where(promise => PromiseTriggerMatches(promise.TriggerHint, "travel"))
            .ToArray())
        {
            if (TravelEligibilityFailure(promise, context) is { } failure)
            {
                RecordPromiseEligibilityFailure(promise, context, failure, deltas);
            }
        }
    }

    private void RecordAmbientEligibilityFailures(
        PromiseRealizationContext context,
        List<StateDelta> deltas)
    {
        foreach (var promise in _state.PromiseLedger.Promises
            .Where(promise => promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase))
            .Where(promise => !string.IsNullOrWhiteSpace(promise.TriggerHint))
            .Where(promise => AmbientTriggerMatches(promise.TriggerHint, context.Trigger))
            .ToArray())
        {
            if (!IsAmbientBuildableKind(promise))
            {
                RecordPromiseEligibilityFailure(promise, context, "unsupported_realization_kind", deltas);
            }
        }
    }

    private void RecordPromiseEligibilityFailure(
        WorldPromise promise,
        PromiseRealizationContext context,
        string failure,
        List<StateDelta> deltas)
    {
        var contextLabel = PromiseContextLabel(context);
        if (promise.LastEligibilityFailure?.Equals(failure, StringComparison.OrdinalIgnoreCase) == true
            && promise.LastEligibilityContext?.Equals(contextLabel, StringComparison.OrdinalIgnoreCase) == true
            && promise.LastEligibilityTurn == _state.Turn)
        {
            return;
        }

        var applied = ApplyConsequence(WorldConsequence.UpdatePromise(
            $"promise:{promise.Id}:eligibility",
            promise.Id,
            lastEligibilityFailure: failure,
            lastEligibilityContext: contextLabel,
            lastEligibilityTurn: _state.Turn,
            evidence: promise.Text,
            reason: "Promise eligibility was checked at a realization apply point.",
            operation: "promiseEligibility",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = context.Trigger,
                ["contextRegionId"] = context.RegionId,
                ["contextZoneId"] = context.ZoneId,
                ["contextDirection"] = context.Direction,
                ["anchorEntityId"] = context.AnchorEntityId,
                ["failure"] = failure,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
        deltas.AddRange(applied.Deltas);
    }

    private IReadOnlyList<ScoredPromise> SelectAmbientPromises(PromiseRealizationContext context) =>
        _state.PromiseLedger.Promises
            .Where(promise => IsAmbientPromise(promise, context))
            .Select(promise => new ScoredPromise(promise, AmbientPromiseScore(promise, context)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Promise.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private PromiseRealizationPlan BuildTravelPlan(
        ScoredPromise candidate,
        PromiseRealizationContext context,
        string zoneId)
    {
        var handler = TravelHandlerFor(candidate.Promise);
        return new PromiseRealizationPlan(
            candidate.Promise,
            context,
            Target: zoneId,
            RealizedIn: zoneId,
            Handler: handler,
            SelectionScore: candidate.Score,
            SelectionReasons: SelectionReasons(candidate.Promise, context, handler, candidate.Score, zoneId));
    }

    private PromiseRealizationPlan BuildAnchoredPlan(
        ScoredPromise candidate,
        PromiseRealizationContext context,
        Entity anchor)
    {
        var handler = AnchoredHandlerFor(candidate.Promise, anchor, context.Trigger);
        var realizedIn = $"{context.Trigger}:{anchor.Id.Value}";
        return new PromiseRealizationPlan(
            candidate.Promise,
            context,
            Target: anchor.Id.Value,
            RealizedIn: realizedIn,
            Handler: handler,
            SelectionScore: candidate.Score,
            SelectionReasons: SelectionReasons(candidate.Promise, context, handler, candidate.Score, anchor.Id.Value));
    }

    private PromiseRealizationPlan BuildAmbientPlan(
        ScoredPromise candidate,
        PromiseRealizationContext context,
        Entity anchor)
    {
        var handler = AnchoredHandlerFor(candidate.Promise, anchor, context.Trigger);
        var realizedIn = $"{context.Trigger}:{_state.CurrentZoneId}:{_state.Turn}";
        return new PromiseRealizationPlan(
            candidate.Promise,
            context,
            Target: anchor.Id.Value,
            RealizedIn: realizedIn,
            Handler: handler,
            SelectionScore: candidate.Score,
            SelectionReasons: SelectionReasons(candidate.Promise, context, handler, candidate.Score, anchor.Id.Value));
    }

    private IReadOnlyDictionary<string, TravelPromiseHandler> CreateTravelHandlers() =>
        new Dictionary<string, TravelPromiseHandler>(StringComparer.OrdinalIgnoreCase)
        {
            ["site"] = new(
                "site",
                (entities, origin) => HasGeneratedOpenPointNear(entities, origin, 1, -1) ? null : "no_open_tile",
                RealizeTravelSitePromise),
            ["item"] = new(
                "item",
                (entities, origin) => HasGeneratedOpenPointNear(entities, origin, 1, 0) ? null : "no_open_tile",
                RealizeTravelItemPromise),
            ["person"] = new(
                "person",
                (entities, origin) => HasGeneratedOpenPointNear(entities, origin, 1, 1) ? null : "no_open_tile",
                RealizeTravelPersonPromise),
            ["threat"] = new(
                "threat",
                (entities, origin) => HasGeneratedOpenPointNear(entities, origin, 2, -1) ? null : "no_open_tile",
                RealizeTravelThreatPromise),
            ["merchant_stock"] = new(
                "merchant_stock",
                (entities, origin) => HasGeneratedOpenPointNear(entities, origin, 1, 0) ? null : "no_open_tile",
                RealizeTravelMerchantStockPromise),
            ["service"] = new(
                "service",
                (entities, origin) => HasGeneratedOpenPointNear(entities, origin, 1, 1) ? null : "no_open_tile",
                RealizeTravelServicePromise),
            ["escape_route"] = new(
                "escape_route",
                (_, _) => null,
                RealizeTravelRoutePromise),
        };

    private IReadOnlyDictionary<string, AnchoredPromiseHandler> CreateAnchoredHandlers() =>
        new Dictionary<string, AnchoredPromiseHandler>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory"] = new("memory", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredMemory(promise, anchor, trigger, messages, persisted)),
            ["threat"] = new("threat", (_, anchor) => PreflightOpenAdjacentPayoffAnchor(anchor), (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredThreat(promise, anchor, trigger, messages, persisted)),
            ["item"] = new("item", (_, anchor) => PreflightOpenAdjacentPayoffAnchor(anchor), (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredItem(promise, anchor, trigger, messages, persisted)),
            ["merchant_stock"] = new("merchant_stock", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredMerchantStock(promise, anchor, trigger, messages, persisted)),
            ["service"] = new("service", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredService(promise, anchor, trigger, messages, persisted)),
            ["door_rule"] = new("door_rule", (_, anchor) => anchor.Has<DoorComponent>() ? null : "anchor_not_door", (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredDoorRule(promise, anchor, trigger, messages, persisted)),
            ["escape_route"] = new("escape_route", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredRoute(promise, anchor, trigger, messages, persisted)),
            ["quest"] = new("quest", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredCanon(promise, anchor, trigger, messages, persisted, "quest", "A quest takes shape")),
            ["site"] = new("site", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredCanon(promise, anchor, trigger, messages, persisted, "site", "A distant place answers")),
            ["omen"] = new("omen", (_, _) => null, (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredCanon(promise, anchor, trigger, messages, persisted, "omen", "The omen settles into the world")),
        };

    private string? PreflightTravelPlan(
        PromiseRealizationPlan plan,
        IReadOnlyDictionary<EntityId, Entity> entities,
        GridPoint placementOrigin)
    {
        return _travelHandlers.TryGetValue(plan.Handler, out var handler)
            ? handler.Preflight(entities, placementOrigin)
            : "unregistered_realization_handler";
    }

    private string? PreflightAnchoredPlan(PromiseRealizationPlan plan, Entity anchor)
    {
        return _anchoredHandlers.TryGetValue(plan.Handler, out var handler)
            ? handler.Preflight(plan, anchor)
            : "unregistered_realization_handler";
    }

    private PromiseRealizationOutcome ApplyTravelRealization(
        PromiseRealizationPlan plan,
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        if (!_travelHandlers.TryGetValue(plan.Handler, out var handler))
        {
            var skipped = new StateDelta(
                "promiseConsequenceSkipped",
                promise.Id,
                $"No registered travel promise handler for {plan.Handler}.",
                RealizationDetails(promise, plan));
            deltas.Add(skipped);
            return PromiseRealizationOutcome.Rejected("unregistered_realization_handler", new[] { skipped });
        }

        var deltaStart = deltas.Count;
        try
        {
            handler.Apply(promise, zoneId, region, entities, deltas, placementOrigin);
        }
        catch (InvalidOperationException ex)
        {
            var skipped = new StateDelta(
                "promiseConsequenceSkipped",
                promise.Id,
                ex.Message,
                RealizationDetails(promise, plan));
            deltas.Add(skipped);
        }

        var produced = deltas.Skip(deltaStart).ToArray();
        return FailureReason(produced) is { } failure
            ? PromiseRealizationOutcome.Rejected(failure, produced)
            : PromiseRealizationOutcome.Success(produced);
    }

    private PromiseRealizationOutcome ApplyAnchoredRealization(
        PromiseRealizationPlan plan,
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        if (!_anchoredHandlers.TryGetValue(plan.Handler, out var handler))
        {
            var skipped = new StateDelta(
                "promiseConsequenceSkipped",
                promise.Id,
                $"No registered anchored promise handler for {plan.Handler}.",
                RealizationDetails(promise, plan));
            return PromiseRealizationOutcome.Rejected("unregistered_realization_handler", new[] { skipped });
        }

        var messageStart = messages.Count;
        var persistedStart = alreadyPersistedMessages?.Count;
        var produced = handler.Apply(plan, promise, anchor, trigger, messages, alreadyPersistedMessages).ToArray();
        if (FailureReason(produced) is not { } failure)
        {
            return PromiseRealizationOutcome.Success(produced);
        }

        RemoveRangeFrom(messages, messageStart);
        if (alreadyPersistedMessages is not null && persistedStart is not null)
        {
            RemoveRangeFrom(alreadyPersistedMessages, persistedStart.Value);
        }

        return PromiseRealizationOutcome.Rejected(failure, produced);
    }

    private static string? FailureReason(IReadOnlyList<StateDelta> deltas)
    {
        var failure = deltas.FirstOrDefault(delta =>
            delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase)
            || delta.Operation.Equals("promiseConsequenceSkipped", StringComparison.OrdinalIgnoreCase));
        if (failure is null)
        {
            return null;
        }

        return failure.Details.TryGetValue("error", out var error) && error is not null
            ? NormalizeToken(error.ToString() ?? "handler_rejected")
            : NormalizeToken(failure.Summary);
    }

    private static void RemoveRangeFrom(List<string> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
    }

    private static void RemoveRangeFrom(List<StateDelta> values, int start)
    {
        if (values.Count > start)
        {
            values.RemoveRange(start, values.Count - start);
        }
    }

    private void RollBackPromiseRealization(
        GameStateSnapshot snapshot,
        Dictionary<EntityId, Entity> entities,
        IReadOnlyDictionary<EntityId, Entity> entitySnapshot,
        List<StateDelta> deltas,
        int deltaStart)
    {
        snapshot.Restore(_state);
        RestoreEntityMap(entities, entitySnapshot);
        RemoveRangeFrom(deltas, deltaStart);
    }

    private void RollBackAnchoredPromiseRealization(
        GameStateSnapshot snapshot,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        List<string>? alreadyPersistedMessages,
        int? persistedStart)
    {
        snapshot.Restore(_state);
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        if (alreadyPersistedMessages is not null && persistedStart is not null)
        {
            RemoveRangeFrom(alreadyPersistedMessages, persistedStart.Value);
        }
    }

    private static IReadOnlyDictionary<EntityId, Entity> CloneEntityMap(IReadOnlyDictionary<EntityId, Entity> entities) =>
        entities.ToDictionary(pair => pair.Key, pair => pair.Value.Clone());

    private static void RestoreEntityMap(
        Dictionary<EntityId, Entity> entities,
        IReadOnlyDictionary<EntityId, Entity> snapshot)
    {
        entities.Clear();
        foreach (var pair in snapshot)
        {
            entities[pair.Key] = pair.Value.Clone();
        }
    }

    private static IReadOnlyList<StateDelta> FailureDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas
            .Where(delta =>
                delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase)
                || delta.Operation.Equals("promiseConsequenceSkipped", StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private static StateDelta PromiseRealizationSkippedDelta(
        PromiseRealizationPlan plan,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        var errors = FailureDiagnostics(failedDeltas)
            .Select(delta => delta.Details.TryGetValue("error", out var error) && error is not null
                ? error.ToString()
                : delta.Summary)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var details = RealizationDetails(plan.Promise, plan);
        details["failure"] = failure;
        details["rejectedCount"] = errors.Length;
        details["errors"] = errors;
        details["auditOnly"] = true;
        details["playerVisible"] = false;
        return new StateDelta(
            "promiseRealizationSkipped",
            plan.Promise.Id,
            $"Promise realization rolled back: {failure}.",
            details);
    }

    private WorldConsequenceApplyResult ApplyConsequence(WorldConsequence consequence) =>
        _applyConsequence is not null
            ? _applyConsequence(consequence)
            : _engine is not null
                ? _engine.ApplyConsequence(consequence)
                : WorldConsequenceGuard.ApplyWithNewApplier(_state, consequence);

    private delegate string? TravelPromisePreflight(
        IReadOnlyDictionary<EntityId, Entity> entities,
        GridPoint placementOrigin);

    private delegate void TravelPromiseApply(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin);

    private delegate string? AnchoredPromisePreflight(
        PromiseRealizationPlan plan,
        Entity anchor);

    private delegate IReadOnlyList<StateDelta> AnchoredPromiseApply(
        PromiseRealizationPlan plan,
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages);

    private sealed record TravelPromiseHandler(
        string Id,
        TravelPromisePreflight Preflight,
        TravelPromiseApply Apply);

    private sealed record AnchoredPromiseHandler(
        string Id,
        AnchoredPromisePreflight Preflight,
        AnchoredPromiseApply Apply);

    private sealed record PromiseRealizationOutcome(
        bool Applied,
        string? Failure,
        IReadOnlyList<StateDelta> Deltas)
    {
        public static PromiseRealizationOutcome Success(IReadOnlyList<StateDelta> deltas) =>
            new(true, null, deltas);

        public static PromiseRealizationOutcome Rejected(string failure, IReadOnlyList<StateDelta> deltas) =>
            new(false, string.IsNullOrWhiteSpace(failure) ? "handler_rejected" : failure, deltas);
    }

    private sealed record PromiseRealizationPlan(
        WorldPromise Promise,
        PromiseRealizationContext Context,
        string Target,
        string RealizedIn,
        string Handler,
        int SelectionScore,
        IReadOnlyList<string> SelectionReasons);

    private sealed record ScoredPromise(WorldPromise Promise, int Score);
}
