using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

/// <summary>
/// <see cref="PromiseRealizationSystem"/> per-archetype realizers: build and submit the child consequences that turn travel/anchored/ambient promises into real sites, items, people, threats, stock, services, routes, and canon, including the detached-sandbox generated-content application.
/// Split from the promise-realization system (Phase 0.4); selection, plan building, the
/// apply/rollback lifecycle, and the injected consequence sink stay in the base file.
/// </summary>
public sealed partial class PromiseRealizationSystem
{
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
            .Concat(new[] { "npc", "objective_contact" })
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
        var archetype = ResolveThreatArchetype(promise, region);
        var tags = PromiseTags(promise, "threat", region)
            .Concat(archetype.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var threat = ApplyGeneratedSpawnEntity(
            WorldConsequence.SpawnEntity(
                $"promise:{promise.Id}:travel",
                archetype.Name,
                position.X,
                position.Y,
                prefix: "promise_threat",
                glyph: archetype.Glyph,
                faction: archetype.Faction,
                hp: archetype.Hp,
                attack: archetype.Attack,
                tags: tags,
                material: archetype.Material,
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
                message: $"{archetype.FlavorText} {archetype.Name}.",
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
        RecordThreatRealizationCooldown(promise, "travel", deltas);
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
        var region = ResolveRegion(_state.RegionId);
        var archetype = ResolveThreatArchetype(promise, region);
        var tags = BasicPromiseTags(promise, "threat")
            .Concat(archetype.Tags)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var message = $"{archetype.FlavorText} {archetype.Name}.";
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
            archetype.Name,
            position.X,
            position.Y,
            prefix: "promise_threat",
            glyph: archetype.Glyph,
            faction: archetype.Faction,
            hp: archetype.Hp,
            attack: archetype.Attack,
            tags: tags,
            material: archetype.Material,
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
            $"{archetype.Name}: {promise.Text}",
            tags,
            trigger);
        if (!canon.Applied)
        {
            return applied.Deltas.Concat(canon.Deltas).ToArray();
        }

        var cooldownDeltas = new List<StateDelta>();
        RecordThreatRealizationCooldown(promise, trigger, cooldownDeltas);

        return applied.Deltas
            .Concat(canon.Deltas)
            .Concat(cooldownDeltas)
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
}
