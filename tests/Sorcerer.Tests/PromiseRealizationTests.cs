using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Validation;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class PromiseRealizationTests
{
    [Fact]
    public async Task WaitRealizesExplicitWaitThreatPromise()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "threat",
            "If you linger, a debt collector will come.",
            triggerHint: "wait",
            realizationKind: "threat",
            subject: "debt collector",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new WaitCommand());

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("realized", promise.Status);
        Assert.StartsWith("wait:", promise.RealizedIn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "wait"));
        Assert.Contains(result.Messages, message =>
            message.Contains("debt collector arrives", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Contains("debt collector", StringComparison.OrdinalIgnoreCase));

        var saved = GameSaveService.Serialize(
            session.Engine.State,
            savedAt: new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));
        var loaded = GameSaveService.Deserialize(saved);
        Assert.True(StateValidator.Validate(loaded.State).IsValid);
        var loadedPromise = loaded.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("realized", loadedPromise.Status);
        Assert.Equal(promise.RealizedIn, loadedPromise.RealizedIn);
        Assert.Contains(loaded.State.Entities.Values, entity =>
            entity.Name.Contains("debt collector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WaitDoesNotRealizeOrdinaryTravelPromise()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "site",
            "There is a quiet refuge south of here.",
            triggerHint: "travel",
            realizationKind: "site",
            subject: "quiet refuge",
            bindPlace: "hollowmere_margin",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new WaitCommand());

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("bound", promise.Status);
        Assert.DoesNotContain(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "wait"));
    }

    [Fact]
    public async Task WaitDoesNotTreatGenericEncounterTriggerAsTimeTrigger()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "threat",
            "A collector will come when the encounter turns.",
            triggerHint: "encounter",
            realizationKind: "threat",
            subject: "collector",
            bindPlace: session.Engine.State.RegionId,
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new WaitCommand());

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("bound", promise.Status);
        Assert.DoesNotContain(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "wait"));
    }

    [Fact]
    public async Task WaitRealizesDebtPromiseAsCollectorThreat()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "debt",
            "If you linger, the debt comes due.",
            triggerHint: "wait",
            realizationKind: "debt",
            subject: "debt collector",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new WaitCommand());

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.Equal("realized", promise.Status);
        var plan = Assert.Single(result.Deltas, delta =>
            delta.Operation == "promiseRealizationPlan"
            && delta.Target == create.TargetId);
        Assert.Equal("threat", plan.Details["handler"]);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseThreat"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Contains("debt collector", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TravelRecordsEligibilityFailureAndClearsItWhenPromiseRealizes()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "North of here, a promised blue knife waits in a hollow stone.",
            triggerHint: "travel",
            realizationKind: "item",
            subject: "promised blue knife",
            bindPlace: session.Engine.State.RegionId,
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var east = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var waiting = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);

        Assert.True(east.Success);
        Assert.Equal("bound", waiting.Status);
        Assert.Equal("direction_mismatch", waiting.LastEligibilityFailure);
        Assert.NotNull(waiting.LastEligibilityContext);
        Assert.Contains("direction=east", waiting.LastEligibilityContext, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(east.TurnBefore, waiting.LastEligibilityTurn);
        Assert.Contains(east.Deltas, delta =>
            delta.Operation == "promiseEligibility"
            && delta.Target == waiting.Id
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdatePromise)
            && Equals(delta.Details["failure"], "direction_mismatch")
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(session.View().Promises, promise =>
            promise.Id == waiting.Id
            && promise.LastEligibilityFailure == "direction_mismatch");

        var north = await session.ExecuteAsync(new TravelCommand(Direction.North));
        var realized = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);

        Assert.True(north.Success);
        Assert.Equal("realized", realized.Status);
        Assert.Null(realized.LastEligibilityFailure);
        Assert.Null(realized.LastEligibilityContext);
        Assert.Null(realized.LastEligibilityTurn);
        Assert.Contains(north.Deltas, delta =>
            delta.Operation == "promiseStatus"
            && delta.Target == realized.Id
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdatePromise)
            && delta.Details["lastEligibilityFailure"] is null);
        var plan = Assert.Single(north.Deltas, delta =>
            delta.Operation == "promiseRealizationPlan"
            && delta.Target == realized.Id);
        Assert.Equal("item", plan.Details["handler"]);
        Assert.Equal("travel", plan.Details["trigger"]);
        Assert.Equal(realized.RealizedIn, plan.Details["realizedIn"]);
        var planReasons = Assert.IsAssignableFrom<IEnumerable<string>>(plan.Details["selectionReasons"]);
        Assert.Contains("direction:matched", planReasons);
        Assert.Contains("handler:item", planReasons);
        var planIndex = north.Deltas.ToList().FindIndex(delta => delta.Operation == "promiseRealizationPlan");
        var realizationIndex = north.Deltas.ToList().FindIndex(delta => delta.Operation == "realizePromise");
        Assert.True(planIndex >= 0 && realizationIndex > planIndex);
        Assert.Contains(north.Deltas, delta =>
            delta.Operation == "promiseItem"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnItem));
        Assert.Contains(north.Deltas, delta =>
            delta.Operation == "promiseCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["realizationKind"], "item")
            && !string.IsNullOrWhiteSpace(Convert.ToString(delta.Details["canonId"])));
    }

    [Fact]
    public async Task WaresRealizesAnchoredMerchantStockPromise()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        Assert.False(lio.Has<MerchantComponent>());
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio can sell you a fine blade.",
            anchorEntityId: lio.Id.Value,
            subject: "fine blade",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new WaresCommand("Lio"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("wares:prisoner_1", promise.RealizedIn);
        Assert.Equal("trade", promise.TriggerHint);
        Assert.Equal("merchant_stock", promise.RealizationKind);
        Assert.True(lio.TryGet<MerchantComponent>(out var merchant));
        Assert.True(merchant.Wares.TryGetValue("promised blade", out var count));
        Assert.Equal(1, count);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "wares"));
        Assert.Contains(result.Messages, message =>
            message.Contains("promised stock", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message =>
            message.Contains("promised blade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TravelRoutePromiseDispatchesThroughRegisteredRouteHandler()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "There is a hidden drain beyond the next road.",
            triggerHint: "travel",
            realizationKind: "escape_route",
            subject: "hidden drain",
            bindPlace: session.Engine.State.RegionId,
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new TravelCommand(Direction.East));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.Equal("realized", promise.Status);
        var plan = Assert.Single(result.Deltas, delta =>
            delta.Operation == "promiseRealizationPlan"
            && delta.Target == create.TargetId);
        Assert.Equal("escape_route", plan.Details["handler"]);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseRoute"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreateRoute));
    }

    [Fact]
    public async Task TravelMerchantStockPromiseCommitsProviderAndStock()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Jimmer can sell you a fine blade east of here.",
            triggerHint: "travel",
            realizationKind: "merchant_stock",
            subject: "fine blade",
            bindPlace: session.Engine.State.RegionId,
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new TravelCommand(Direction.East));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        var merchant = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("Jimmer", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Success);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("1,0", promise.RealizedIn);
        Assert.True(merchant.TryGet<MerchantComponent>(out var stock));
        Assert.True(stock.Wares.TryGetValue("promised blade", out var count));
        Assert.Equal(1, count);
        var spawnIndex = result.Deltas.ToList().FindIndex(delta => delta.Operation == "promiseMerchant");
        var stockIndex = result.Deltas.ToList().FindIndex(delta => delta.Operation == "promiseMerchantStock");
        Assert.True(spawnIndex >= 0);
        Assert.True(stockIndex > spawnIndex);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseMerchant"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["realizationKind"], "merchant_stock")
            && !string.IsNullOrWhiteSpace(Convert.ToString(delta.Details["canonId"])));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "merchant_stock"
            && record.Source == $"promise:{promise.Id}:travel"
            && record.AttachedTo == merchant.Id.Value);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "travel"));
    }

    [Fact]
    public async Task TravelServicePromiseCommitsProviderAndOffer()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Maren can break the ward on the cell door east of here.",
            triggerHint: "travel",
            realizationKind: "service",
            subject: "ward-breaking",
            bindPlace: session.Engine.State.RegionId,
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new TravelCommand(Direction.East));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        var provider = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name.Equals("Maren", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Success);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("1,0", promise.RealizedIn);
        Assert.True(provider.TryGet<ServiceComponent>(out var services));
        var offer = Assert.Single(services.Offers);
        Assert.Equal("ward_breaking", offer.Id);
        Assert.Equal("open_or_unlock", offer.EffectKind);
        var spawnIndex = result.Deltas.ToList().FindIndex(delta => delta.Operation == "promiseServiceProvider");
        var offerIndex = result.Deltas.ToList().FindIndex(delta => delta.Operation == "promiseService");
        Assert.True(spawnIndex >= 0);
        Assert.True(offerIndex > spawnIndex);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseServiceProvider"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferService));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["realizationKind"], "service")
            && !string.IsNullOrWhiteSpace(Convert.ToString(delta.Details["canonId"])));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "service"
            && record.Source == $"promise:{promise.Id}:travel"
            && record.AttachedTo == provider.Id.Value);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "travel"));
    }

    [Fact]
    public async Task BuyRealizesAnchoredMerchantStockPromiseBeforeExecutingTrade()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio can sell you a fine blade.",
            anchorEntityId: lio.Id.Value,
            triggerHint: "buy",
            realizationKind: "merchant_stock",
            subject: "fine blade",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new BuyCommand("promised blade", "Lio"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        var inventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("buy:prisoner_1", promise.RealizedIn);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "executeTrade"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ExecuteTrade));
        Assert.True(inventory.Items.TryGetValue("promised blade", out var owned));
        Assert.Equal(1, owned);
    }

    [Fact]
    public async Task BuyRealizesPromiseHintedForTheOppositeCommerceVerb()
    {
        // Commerce trigger hints (trade/buy/sell/wares/...) must be interchangeable in both
        // directions: a promise hinted "sell" should still realize on a buy interaction, the
        // same way a promise hinted "buy" already does on a sell interaction.
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio can sell you a fine blade.",
            anchorEntityId: lio.Id.Value,
            triggerHint: "sell",
            realizationKind: "merchant_stock",
            subject: "fine blade",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new BuyCommand("promised blade", "Lio"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("buy:prisoner_1", promise.RealizedIn);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "executeTrade"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ExecuteTrade));
    }

    [Fact]
    public async Task SellRealizesAnchoredMerchantPromiseBeforeExecutingTrade()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        Assert.False(lio.Has<MerchantComponent>());
        var setup = session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test",
            session.Engine.State.ControlledEntityId.Value,
            "red tincture",
            operation: "testAddSaleItem",
            emitMessage: false));
        Assert.True(setup.Applied, setup.Error);
        var inventoryBefore = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        inventoryBefore.Items.TryGetValue("gold", out var goldBefore);
        Assert.Contains(inventoryBefore.Items, pair =>
            pair.Key.Equals("red tincture", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Equals("red_tincture", StringComparison.OrdinalIgnoreCase));
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio will buy red tincture if you offer it.",
            anchorEntityId: lio.Id.Value,
            subject: "red tincture",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new SellCommand("red tincture", "Lio"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        var inventoryAfter = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("sell:prisoner_1", promise.RealizedIn);
        Assert.Equal("trade", promise.TriggerHint);
        Assert.Equal("merchant_stock", promise.RealizationKind);
        Assert.True(lio.Has<MerchantComponent>());
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "executeTrade"
            && Equals(delta.Details["mode"], "sell"));
        Assert.Contains(result.Messages, message =>
            message.Contains("ready to buy red tincture", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(inventoryAfter.Items, pair =>
            pair.Key.Equals("red tincture", StringComparison.OrdinalIgnoreCase)
            || pair.Key.Equals("red_tincture", StringComparison.OrdinalIgnoreCase));
        Assert.True(inventoryAfter.Items.TryGetValue("gold", out var goldAfter));
        Assert.True(goldAfter > goldBefore);
    }

    [Fact]
    public async Task ServicesRealizesAnchoredServicePromise()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        Assert.False(lio.Has<ServiceComponent>());
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio can break the ward on the cell door with grave salt.",
            anchorEntityId: lio.Id.Value,
            subject: "ward-breaking",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new ServicesCommand("Lio"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("services:prisoner_1", promise.RealizedIn);
        Assert.Equal("service", promise.TriggerHint);
        Assert.Equal("service", promise.RealizationKind);
        Assert.True(lio.TryGet<ServiceComponent>(out var services));
        var offer = Assert.Single(services.Offers);
        Assert.Equal("ward_breaking", offer.Id);
        Assert.Equal("open_or_unlock", offer.EffectKind);
        Assert.Equal("grave salt", offer.ItemCost);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferService));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "services"));
        Assert.Contains(result.Messages, message =>
            message.Contains("reveals the promised service", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message =>
            message.Contains("ward-breaking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnchoredPromiseSelectionPrefersTriggerSpecificPayoffWithinBudget()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var generic = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio knows an impressive omen that is not the service being requested.",
            anchorEntityId: lio.Id.Value,
            realizationKind: "omen",
            subject: "impressive omen",
            playerVisible: true,
            salience: 5,
            operation: "testCreateGenericPromise",
            emitMessage: false));
        var service = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio can break the ward on the cell door with grave salt.",
            anchorEntityId: lio.Id.Value,
            triggerHint: "services",
            realizationKind: "service",
            subject: "ward-breaking",
            playerVisible: true,
            salience: 3,
            operation: "testCreateServicePromise",
            emitMessage: false));
        Assert.True(generic.Applied, generic.Error);
        Assert.True(service.Applied, service.Error);

        var messages = new List<string>();
        var deltas = new PromiseRealizationSystem(session.Engine.State, session.Engine)
            .RealizeAnchoredPromises(lio, "services", messages, budget: 1);

        var genericPromise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == generic.TargetId);
        var servicePromise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == service.TargetId);
        var realized = Assert.Single(deltas, delta => delta.Operation == "realizePromise");
        var plan = Assert.Single(deltas, delta => delta.Operation == "promiseRealizationPlan");
        Assert.Equal(service.TargetId, realized.Target);
        Assert.Equal(service.TargetId, plan.Target);
        Assert.Equal("service", plan.Details["handler"]);
        Assert.Equal("services:prisoner_1", plan.Details["realizedIn"]);
        var planReasons = Assert.IsAssignableFrom<IEnumerable<string>>(plan.Details["selectionReasons"]);
        Assert.Contains("trigger:exact", planReasons);
        Assert.Contains("bound_target:matched", planReasons);
        Assert.Contains("handler:service", planReasons);
        Assert.Equal("bound", genericPromise.Status);
        Assert.Equal("budgeted_out", genericPromise.LastEligibilityFailure);
        Assert.NotNull(genericPromise.LastEligibilityContext);
        Assert.Contains("anchor=prisoner_1", genericPromise.LastEligibilityContext, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("realized", servicePromise.Status);
        Assert.Equal("services:prisoner_1", servicePromise.RealizedIn);
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseEligibility"
            && delta.Target == generic.TargetId
            && Equals(delta.Details["failure"], "budgeted_out")
            && !delta.IsPlayerVisible());
        Assert.True(Convert.ToInt32(realized.Details["selectionScore"]) > 0);
        Assert.True(lio.Has<ServiceComponent>());
        Assert.Contains(messages, message =>
            message.Contains("reveals the promised service", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnchoredTownPromiseDispatchesThroughRegisteredSiteHandler()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio knows there is a town called Hollowmere south of here.",
            anchorEntityId: lio.Id.Value,
            triggerHint: "talk",
            realizationKind: "town",
            subject: "Hollowmere",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var messages = new List<string>();
        var deltas = new PromiseRealizationSystem(session.Engine.State, session.Engine)
            .RealizeAnchoredPromises(lio, "talk", messages, budget: 1);

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("realized", promise.Status);
        var plan = Assert.Single(deltas, delta => delta.Operation == "promiseRealizationPlan");
        Assert.Equal("site", plan.Details["handler"]);
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseCanon"
            && Equals(delta.Details["kind"], "site"));
        Assert.Contains(messages, message =>
            message.Contains("A distant place answers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AnchoredConcretePlanPreflightKeepsPromiseBoundWhenNoAdjacentTile()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        var state = session.Engine.State;
        state.ControlledEntity.Set(new PositionComponent(new GridPoint(20, 20)));
        BlockAdjacentTiles(state, lio.Get<PositionComponent>().Position);
        BlockAdjacentTiles(state, state.ControlledEntity.Get<PositionComponent>().Position);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio keeps a promised pearl in his sleeve.",
            anchorEntityId: lio.Id.Value,
            triggerHint: "inspect",
            realizationKind: "item",
            subject: "promised pearl",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var messages = new List<string>();
        var deltas = new PromiseRealizationSystem(state, session.Engine)
            .RealizeAnchoredPromises(lio, "inspect", messages, budget: 1);

        var promise = state.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("bound", promise.Status);
        Assert.Equal("no_open_adjacent_tile", promise.LastEligibilityFailure);
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseRealizationPlan"
            && delta.Target == create.TargetId
            && Equals(delta.Details["handler"], "item"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseEligibility"
            && delta.Target == create.TargetId
            && Equals(delta.Details["failure"], "no_open_adjacent_tile")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseStatus");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "realizePromise");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseItem");
        Assert.Empty(messages);
    }

    [Fact]
    public void AnchoredHandlerRejectionDoesNotSpendPromise()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var anchor = new Entity(EntityId.Create("claim_marker_without_position"), "claim marker without position");
        state.Entities[anchor.Id] = anchor;
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "There is a hidden passage where the marker points.",
            anchorEntityId: anchor.Id.Value,
            triggerHint: "inspect",
            realizationKind: "escape_route",
            subject: "hidden passage",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);
        Assert.True(anchor.Has<PromiseAnchorComponent>());

        var messages = new List<string>();
        var deltas = new PromiseRealizationSystem(state, session.Engine)
            .RealizeAnchoredPromises(anchor, "inspect", messages, budget: 1);

        var promise = state.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.Equal("bound", promise.Status);
        Assert.NotNull(promise.LastEligibilityFailure);
        Assert.StartsWith("route_consequence_needs", promise.LastEligibilityFailure, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseRealizationPlan"
            && delta.Target == promise.Id
            && Equals(delta.Details["handler"], "escape_route"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreateRoute));
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseEligibility"
            && delta.Target == promise.Id
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseStatus");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "realizePromise");
        Assert.Empty(messages);
    }

    [Fact]
    public void AnchoredStatusRejectionRollsBackCreatedPayoffContent()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var anchor = session.Engine.EntityById("brazier_1")!;
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "There is a silver knife hidden in the brazier ash.",
            anchorEntityId: anchor.Id.Value,
            triggerHint: "inspect",
            realizationKind: "item",
            subject: "silver knife",
            playerVisible: true,
            salience: 5,
            operation: "testCreatePromise",
            emitMessage: false));
        var entityCountBefore = state.Entities.Count;

        WorldConsequenceApplyResult Apply(WorldConsequence consequence)
        {
            var operation = consequence.Payload is not null
                && consequence.Payload.TryGetValue("operation", out var rawOperation)
                ? Convert.ToString(rawOperation)
                : null;
            if (consequence.Type.Equals(WorldConsequenceTypes.UpdatePromise, StringComparison.OrdinalIgnoreCase)
                && operation?.Equals("promiseStatus", StringComparison.OrdinalIgnoreCase) == true)
            {
                const string error = "forced promise status rejection";
                var details = new Dictionary<string, object?>
                {
                    ["consequenceType"] = consequence.Type,
                    ["operation"] = operation,
                    ["error"] = error,
                    ["playerVisible"] = false,
                };
                return new WorldConsequenceApplyResult(
                    false,
                    consequence.TargetEntityId,
                    error,
                    Array.Empty<string>(),
                    new[] { new StateDelta("worldConsequenceRejected", consequence.TargetEntityId ?? "", error, details) },
                    details);
            }

            return session.Engine.ApplyConsequence(consequence);
        }

        var messages = new List<string>();
        var deltas = new PromiseRealizationSystem(state, applyConsequence: Apply)
            .RealizeAnchoredPromises(anchor, "inspect", messages, budget: 1);

        var promise = state.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(create.Applied, create.Error);
        Assert.Equal("bound", promise.Status);
        Assert.Equal("promise_status_rejected", promise.LastEligibilityFailure);
        Assert.Equal(entityCountBefore, state.Entities.Count);
        Assert.Empty(messages);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseItem");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseItemMessage");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseStatus");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "realizePromise");
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseRealizationSkipped"
            && delta.Target == promise.Id
            && Equals(delta.Details["failure"], "promise_status_rejected")
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["error"], "forced promise status rejection"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseEligibility"
            && delta.Target == promise.Id
            && Equals(delta.Details["failure"], "promise_status_rejected")
            && !delta.IsPlayerVisible());
    }

    [Fact]
    public void AnchoredCanonRejectionRollsBackCreatedPayoffContent()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var anchor = session.Engine.EntityById("brazier_1")!;
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "There is a silver knife hidden in the brazier ash.",
            anchorEntityId: anchor.Id.Value,
            triggerHint: "inspect",
            realizationKind: "item",
            subject: "silver knife",
            playerVisible: true,
            salience: 5,
            operation: "testCreatePromise",
            emitMessage: false));
        var entityCountBefore = state.Entities.Count;

        WorldConsequenceApplyResult Apply(WorldConsequence consequence)
        {
            var operation = consequence.Payload is not null
                && consequence.Payload.TryGetValue("operation", out var rawOperation)
                ? Convert.ToString(rawOperation)
                : null;
            if (consequence.Type.Equals(WorldConsequenceTypes.AddCanon, StringComparison.OrdinalIgnoreCase)
                && operation?.Equals("promiseCanon", StringComparison.OrdinalIgnoreCase) == true)
            {
                const string error = "forced_canon_rejection";
                var details = new Dictionary<string, object?>
                {
                    ["consequenceType"] = consequence.Type,
                    ["operation"] = operation,
                    ["error"] = error,
                    ["playerVisible"] = false,
                };
                return new WorldConsequenceApplyResult(
                    false,
                    consequence.TargetEntityId,
                    error,
                    Array.Empty<string>(),
                    new[] { new StateDelta("worldConsequenceRejected", consequence.TargetEntityId ?? "", error, details) },
                    details);
            }

            return session.Engine.ApplyConsequence(consequence);
        }

        var messages = new List<string>();
        var deltas = new PromiseRealizationSystem(state, applyConsequence: Apply)
            .RealizeAnchoredPromises(anchor, "inspect", messages, budget: 1);

        var promise = state.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(create.Applied, create.Error);
        Assert.Equal("bound", promise.Status);
        Assert.Equal("forced_canon_rejection", promise.LastEligibilityFailure);
        Assert.Equal(entityCountBefore, state.Entities.Count);
        Assert.Empty(messages);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseItem");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseItemMessage");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseCanon");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "promiseStatus");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "realizePromise");
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseRealizationSkipped"
            && delta.Target == promise.Id
            && Equals(delta.Details["failure"], "forced_canon_rejection")
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["error"], "forced_canon_rejection"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "promiseEligibility"
            && delta.Target == promise.Id
            && Equals(delta.Details["failure"], "forced_canon_rejection")
            && !delta.IsPlayerVisible());
    }

    [Fact]
    public async Task RequestRealizesAnchoredServicePromiseBeforeApplyingService()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Lio can break the ward on the cell door with grave salt.",
            anchorEntityId: lio.Id.Value,
            subject: "ward-breaking",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new RequestServiceCommand("ward-breaking", "Lio"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        var door = session.Engine.EntityById("cell_door_1")!;
        var inventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("request:prisoner_1", promise.RealizedIn);
        Assert.True(lio.Has<ServiceComponent>());
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Null(door.Get<DoorComponent>().KeyId);
        Assert.Equal(1, inventory.Items["grave salt"]);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferService));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "requestService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.Contains(result.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceCost"
            && Equals(delta.Details["item"], "grave salt"));
    }

    [Fact]
    public async Task OpenRealizesAnchoredDoorRulePromiseBeforeLockCheck()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var door = session.Engine.EntityById("cell_door_1")!;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "The locked imperial cell door opens when the promise is tested.",
            anchorEntityId: door.Id.Value,
            triggerHint: "open",
            realizationKind: "door_rule",
            subject: "cell door",
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);
        Assert.False(door.Get<DoorComponent>().IsOpen);
        Assert.NotNull(door.Get<DoorComponent>().KeyId);

        var result = await session.ExecuteAsync(new OpenCommand("cell"));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("realized", promise.Status);
        Assert.Equal("open:cell_door_1", promise.RealizedIn);
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Null(door.Get<DoorComponent>().KeyId);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "promiseDoorRule"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OpenOrUnlock)
            && Equals(delta.Details["open"], true));
        Assert.Single(result.Deltas, delta => delta.Operation == "promiseDoorRule");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "open"));
        Assert.Contains(result.Deltas, delta => delta.Operation == "freeCaptiveFaction");
        Assert.Contains(result.Messages, message =>
            message.Contains("obeys the promised door rule", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Messages, message =>
            message.Equals("locked imperial cell door is locked.", StringComparison.OrdinalIgnoreCase));
    }

    private static void BlockAdjacentTiles(GameState state, GridPoint origin)
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
            var point = origin.Translate(offset.X, offset.Y);
            if (point.X > 0
                && point.Y > 0
                && point.X < state.Width - 1
                && point.Y < state.Height - 1)
            {
                state.BlockingTerrain.Add(point);
            }
        }
    }
}
