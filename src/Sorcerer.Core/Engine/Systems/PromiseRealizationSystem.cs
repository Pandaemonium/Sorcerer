using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class PromiseRealizationSystem
{
    private readonly GameState _state;

    public PromiseRealizationSystem(GameState state)
    {
        _state = state;
    }

    public IReadOnlyList<string> RealizeTravelPromises(
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        if (zoneId.Equals("0,0", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<string>();
        }

        var realizedIds = new List<string>();
        foreach (var candidate in SelectTravelPromises().Take(2).ToArray())
        {
            var realized = _state.PromiseLedger.SetStatus(candidate.Promise.Id, "realized", zoneId);
            if (realized is null)
            {
                continue;
            }

            deltas.Add(RealizePromiseDelta(realized, "travel", zoneId, zoneId, candidate.Score));
            switch (NormalizeToken(realized.RealizationKind ?? realized.Kind))
            {
                case "item":
                    RealizeTravelItemPromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
                case "person":
                    RealizeTravelPersonPromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
                case "threat":
                    RealizeTravelThreatPromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
                case "merchant_stock":
                case "stock":
                case "trade":
                    RealizeTravelMerchantStockPromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
                case "service":
                    RealizeTravelServicePromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
                case "escape_route":
                case "route":
                case "door_rule":
                    RealizeTravelRoutePromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
                default:
                    RealizeTravelSitePromise(realized, zoneId, region, entities, deltas, placementOrigin);
                    break;
            }

            realizedIds.Add(realized.Id);
        }

        return realizedIds;
    }

    private IReadOnlyList<ScoredPromise> SelectTravelPromises() =>
        _state.PromiseLedger.Promises
            .Where(IsTravelPromise)
            .Select(promise => new ScoredPromise(promise, TravelPromiseScore(promise)))
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Promise.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<StateDelta> RealizeAnchoredPromises(
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        if (!anchor.TryGet<PromiseAnchorComponent>(out var promiseAnchor))
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        foreach (var promiseId in promiseAnchor.PromiseIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var existing = _state.PromiseLedger.Promises.FirstOrDefault(promise =>
                promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase));
            if (existing is null
                || existing.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                || existing.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
                || !PromiseTriggerMatches(existing.TriggerHint, trigger))
            {
                continue;
            }

            var realizedIn = $"{trigger}:{anchor.Id.Value}";
            var realized = _state.PromiseLedger.SetStatus(existing.Id, "realized", realizedIn);
            if (realized is null)
            {
                continue;
            }

            var message = $"A promise stirs awake: {realized.Text}";
            messages.Add(message);
            deltas.Add(RealizePromiseDelta(realized, trigger, anchor.Id.Value, realizedIn, selectionScore: null));
            deltas.AddRange(ApplyAnchoredRealization(realized, anchor, trigger, messages));
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyAnchoredRealization(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        return NormalizeToken(promise.RealizationKind ?? promise.Kind) switch
        {
            "memory" => RealizeAnchoredMemory(promise, anchor, trigger, messages),
            "threat" => RealizeAnchoredThreat(promise, anchor, trigger, messages),
            "item" => RealizeAnchoredItem(promise, anchor, trigger, messages),
            "escape_route" or "route" or "door_rule" => RealizeAnchoredRoute(promise, anchor, trigger, messages),
            "quest" => RealizeAnchoredCanon(promise, anchor, trigger, messages, "quest", "A quest takes shape"),
            "site" or "town" or "landmark" => RealizeAnchoredCanon(promise, anchor, trigger, messages, "site", "A distant place answers"),
            _ => RealizeAnchoredCanon(promise, anchor, trigger, messages, "omen", "The omen settles into the world"),
        };
    }

    private void RealizeTravelSitePromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var siteId = _state.NextEntityId("promise_site");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, -1);
        var tags = PromiseTags(promise, "site", region);
        var site = new Entity(siteId, PromiseSiteName(promise, region))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('?', "promise"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: region.TerrainTags.FirstOrDefault() ?? "stone"))
            .Set(new FixtureComponent("promise_site", tags))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
        entities[siteId] = site;
        var canon = AddCanon("site", siteId.Value, promise, $"{site.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised place takes shape: {site.Name}.";
        deltas.Add(PromiseRealizationDelta("promiseSite", siteId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
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
        var itemId = _state.NextEntityId("promise_item");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 0);
        var tags = PromiseTags(promise, "item", region);
        var item = BuildPromiseItem(itemId, itemName, promise, position, tags, "This object exists because a claim became reachable");
        entities[itemId] = item;
        var canon = AddCanon("item", itemId.Value, promise, $"{item.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised object is waiting: {item.Name}.";
        deltas.Add(PromiseRealizationDelta("promiseItem", itemId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
    }

    private void RealizeTravelPersonPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var personId = _state.NextEntityId("promise_person");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 1);
        var tags = PromiseTags(promise, "person", region);
        var person = new Entity(personId, PromisePersonName(promise))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('p', "neutral"))
            .Set(new TagsComponent(tags.Concat(new[] { "npc" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(6, 6, 0, 0, 1, 0, "neutral"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("resident"))
            .Set(new SoulComponent($"{personId.Value}_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new FactionComponent("neutral", new[] { "promise", "resident" }))
            .Set(new InteractableComponent(new[] { "talk", "give", "recruit" }))
            .Set(new ProfileComponent(PromisePersonName(promise), promise.Text))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
        entities[personId] = person;
        var canon = AddCanon("person", personId.Value, promise, $"{person.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised person is here: {person.Name}.";
        deltas.Add(PromiseRealizationDelta("promisePerson", personId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
    }

    private void RealizeTravelThreatPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var threatId = _state.NextEntityId("promise_threat");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 2, -1);
        var tags = PromiseTags(promise, "threat", region);
        var threat = BuildPromiseThreat(threatId, PromiseThreatName(promise), promise, position, tags);
        entities[threatId] = threat;
        var canon = AddCanon("threat", threatId.Value, promise, $"{threat.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised threat steps into the road: {threat.Name}.";
        deltas.Add(PromiseRealizationDelta("promiseThreat", threatId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
    }

    private void RealizeTravelMerchantStockPromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var merchantId = _state.NextEntityId("promise_merchant");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 0);
        var tags = PromiseTags(promise, "merchant_stock", region)
            .Concat(new[] { "npc", "merchant" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var itemName = PromiseItemName(promise);
        var merchant = new Entity(merchantId, PromiseMerchantName(promise))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('p', "neutral"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(6, 6, 0, 0, 1, 0, "neutral"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("resident"))
            .Set(new SoulComponent($"{merchantId.Value}_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new FactionComponent("neutral", new[] { "promise", "merchant" }))
            .Set(new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                [itemName] = 1,
            }, Gold: 30))
            .Set(new InteractableComponent(new[] { "talk", "give", "wares", "buy", "sell" }))
            .Set(new ProfileComponent(PromiseMerchantName(promise), promise.Text))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
        entities[merchantId] = merchant;
        var canon = AddCanon("merchant_stock", merchantId.Value, promise, $"{merchant.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised merchant is here: {merchant.Name} has {itemName}.";
        deltas.Add(PromiseRealizationDelta("promiseMerchantStock", merchantId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
    }

    private void RealizeTravelServicePromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var providerId = _state.NextEntityId("promise_service");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 1, 1);
        var tags = PromiseTags(promise, "service", region)
            .Concat(new[] { "npc", "service_provider", "folk_magic" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var serviceName = PromiseServiceName(promise);
        var provider = new Entity(providerId, PromiseServiceProviderName(promise))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('p', "neutral"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(6, 6, 0, 0, 1, 0, "neutral"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("resident"))
            .Set(new SoulComponent($"{providerId.Value}_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new FactionComponent("neutral", new[] { "promise", "service_provider" }))
            .Set(new ServiceComponent(new[]
            {
                new ServiceOffer(
                    NormalizeToken(serviceName),
                    serviceName,
                    promise.Text,
                    PromiseServiceEffect(promise),
                    GoldCost: 0,
                    TargetHint: serviceName,
                    Revealed: true,
                    Tags: BasicPromiseTags(promise, "service")),
            }))
            .Set(new InteractableComponent(new[] { "talk", "give", "services", "request_service" }))
            .Set(new ProfileComponent(PromiseServiceProviderName(promise), promise.Text))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
        entities[providerId] = provider;
        var canon = AddCanon("service", providerId.Value, promise, $"{provider.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised service is reachable: {provider.Name} offers {serviceName}.";
        deltas.Add(PromiseRealizationDelta("promiseService", providerId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
    }

    private void RealizeTravelRoutePromise(
        WorldPromise promise,
        string zoneId,
        RegionDefinition region,
        Dictionary<EntityId, Entity> entities,
        List<StateDelta> deltas,
        GridPoint placementOrigin)
    {
        var routeId = _state.NextEntityId("promise_route");
        var position = FindGeneratedOpenPointNear(entities, placementOrigin, 0, 1);
        var tags = PromiseTags(promise, "escape_route", region)
            .Concat(new[] { "route", "hidden_exit" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var route = new Entity(routeId, PromiseRouteName(promise))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('>', "route"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "passage"))
            .Set(new FixtureComponent("escape_route", tags))
            .Set(new InteractableComponent(new[] { "examine", "travel" }))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
        entities[routeId] = route;
        var canon = AddCanon("escape_route", routeId.Value, promise, $"{route.Name}: {promise.Text}", tags, "travel");
        var summary = $"A promised route becomes visible: {route.Name}.";
        deltas.Add(PromiseRealizationDelta("promiseRoute", routeId.Value, summary, promise.Id, zoneId, region.Id, canon.Id, position));
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredMemory(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var worldMemory = _state.Memories.Append(
            anchor.Id.Value,
            promise.Text,
            $"promise:{promise.Id}:{trigger}",
            Math.Max(2, promise.Salience + 1),
            shareable: true);
        var existing = anchor.TryGet<MemoryComponent>(out var memory)
            ? memory.Records.ToList()
            : new List<EntityMemoryRecord>();
        existing.Add(new EntityMemoryRecord(
            $"memory_{promise.Id}",
            promise.Text,
            promise.Id,
            trigger,
            Math.Max(2, promise.Salience + 1),
            Shareable: true));
        anchor.Set(new MemoryComponent(existing));

        var message = $"{anchor.Name} remembers something that was not there before.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseMemory",
                worldMemory.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["anchor"] = anchor.Id.Value,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredThreat(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : _state.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin)
            ?? FindOpenAdjacent(_state.ControlledEntity.Get<PositionComponent>().Position)
            ?? origin;
        var threatId = _state.NextEntityId("promise_threat");
        var threat = BuildPromiseThreat(threatId, PromiseThreatName(promise), promise, position, BasicPromiseTags(promise, "threat"));
        _state.Entities[threatId] = threat;
        var message = $"{threat.Name} arrives to collect on the promise.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseThreat",
                threat.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredItem(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : _state.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin) ?? origin;
        var itemName = PromiseItemName(promise);
        var itemId = _state.NextEntityId("promise_item");
        var item = BuildPromiseItem(
            itemId,
            itemName,
            promise,
            position,
            BasicPromiseTags(promise, "item"),
            "This object exists because a promise became concrete");
        _state.Entities[item.Id] = item;

        var message = $"{item.Name} appears where the promise can reach it.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseItem",
                item.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredRoute(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages)
    {
        var origin = anchor.TryGet<PositionComponent>(out var anchorPosition)
            ? anchorPosition.Position
            : _state.ControlledEntity.Get<PositionComponent>().Position;
        var position = FindOpenAdjacent(origin)
            ?? FindOpenAdjacent(_state.ControlledEntity.Get<PositionComponent>().Position)
            ?? origin;
        var routeId = _state.NextEntityId("promise_route");
        var tags = BasicPromiseTags(promise, "escape_route")
            .Concat(new[] { "route", "hidden_exit" })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var route = new Entity(routeId, PromiseRouteName(promise))
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('>', "route"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "passage"))
            .Set(new FixtureComponent("escape_route", tags))
            .Set(new InteractableComponent(new[] { "examine", "travel" }))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
        _state.Entities[routeId] = route;

        var canon = AddCanon(
            "escape_route",
            routeId.Value,
            promise,
            $"{route.Name}: {promise.Text}",
            tags,
            trigger);
        var message = $"A promised route becomes visible: {route.Name}.";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseRoute",
                route.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["x"] = position.X,
                    ["y"] = position.Y,
                    ["trigger"] = trigger,
                    ["canonId"] = canon.Id,
                    ["realizationKind"] = "escape_route",
                }),
        };
    }

    private IReadOnlyList<StateDelta> RealizeAnchoredCanon(
        WorldPromise promise,
        Entity anchor,
        string trigger,
        List<string> messages,
        string canonKind,
        string messagePrefix)
    {
        var canon = AddCanon(
            canonKind,
            anchor.Id.Value,
            promise,
            promise.Text,
            new[] { "promise", promise.Kind, canonKind },
            trigger);
        var message = $"{messagePrefix}: {promise.Text}";
        messages.Add(message);
        return new[]
        {
            new StateDelta(
                "promiseCanon",
                canon.Id,
                message,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["anchor"] = anchor.Id.Value,
                    ["kind"] = canonKind,
                    ["trigger"] = trigger,
                }),
        };
    }

    private Entity BuildPromiseItem(
        EntityId itemId,
        string itemName,
        WorldPromise promise,
        GridPoint position,
        IReadOnlyList<string> tags,
        string descriptionPrefix)
    {
        return new Entity(itemId, itemName)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('*', "item"))
            .Set(new TagsComponent(tags.Concat(new[] { "item" }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "promise"))
            .Set(new DescriptionComponent($"{descriptionPrefix}: {promise.Text}"))
            .Set(new ItemComponent(NormalizeToken(itemName), 1, "promise", tags, StackPolicy: "unique"))
            .Set(new StackComponent(1))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
    }

    private static Entity BuildPromiseThreat(
        EntityId threatId,
        string threatName,
        WorldPromise promise,
        GridPoint position,
        IReadOnlyList<string> tags)
    {
        return new Entity(threatId, threatName)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('D', "empire"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(promise.Text))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(8, 8, 0, 0, 3, 0, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile"))
            .Set(new SoulComponent($"{threatId.Value}_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new FactionComponent("empire", new[] { "promise", "threat" }))
            .Set(new InteractableComponent(new[] { "talk", "examine" }))
            .Set(new PromiseAnchorComponent(new[] { promise.Id }));
    }

    private CanonRecord AddCanon(
        string kind,
        string subjectId,
        WorldPromise promise,
        string summary,
        IReadOnlyList<string> tags,
        string trigger)
    {
        return _state.Canon.Add(
            kind,
            subjectId,
            promise.Text,
            summary,
            tags,
            $"promise:{promise.Id}:{trigger}",
            _state.Turn);
    }

    private static StateDelta RealizePromiseDelta(
        WorldPromise promise,
        string trigger,
        string target,
        string realizedIn,
        int? selectionScore) =>
        new(
            "realizePromise",
            promise.Id,
            $"A promise stirs awake: {promise.Text}",
            new Dictionary<string, object?>
            {
                ["status"] = promise.Status,
                ["trigger"] = trigger,
                ["target"] = target,
                ["realizedIn"] = realizedIn,
                ["realizationKind"] = promise.RealizationKind,
                ["selectionScore"] = selectionScore,
            });

    private int TravelPromiseScore(WorldPromise promise)
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

    private static StateDelta PromiseRealizationDelta(
        string operation,
        string target,
        string summary,
        string promiseId,
        string zoneId,
        string regionId,
        string canonId,
        GridPoint position) =>
        new(
            operation,
            target,
            summary,
            new Dictionary<string, object?>
            {
                ["promiseId"] = promiseId,
                ["zoneId"] = zoneId,
                ["regionId"] = regionId,
                ["canonId"] = canonId,
                ["x"] = position.X,
                ["y"] = position.Y,
            });

    private static bool IsTravelPromise(WorldPromise promise)
    {
        if (!promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
            || !PromiseTriggerMatches(promise.TriggerHint, "travel"))
        {
            return false;
        }

        return NormalizeToken(promise.RealizationKind ?? promise.Kind)
            is "site" or "quest" or "prophecy" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "service" or "escape_route" or "route" or "door_rule";
    }

    private static bool PromiseTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = trigger.Trim().ToLowerInvariant();
        var hints = triggerHint.ToLowerInvariant()
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return hints.Any(hint =>
            hint == normalizedTrigger
            || hint.Equals("encounter", StringComparison.OrdinalIgnoreCase)
            || (normalizedTrigger == "open" && hint is "door" or "opened" or "unlock")
            || (normalizedTrigger == "talk" && hint is "speak" or "name" or "dialogue")
            || (normalizedTrigger == "read" && hint is "notice" or "sign" or "book")
            || (normalizedTrigger == "inspect" && hint is "examine" or "look" or "fixture"));
    }

    private static bool TriggerHintHasExactMatch(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return false;
        }

        return triggerHint.ToLowerInvariant()
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(hint => hint.Equals(trigger, StringComparison.OrdinalIgnoreCase));
    }

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

    private GridPoint FindGeneratedOpenPoint(IReadOnlyDictionary<EntityId, Entity> entities, GridPoint origin) =>
        FindOpenNear(origin, OccupiedPoints(entities.Values)) ?? origin;

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

        if (UsefulSubject(promise) is { } subject)
        {
            return subject;
        }

        return "promise token";
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

    private static string PromiseThreatName(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("collector"))
        {
            return "debt collector";
        }

        if (lower.Contains("soldier") || lower.Contains("empire") || lower.Contains("imperial"))
        {
            return "promised imperial claimant";
        }

        return "promised threat";
    }

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

    private sealed record ScoredPromise(WorldPromise Promise, int Score);
}
