using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Xunit;

namespace Sorcerer.Tests;

public sealed class FollowupSystemsTests
{
    [Fact]
    public void JourneyCompressionPausesWithinItsBoundedSceneBudget()
    {
        var session = GameSession.CreateImperialEncounter(seed: 19);
        NeutralizeActors(session);
        foreach (var promise in session.Engine.State.PromiseLedger.Promises.ToArray())
        {
            session.Engine.State.PromiseLedger.SetStatus(promise.Id, "cleared");
        }

        var first = session.Engine.Journey("hollowmere_margin");

        Assert.True(first.Success, string.Join(" | ", first.Messages));
        Assert.InRange(first.TurnAfter - first.TurnBefore, 1, 3);
        var journey = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise => promise.Journey is not null);
        Assert.Equal(2, journey.Journey!.SceneBudget);
        Assert.InRange(journey.Journey.ScenesSpent, 0, 1);

        for (var attempt = 0; attempt < 8
            && session.Engine.State.PromiseLedger.Promises.Single(promise => promise.Id == journey.Id).Status == "active";
            attempt++)
        {
            NeutralizeActors(session);
            session.Engine.Journey("hollowmere_margin");
        }

        journey = session.Engine.State.PromiseLedger.Promises.Single(promise => promise.Id == journey.Id);
        Assert.InRange(journey.Journey!.ScenesSpent, 0, journey.Journey.SceneBudget);
    }

    [Fact]
    public void FullBrallJourneyUsesThreeCommandsAndOnlyTwoAmbientStops()
    {
        var session = GameSession.CreateImperialEncounter(seed: 19);
        NeutralizeActors(session);
        var state = session.Engine.State;
        state.ControlledEntity.Set(state.ControlledEntity.Get<ActorComponent>() with { Faction = "neutral" });
        foreach (var promise in state.PromiseLedger.Promises.ToArray())
        {
            state.PromiseLedger.SetStatus(promise.Id, "cleared");
        }

        var results = new List<ActionResult>();
        for (var attempt = 0; attempt < 4; attempt++)
        {
            var result = session.Engine.Journey("Brall");
            results.Add(result);
            Assert.True(result.Success, string.Join(" | ", result.Messages));
            var journey = state.PromiseLedger.Promises.Single(promise => promise.Journey is not null);
            if (journey.Status == "completed")
            {
                break;
            }
        }

        var completed = Assert.Single(state.PromiseLedger.Promises, promise => promise.Journey is not null);
        Assert.Equal("completed", completed.Status);
        Assert.Equal(completed.Journey!.DestinationZoneId, state.CurrentZoneId);
        Assert.Equal(3, results.Count);
        Assert.Equal(2, results.SelectMany(result => result.Deltas).Count(delta => delta.Operation == "journeyScene"));
        Assert.Equal(completed.Journey.SceneBudget, completed.Journey.ScenesSpent);
        Assert.Equal(completed.Journey.ZonesCrossed, results.Sum(result => result.TurnAfter - result.TurnBefore));
        Assert.DoesNotContain(results.SelectMany(result => result.Deltas), delta => delta.Operation == "journeyInterruptedByPursuit");
    }

    [Fact]
    public void AnEngagedPursuerInterruptsJourneyAfterOneZone()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var player = session.Engine.State.ControlledEntity;
        var hostile = session.Engine.State.Entities.Values.First(entity =>
            entity.Id != player.Id && entity.TryGet<ActorComponent>(out var actor)
                && session.Engine.IsHostile(entity, player));
        hostile.Set(new PositionComponent(player.Get<PositionComponent>().Position.Translate(1, 0)));

        var result = session.Engine.Journey("hollowmere_margin");

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Equal(1, result.TurnAfter - result.TurnBefore);
        Assert.Contains(result.Deltas, delta => delta.Operation == "journeyInterruptedByPursuit");
        Assert.True(session.Engine.State.Entities.ContainsKey(hostile.Id));
    }

    [Fact]
    public void ExplicitTravelCarriesAnEngagedPursuerAcrossTheBoundary()
    {
        var session = GameSession.CreateImperialEncounter(seed: 31);
        NeutralizeActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var hostile = AddTravelThreat(session, "boundary_hunter", "boundary hunter", new[] { "enemy", "hunter" });

        var result = session.Engine.Travel(Direction.East);

        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta => delta.Operation == "pursuitContinues");
        Assert.True(state.Entities.ContainsKey(hostile.Id));
        Assert.DoesNotContain(hostile.Id, state.Zones["0,0"].Entities.Keys);
        Assert.True(session.Engine.IsHostile(state.Entities[hostile.Id], player));
    }

    [Fact]
    public void GuardPostsKeepTheirLeashInsteadOfUniversallyBlockingTravel()
    {
        var session = GameSession.CreateImperialEncounter(seed: 32);
        NeutralizeActors(session);
        var state = session.Engine.State;
        var guard = AddTravelThreat(session, "leashed_guard", "leashed gate guard", new[] { "enemy", "guard", "threshold_guard" });
        guard.Set(new AiComponent("guard"));

        var result = session.Engine.Travel(Direction.East);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "pursuitContinues");
        Assert.False(state.Entities.ContainsKey(guard.Id));
        Assert.Contains(guard.Id, state.Zones["0,0"].Entities.Keys);
    }

    [Fact]
    public async Task ArchetypeEnemyCommitsBeforeAttackingAndCanBeCountered()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        NeutralizeActors(session);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        var enemy = new Entity(EntityId.Create("intent_warden"), "intent warden")
            .Set(new PositionComponent(origin.Translate(1, 0)))
            .Set(new ActorComponent(12, 12, 0, 0, 4, 1, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile"))
            .Set(new SoulComponent("intent_warden_soul"))
            .Set(new TagsComponent(new[] { "enemy", "yard_warden" }))
            .Set(StatusContainerComponent.Empty());
        state.Entities[enemy.Id] = enemy;
        player.Get<InventoryComponent>().Items["compliance_writ"] = 1;
        var hpBefore = player.Get<ActorComponent>().HitPoints;

        var telegraphDeltas = session.Engine.RunActorTurns();

        Assert.Equal(hpBefore, player.Get<ActorComponent>().HitPoints);
        Assert.Contains(telegraphDeltas, delta => delta.Operation == "telegraphIntent");
        var threat = Assert.Single(session.Engine.DescribeThreats(), card => card.EntityId == enemy.Id.Value);
        Assert.Contains("committed", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compliance writ", threat.Counter!, StringComparison.OrdinalIgnoreCase);

        var counter = await session.ExecuteAsync(new CounterCommand("intent warden with compliance writ"));

        Assert.True(counter.Success, string.Join(" | ", counter.Messages));
        Assert.Equal(hpBefore, player.Get<ActorComponent>().HitPoints);
        Assert.Equal(1, player.Get<InventoryComponent>().Items["compliance_writ"]); // durable tool
        Assert.Contains(counter.Deltas, delta => delta.Operation == "counterIntent");
    }

    [Fact]
    public async Task BracingUsesWornDefenseInsteadOfCreatingAFreeShield()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        NeutralizeActors(session);
        var player = session.Engine.State.ControlledEntity;
        player.Get<InventoryComponent>().Items["reed_woven_vest"] = 1;
        var equipped = await session.ExecuteAsync(new EquipCommand("reed_woven_vest"));
        Assert.True(equipped.Success, string.Join(" | ", equipped.Messages));

        var braced = await session.ExecuteAsync(new BraceCommand());
        Assert.True(braced.Success, string.Join(" | ", braced.Messages));
        var damage = session.Engine.ApplyConsequence(WorldConsequence.Damage(
            "test", player.Id.Value, 10, "physical", operation: "testBracedDamage"));

        var delta = Assert.Single(damage.Deltas, item => item.Operation == "testBracedDamage");
        Assert.True(Convert.ToInt32(delta.Details["braceBonus"]) > 0);
        Assert.Equal(
            Convert.ToInt32(delta.Details["equipmentDefense"]),
            Convert.ToInt32(delta.Details["braceBonus"]));
    }

    [Theory]
    [InlineData("force")]
    [InlineData("stealth")]
    [InlineData("clerk")]
    [InlineData("interception")]
    [InlineData("forgery")]
    [InlineData("body_swap")]
    [InlineData("magic")]
    public async Task RelayWaystationSupportsEveryAuthoredEntryRoute(string route)
    {
        var (session, site) = await MaterializeWaystation();
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var clerk = state.Entities.Values.Single(entity => entity.Name.Contains("relay gate clerk", StringComparison.OrdinalIgnoreCase));
        var courier = state.Entities.Values.Single(entity => entity.Name.Contains("delayed courier", StringComparison.OrdinalIgnoreCase));
        ActionResult preparation;
        switch (route)
        {
            case "force":
                preparation = await session.ExecuteAsync(new BreachCommand("waystation"));
                break;
            case "stealth":
                var concealed = session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
                    "test", player.Id.Value, "concealed", duration: 4));
                Assert.True(concealed.Applied, concealed.Error);
                preparation = ActionResult.Simple("stealth", true, false, state.Turn, state.Turn, "concealed");
                break;
            case "clerk":
                player.Get<InventoryComponent>().Items["gold"] = 99;
                preparation = await session.ExecuteAsync(new BargainCommand("relay gate clerk"));
                Assert.True(preparation.Success, string.Join(" | ", preparation.Messages));
                var terms = state.PromiseLedger.Promises.Single(promise =>
                    promise.BargainOffer?.ClaimantEntityId == clerk.Id.Value);
                preparation = await session.ExecuteAsync(new SettleCommand($"{clerk.Id.Value} with pay_gold"));
                Assert.Equal("cleared", state.PromiseLedger.Promises.Single(promise => promise.Id == terms.Id).Status);
                break;
            case "interception":
                player.Get<InventoryComponent>().Items["red_tincture"] = 1;
                preparation = await session.ExecuteAsync(new ExchangeCommand("red_tincture for compliance_writ with delayed courier"));
                Assert.True(player.Get<InventoryComponent>().Items.GetValueOrDefault("compliance_writ") > 0);
                break;
            case "forgery":
                player.Get<InventoryComponent>().Items["blank_permit"] = 1;
                player.Get<InventoryComponent>().Items["permit_ink"] = 1;
                preparation = await session.ExecuteAsync(new ForgeCommand("a relay travel permit"));
                Assert.True(player.Get<InventoryComponent>().Items.GetValueOrDefault("compliance_writ") > 0);
                break;
            case "body_swap":
                player.Set(new PositionComponent(clerk.Get<PositionComponent>().Position.Translate(-1, 0)));
                preparation = await session.ExecuteAsync(new PossessCommand(clerk.Id.Value));
                Assert.Equal(clerk.Id, state.ControlledEntityId);
                break;
            default:
                var opened = session.Engine.ApplyConsequence(WorldConsequence.OpenOrUnlock(
                    "wild_magic", site.Id.Value, actorId: player.Id.Value,
                    sourceEntityId: player.Id.Value, operation: "magicWaystationEntry"));
                Assert.True(opened.Applied, opened.Error);
                preparation = ActionResult.Simple("magic", true, false, state.Turn, state.Turn, "opened");
                break;
        }

        Assert.True(preparation.Success, $"{route}: {string.Join(" | ", preparation.Messages)}");
        var current = state.ControlledEntity;
        current.Set(new PositionComponent(site.Get<PositionComponent>().Position));
        var entered = await session.ExecuteAsync(new EnterCommand(site.Id.Value));
        Assert.True(entered.Success, $"{route}: {string.Join(" | ", entered.Messages)}");
        Assert.StartsWith("interior:hollowmere_margin:relay_waystation", state.CurrentZoneId);
        Assert.Contains(state.Entities.Values, entity => entity.Name.Contains("records clerk", StringComparison.OrdinalIgnoreCase));
        var warden = Assert.Single(state.Entities.Values, entity => entity.Name.Contains("post warden", StringComparison.OrdinalIgnoreCase));
        if (route is "clerk" or "interception" or "forgery" or "body_swap")
        {
            Assert.False(session.Engine.IsHostile(warden, state.ControlledEntity));
        }
        else if (route is "force" or "magic")
        {
            Assert.True(session.Engine.IsHostile(warden, state.ControlledEntity));
        }
        _ = courier; // asserts the threshold cast exists in every route setup.
    }

    private static async Task<(GameSession Session, Entity Site)> MaterializeWaystation()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        NeutralizeActors(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 6)));
        var read = await session.ExecuteAsync(new ReadCommand("notice"));
        Assert.True(read.Success, string.Join(" | ", read.Messages));
        var lead = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Subject.Contains("waystation", StringComparison.OrdinalIgnoreCase));
        await TravelTo(session, lead.ClaimedPlace!);
        var site = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Has<InteriorEntranceComponent>()
            && entity.Name.Contains("waystation", StringComparison.OrdinalIgnoreCase));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(site.Get<PositionComponent>().Position));
        return (session, site);
    }

    private static async Task TravelTo(GameSession session, string zoneId)
    {
        var destination = ParseZone(zoneId);
        for (var guard = 0; guard < 16 && ParseZone(session.Engine.State.CurrentZoneId) != destination; guard++)
        {
            NeutralizeActors(session);
            var current = ParseZone(session.Engine.State.CurrentZoneId);
            var direction = current.X != destination.X
                ? current.X < destination.X ? Direction.East : Direction.West
                : current.Y < destination.Y ? Direction.South : Direction.North;
            var result = await session.ExecuteAsync(new TravelCommand(direction));
            Assert.True(result.Success, string.Join(" | ", result.Messages));
        }

        Assert.Equal(destination, ParseZone(session.Engine.State.CurrentZoneId));
    }

    private static (int X, int Y) ParseZone(string zone)
    {
        var parts = zone.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static void NeutralizeActors(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity =>
            entity.Id != session.Engine.State.ControlledEntityId
            && entity.TryGet<ActorComponent>(out _)))
        {
            var actor = entity.Get<ActorComponent>();
            entity.Set(actor with { Faction = "neutral" });
            if (entity.Has<AiComponent>())
            {
                entity.Set(new AiComponent("idle"));
            }
        }
    }

    private static Entity AddTravelThreat(GameSession session, string id, string name, IReadOnlyList<string> tags)
    {
        var state = session.Engine.State;
        var origin = state.ControlledEntity.Get<PositionComponent>().Position;
        var entity = new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(origin.Translate(1, 0)))
            .Set(new ActorComponent(10, 10, 0, 0, 2, 0, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile"))
            .Set(new SoulComponent(id + "_soul"))
            .Set(new TagsComponent(tags));
        state.Entities[entity.Id] = entity;
        return entity;
    }
}
