using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;
using Sorcerer.Magic.Costs;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class CostProfileTests
{
    [Fact]
    public void CostProfilesMeetTheFloorAndCarryCounterplay()
    {
        var catalog = CostProfileCatalog.LoadDefault();

        Assert.True(catalog.Profiles.Count >= 9, $"expected >=9 cost profiles, found {catalog.Profiles.Count}");
        Assert.True(catalog.OfKind("curse").Count >= 4);
        Assert.True(catalog.OfKind("debt").Count >= 3);
        Assert.True(catalog.OfKind("altered_item").Count >= 3);

        // Every profile has a mechanical condition, a cause, a journal surface, and at least one
        // route to clear / transfer / exploit / bargain / endure it.
        Assert.All(catalog.Profiles, profile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Condition), $"{profile.Id} needs a condition");
            Assert.False(string.IsNullOrWhiteSpace(profile.Cause), $"{profile.Id} needs a cause");
            Assert.False(string.IsNullOrWhiteSpace(profile.JournalSurface), $"{profile.Id} needs a journal surface");
            Assert.True(profile.ClearRoutes.Count >= 1, $"{profile.Id} needs at least one counterplay route");
        });
    }

    [Fact]
    public void LooseAndEmbeddedCostProfilesAgree()
    {
        var loose = CostProfileCatalog.LoadDefault();
        var embedded = CostProfileCatalog.LoadEmbedded();
        Assert.Equal(
            loose.Profiles.Select(p => p.Id).OrderBy(id => id, System.StringComparer.OrdinalIgnoreCase),
            embedded.Profiles.Select(p => p.Id).OrderBy(id => id, System.StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void AuthoredCurseProfileCreatesDurableRuntimeCondition()
    {
        var session = GameSession.CreateImperialEncounter();
        var deltas = SpellCostApplier.Apply(
            session.Engine,
            new[]
            {
                new SpellCost(
                    "curse",
                    new Dictionary<string, object?> { ["profileId"] = "curse_hollow_name" }),
            });

        var curse = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Kind == "curse" && promise.CostProfileId == "curse_hollow_name");
        Assert.True(curse.PlayerVisible);
        Assert.Equal(session.Engine.State.ControlledEntityId.Value, curse.BoundTargetId);
        Assert.Contains(session.Engine.State.ControlledEntity.Get<StatusContainerComponent>().Statuses,
            status => status.Id == "hollow_name");
        Assert.Contains(deltas, delta => delta.Operation == "cost:curse");
    }

    [Fact]
    public void SelectedDebtProfileBecomesAVisibleCounterplayBearingTravelPressure()
    {
        var session = GameSession.CreateImperialEncounter();
        var deltas = SpellCostApplier.Apply(
            session.Engine,
            new[]
            {
                new SpellCost(
                    "curse",
                    new Dictionary<string, object?> { ["profileId"] = "debt_imperial_ledger" }),
            });

        var profile = CostProfileCatalog.Default.Find("debt_imperial_ledger")!;
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, record => record.Kind == "debt");
        Assert.Equal("debt", promise.Kind);
        Assert.True(promise.PlayerVisible);
        Assert.Equal("bound", promise.Status);
        Assert.Contains(profile.Condition, promise.Text, StringComparison.Ordinal);
        Assert.All(profile.ClearRoutes, route => Assert.Contains(route, promise.Text, StringComparison.Ordinal));
        Assert.Equal("travel", promise.TriggerHint);
        Assert.Equal("threat", promise.RealizationKind);
        Assert.Contains(deltas, delta =>
            delta.Operation == "cost:curse"
            && Equals(delta.Details["profileId"], profile.Id));

        var journal = JournalViewBuilder.BuildStructured(session.Engine.State);
        Assert.Contains(journal.Objectives, entry => entry.Text.Contains(profile.Name, StringComparison.Ordinal));

        session.Engine.Travel(Direction.East);

        Assert.Equal("realized", session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id).Status);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("threat", StringComparer.OrdinalIgnoreCase)
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void BorrowedTideHealsOnWaterAndBitesOnDryEvenTurns()
    {
        var session = GameSession.CreateImperialEncounter(seed: 11);
        var player = session.Engine.State.ControlledEntity;
        var actor = player.Get<ActorComponent>();
        player.Set(actor with { HitPoints = actor.MaxHitPoints - 4 });
        SpellCostApplier.Apply(session.Engine, new[]
        {
            new SpellCost("curse", new Dictionary<string, object?>
            {
                ["profileId"] = "curse_tide_debt_body",
            }),
        });

        var point = player.Get<PositionComponent>().Position;
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test", point.X, point.Y, "shallow_water")).Applied);
        var wet = session.Engine.AdvanceTurn();
        Assert.Contains(wet, delta => delta.Operation == "borrowedTideWet");

        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test", point.X, point.Y, "dry_floor")).Applied);
        var dry = session.Engine.AdvanceTurn();
        Assert.Contains(dry, delta => delta.Operation == "borrowedTideDry");
    }

    [Fact]
    public async Task IronThirstHasTypedWeaknessAndQuicklimeAshActuallyCleansesIt()
    {
        var session = GameSession.CreateImperialEncounter(seed: 13);
        var player = session.Engine.State.ControlledEntity;
        player.Get<InventoryComponent>().Items.Remove("grave salt");
        player.Get<InventoryComponent>().Items["quicklime_ash"] = 1;
        SpellCostApplier.Apply(session.Engine, new[]
        {
            new SpellCost("curse", new Dictionary<string, object?>
            {
                ["profileId"] = "curse_iron_thirst",
            }),
        });

        var weakness = player.Get<ResistanceComponent>().Weaknesses;
        Assert.Equal(50, weakness["iron"]);
        Assert.Equal(50, weakness["metal"]);
        Assert.Equal(50, weakness["charter"]);

        var cleansed = await session.ExecuteAsync(new CleanseCommand("iron"));

        Assert.True(cleansed.Success, string.Join(" / ", cleansed.Messages));
        Assert.DoesNotContain("quicklime_ash", player.Get<InventoryComponent>().Items.Keys);
        Assert.All(new[] { "iron", "metal", "charter" }, type =>
            Assert.Equal(0, player.Get<ResistanceComponent>().Weaknesses.GetValueOrDefault(type)));
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.CostProfileId == "curse_iron_thirst" && promise.Status == "cleared");
    }

    [Fact]
    public void HollowNameDecaysDurableOffZoneBondsButOnlyForItsBoundBody()
    {
        var session = GameSession.CreateImperialEncounter(seed: 17);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var playerSoul = player.Get<SoulComponent>().SoulId;
        var soldierSoul = soldier.Get<SoulComponent>().SoulId;
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "test", soldier.Id.Value, playerSoul,
            loyaltyDelta: 4, fearDelta: 0, admirationDelta: 3, resentmentDelta: 0,
            posture: "curious", maxDelta: 4)).Applied);
        SpellCostApplier.Apply(session.Engine, new[]
        {
            new SpellCost("curse", new Dictionary<string, object?>
            {
                ["profileId"] = "curse_hollow_name",
            }),
        });
        state.Entities.Remove(soldier.Id);

        state.ControlledEntityId = EntityId.Create("prisoner_1");
        state.Turn = 11;
        var wrongBody = session.Engine.AdvanceTurn();
        Assert.DoesNotContain(wrongBody, delta => delta.Operation == "hollowNameBondDecay");
        Assert.True(state.Bonds.TryGet(soldierSoul, playerSoul, out var unchanged));
        Assert.Equal(4, unchanged.Loyalty);

        state.ControlledEntityId = player.Id;
        state.Turn = 23;
        var cadence = session.Engine.AdvanceTurn();

        Assert.Contains(cadence, delta => delta.Operation == "hollowNameBondDecay");
        Assert.True(state.Bonds.TryGet(soldierSoul, playerSoul, out var decayed));
        Assert.Equal(3, decayed.Loyalty);
        Assert.Equal(2, decayed.Admiration);
    }

    [Fact]
    public async Task AlteredFocusedItemSurfacesPersistsAndTransfersWithoutGhostEquipment()
    {
        var session = GameSession.CreateImperialEncounter(seed: 19);
        var player = session.Engine.State.ControlledEntity;
        Assert.True((await session.ExecuteAsync(new EquipCommand("charcoal wand"))).Success);
        Assert.True((await session.ExecuteAsync(new FocusCommand("charcoal wand"))).Success);
        SpellCostApplier.Apply(session.Engine, new[]
        {
            new SpellCost("curse", new Dictionary<string, object?>
            {
                ["profileId"] = "altered_wild_stained",
                ["item"] = "charcoal wand",
            }),
        });

        var card = Assert.Single(session.View().Inventory!, item => item.Name == "charcoal wand");
        Assert.Equal("altered_wild_stained", card.AlterationProfileId);
        Assert.Contains("Wild-Stained", card.Alteration, StringComparison.Ordinal);
        Assert.Contains(EquipmentEffectService.Effect(player).FocusBias,
            bias => bias.Contains("wild color", StringComparison.OrdinalIgnoreCase));

        var loaded = GameSaveService.Deserialize(GameSaveService.Serialize(session.Engine.State));
        var loadedPlayer = loaded.State.ControlledEntity;
        Assert.Equal("altered_wild_stained",
            loadedPlayer.Get<ItemAlterationComponent>().Profiles["charcoal wand"]);

        var recipient = session.Engine.EntityById("prisoner_1")!;
        var transfer = session.Engine.ApplyConsequence(WorldConsequence.TransferItem(
            "test", player.Id.Value, "give", "charcoal wand",
            recipientEntityId: recipient.Id.Value));

        Assert.True(transfer.Applied, transfer.Error);
        Assert.False(player.Get<InventoryComponent>().Items.ContainsKey("charcoal wand"));
        Assert.DoesNotContain(player.Get<EquipmentComponent>().Slots.Values,
            item => item.Equals("charcoal wand", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(EquipmentEffectService.Effect(player).FocusBias,
            bias => bias.Contains("wild color", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("altered_wild_stained",
            recipient.Get<ItemAlterationComponent>().Profiles["charcoal wand"]);
    }

    [Fact]
    public void AlteredItemCostsRejectUncarriedAndProtectedTargetsBeforeApplication()
    {
        var session = GameSession.CreateImperialEncounter(seed: 23);
        var player = session.Engine.State.ControlledEntity;
        player.Get<InventoryComponent>().TreasuredItems.Add("charcoal wand");
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The seal flickers.",
            Effects: new[]
            {
                new SpellEffect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["status"] = "warded",
                    ["duration"] = 1,
                }),
            },
            Costs: new[]
            {
                new SpellCost("curse", new Dictionary<string, object?>
                {
                    ["profileId"] = "altered_charter_touched",
                    ["item"] = "charcoal wand",
                }),
                new SpellCost("curse", new Dictionary<string, object?>
                {
                    ["profileId"] = "altered_bone_bound",
                    ["item"] = "imperial crown",
                }),
            },
            RejectedReason: null);

        var report = new SpellValidator().Validate(session.Engine, resolution, OperationRegistry.CreateDefault());

        Assert.Equal(2, report.Issues.Count(issue => issue.Code == "altered_item_cost_missing_target"));
    }
}
