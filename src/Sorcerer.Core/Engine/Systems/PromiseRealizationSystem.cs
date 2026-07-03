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

public sealed class PromiseRealizationSystem
{
    private readonly GameState _state;
    private readonly GameEngine? _engine;
    private readonly Func<WorldConsequence, WorldConsequenceApplyResult>? _applyConsequence;
    private readonly IReadOnlyDictionary<string, TravelPromiseHandler> _travelHandlers;
    private readonly IReadOnlyDictionary<string, AnchoredPromiseHandler> _anchoredHandlers;

    public PromiseRealizationSystem(
        GameState state,
        GameEngine? engine = null,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        _state = state;
        _engine = engine;
        _applyConsequence = applyConsequence;
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
        var realizedIds = new List<string>();
        foreach (var candidate in SelectTravelPromises(context).Take(2).ToArray())
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
            ["threat"] = new("threat", (_, anchor) => HasOpenAdjacentPayoffTile(anchor) ? null : "no_open_adjacent_tile", (_, promise, anchor, trigger, messages, persisted) =>
                RealizeAnchoredThreat(promise, anchor, trigger, messages, persisted)),
            ["item"] = new("item", (_, anchor) => HasOpenAdjacentPayoffTile(anchor) ? null : "no_open_adjacent_tile", (_, promise, anchor, trigger, messages, persisted) =>
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

    private void RealizeTravelSitePromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, -1);
        var tags = PromiseTags(promise, "site", region);
        var siteName = PromiseSiteName(promise, region);
        var site = ApplyGeneratedSpawnFixture(
            WorldConsequence.SpawnFixture(
                $"promise:{promise.Id}:travel",
                siteName,
                position.X,
                position.Y,
                prefix: "promise_site",
                glyph: '?',
                palette: "promise",
                fixtureType: "promise_site",
                material: region.TerrainTags.FirstOrDefault() ?? "stone",
                tags: tags,
                blocksMovement: true,
                description: promise.Text,
                promiseIds: new[] { promise.Id },
                visibility: WorldConsequenceVisibility.Message,
                evidence: promise.Text,
                operation: "promiseSite",
                emitMessage: false,
                message: $"A promised place takes shape: {siteName}.",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realizationKind"] = "site",
                }),
            entities,
            deltas);
        AppendPromiseCanon("site", site.Id.Value, promise, $"{site.Name}: {promise.Text}", tags, "travel", deltas);
    }

    private void RealizeTravelItemPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var itemName = PromiseItemName(promise);
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 0);
        var tags = PromiseTags(promise, "item", region)
            .Concat(new[] { "item" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var item = ApplyGeneratedSpawnItem(
            WorldConsequence.SpawnItem(
                $"promise:{promise.Id}:travel",
                itemName,
                position.X,
                position.Y,
                prefix: "promise_item",
                itemType: NormalizeToken(itemName),
                material: "promise",
                tags: tags,
                stackPolicy: "unique",
                description: $"This object exists because a claim became reachable: {promise.Text}",
                promiseIds: new[] { promise.Id },
                visibility: WorldConsequenceVisibility.Message,
                evidence: promise.Text,
                operation: "promiseItem",
                emitMessage: false,
                message: $"A promised object is waiting: {itemName}.",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realizationKind"] = "item",
                }),
            entities,
            deltas);
        AppendPromiseCanon("item", item.Id.Value, promise, $"{item.Name}: {promise.Text}", tags, "travel", deltas);
    }

    private void RealizeTravelPersonPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 1);
        var tags = PromiseTags(promise, "person", region)
            .Concat(new[] { "npc" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var personName = PromisePersonName(promise);
        var person = ApplyGeneratedSpawnEntity(
            WorldConsequence.SpawnEntity(
                $"promise:{promise.Id}:travel",
                personName,
                position.X,
                position.Y,
                prefix: "promise_person",
                glyph: 'p',
                faction: "neutral",
                hp: 6,
                attack: 1,
                tags: tags,
                material: "flesh",
                roles: new[] { "promise", "resident" },
                controllerKind: "ai",
                aiPolicyId: "resident",
                summoned: false,
                description: promise.Text,
                promiseIds: new[] { promise.Id },
                interactableVerbs: new[] { "talk", "give", "recruit" },
                bodyVigor: 3,
                includeMemory: true,
                visibility: WorldConsequenceVisibility.Message,
                evidence: promise.Text,
                operation: "promisePerson",
                emitMessage: false,
                message: $"A promised person is here: {personName}.",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realizationKind"] = "person",
                    ["profileName"] = personName,
                    ["profileAppearance"] = promise.Text,
                    ["wantId"] = PromiseWantId(promise, "person"),
                    ["wantText"] = $"Find out whether the promise that named them can become help, leverage, or danger: {promise.Text}",
                    ["wantSalience"] = Math.Clamp(promise.Salience, 2, 5),
                    ["wantStakes"] = "This meeting can become trust, trouble, or a new lead depending on how the sorcerer treats them.",
                    ["wantTags"] = PromiseWantTags(promise, "person"),
                }),
            entities,
            deltas);
        AppendPromiseCanon("person", person.Id.Value, promise, $"{person.Name}: {promise.Text}", tags, "travel", deltas);
    }

    private void RealizeTravelThreatPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 2, -1);
        var tags = PromiseTags(promise, "threat", region);
        var threatName = PromiseThreatName(promise);
        var threat = ApplyGeneratedSpawnEntity(
            WorldConsequence.SpawnEntity(
                $"promise:{promise.Id}:travel",
                threatName,
                position.X,
                position.Y,
                prefix: "promise_threat",
                glyph: 'D',
                faction: PromiseThreatFaction(promise),
                hp: 8,
                attack: 3,
                tags: tags,
                material: "flesh",
                roles: new[] { "promise", "threat" },
                controllerKind: "ai",
                aiPolicyId: "hostile",
                summoned: false,
                description: promise.Text,
                promiseIds: new[] { promise.Id },
                interactableVerbs: new[] { "talk", "examine" },
                bodyVigor: 3,
                includeMemory: true,
                visibility: WorldConsequenceVisibility.Message,
                evidence: promise.Text,
                operation: "promiseThreat",
                emitMessage: false,
                message: $"A promised threat steps into the road: {threatName}.",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realizationKind"] = "threat",
                }),
            entities,
            deltas);
        AppendPromiseCanon("threat", threat.Id.Value, promise, $"{threat.Name}: {promise.Text}", tags, "travel", deltas);
    }

    private void RealizeTravelMerchantStockPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 0);
        var tags = PromiseTags(promise, "merchant_stock", region)
            .Concat(new[] { "npc", "merchant" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var itemName = PromiseItemName(promise);
        var merchantName = PromiseMerchantName(promise);
        var detached = DetachedGeneratedState(entities);
        var transactionDeltas = new List<StateDelta>();
        var spawnConsequence = WorldConsequence.SpawnEntity(
            $"promise:{promise.Id}:travel",
            merchantName,
            position.X,
            position.Y,
            prefix: "promise_merchant",
            glyph: 'p',
            faction: "neutral",
            hp: 6,
            attack: 1,
            tags: tags,
            description: promise.Text,
            roles: new[] { "promise", "merchant" },
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            promiseIds: new[] { promise.Id },
            interactableVerbs: new[] { "talk", "give" },
            includeMemory: true,
            visibility: WorldConsequenceVisibility.Message,
            evidence: promise.Text,
            operation: "promiseMerchant",
            emitMessage: false,
            message: $"A promised merchant is here: {merchantName}.",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["zoneId"] = zoneId,
                ["regionId"] = region.Id,
                ["realizationKind"] = "merchant_stock",
                ["profileName"] = merchantName,
                ["profileAppearance"] = promise.Text,
                ["wantId"] = PromiseWantId(promise, "merchant"),
                ["wantText"] = $"Complete a quiet exchange around the promised stock without drawing imperial attention: {promise.Text}",
                ["wantSalience"] = Math.Clamp(promise.Salience, 2, 5),
                ["wantStakes"] = "A useful trade could build trust; a loud or coercive one could make the merchant vanish or talk.",
                ["wantTags"] = PromiseWantTags(promise, "merchant"),
            });
        if (TryApplyGeneratedEntityConsequence(detached, spawnConsequence, transactionDeltas, deltas, "spawn merchant") is not { } merchant)
        {
            return;
        }

        var offerConsequence = WorldConsequence.OfferTrade(
            $"promise:{promise.Id}:travel",
            merchant.Id.Value,
            itemName,
            quantity: 1,
            gold: 30,
            visibility: WorldConsequenceVisibility.Message,
            evidence: promise.Text,
            operation: "promiseMerchantStock",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["zoneId"] = zoneId,
                ["regionId"] = region.Id,
                ["realizationKind"] = "merchant_stock",
            });
        if (!TryApplyGeneratedConsequence(detached, offerConsequence, transactionDeltas, deltas, "offer trade"))
        {
            return;
        }

        if (!TryApplyGeneratedCanon(
            detached,
            promise,
            kind: "merchant_stock",
            subjectId: merchant.Id.Value,
            summary: $"{merchant.Name}: {promise.Text}",
            tags,
            trigger: "travel",
            transactionDeltas,
            deltas,
            out var canonId))
        {
            return;
        }

        CommitGeneratedTransaction(detached, entities, transactionDeltas, deltas);
        AddCanonIdToLastDelta(deltas, canonId);
    }

    private void RealizeTravelServicePromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 1);
        var tags = PromiseTags(promise, "service", region)
            .Concat(new[] { "npc", "service_provider", "folk_magic" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var serviceName = PromiseServiceName(promise);
        var providerName = PromiseServiceProviderName(promise);
        var detached = DetachedGeneratedState(entities);
        var transactionDeltas = new List<StateDelta>();
        var spawnConsequence = WorldConsequence.SpawnEntity(
            $"promise:{promise.Id}:travel",
            providerName,
            position.X,
            position.Y,
            prefix: "promise_service",
            glyph: 'p',
            faction: "neutral",
            hp: 6,
            attack: 1,
            tags: tags,
            description: promise.Text,
            roles: new[] { "promise", "service_provider" },
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            promiseIds: new[] { promise.Id },
            interactableVerbs: new[] { "talk", "give" },
            includeMemory: true,
            visibility: WorldConsequenceVisibility.Message,
            evidence: promise.Text,
            operation: "promiseServiceProvider",
            emitMessage: false,
            message: $"A promised folk-practitioner is here: {providerName}.",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["zoneId"] = zoneId,
                ["regionId"] = region.Id,
                ["realizationKind"] = "service",
                ["profileName"] = providerName,
                ["profileAppearance"] = promise.Text,
                ["wantId"] = PromiseWantId(promise, "service_provider"),
                ["wantText"] = $"Practice folk-magic carefully enough to help without giving Vigovia a name to execute: {promise.Text}",
                ["wantSalience"] = Math.Clamp(promise.Salience, 2, 5),
                ["wantStakes"] = "Helping may deepen trust, but careless attention could make the provider a target.",
                ["wantTags"] = PromiseWantTags(promise, "service_provider"),
            });
        if (TryApplyGeneratedEntityConsequence(detached, spawnConsequence, transactionDeltas, deltas, "spawn service provider") is not { } provider)
        {
            return;
        }

        var offerConsequence = WorldConsequence.OfferService(
            $"promise:{promise.Id}:travel",
            provider.Id.Value,
            NormalizeToken(serviceName),
            serviceName,
            promise.Text,
            PromiseServiceEffect(promise),
            goldCost: 0,
            targetHint: serviceName,
            tags: BasicPromiseTags(promise, "service"),
            wantStatusOnComplete: "satisfied",
            wantStakesOnComplete: "The promised service was performed; later consequences can turn on trust, attention, or repayment.",
            wantAddTagsOnComplete: new[] { "satisfied_by_player", "service_completed" },
            visibility: WorldConsequenceVisibility.Message,
            evidence: promise.Text,
            operation: "promiseService",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["zoneId"] = zoneId,
                ["regionId"] = region.Id,
                ["realizationKind"] = "service",
            });
        if (!TryApplyGeneratedConsequence(detached, offerConsequence, transactionDeltas, deltas, "offer service"))
        {
            return;
        }

        if (!TryApplyGeneratedCanon(
            detached,
            promise,
            kind: "service",
            subjectId: provider.Id.Value,
            summary: $"{provider.Name}: {promise.Text}",
            tags,
            trigger: "travel",
            transactionDeltas,
            deltas,
            out var canonId))
        {
            return;
        }

        CommitGeneratedTransaction(detached, entities, transactionDeltas, deltas);
        AddCanonIdToLastDelta(deltas, canonId);
    }

    private void RealizeTravelRoutePromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 0, 1);
        var tags = PromiseTags(promise, "escape_route", region)
            .Concat(new[] { "route", "hidden_exit" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var routeName = PromiseRouteName(promise);
        var route = ApplyGeneratedCreateRoute(
            WorldConsequence.CreateRoute(
                $"promise:{promise.Id}:travel",
                zoneId,
                routeName,
                promise.Text,
                "escape_route",
                tags: tags,
                promiseIds: new[] { promise.Id },
                material: "passage",
                visibility: WorldConsequenceVisibility.Message,
                evidence: promise.Text,
                operation: "promiseRoute",
                message: $"A promised route becomes visible: {routeName}.",
                details: new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["zoneId"] = zoneId,
                    ["regionId"] = region.Id,
                    ["realizationKind"] = "escape_route",
                    ["x"] = position.X,
                    ["y"] = position.Y,
                }),
            entities,
            deltas);
        AppendPromiseCanon("escape_route", route.Id.Value, promise, $"{route.Name}: {promise.Text}", tags, "travel", deltas);
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredMemory(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var salience = Math.Max(2, promise.Salience + 1);
        var message = $"{anchor.Name} remembers something that was not there before.";
        var applied = ApplyConsequence(WorldConsequence.RecordMemory(
            promise.Id,
            anchor.Id.Value,
            promise.Text,
            $"promise:{promise.Id}:{trigger}",
            salience,
            shareable: true,
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            operation: "promiseMemory",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["anchor"] = anchor.Id.Value,
                ["trigger"] = trigger,
                ["summary"] = message,
            }));
        return applied.Deltas
            .Concat(AddVisiblePromiseMessage(
                promise,
                anchor,
                trigger,
                message,
                "promiseMemoryMessage",
                messages,
                alreadyPersistedMessages,
                ("salience", salience)))
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredThreat(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : _state.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin)
            ?? FindOpenAdjacent(_state.ControlledEntity.Get<PositionComponent>().Position)
            ?? origin;
        var threatName = PromiseThreatName(promise);
        var message = $"{threatName} arrives to collect on the promise.";
        var messageDeltas = AddVisiblePromiseMessage(
            promise,
            anchor,
            trigger,
            message,
            "promiseThreatMessage",
            messages,
            alreadyPersistedMessages,
            ("x", position.X),
            ("y", position.Y));
        var applied = ApplyConsequence(WorldConsequence.SpawnEntity(
            $"promise:{promise.Id}:{trigger}",
            threatName,
            position.X,
            position.Y,
            prefix: "promise_threat",
            glyph: 'D',
            faction: PromiseThreatFaction(promise),
            hp: 8,
            attack: 3,
            tags: BasicPromiseTags(promise, "threat"),
            material: "flesh",
            roles: new[] { "promise", "threat" },
            controllerKind: "ai",
            aiPolicyId: "hostile",
            summoned: false,
            description: promise.Text,
            promiseIds: new[] { promise.Id },
            interactableVerbs: new[] { "talk", "examine" },
            bodyVigor: 3,
            includeMemory: true,
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            operation: "promiseThreat",
            emitMessage: false,
            message: message));
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        var canon = ApplyPromiseCanon(
            "threat",
            applied.TargetId ?? anchor.Id.Value,
            promise,
            $"{threatName}: {promise.Text}",
            BasicPromiseTags(promise, "threat"),
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(messageDeltas)
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredItem(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : _state.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin) ?? origin;
        var itemName = PromiseItemName(promise);
        var message = $"{itemName} appears where the promise can reach it.";
        var messageDeltas = AddVisiblePromiseMessage(
            promise,
            anchor,
            trigger,
            message,
            "promiseItemMessage",
            messages,
            alreadyPersistedMessages,
            ("x", position.X),
            ("y", position.Y));
        var tags = BasicPromiseTags(promise, "item")
            .Concat(new[] { "item" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var applied = ApplyConsequence(WorldConsequence.SpawnItem(
            $"promise:{promise.Id}:{trigger}",
            itemName,
            position.X,
            position.Y,
            prefix: "promise_item",
            itemType: NormalizeToken(itemName),
            material: "promise",
            tags: tags,
            stackPolicy: "unique",
            description: $"This object exists because a promise became concrete: {promise.Text}",
            promiseIds: new[] { promise.Id },
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            operation: "promiseItem",
            emitMessage: false,
            message: message));
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        var canon = ApplyPromiseCanon(
            "item",
            applied.TargetId ?? anchor.Id.Value,
            promise,
            $"{itemName}: {promise.Text}",
            tags,
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(messageDeltas)
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredMerchantStock(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var sellTrigger = NormalizeToken(trigger) == "sell";
        var itemName = sellTrigger ? null : PromiseItemName(promise);
        var commerceSubject = PromiseCommerceSubject(promise);
        var message = sellTrigger
            ? $"{anchor.Name} is ready to buy {commerceSubject}."
            : $"{anchor.Name} produces the promised stock: {itemName}.";
        var applied = ApplyConsequence(WorldConsequence.OfferTrade(
            $"promise:{promise.Id}:{trigger}",
            anchor.Id.Value,
            itemName,
            quantity: sellTrigger ? 0 : 1,
            gold: anchor.TryGet<MerchantComponent>(out var merchant) ? merchant.Gold : 30,
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            reason: "A merchant-stock promise realized through an explicit commerce interaction.",
            operation: "promiseMerchantStock",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = trigger,
                ["realizationKind"] = "merchant_stock",
                ["itemName"] = itemName,
                ["commerceSubject"] = commerceSubject,
            }));
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        var canon = ApplyPromiseCanon(
            "merchant_stock",
            anchor.Id.Value,
            promise,
            $"{anchor.Name}: {promise.Text}",
            BasicPromiseTags(promise, "merchant_stock"),
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(AddVisiblePromiseMessage(
                promise,
                anchor,
                trigger,
                message,
                "promiseMerchantStockMessage",
                messages,
                alreadyPersistedMessages,
                ("itemName", itemName),
                ("commerceSubject", commerceSubject),
                ("realizationKind", "merchant_stock")))
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredService(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var serviceName = PromiseServiceName(promise);
        var effectKind = PromiseServiceEffect(promise);
        var serviceId = NormalizeToken(serviceName);
        var itemCost = PromiseServiceItemCost(promise);
        var message = $"{anchor.Name} reveals the promised service: {serviceName}.";
        var applied = ApplyConsequence(WorldConsequence.OfferService(
            $"promise:{promise.Id}:{trigger}",
            anchor.Id.Value,
            serviceId,
            serviceName,
            $"A promised service made concrete: {promise.Text}",
            effectKind,
            itemCost: itemCost,
            targetHint: PromiseServiceTargetHint(promise, serviceName),
            tags: BasicPromiseTags(promise, "service").Concat(new[] { "service", "folk_magic" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            wantStatusOnComplete: "satisfied",
            wantStakesOnComplete: "The promised service was performed; later consequences can turn on trust, attention, or repayment.",
            wantAddTagsOnComplete: new[] { "satisfied_by_player", "service_completed" },
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            reason: "A service promise realized through an explicit service interaction.",
            operation: "promiseService",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = trigger,
                ["realizationKind"] = "service",
                ["serviceId"] = serviceId,
                ["serviceName"] = serviceName,
                ["effectKind"] = effectKind,
                ["itemCost"] = itemCost,
            }));
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        var canon = ApplyPromiseCanon(
            "service",
            anchor.Id.Value,
            promise,
            $"{anchor.Name}: {promise.Text}",
            BasicPromiseTags(promise, "service"),
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(AddVisiblePromiseMessage(
                promise,
                anchor,
                trigger,
                message,
                "promiseServiceMessage",
                messages,
                alreadyPersistedMessages,
                ("serviceId", serviceId),
                ("serviceName", serviceName),
                ("effectKind", effectKind),
                ("itemCost", itemCost),
                ("realizationKind", "service")))
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredDoorRule(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var message = $"{anchor.Name} obeys the promised door rule and opens.";
        var applied = ApplyConsequence(WorldConsequence.OpenOrUnlock(
            $"promise:{promise.Id}:{trigger}",
            anchor.Id.Value,
            actorId: null,
            unlock: true,
            open: true,
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            reason: "A door-rule promise realized through an explicit door interaction.",
            operation: "promiseDoorRule",
            emitMessage: false,
            message: message,
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = trigger,
                ["realizationKind"] = "door_rule",
            }));
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        messages.AddRange(applied.Messages);
        alreadyPersistedMessages?.AddRange(applied.Messages);
        var canon = ApplyPromiseCanon(
            "door_rule",
            anchor.Id.Value,
            promise,
            $"{anchor.Name}: {promise.Text}",
            BasicPromiseTags(promise, "door_rule"),
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(AddVisiblePromiseMessage(
                promise,
                anchor,
                trigger,
                message,
                "promiseDoorRuleMessage",
                messages,
                alreadyPersistedMessages,
                ("doorId", anchor.Id.Value),
                ("realizationKind", "door_rule")))
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredRoute(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages)
    {
        var tags = BasicPromiseTags(promise, "escape_route")
            .Concat(new[] { "route", "hidden_exit" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var routeName = PromiseRouteName(promise);
        var message = $"A promised route becomes visible: {routeName}.";
        var applied = ApplyConsequence(WorldConsequence.CreateRoute(
            $"promise:{promise.Id}:{trigger}",
            anchor.Id.Value,
            routeName,
            promise.Text,
            "escape_route",
            tags: tags,
            promiseIds: new[] { promise.Id },
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            operation: "promiseRoute",
            message: message,
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = trigger,
                ["realizationKind"] = "escape_route",
            }));
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        var canon = ApplyPromiseCanon(
            "escape_route",
            applied.TargetId!,
            promise,
            $"{routeName}: {promise.Text}",
            tags,
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(AddVisiblePromiseMessage(
                promise,
                anchor,
                trigger,
                message,
                "promiseRouteMessage",
                messages,
                alreadyPersistedMessages,
                ("routeId", applied.TargetId),
                ("realizationKind", "escape_route")))
            .ToArray();
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredCanon(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        List<string>? alreadyPersistedMessages,
        string canonKind,
        string messagePrefix)
    {
        var message = $"{messagePrefix}: {promise.Text}";
        var applied = ApplyPromiseCanon(
            canonKind,
            anchor.Id.Value,
            promise,
            message,
            new[] { "promise", promise.Kind, canonKind },
            trigger,
            new Dictionary<string, object?>
            {
                ["anchor"] = anchor.Id.Value,
                ["kind"] = canonKind,
            });
        if (!applied.Applied)
        {
            return applied.Deltas;
        }

        return applied.Deltas
            .Concat(AddVisiblePromiseMessage(
                promise,
                anchor,
                trigger,
                message,
                "promiseCanonMessage",
                messages,
                alreadyPersistedMessages,
                ("kind", canonKind)))
            .ToArray();
    }

    private WorldConsequenceApplyResult ApplyPromiseCanon(
        string kind,
        string subjectId,
        WorldPromise promise,
        string summary,
        IReadOnlyList<string> tags,
        string trigger,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var payloadDetails = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        payloadDetails["promiseId"] = promise.Id;
        payloadDetails["trigger"] = trigger;
        payloadDetails["realizationKind"] = kind;
        var applied = ApplyConsequence(WorldConsequence.AddCanon(
            $"promise:{promise.Id}:{trigger}",
            kind,
            subjectId,
            promise.Text,
            summary,
            tags,
            evidence: promise.Text,
            operation: "promiseCanon",
            details: payloadDetails));
        if (!applied.Applied)
        {
            return applied;
        }

        if (string.IsNullOrWhiteSpace(applied.TargetId))
        {
            var skipped = new StateDelta(
                "promiseConsequenceSkipped",
                promise.Id,
                "Promise canon consequence did not produce a canon record.",
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["trigger"] = trigger,
                    ["realizationKind"] = kind,
                    ["consequenceType"] = WorldConsequenceTypes.AddCanon,
                    ["auditOnly"] = true,
                    ["playerVisible"] = false,
                });
            return new WorldConsequenceApplyResult(
                false,
                promise.Id,
                "canon_missing_target",
                Array.Empty<string>(),
                applied.Deltas.Concat(new[] { skipped }).ToArray(),
                skipped.Details);
        }

        return applied with
        {
            Deltas = AddCanonIdToDeltas(applied.Deltas, applied.TargetId),
        };
    }

    private IReadOnlyList<StateDelta> AddVisiblePromiseMessage(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        string message,
        string operation,
        List<string> messages,
        List<string>? alreadyPersistedMessages,
        params (string Key, object? Value)[] fields)
    {
        var details = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["promiseId"] = promise.Id,
            ["anchor"] = anchor.Id.Value,
            ["trigger"] = trigger,
            ["realizationKind"] = promise.RealizationKind ?? promise.Kind,
        };
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        var applied = ApplyConsequence(WorldConsequence.Message(
            $"promise:{promise.Id}:{trigger}",
            message,
            targetEntityId: anchor.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: anchor.Id.Value,
            evidence: promise.Text,
            operation: operation,
            details: details));
        messages.AddRange(applied.Messages);
        if (alreadyPersistedMessages is not null)
        {
            alreadyPersistedMessages.AddRange(applied.Messages);
        }

        return applied.Deltas;
    }

    private Entity ApplyGeneratedSpawnItem(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas) =>
        ApplyGeneratedEntityConsequence(consequence, entities, deltas, "spawn item");

    private Entity ApplyGeneratedSpawnFixture(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas) =>
        ApplyGeneratedEntityConsequence(consequence, entities, deltas, "spawn fixture");

    private Entity ApplyGeneratedSpawnEntity(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas) =>
        ApplyGeneratedEntityConsequence(consequence, entities, deltas, "spawn entity");

    private void ApplyGeneratedOfferTrade(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas)
    {
        ApplyGeneratedConsequence(consequence, entities, deltas, "offer trade");
    }

    private void ApplyGeneratedOfferService(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas)
    {
        ApplyGeneratedConsequence(consequence, entities, deltas, "offer service");
    }

    private Entity ApplyGeneratedCreateRoute(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas) =>
        ApplyGeneratedEntityConsequence(consequence, entities, deltas, "create route");

    private Entity ApplyGeneratedEntityConsequence(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        string label)
    {
        var applied = ApplyGeneratedConsequence(consequence, entities, deltas, label);
        if (applied.Applied
            && !string.IsNullOrWhiteSpace(applied.TargetId)
            && entities.TryGetValue(EntityId.Create(applied.TargetId), out var entity))
        {
            return entity;
        }

        var reason = applied.Error ?? $"Generated {label} consequence did not produce an entity.";
        throw new InvalidOperationException(reason);
    }

    private WorldConsequenceApplyResult ApplyGeneratedConsequence(
        WorldConsequence consequence,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        string label)
    {
        var detached = DetachedGeneratedState(entities);
        var applied = ApplyGeneratedConsequenceToDetached(detached, consequence);
        deltas.AddRange(applied.Deltas);
        if (!applied.Applied)
        {
            AddGeneratedConsequenceSkipped(consequence, deltas, applied.Error ?? $"Generated {label} consequence was rejected.");
            return applied;
        }

        CommitGeneratedState(detached, entities);
        return applied;
    }

    private Entity? TryApplyGeneratedEntityConsequence(
        GameState detached,
        WorldConsequence consequence,
        List<StateDelta> transactionDeltas,
        List<StateDelta> deltas,
        string label)
    {
        var applied = ApplyGeneratedConsequenceToDetached(detached, consequence);
        if (!applied.Applied)
        {
            AddFailedGeneratedConsequence(consequence, applied, deltas, $"Generated {label} consequence was rejected.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(applied.TargetId)
            || !detached.Entities.TryGetValue(EntityId.Create(applied.TargetId), out var entity))
        {
            AddGeneratedConsequenceSkipped(consequence, deltas, $"Generated {label} consequence did not produce an entity.");
            return null;
        }

        transactionDeltas.AddRange(applied.Deltas);
        return entity;
    }

    private bool TryApplyGeneratedConsequence(
        GameState detached,
        WorldConsequence consequence,
        List<StateDelta> transactionDeltas,
        List<StateDelta> deltas,
        string label)
    {
        var applied = ApplyGeneratedConsequenceToDetached(detached, consequence);
        if (!applied.Applied)
        {
            AddFailedGeneratedConsequence(consequence, applied, deltas, $"Generated {label} consequence was rejected.");
            return false;
        }

        transactionDeltas.AddRange(applied.Deltas);
        return true;
    }

    private bool TryApplyGeneratedCanon(
        GameState detached,
        WorldPromise promise,
        string kind,
        string subjectId,
        string summary,
        IReadOnlyList<string> tags,
        string trigger,
        List<StateDelta> transactionDeltas,
        List<StateDelta> deltas,
        out string canonId)
    {
        canonId = "";
        var consequence = WorldConsequence.AddCanon(
            $"promise:{promise.Id}:{trigger}",
            kind,
            subjectId,
            promise.Text,
            summary,
            tags,
            evidence: promise.Text,
            operation: "promiseCanon",
            details: new Dictionary<string, object?>
            {
                ["promiseId"] = promise.Id,
                ["trigger"] = trigger,
                ["realizationKind"] = kind,
            });
        var applied = ApplyGeneratedConsequenceToDetached(detached, consequence);
        if (!applied.Applied)
        {
            AddFailedGeneratedConsequence(
                consequence,
                applied,
                deltas,
                "Generated canon consequence was rejected.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(applied.TargetId))
        {
            AddGeneratedConsequenceSkipped(
                consequence,
                deltas,
                "Generated canon consequence did not produce a canon record.");
            return false;
        }

        canonId = applied.TargetId;
        transactionDeltas.AddRange(applied.Deltas);
        return true;
    }

    private void CommitGeneratedTransaction(
        GameState detached,
        Dictionary<EntityId, Entity> entities,
        IReadOnlyList<StateDelta> transactionDeltas,
        List<StateDelta> deltas)
    {
        CommitGeneratedState(detached, entities);
        deltas.AddRange(transactionDeltas);
    }

    private static WorldConsequenceApplyResult ApplyGeneratedConsequenceToDetached(
        GameState detached,
        WorldConsequence consequence) =>
        WorldConsequenceGuard.ApplyWithNewApplier(detached, consequence);

    private static void AddFailedGeneratedConsequence(
        WorldConsequence consequence,
        WorldConsequenceApplyResult applied,
        List<StateDelta> deltas,
        string fallbackReason)
    {
        deltas.AddRange(applied.Deltas);
        AddGeneratedConsequenceSkipped(consequence, deltas, applied.Error ?? fallbackReason);
    }

    private GameState DetachedGeneratedState(IReadOnlyDictionary<EntityId, Entity> entities)
    {
        var detached = new GameState(_state.Width, _state.Height);
        GameStateSnapshot.Capture(_state).Restore(detached);
        detached.Entities.Clear();
        foreach (var pair in entities)
        {
            detached.Entities[pair.Key] = pair.Value.Clone();
        }

        return detached;
    }

    private void CommitGeneratedState(GameState detached, Dictionary<EntityId, Entity> entities)
    {
        entities.Clear();
        foreach (var pair in detached.Entities)
        {
            entities[pair.Key] = pair.Value.Clone();
        }

        CommitGeneratedGlobalState(detached);
    }

    private void CommitGeneratedGlobalState(GameState detached)
    {
        _state.RunStatus = detached.RunStatus;
        _state.RunConclusion = detached.RunConclusion;
        _state.NextEntitySerial = detached.NextEntitySerial;
        _state.Rng = new DeterministicRng(detached.Rng.State);
        _state.BackgroundSettings = detached.BackgroundSettings;

        _state.Messages.Clear();
        _state.Messages.AddRange(detached.Messages);
        _state.Souls.ReplaceAll(detached.Souls.Snapshot());
        _state.Deeds.ReplaceAll(detached.Deeds.Records, detached.Deeds.AppliedSnapshot());
        _state.Factions.ReplaceAll(detached.Factions.Snapshot());
        _state.Legend.ReplaceAll(detached.Legend.Snapshot());
        _state.Memories.ReplaceAll(detached.Memories.Snapshot());
        _state.Claims.ReplaceAll(detached.Claims.Snapshot());
        _state.Rumors.ReplaceAll(detached.Rumors.Snapshot());
        _state.WorldTurns.ReplaceAll(detached.WorldTurns.Snapshot());
        _state.PromiseLedger.ReplaceAll(detached.PromiseLedger.Snapshot());
        _state.ScheduledEvents.ReplaceAll(detached.ScheduledEvents.Snapshot());
        _state.Triggers.ReplaceAll(detached.Triggers.Snapshot());
        _state.Suspicions.ReplaceAll(detached.Suspicions.Snapshot());
        _state.Canon.ReplaceAll(detached.Canon.Snapshot());
        _state.Bonds.ReplaceAll(detached.Bonds.Snapshot());
        _state.PersistentEffects.ReplaceAll(detached.PersistentEffects.Snapshot());
        _state.WorldFlags.Clear();
        foreach (var pair in detached.WorldFlags)
        {
            _state.WorldFlags[pair.Key] = pair.Value;
        }

        _state.BackgroundJobs.ReplaceAll(detached.BackgroundJobs.Snapshot());
    }

    private static void AddGeneratedConsequenceSkipped(
        WorldConsequence consequence,
        List<StateDelta> deltas,
        string reason)
    {
        deltas.Add(new StateDelta(
            "promiseConsequenceSkipped",
            consequence.TargetEntityId ?? "",
            reason,
            ConsequenceDetails(consequence, ("skipReason", reason))));
    }

    private void AppendPromiseCanon(
        string kind,
        string subjectId,
        WorldPromise promise,
        string summary,
        IReadOnlyList<string> tags,
        string trigger,
        List<StateDelta> deltas) =>
        deltas.AddRange(ApplyPromiseCanon(kind, subjectId, promise, summary, tags, trigger).Deltas);

    private static void AddCanonIdToLastDelta(List<StateDelta> deltas, string canonId)
    {
        if (deltas.Count == 0)
        {
            return;
        }

        var last = deltas[^1];
        var details = new Dictionary<string, object?>(last.Details, StringComparer.OrdinalIgnoreCase)
        {
            ["canonId"] = canonId,
        };
        deltas[^1] = new StateDelta(last.Operation, last.Target, last.Summary, details);
    }

    private static IReadOnlyList<StateDelta> AddCanonIdToDeltas(IReadOnlyList<StateDelta> deltas, string canonId) =>
        deltas
            .Select(delta =>
            {
                if (!delta.Operation.Equals("promiseCanon", StringComparison.OrdinalIgnoreCase))
                {
                    return delta;
                }

                var details = new Dictionary<string, object?>(delta.Details, StringComparer.OrdinalIgnoreCase)
                {
                    ["canonId"] = canonId,
                };
                return new StateDelta(delta.Operation, delta.Target, delta.Summary, details);
            })
            .ToArray();

    private static StateDelta RealizePromiseDelta(
        WorldPromise promise,
        PromiseRealizationPlan plan) =>
        new(
            "realizePromise",
            promise.Id,
            $"A promise stirs awake: {promise.Text}",
            RealizationDetails(promise, plan));

    private static StateDelta PromiseRealizationPlanDelta(PromiseRealizationPlan plan) =>
        new(
            "promiseRealizationPlan",
            plan.Promise.Id,
            $"Promise realization planned through {plan.Handler}: {plan.Promise.Text}",
            RealizationDetails(plan.Promise, plan, includeStatus: false));

    private static Dictionary<string, object?> RealizationDetails(
        WorldPromise promise,
        PromiseRealizationPlan plan,
        bool includeStatus = true)
    {
        var details = new Dictionary<string, object?>
        {
            ["trigger"] = plan.Context.Trigger,
            ["target"] = plan.Target,
            ["realizedIn"] = plan.RealizedIn,
            ["realizationKind"] = promise.RealizationKind,
            ["handler"] = plan.Handler,
            ["selectionScore"] = plan.SelectionScore,
            ["selectionReasons"] = plan.SelectionReasons.ToArray(),
            ["contextRegionId"] = plan.Context.RegionId,
            ["contextZoneId"] = plan.Context.ZoneId,
            ["contextDirection"] = plan.Context.Direction,
            ["anchorEntityId"] = plan.Context.AnchorEntityId,
            ["sourceClaimId"] = promise.SourceClaimId,
            ["sourceSpeakerId"] = promise.SourceSpeakerId,
            ["sourceListenerSoulId"] = promise.SourceListenerSoulId,
            ["sourceConfidence"] = promise.SourceConfidence,
            ["auditOnly"] = true,
            ["playerVisible"] = false,
        };
        if (includeStatus)
        {
            details["status"] = promise.Status;
        }

        if (plan.Context.PlacementOrigin is { } origin)
        {
            details["placementX"] = origin.X;
            details["placementY"] = origin.Y;
        }

        return details;
    }

    private IReadOnlyList<string> SelectionReasons(
        WorldPromise promise,
        PromiseRealizationContext context,
        string handler,
        int score,
        string target)
    {
        var reasons = new List<string>
        {
            "status:bound",
            $"handler:{handler}",
            $"salience:{Math.Clamp(promise.Salience, 1, 5)}",
            $"score:{score}",
        };

        if (TriggerHintHasExactMatch(promise.TriggerHint, context.Trigger))
        {
            reasons.Add("trigger:exact");
        }
        else if (string.IsNullOrWhiteSpace(promise.TriggerHint))
        {
            reasons.Add("trigger:broad");
        }
        else if (PromiseTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            reasons.Add("trigger:compatible");
        }

        if (!string.IsNullOrWhiteSpace(promise.BoundTargetId)
            && promise.BoundTargetId.Equals(target, StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("bound_target:matched");
        }

        if (PromiseTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection)
        {
            reasons.Add(promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase)
                ? "direction:matched"
                : "direction:soft_mismatch");
        }

        if (PlaceMatchesContext(promise.ClaimedPlace, context)
            || PlaceMatchesContext(promise.BoundPlace, context))
        {
            reasons.Add("place:matched");
        }

        if (promise.Stacks > 1)
        {
            reasons.Add($"stacks:{promise.Stacks}");
        }

        if (context.PlacementOrigin is not null)
        {
            reasons.Add("placement:available");
        }

        return reasons
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private int TravelPromiseScore(WorldPromise promise, PromiseRealizationContext context)
    {
        var score = Math.Clamp(promise.Salience, 1, 5) * 20;
        if (TriggerHintHasExactMatch(promise.TriggerHint, "travel"))
        {
            score += 18;
        }
        else if (string.IsNullOrWhiteSpace(promise.TriggerHint))
        {
            score += 6;
        }

        if (PromiseTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection
            && promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase))
        {
            score += 24;
        }

        if (PlaceMatchesContext(promise.ClaimedPlace, context)
            || PlaceMatchesContext(promise.BoundPlace, context))
        {
            score += 18;
        }

        score += NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "person" => 12,
            "merchant_stock" or "stock" or "trade" => 11,
            "service" => 11,
            "escape_route" or "route" or "door_rule" => 10,
            "site" or "town" or "landmark" => 10,
            "item" => 8,
            "threat" => 7,
            "quest" => 6,
            _ => 3,
        };

        if (_state.CurrentZoneId.Equals("0,0", StringComparison.OrdinalIgnoreCase))
        {
            score += 10;
        }

        score += Math.Min(10, Math.Max(0, promise.Stacks - 1) * 3);
        score += _state.Rng.NextInt(0, 12);
        return score;
    }

    private int AmbientPromiseScore(WorldPromise promise, PromiseRealizationContext context)
    {
        var score = Math.Clamp(promise.Salience, 1, 5) * 20;
        if (TriggerHintHasExactMatch(promise.TriggerHint, context.Trigger))
        {
            score += 18;
        }

        score += NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "threat" or "debt" => 14,
            "prophecy" or "omen" or "event" => 12,
            "escape_route" or "route" or "door_rule" => 10,
            "item" or "person" or "service" or "merchant_stock" or "stock" or "trade" => 8,
            "memory" or "quest" => 6,
            _ => 3,
        };

        score += Math.Min(10, Math.Max(0, promise.Stacks - 1) * 3);
        score += _state.Rng.NextInt(0, 8);
        return score;
    }

    private static int AnchoredPromiseScore(
        WorldPromise promise,
        Entity anchor,
        PromiseRealizationContext context)
    {
        var trigger = NormalizeToken(context.Trigger);
        var kind = NormalizeToken(promise.RealizationKind ?? promise.Kind);
        var score = Math.Clamp(promise.Salience, 1, 5) * 20;
        if (TriggerHintHasExactMatch(promise.TriggerHint, context.Trigger))
        {
            score += 24;
        }
        else if (PromiseTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            score += string.IsNullOrWhiteSpace(promise.TriggerHint) ? 0 : 12;
        }

        if (!string.IsNullOrWhiteSpace(promise.BoundTargetId)
            && promise.BoundTargetId.Equals(anchor.Id.Value, StringComparison.OrdinalIgnoreCase))
        {
            score += 20;
        }

        score += kind switch
        {
            "door_rule" when anchor.Has<DoorComponent>() => 48,
            "merchant_stock" or "stock" or "trade" when IsTradeTrigger(trigger) => 44,
            "service" or "folk_magic" or "folk_magic_service" when IsServiceTrigger(trigger) => 44,
            "memory" when trigger == "talk" => 18,
            "escape_route" or "route" when trigger is "open" or "inspect" or "read" => 16,
            "item" when trigger is "inspect" or "read" or "talk" => 14,
            "quest" or "site" or "town" or "landmark" when trigger is "talk" or "read" or "inspect" => 12,
            "threat" when trigger is "open" or "talk" or "read" or "inspect" => 10,
            _ => 3,
        };

        if (anchor.Has<DoorComponent>() && trigger == "open")
        {
            score += kind == "door_rule" ? 24 : 8;
        }

        if (anchor.Has<MerchantComponent>() && IsTradeTrigger(trigger))
        {
            score += kind is "merchant_stock" or "stock" or "trade" ? 10 : 2;
        }

        if (anchor.Has<ServiceComponent>() && IsServiceTrigger(trigger))
        {
            score += kind is "service" or "folk_magic" or "folk_magic_service" ? 10 : 2;
        }

        score += Math.Min(10, Math.Max(0, promise.Stacks - 1) * 3);
        return score;
    }

    private static Dictionary<string, object?> ConsequenceDetails(
        WorldConsequence consequence,
        params (string Key, object? Value)[] fields)
    {
        var details = consequence.Payload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(consequence.Payload, StringComparer.OrdinalIgnoreCase);
        details["consequenceType"] = consequence.Type;
        details["source"] = consequence.Source;
        details["sourceEntityId"] = consequence.SourceEntityId;
        details["visibility"] = consequence.Visibility;
        details["timing"] = consequence.Timing;
        details["salience"] = consequence.Salience;
        details["confidence"] = consequence.Confidence;
        details["evidence"] = consequence.Evidence;
        details["reason"] = consequence.Reason;
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        return details;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private bool IsTravelPromise(WorldPromise promise, PromiseRealizationContext context)
    {
        if (!promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            || !PromiseTriggerMatches(promise.TriggerHint, "travel"))
        {
            return false;
        }

        if (!PromiseTravelContextMatches(promise, context))
        {
            return false;
        }

        return IsTravelBuildableKind(promise);
    }

    private bool IsAmbientPromise(WorldPromise promise, PromiseRealizationContext context)
    {
        if (!promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(promise.TriggerHint)
            || !AmbientTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            return false;
        }

        return IsAmbientBuildableKind(promise);
    }

    private static bool IsAnchoredPromise(WorldPromise promise, PromiseRealizationContext context)
    {
        if (promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
            || promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
            || !PromiseTriggerMatches(promise.TriggerHint, context.Trigger))
        {
            return false;
        }

        return NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "memory" or "quest" or "prophecy" or "omen" or "event" or "item" or "person" or "threat" or "debt" or "merchant_stock" or "stock" or "trade" or "service" or "folk_magic" or "folk_magic_service" or "escape_route" or "route" or "door_rule" or "site" or "town" or "landmark";
    }

    private static bool IsTravelBuildableKind(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "site" or "quest" or "prophecy" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "service" or "escape_route" or "route" or "door_rule";

    private static bool IsAmbientBuildableKind(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "memory" or "quest" or "prophecy" or "omen" or "event" or "item" or "person" or "threat" or "debt" or "merchant_stock" or "stock" or "trade" or "service" or "escape_route" or "route" or "door_rule";

    private static string TravelHandlerFor(WorldPromise promise) =>
        NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "item" => "item",
            "person" => "person",
            "threat" => "threat",
            "merchant_stock" or "stock" or "trade" => "merchant_stock",
            "service" or "folk_magic" or "folk_magic_service" => "service",
            "escape_route" or "route" or "door_rule" => "escape_route",
            _ => "site",
        };

    private static string AnchoredHandlerFor(WorldPromise promise, Entity anchor, string trigger)
    {
        var kind = NormalizeToken(promise.RealizationKind ?? promise.Kind);
        return kind switch
        {
            "memory" => "memory",
            "threat" or "debt" => "threat",
            "item" => "item",
            "merchant_stock" or "stock" or "trade" => "merchant_stock",
            "service" or "folk_magic" or "folk_magic_service" => "service",
            "door_rule" when anchor.Has<DoorComponent>() && NormalizeToken(trigger) == "open" => "door_rule",
            "escape_route" or "route" or "door_rule" => "escape_route",
            "quest" => "quest",
            "site" or "town" or "landmark" => "site",
            _ => "omen",
        };
    }

    private static string? TravelEligibilityFailure(WorldPromise promise, PromiseRealizationContext context)
    {
        if (!IsTravelBuildableKind(promise))
        {
            return "unsupported_realization_kind";
        }

        if (PromiseHardTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection
            && !promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase))
        {
            return "direction_mismatch";
        }

        if (SpecificPlaceMismatch(promise.ClaimedPlace, context))
        {
            return "zone_mismatch";
        }

        return null;
    }

    private bool PromiseTravelContextMatches(WorldPromise promise, PromiseRealizationContext context)
    {
        if (PromiseHardTravelDirection(promise) is { } promisedDirection
            && context.Direction is { } actualDirection
            && !promisedDirection.ToString().Equals(actualDirection, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SpecificPlaceMismatch(promise.ClaimedPlace, context))
        {
            return false;
        }

        return true;
    }

    private static bool SpecificPlaceMismatch(string? place, PromiseRealizationContext context)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return false;
        }

        if (LooksLikeZoneId(place))
        {
            return !string.Equals(place.Trim(), context.ZoneId, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool PlaceMatchesContext(string? place, PromiseRealizationContext context)
    {
        if (string.IsNullOrWhiteSpace(place))
        {
            return false;
        }

        return (LooksLikeZoneId(place)
                && string.Equals(place.Trim(), context.ZoneId, StringComparison.OrdinalIgnoreCase))
            || NormalizeToken(place).Equals(NormalizeToken(context.RegionId), StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeZoneId(string place)
    {
        var parts = place.Trim().Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2
            && int.TryParse(parts[0], out _)
            && int.TryParse(parts[1], out _);
    }

    private static Direction? PromiseTravelDirection(WorldPromise promise)
    {
        var text = $"{promise.ClaimedPlace} {promise.Text} {promise.Subject}";
        var directions = new[]
        {
            (Direction.North, new[] { "north", "northern", "northward" }),
            (Direction.South, new[] { "south", "southern", "southward" }),
            (Direction.East, new[] { "east", "eastern", "eastward" }),
            (Direction.West, new[] { "west", "western", "westward" }),
        };
        var matches = directions
            .Where(pair => pair.Item2.Any(word => ContainsWord(text, word)))
            .Select(pair => pair.Item1)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static Direction? PromiseHardTravelDirection(WorldPromise promise)
    {
        var text = $"{promise.ClaimedPlace} {promise.Text}";
        var directions = new[]
        {
            (Direction.North, new[] { "north of here", "north from here", "to the north", "toward the north", "northward" }),
            (Direction.South, new[] { "south of here", "south from here", "to the south", "toward the south", "southward" }),
            (Direction.East, new[] { "east of here", "east from here", "to the east", "toward the east", "eastward" }),
            (Direction.West, new[] { "west of here", "west from here", "to the west", "toward the west", "westward" }),
        };
        var matches = directions
            .Where(pair => pair.Item2.Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
            .Select(pair => pair.Item1)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool ContainsWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var afterIndex = index + word.Length;
            var after = afterIndex >= text.Length || !char.IsLetterOrDigit(text[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index = text.IndexOf(word, index + word.Length, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>
    /// Trigger words that should be treated as fully interchangeable, in both directions. A
    /// pairwise "if trigger is X, hint Y also matches" table silently drifts asymmetric (a
    /// promise hinted "sell" never fired on a "buy" trigger, because the "buy" row's allowed
    /// hints never listed "sell" as its own trigger's synonym, and vice versa); one symmetric
    /// group per concept fixes that structurally, since group membership is checked without
    /// regard to which side is the trigger and which is the hint.
    /// </summary>
    private static readonly string[] WaitSynonymGroup =
        { "wait", "rest", "linger", "delay", "time", "turn", "bellfall", "nightfall" };

    private static readonly string[][] TriggerSynonymGroups =
    {
        new[] { "open", "door", "opened", "unlock" },
        new[] { "talk", "speak", "name", "dialogue" },
        new[] { "read", "notice", "sign", "book" },
        new[] { "inspect", "examine", "look", "fixture" },
        new[] { "trade", "buy", "sell", "wares", "merchant", "market", "stock" },
        new[]
        {
            "services", "request", "service", "offer", "folk_magic",
            "door", "lock", "ward", "mend", "heal", "guide",
        },
        WaitSynonymGroup,
    };

    private static IEnumerable<string> SplitHints(string triggerHint) =>
        triggerHint.ToLowerInvariant()
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool PromiseTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        return SplitHints(triggerHint).Any(hint =>
            hint == normalizedTrigger
            || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase)
            || TriggerSynonymGroups.Any(group => group.Contains(normalizedTrigger) && group.Contains(hint)));
    }

    private static bool AmbientTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return false;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        return SplitHints(triggerHint).Any(hint =>
            hint == normalizedTrigger
            || (WaitSynonymGroup.Contains(normalizedTrigger) && WaitSynonymGroup.Contains(hint)));
    }

    private static bool TriggerHintHasExactMatch(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return false;
        }

        return SplitHints(triggerHint).Any(hint => hint.Equals(trigger, StringComparison.OrdinalIgnoreCase));
    }

    private static string PromiseContextLabel(PromiseRealizationContext context)
    {
        var parts = new List<string>
        {
            $"trigger={NormalizeToken(context.Trigger)}",
            $"region={NormalizeToken(context.RegionId)}",
        };
        if (!string.IsNullOrWhiteSpace(context.ZoneId))
        {
            parts.Add($"zone={context.ZoneId.Trim()}");
        }

        if (!string.IsNullOrWhiteSpace(context.Direction))
        {
            parts.Add($"direction={NormalizeToken(context.Direction)}");
        }

        if (!string.IsNullOrWhiteSpace(context.AnchorEntityId))
        {
            parts.Add($"anchor={context.AnchorEntityId.Trim()}");
        }

        return string.Join(";", parts);
    }

    private static bool IsTradeTrigger(string trigger) =>
        trigger is "trade" or "buy" or "sell" or "wares";

    private static bool IsServiceTrigger(string trigger) =>
        trigger is "service" or "services" or "request";

    private static IReadOnlyList<string> PromiseTags(
        WorldPromise promise,
        string realization,
        RegionDefinition region) =>
        BasicPromiseTags(promise, realization)
            .Concat(region.TerrainTags)
            .Concat(region.VoiceTags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> BasicPromiseTags(WorldPromise promise, string realization) =>
        new[] { "promise", realization, NormalizeToken(promise.Kind), NormalizeToken(promise.RealizationKind ?? realization) }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static string PromiseWantId(WorldPromise promise, string role) =>
        $"want_{NormalizeToken(promise.Id)}_{NormalizeToken(role)}";

    private static IReadOnlyList<string> PromiseWantTags(WorldPromise promise, string role) =>
        BasicPromiseTags(promise, role)
            .Concat(new[] { "want", "promise_source", "generated_npc" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private GridPoint FindGeneratedOpenPointNear(
        IReadOnlyDictionary<EntityId, Entity> entities,
        GridPoint origin,
        int dx,
        int dy)
    {
        var preferred = new GridPoint(
            Math.Clamp(origin.X + dx, 1, _state.Width - 2),
            Math.Clamp(origin.Y + dy, 1, _state.Height - 2));
        return FindGeneratedOpenPoint(entities, preferred);
    }

    private bool HasGeneratedOpenPointNear(
        IReadOnlyDictionary<EntityId, Entity> entities,
        GridPoint origin,
        int dx,
        int dy)
    {
        var preferred = new GridPoint(
            Math.Clamp(origin.X + dx, 1, _state.Width - 2),
            Math.Clamp(origin.Y + dy, 1, _state.Height - 2));
        return FindOpenNear(preferred, OccupiedPoints(entities.Values)) is not null;
    }

    private GridPoint FindGeneratedOpenPoint(IReadOnlyDictionary<EntityId, Entity> entities, GridPoint origin) =>
        FindOpenNear(origin, OccupiedPoints(entities.Values)) ?? origin;

    private bool HasOpenAdjacentPayoffTile(Entity anchor)
    {
        if (anchor.TryGet<PositionComponent>(out var anchorPosition)
            && FindOpenAdjacent(anchorPosition.Position) is not null)
        {
            return true;
        }

        return _state.ControlledEntity.TryGet<PositionComponent>(out var controlledPosition)
            && FindOpenAdjacent(controlledPosition.Position) is not null;
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        foreach (var offset in new[]
        {
            new GridPoint(0, -1),
            new GridPoint(1, 0),
            new GridPoint(0, 1),
            new GridPoint(-1, 0),
            new GridPoint(1, -1),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(-1, -1),
        })
        {
            var candidate = origin.Translate(offset.X, offset.Y);
            if (CanEnter(candidate, OccupiedPoints(_state.Entities.Values)))
            {
                return candidate;
            }
        }

        return null;
    }

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

    private static HashSet<GridPoint> OccupiedPoints(IEnumerable<Entity> entities) =>
        entities
            .Where(entity => entity.TryGet<PositionComponent>(out _)
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement
                && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive))
            .Select(entity => entity.Get<PositionComponent>().Position)
            .ToHashSet();

    private static string PromiseSiteName(WorldPromise promise, RegionDefinition region)
    {
        if (!string.IsNullOrWhiteSpace(promise.ClaimedPlace)
            && !promise.ClaimedPlace.Equals(region.Id, StringComparison.OrdinalIgnoreCase))
        {
            return promise.ClaimedPlace;
        }

        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("refuge"))
        {
            return lower.Contains("hollowmere") ? "Hollowmere refuge" : "promised refuge";
        }

        return region.Id switch
        {
            "hollowmere_margin" => "folded-road checkpoint",
            "wild_border" => "promise-touched border stone",
            _ => "promised waymark",
        };
    }

    private static string PromiseItemName(WorldPromise promise)
    {
        var lower = $"{promise.Subject} {promise.Text}".ToLowerInvariant();
        if (lower.Contains("blade") || lower.Contains("knife") || lower.Contains("sword"))
        {
            return "promised blade";
        }

        if (lower.Contains("key"))
        {
            return "promised key";
        }

        if (lower.Contains("pearl"))
        {
            return "promised pearl";
        }

        if (lower.Contains("red tincture"))
        {
            return "red tincture";
        }

        if (lower.Contains("tincture"))
        {
            return "promised tincture";
        }

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return "promise token";
    }

    private static string PromiseCommerceSubject(WorldPromise promise)
    {
        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        var itemName = PromiseItemName(promise);
        if (!itemName.Equals("promise token", StringComparison.OrdinalIgnoreCase))
        {
            return itemName;
        }

        if (ExtractAfterWord(promise.Text, "buy") is { } boughtThing)
        {
            return boughtThing;
        }

        return "promised goods";
    }

    private static string? ExtractAfterWord(string text, string word)
    {
        var marker = $"{word} ";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = text[(index + marker.Length)..];
        foreach (var stop in new[] { " if ", " when ", " from ", " for ", " to ", ".", ",", ";" })
        {
            var stopIndex = value.IndexOf(stop, StringComparison.OrdinalIgnoreCase);
            if (stopIndex >= 0)
            {
                value = value[..stopIndex];
            }
        }

        value = value.Trim().Trim('"', '\'', ':', '-', ' ');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string PromisePersonName(WorldPromise promise)
    {
        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return promise.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase)
            ? "Nannerl"
            : "promised stranger";
    }

    private static string PromiseMerchantName(WorldPromise promise)
    {
        var text = promise.Text.Trim();
        if (text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase))
        {
            return "Jimmer";
        }

        foreach (var phrase in new[] { " can sell", " sells", " trades", " offers" })
        {
            var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                var name = text[..index].Trim(' ', '.', ',', ';', ':', '"', '\'');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return "promised merchant";
    }

    /// <summary>
    /// A threat promise only reads as imperial when its own text says so; otherwise it is a
    /// private grudge (a debt collector, a rival, a personal enemy) and must not be spawned
    /// under the Empire's faction, or killing it would feed Censorate heat and warrant pressure
    /// for a threat that had nothing to do with the Empire.
    /// </summary>
    private static bool IsImperialThreat(WorldPromise promise)
    {
        var lower = $"{promise.Subject} {promise.Text}".ToLowerInvariant();
        return lower.Contains("soldier") || lower.Contains("empire") || lower.Contains("imperial");
    }

    private static string PromiseThreatName(WorldPromise promise)
    {
        var lower = $"{promise.Subject} {promise.Text}".ToLowerInvariant();
        if (lower.Contains("collector"))
        {
            return "debt collector";
        }

        if (IsImperialThreat(promise))
        {
            return "promised imperial claimant";
        }

        return "promised threat";
    }

    /// <summary>
    /// "empire" only for promises whose own text names the Empire; everything else spawns under
    /// the "independent" faction (hostile to the player, but not in the empire_bloc role) so
    /// WorldTurnSystem's empire-heat pressure (which reads FactionsByRole("empire_bloc")) is
    /// never fed by a private threat like a debt collector.
    /// </summary>
    private static string PromiseThreatFaction(WorldPromise promise) =>
        IsImperialThreat(promise) ? "empire" : "independent";

    private static string PromiseServiceName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward"))
        {
            return "ward-breaking";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape"))
        {
            return "hidden-route finding";
        }

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return "quiet folk-magic service";
    }

    private static string PromiseServiceProviderName(WorldPromise promise)
    {
        var text = promise.Text.Trim();
        foreach (var phrase in new[] { " can ", " offers ", " knows ", " keeps " })
        {
            var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                var name = text[..index].Trim(' ', '.', ',', ';', ':', '"', '\'');
                if (!string.IsNullOrWhiteSpace(name))
                {
                    return name;
                }
            }
        }

        return "promised service keeper";
    }

    private static string PromiseServiceEffect(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward") || lower.Contains("key"))
        {
            return "open_or_unlock";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape") || lower.Contains("passage"))
        {
            return "create_route";
        }

        return "record_memory";
    }

    private static string? PromiseServiceItemCost(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("grave salt"))
        {
            return "grave salt";
        }

        if (lower.Contains("moon pearl"))
        {
            return "moon pearl";
        }

        return null;
    }

    private static string PromiseServiceTargetHint(WorldPromise promise, string serviceName)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("cell door"))
        {
            return "cell door";
        }

        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward"))
        {
            return "door";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape") || lower.Contains("passage"))
        {
            return serviceName;
        }

        return serviceName;
    }

    private static string PromiseRouteName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("drain"))
        {
            return "imperial drainage route";
        }

        if (lower.Contains("tunnel"))
        {
            return "hidden tunnel";
        }

        if (lower.Contains("grate"))
        {
            return "concealed grate";
        }

        if (lower.Contains("refuge"))
        {
            return lower.Contains("hollowmere") ? "path to Hollowmere refuge" : "refuge path";
        }

        if (lower.Contains("oak"))
        {
            return "burned oak road";
        }

        if (lower.Contains("road"))
        {
            return "hidden road";
        }

        if (lower.Contains("passage"))
        {
            return "secret passage";
        }

        if (lower.Contains("path"))
        {
            return "hidden path";
        }

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return lower.Contains("route") ? "concealed route" : "promised hidden route";
    }

    private static string? UsefulSubject(WorldPromise promise)
    {
        if (string.IsNullOrWhiteSpace(promise.Subject)
            || promise.Subject.Equals(promise.Kind, StringComparison.OrdinalIgnoreCase)
            || LooksTechnicalSubject(promise.Subject))
        {
            return null;
        }

        return promise.Subject;
    }

    private static bool LooksTechnicalSubject(string subject)
    {
        var normalized = subject.Trim().ToLowerInvariant();
        return normalized.Equals("player", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("_soul", StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith("promise_", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains('_', StringComparison.Ordinal);
    }

    private static string NormalizeToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

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
