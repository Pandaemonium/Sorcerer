using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ObligationSettlementTests
{
    [Fact]
    public void GroundedBargainRepairRecoversOnlyExactUnprotectedSpokenResources()
    {
        var request = new DialogueClaimRequest(
            4,
            "imperial_encounter",
            "0,0",
            "soldier_1",
            "imperial containment soldier",
            new[] { "soldier", "imperial" },
            "player_soul",
            "State exact terms.",
            new[]
            {
                "Pay all fifteen gold and hand me red tincture x1; in exchange I will turn my back and let you leave.",
            },
            Array.Empty<WorldMemoryRecord>(),
            Array.Empty<ClaimRecord>(),
            "player",
            SelectedParserCapabilityIds: new[] { "bargains" },
            ListenerInventory: new[] { "gold x15", "red tincture x1", "moon pearl x1 [protected]" });

        var repaired = GameSession.RepairGroundedBargain(request);

        var option = Assert.Single(Assert.IsType<BargainOffer>(repaired).Options);
        Assert.Equal(15, option.Terms.Single(term => term.ResourceId == "gold").Quantity);
        Assert.Equal(1, option.Terms.Single(term => term.ResourceId == "red tincture").Quantity);
        Assert.DoesNotContain(option.Terms, term => term.ResourceId == "moon pearl");
    }

    [Fact]
    public void ImmediateTypedTermsApplyAtomicallyAndOnlyThenClearTheAgreement()
    {
        var session = GameSession.CreateImperialEncounter(seed: 11);
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var claimant = AddClaimant(session, "many_term_claimant", "many-term claimant", "catalogue a missing relic");
        claimant.Set(InventoryComponent.Empty());
        player.Get<InventoryComponent>().Items["gold"] = 20;
        player.Get<InventoryComponent>().Items["grave salt"] = 2;
        var debt = AddDebt(state, claimant, "Settle the claimant's many-part account.");
        var offer = new BargainOffer(
            claimant.Id.Value,
            "Every term is concrete.",
            new[]
            {
                new BargainOption("full_account", "pay the full account", new BargainTerm[]
                {
                    new("coin", BargainTermKinds.Currency, "Pay 7 gold.", 7, "gold"),
                    new("salt", BargainTermKinds.Item, "Give one measure of grave salt.", 1, "grave salt"),
                    new("standing", BargainTermKinds.Standing, "Yield standing with Hollowmere.", FactionId: "hollowmere", StandingAxis: "freedom", StandingDelta: -2),
                    new("concession", BargainTermKinds.Concession, "The sorcerer surrenders salvage rights to the blue reliquary."),
                }),
            },
            state.Turn);

        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.OfferBargain("test", debt.Id, offer, sourceEntityId: claimant.Id.Value)).Applied);
        var accepted = session.Engine.ApplyConsequence(WorldConsequence.AcceptBargain(
            "test", debt.Id, "full_account", player.Id.Value));

        Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal(13, player.Get<InventoryComponent>().Items["gold"]);
        Assert.Equal(7, claimant.Get<InventoryComponent>().Items["gold"]);
        Assert.Equal(1, player.Get<InventoryComponent>().Items["grave salt"]);
        Assert.Equal(1, claimant.Get<InventoryComponent>().Items["grave salt"]);
        Assert.Equal(-2, state.Factions.StandingValue("hollowmere", "freedom"));
        Assert.Contains(state.Canon.Records, record => record.Text.Contains("blue reliquary", StringComparison.OrdinalIgnoreCase));
        var settled = state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id);
        Assert.Equal("cleared", settled.Status);
        Assert.Equal("fulfilled", settled.BargainAgreement!.Status);
        Assert.All(settled.BargainAgreement.Terms, term => Assert.Equal("fulfilled", term.Status));
    }

    [Fact]
    public void FailedImmediateTermRollsBackEveryEarlierPayment()
    {
        var session = GameSession.CreateImperialEncounter(seed: 12);
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var claimant = AddClaimant(session, "rollback_claimant", "rollback claimant", "recover a numbered seal");
        claimant.Set(InventoryComponent.Empty());
        player.Get<InventoryComponent>().Items["gold"] = 20;
        player.Get<InventoryComponent>().Items.Remove("ambergris lump");
        var debt = AddDebt(state, claimant, "Settle an account that cannot presently be paid.");
        var offered = session.Engine.ApplyConsequence(WorldConsequence.OfferBargain(
            "test",
            debt.Id,
            new BargainOffer(claimant.Id.Value, "Both parts or nothing.", new[]
            {
                new BargainOption("impossible", "coin and missing ambergris", new BargainTerm[]
                {
                    new("coin", BargainTermKinds.Currency, "Pay 5 gold.", 5, "gold"),
                    new("ambergris", BargainTermKinds.Item, "Give ambergris.", 1, "ambergris lump"),
                }),
            }, state.Turn),
            sourceEntityId: claimant.Id.Value));
        Assert.True(offered.Applied, offered.Error);

        var accepted = session.Engine.ApplyConsequence(WorldConsequence.AcceptBargain(
            "test", debt.Id, "impossible", player.Id.Value));

        Assert.False(accepted.Applied);
        Assert.Equal(20, state.ControlledEntity.Get<InventoryComponent>().Items["gold"]);
        Assert.Equal(0, state.Entities[claimant.Id].Get<InventoryComponent>().Items.GetValueOrDefault("gold"));
        var unresolved = state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id);
        Assert.NotNull(unresolved.BargainOffer);
        Assert.Null(unresolved.BargainAgreement);
    }

    [Fact]
    public async Task ServiceAndDeadlineRemainPendingUntilARealWantConsequenceOccurs()
    {
        var session = GameSession.CreateImperialEncounter(seed: 13);
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var claimant = AddClaimant(session, "service_claimant", "service claimant", "repair the rain tally");
        var want = claimant.Get<WantComponent>();
        var debt = AddDebt(state, claimant, "Repair the claimant's rain tally.");
        var offered = session.Engine.ApplyConsequence(WorldConsequence.OfferBargain(
            "test",
            debt.Id,
            new BargainOffer(claimant.Id.Value, "Work may settle it.", new[]
            {
                new BargainOption("repair", "repair the tally", new BargainTerm[]
                {
                    new("service", BargainTermKinds.Service, "Repair the rain tally.", ResourceId: want.Id),
                    new("deadline", BargainTermKinds.Deadline, $"Finish by turn {state.Turn + 4}.", DueTurn: state.Turn + 4),
                }),
            }, state.Turn),
            sourceEntityId: claimant.Id.Value));
        Assert.True(offered.Applied, offered.Error);
        var accepted = session.Engine.ApplyConsequence(WorldConsequence.AcceptBargain(
            "test", debt.Id, "repair", player.Id.Value));
        Assert.True(accepted.Applied, accepted.Error);
        Assert.Equal("agreement", state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id).Status);
        Assert.Equal("active", claimant.Get<WantComponent>().Status);

        var fulfilled = await session.ExecuteAsync(new FulfillCommand($"{debt.Id} service"));

        Assert.True(fulfilled.Success, string.Join(" | ", fulfilled.Messages));
        Assert.Equal("satisfied", claimant.Get<WantComponent>().Status);
        Assert.Contains("bargain_fulfilled", claimant.Get<WantComponent>().Tags);
        var settled = state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id);
        Assert.Equal("cleared", settled.Status);
        Assert.Equal("fulfilled", settled.BargainAgreement!.Status);
        Assert.Contains(fulfilled.Deltas, delta => delta.Operation == "fulfillBargainWant");
    }

    [Fact]
    public void ExpiredDeadlineBreachesAgreementAndRestoresClaimantHostility()
    {
        var session = GameSession.CreateImperialEncounter(seed: 14);
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var claimant = AddClaimant(session, "deadline_claimant", "deadline claimant", "return the drowned bell");
        var want = claimant.Get<WantComponent>();
        claimant.Set(new ActorComponent(8, 8, 0, 0, 2, 0, "empire"));
        claimant.Set(new AiComponent("hostile"));
        var debt = AddDebt(state, claimant, "Return a drowned bell before the deadline.");
        var offered = session.Engine.ApplyConsequence(WorldConsequence.OfferBargain(
            "test",
            debt.Id,
            new BargainOffer(claimant.Id.Value, "The bell buys one night's peace.", new[]
            {
                new BargainOption("bell", "return the bell", new BargainTerm[]
                {
                    new("service", BargainTermKinds.Service, "Return the drowned bell.", ResourceId: want.Id),
                    new("deadline", BargainTermKinds.Deadline, "Before turn 1 ends.", DueTurn: state.Turn + 1),
                }),
            }, state.Turn),
            sourceEntityId: claimant.Id.Value));
        Assert.True(offered.Applied, offered.Error);
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.AcceptBargain(
            "test", debt.Id, "bell", player.Id.Value)).Applied);
        Assert.False(session.Engine.IsHostile(claimant, player));

        session.Engine.AdvanceTurn();
        var breachDeltas = session.Engine.AdvanceTurn();

        var breached = state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id);
        Assert.Equal("breached", breached.Status);
        Assert.Equal("breached", breached.BargainAgreement!.Status);
        Assert.Equal("betrayer", state.Bonds.TryGet(claimant.Get<SoulComponent>().SoulId, player.Get<SoulComponent>().SoulId, out var bond) ? bond.Posture : null);
        Assert.Equal("hostile", claimant.Get<AiComponent>().PolicyId);
        Assert.True(session.Engine.IsHostile(claimant, player));
        Assert.Contains(breachDeltas, delta => delta.Operation == "breachBargainDeadline");
        Assert.Contains(breachDeltas, delta => delta.Operation == "bargainBreachBond");
    }

    [Fact]
    public void DeadlineCannotBeOfferedAsASettlementByItself()
    {
        var session = GameSession.CreateImperialEncounter(seed: 15);
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var claimant = AddClaimant(session, "deadline_only_claimant", "deadline-only claimant", "hear a concrete offer");
        var debt = AddDebt(state, claimant, "Reject empty terms.");

        var result = session.Engine.ApplyConsequence(WorldConsequence.OfferBargain(
            "test",
            debt.Id,
            new BargainOffer(claimant.Id.Value, "Wait and see.", new[]
            {
                new BargainOption("wait", "wait", new[]
                {
                    new BargainTerm("deadline", BargainTermKinds.Deadline, "Wait until turn 5.", DueTurn: state.Turn + 5),
                }),
            }, state.Turn),
            sourceEntityId: claimant.Id.Value));

        Assert.False(result.Applied);
        Assert.Contains("cannot be the settlement", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TypedTermsSettleAnchoredDebtAndEndClaimantHostility()
    {
        var session = GameSession.CreateImperialEncounter();
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        var debt = state.PromiseLedger.Add(
            "debt",
            "An imperial claimant will collect, but may bargain.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "the red ledger");
        state.PromiseLedger.SetStatus(debt.Id, "realized", state.RegionId);
        var claimant = new Entity(EntityId.Create("test_claimant"), "water-memoried claimant")
            .Set(new PositionComponent(new GridPoint(origin.X + 1, origin.Y)))
            .Set(new ActorComponent(8, 8, 0, 0, 2, 0, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile"))
            .Set(new SoulComponent("test_claimant_soul"))
            .Set(new TagsComponent(new[] { "npc", "claimant", "threat" }))
            .Set(new PromiseAnchorComponent(debt.Id))
            .Set(MemoryComponent.Empty());
        state.Entities[claimant.Id] = claimant;
        var offered = session.Engine.ApplyConsequence(WorldConsequence.OfferBargain(
            "dialogue_exchange",
            debt.Id,
            new BargainOffer(
                claimant.Id.Value,
                "The ledger closes for a concrete concession.",
                new[]
                {
                    new BargainOption(
                        "surrender_claim",
                        "surrender the disputed salvage claim",
                        new[]
                        {
                            new BargainTerm(
                                "salvage_concession",
                                BargainTermKinds.Concession,
                                "The sorcerer surrenders the disputed salvage claim."),
                        }),
                },
                state.Turn),
            sourceEntityId: claimant.Id.Value));
        Assert.True(offered.Applied, offered.Error);
        var listedBySpokenName = await session.ExecuteAsync(new BargainsCommand("water memoried claimant"));
        Assert.Contains(listedBySpokenName.Messages, message =>
            message.Contains("surrender_claim", StringComparison.OrdinalIgnoreCase));
        var hpBefore = player.Get<ActorComponent>().HitPoints;
        var claimantCard = Assert.Single(session.View().Entities, entity => entity.Id == claimant.Id.Value);
        var settleAction = Assert.Single(claimantCard.Actions!, action => action.Id == "settle");
        Assert.True(settleAction.Enabled);
        Assert.Equal($"settle {claimant.Id.Value} with surrender_claim", settleAction.Command);

        var result = await session.ExecuteAsync(new SettleCommand("claimant with surrender_claim"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.Equal(result.TurnBefore + 1, result.TurnAfter);
        Assert.Equal("cleared", state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id).Status);
        var agreement = state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id).BargainAgreement;
        Assert.NotNull(agreement);
        Assert.Equal("fulfilled", agreement!.Status);
        Assert.All(agreement.Terms, term => Assert.Equal("fulfilled", term.Status));
        Assert.Equal("empire", claimant.Get<ActorComponent>().Faction);
        Assert.Equal("resident", claimant.Get<AiComponent>().PolicyId);
        Assert.True(state.Bonds.TryGet(
            claimant.Get<SoulComponent>().SoulId,
            player.Get<SoulComponent>().SoulId,
            out var bond));
        Assert.True(bond.Loyalty >= 5);
        Assert.False(session.Engine.IsHostile(claimant, player));
        Assert.DoesNotContain(session.Engine.DescribeThreats(), threat => threat.EntityId == claimant.Id.Value);
        Assert.NotEqual(claimant.Id, session.Engine.FindNearestHostile()?.Id);
        Assert.Equal(hpBefore, player.Get<ActorComponent>().HitPoints);
        Assert.Contains(state.Canon.Records, record =>
            record.AttachedTo == claimant.Id.Value
            && record.Text.Contains("salvage claim", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta => delta.Operation == "acceptBargain");
        Assert.Contains(result.Deltas, delta => delta.Operation == "recordBargainConcession");
        Assert.Contains(result.Messages, message => message.Contains("claim is cleared", StringComparison.OrdinalIgnoreCase));
        claimantCard = Assert.Single(session.View().Entities, entity => entity.Id == claimant.Id.Value);
        Assert.DoesNotContain(claimantCard.Actions!, action => action.Id == "settle");
    }

    [Fact]
    public async Task SettlementRequiresTermsFromAConversation()
    {
        var session = GameSession.CreateImperialEncounter();
        NeutralizeOpeningActors(session);
        var state = session.Engine.State;
        var origin = state.ControlledEntity.Get<PositionComponent>().Position;
        var debt = state.PromiseLedger.Add("debt", "A collector arrives.", subject: "the debt");
        state.PromiseLedger.SetStatus(debt.Id, "realized", state.RegionId);
        var claimant = new Entity(EntityId.Create("silent_claimant"), "silent claimant")
            .Set(new PositionComponent(new GridPoint(origin.X + 1, origin.Y)))
            .Set(new ActorComponent(8, 8, 0, 0, 2, 0, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile"))
            .Set(new SoulComponent("silent_claimant_soul"))
            .Set(new PromiseAnchorComponent(debt.Id));
        state.Entities[claimant.Id] = claimant;

        var result = await session.ExecuteAsync(new SettleCommand("silent"));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Contains(result.Messages, message => message.Contains("engine-verifiable terms", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("realized", state.PromiseLedger.Promises.Single(promise => promise.Id == debt.Id).Status);
    }

    private static void NeutralizeOpeningActors(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values
                     .Where(entity => entity.TryGet<ActorComponent>(out _)))
        {
            var actor = entity.Get<ActorComponent>();
            if (entity.Id != session.Engine.State.ControlledEntityId)
            {
                entity.Set(actor with { Faction = "neutral" });
            }
        }
    }

    private static Entity AddClaimant(GameSession session, string id, string name, string wantText)
    {
        var state = session.Engine.State;
        var origin = state.ControlledEntity.Get<PositionComponent>().Position;
        var claimant = new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(origin.Translate(1, 0)))
            .Set(new ActorComponent(8, 8, 0, 0, 1, 0, "neutral"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("resident"))
            .Set(new SoulComponent(id + "_soul"))
            .Set(new TagsComponent(new[] { "npc", "claimant" }))
            .Set(new WantComponent(id + "_want", wantText, 4))
            .Set(MemoryComponent.Empty());
        state.Entities[claimant.Id] = claimant;
        return claimant;
    }

    private static WorldPromise AddDebt(GameState state, Entity claimant, string text)
    {
        var debt = state.PromiseLedger.Add("debt", text, playerVisible: true, source: "test", salience: 4);
        state.PromiseLedger.SetStatus(debt.Id, "realized", state.RegionId);
        claimant.Set(new PromiseAnchorComponent(debt.Id));
        return debt;
    }
}
