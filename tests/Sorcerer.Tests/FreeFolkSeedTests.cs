using System;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// FREE_FOLK_MOVEMENT slice S1 ("the seed"): the Free Folk exist as a rolled faction from turn
/// one, the Provincial Reconciliation Sweep is a standing scheduled operation aimed at a real
/// settlement, the containment docket reveals it and points at the waystation, the rescue
/// handoff becomes the sweep warning aimed at the same settlement, and an ignored sweep lands
/// on real people.
/// </summary>
public sealed class FreeFolkSeedTests
{
    [Fact]
    public void TheFreeFolkExistStarvedFromTurnOne()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;

        var freeFolk = Assert.Single(state.Factions.Factions, faction => faction.Id == "free_folk");
        Assert.Equal("resistance", freeFolk.Role);
        Assert.True(state.Factions.ResourceValue("free_folk", "support") <= 1,
            "The Movement starts starved; the map does not open with rebel bases.");
        Assert.Equal(0, state.Factions.ResourceValue("free_folk", "hands"));
    }

    [Fact]
    public void TheReapingIsScheduledAgainstARealSettlementFromTurnOne()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;

        var sweep = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase));
        var zone = Convert.ToString(sweep.Payload["zone"]);
        var graph = WorldPlaceGraph.Create(state.Seed, RegionCatalog.LoadDefault());
        var target = Assert.Single(graph.Settlements, settlement =>
            $"{settlement.CenterX},{settlement.CenterY}" == zone);
        Assert.Equal(target.Name, Convert.ToString(sweep.Payload["settlementName"]));
        Assert.True(sweep.DueTurn >= 50, "The reaping presses but leaves time to act.");
    }

    [Fact]
    public async Task TheJournalHidesTheReapingUntilTheDocketIsRead()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);

        var before = await session.ExecuteAsync(new JournalCommand());
        Assert.DoesNotContain(before.Messages, message =>
            message.Contains("reaping", StringComparison.OrdinalIgnoreCase));

        MovePlayerTo(session, new GridPoint(5, 6));
        var read = await session.ExecuteAsync(new ReadCommand("notice"));
        Assert.True(read.Success, string.Join(" | ", read.Messages));
        Assert.Contains(session.Engine.State.Claims.Records, claim =>
            claim.Tags.Contains("reaping", StringComparer.OrdinalIgnoreCase));
        var lead = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Subject.Contains("waystation", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("bound", lead.Status);

        // The waystation sits on the first road leg toward the sweep's target, so the warning
        // route and the plans route share their first steps.
        var sweep = Assert.Single(session.Engine.State.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase));
        var target = Convert.ToString(sweep.Payload["zone"])!.Split(',');
        Assert.Equal(
            $"{Math.Sign(int.Parse(target[0]))},{Math.Sign(int.Parse(target[1]))}",
            lead.ClaimedPlace);

        var after = await session.ExecuteAsync(new JournalCommand());
        Assert.Contains(after.Messages, message =>
            message.Contains("The reaping:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("turn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheRescueHandoffBecomesTheSweepWarningAimedAtTheSweepTarget()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        var state = session.Engine.State;
        var sweep = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase));
        var sweepZone = Convert.ToString(sweep.Payload["zone"]);

        var open = await FreeOpeningCaptive(session);
        Assert.True(open.Success, string.Join(" | ", open.Messages));

        var claim = Assert.Single(state.Claims.Records, claim =>
            claim.SpeakerId == "prisoner_1"
            && claim.Tags.Contains("generated_objective", StringComparer.OrdinalIgnoreCase));
        Assert.Contains("sweep", claim.Tags, StringComparer.OrdinalIgnoreCase);
        var objective = Assert.Single(state.PromiseLedger.Promises, promise =>
            promise.SourceClaimId == claim.Id);
        Assert.Equal(sweepZone, objective.ClaimedPlace);
        Assert.Contains("warn", objective.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(open.Messages, message =>
            message.Contains("reaping list", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TravelingTowardTheSweepMaterializesTheWaystation()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        MovePlayerTo(session, new GridPoint(5, 6));
        var read = await session.ExecuteAsync(new ReadCommand("notice"));
        Assert.True(read.Success, string.Join(" | ", read.Messages));
        var lead = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Subject.Contains("waystation", StringComparison.OrdinalIgnoreCase));

        await TravelTo(session, lead.ClaimedPlace!);

        var realized = session.Engine.State.PromiseLedger.Promises.Single(promise => promise.Id == lead.Id);
        Assert.Equal("realized", realized.Status);
        var site = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Id.Value.StartsWith("promise_site_", StringComparison.OrdinalIgnoreCase)
            && entity.Name.Contains("waystation", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("imperial relay waystation", site.Name);
    }

    [Fact]
    public async Task TheWaystationIsEnterableAndHoldsThePlans()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        var state = session.Engine.State;
        MovePlayerTo(session, new GridPoint(5, 6));
        var read = await session.ExecuteAsync(new ReadCommand("notice"));
        Assert.True(read.Success, string.Join(" | ", read.Messages));
        var lead = Assert.Single(state.PromiseLedger.Promises, promise =>
            promise.Subject.Contains("waystation", StringComparison.OrdinalIgnoreCase));

        await TravelTo(session, lead.ClaimedPlace!);

        // The promised site is a building, not a plaque: it carries a real threshold.
        var site = Assert.Single(state.Entities.Values, entity =>
            entity.Id.Value.StartsWith("promise_site_", StringComparison.OrdinalIgnoreCase)
            && entity.Name.Contains("waystation", StringComparison.OrdinalIgnoreCase));
        Assert.True(site.Has<InteriorEntranceComponent>());

        // Enter with the ordinary credential route (force/magic are other doors in).
        state.ControlledEntity.Get<InventoryComponent>().Items["imperial cell key"] = 1;
        MovePlayerTo(session, site.Get<PositionComponent>().Position);
        var entered = await session.ExecuteAsync(new EnterCommand(site.Id.Value));
        Assert.True(entered.Success, string.Join(" | ", entered.Messages));

        // The plans are inside as ordinary readable claim sources.
        var ledger = Assert.Single(state.Entities.Values, entity =>
            entity.Name.Equals("requisition ledger", StringComparison.OrdinalIgnoreCase));
        Assert.True(ledger.Has<ClaimSourceComponent>());
        MovePlayerTo(session, ledger.Get<PositionComponent>().Position.Translate(1, 0));
        var readLedger = await session.ExecuteAsync(new ReadCommand("requisition ledger"));
        Assert.True(readLedger.Success, string.Join(" | ", readLedger.Messages));
        Assert.Contains(state.PromiseLedger.Promises, promise =>
            promise.Subject.Equals("confiscated name-charm", StringComparison.OrdinalIgnoreCase)
            && promise.Status == "bound");

        var dispatch = Assert.Single(state.Entities.Values, entity =>
            entity.Name.Equals("sweep dispatch case", StringComparison.OrdinalIgnoreCase));
        MovePlayerTo(session, dispatch.Get<PositionComponent>().Position.Translate(1, 0));
        var readSchedule = await session.ExecuteAsync(new ReadCommand("sweep dispatch case"));
        Assert.True(readSchedule.Success, string.Join(" | ", readSchedule.Messages));
        Assert.Contains(state.Claims.Records, claim =>
            claim.Tags.Contains("sweep_schedule", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheReapingLandsOnRealPeopleIfIgnored()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableImperialAi(session);
        var state = session.Engine.State;
        var sweep = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase));
        var settlementName = Convert.ToString(sweep.Payload["settlementName"])!;
        var supportBefore = state.Factions.FactionsByRole("resistance")
            .Sum(faction => state.Factions.ResourceValue(faction.Id, "support"));
        Assert.True(supportBefore > 0);

        var guard = 0;
        while (state.Turn < sweep.DueTurn && guard++ < 300)
        {
            session.Engine.AdvanceTurn();
        }

        Assert.Contains(state.Rumors.Records, rumor =>
            rumor.Tags.Contains("reaping", StringComparer.OrdinalIgnoreCase)
            && rumor.Text.Contains(settlementName, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.Canon.Records, record =>
            record.Kind == "censorate_memo"
            && record.Text.Contains("Reconciliation Sweep", StringComparison.OrdinalIgnoreCase)
            && record.Text.Contains(settlementName, StringComparison.OrdinalIgnoreCase));
        var supportAfter = state.Factions.FactionsByRole("resistance")
            .Sum(faction => state.Factions.ResourceValue(faction.Id, "support"));
        Assert.True(supportAfter < supportBefore,
            "The reaping costs the free folk real ground when nobody stops it.");
    }

    [Fact]
    public void FreeingACaptiveEarnsTheMovementsNotice()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;

        var applied = session.Engine.ApplyConsequence(WorldConsequence.RecordDeed(
            "test", player.Id.Value, "freed_prisoner", magnitude: 2,
            originX: origin.X, originY: origin.Y, effectX: origin.X, effectY: origin.Y,
            sourceEntityId: player.Id.Value));
        Assert.True(applied.Applied, applied.Error);
        new WorldReactionSystem().ApplyPending(state);

        Assert.True(state.Factions.StandingValue("free_folk", "gratitude") > 0,
            "The Movement's lines notice a rescue.");
    }

    private static async Task<ActionResult> FreeOpeningCaptive(GameSession session)
    {
        MovePlayerTo(session, new GridPoint(7, 6));
        var pickup = await session.ExecuteAsync(new PickupCommand("key"));
        Assert.True(pickup.Success, string.Join(" | ", pickup.Messages));
        MovePlayerTo(session, new GridPoint(12, 5));
        return await session.ExecuteAsync(new OpenCommand("cell"));
    }

    private static void MovePlayerTo(GameSession session, GridPoint point) =>
        session.Engine.State.ControlledEntity.Set(new PositionComponent(point));

    private static async Task TravelTo(GameSession session, string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        var destination = (X: int.Parse(parts[0]), Y: int.Parse(parts[1]));
        var guard = 0;
        while (guard++ < 12)
        {
            var currentParts = session.Engine.State.CurrentZoneId.Split(',', StringSplitOptions.TrimEntries);
            var current = (X: int.Parse(currentParts[0]), Y: int.Parse(currentParts[1]));
            if (current == destination)
            {
                return;
            }

            var direction = current.X != destination.X
                ? (current.X < destination.X ? Direction.East : Direction.West)
                : (current.Y < destination.Y ? Direction.South : Direction.North);
            var travel = await session.ExecuteAsync(new TravelCommand(direction));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
        }

        Assert.Fail($"Could not reach zone {zoneId} from {session.Engine.State.CurrentZoneId}.");
    }

    private static void DisableImperialAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
