using System;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// FREE_FOLK_MOVEMENT slice S0 ("the marble answers slowly"): imperial alarm is knowledge that
/// must physically travel, and imperial responders arrive by road outside the player's sight.
/// These tests pin the report-borne heat lifecycle, the carrier-killed counterplay, the overdue
/// audit for silent imperial losses, edge arrival, the named witchhunter pipeline, and the slow
/// logistics cadence of pressure regeneration.
/// </summary>
public sealed class ImperialResponsePacingTests
{
    [Fact]
    public void WitnessedImperialKillRaisesHeatOnlyWhenTheReportArrives()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        DisableImperialAi(session);

        RecordKillDeed(session, victimFactionTag: "empire");
        new WorldReactionSystem().ApplyPending(state);

        // The deed is witnessed by living imperial soldiers, but no alarm rises until their
        // report arrives at a desk.
        Assert.Equal(0, state.Factions.ResourceValue("empire", "heat"));
        var report = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_report", StringComparison.OrdinalIgnoreCase));
        Assert.False(IsOverdue(report));
        Assert.False(string.IsNullOrWhiteSpace(Convert.ToString(report.Payload["witnessIds"])));

        AdvanceUntil(session, report.DueTurn);

        // The report landed, alarm rose, and the pressure ladder answered in the same turn:
        // a patrol was dispatched with a long road fuse instead of popping in.
        Assert.Contains(state.WorldTurns.Records, record =>
            record.Kind == "faction_pressure"
            && Equals(record.Details["response"], "empire_patrol"));
        var patrol = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_patrol", StringComparison.OrdinalIgnoreCase));
        Assert.True(patrol.DueTurn - report.DueTurn >= 10,
            $"Patrol fuse should be long road time, got {patrol.DueTurn - report.DueTurn} turns.");
    }

    [Fact]
    public void KillingTheCarrierBeforeTheReportLandsSilencesIt()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        DisableImperialAi(session);

        RecordKillDeed(session, victimFactionTag: "empire");
        new WorldReactionSystem().ApplyPending(state);
        var report = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_report", StringComparison.OrdinalIgnoreCase));

        // The witnesses die before the word reaches a desk: dead men file no reports.
        KillImperialActors(session);
        var deltas = AdvanceUntil(session, report.DueTurn);

        Assert.Equal(0, state.Factions.ResourceValue("empire", "heat"));
        Assert.DoesNotContain(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_patrol", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deltas, delta =>
            delta.Summary?.Contains("past telling", StringComparison.OrdinalIgnoreCase) == true
            || (delta.Details.TryGetValue("message", out var message)
                && Convert.ToString(message)?.Contains("past telling", StringComparison.OrdinalIgnoreCase) == true));
    }

    [Fact]
    public void SilentImperialKillIsNoticedOnlyByTheOverdueAudit()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        KillAllNonPlayerActors(session);

        RecordKillDeed(session, victimFactionTag: "empire");
        RecordKillDeed(session, victimFactionTag: "empire");
        new WorldReactionSystem().ApplyPending(state);

        // No witness survived, so no report travels - but the Empire will notice its own
        // silence. Several silent kills read as one discovered silence, not several files.
        Assert.Equal(0, state.Factions.ResourceValue("empire", "heat"));
        var audit = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_report", StringComparison.OrdinalIgnoreCase));
        Assert.True(IsOverdue(audit));
        Assert.True(audit.DueTurn - state.Turn >= 12,
            $"An overdue audit is slow institutional noticing, got {audit.DueTurn - state.Turn} turns.");

        AdvanceUntil(session, audit.DueTurn);
        Assert.Equal(2, state.Factions.ResourceValue("empire", "heat"));
    }

    [Fact]
    public void PatrolArrivesOutsideThePlayersSight()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        DisableImperialAi(session);
        state.Factions.AdjustResource("empire", "heat", 4);

        session.Engine.AdvanceTurn();
        var scheduled = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_patrol", StringComparison.OrdinalIgnoreCase));

        AdvanceUntil(session, scheduled.DueTurn);
        var patrol = Assert.Single(state.Entities.Values, entity =>
            entity.Id.Value.StartsWith("imperial_patrol_", StringComparison.OrdinalIgnoreCase));

        // The responder entered the world where the player cannot see it - never in view.
        Assert.DoesNotContain(patrol.Id, session.Engine.Perception().VisibleEntityIds);
    }

    [Fact]
    public void WarrantPutsANamedWitchhunterOnTheRoadWithWordTravelingAhead()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        DisableImperialAi(session);
        // Quiet-pump recovery decays one heat before pressure fires, so 6 lands the warrant
        // rung (heat >= 5) rather than the patrol rung.
        state.Factions.AdjustResource("empire", "heat", 6);

        session.Engine.AdvanceTurn();
        var warrant = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_warrant", StringComparison.OrdinalIgnoreCase));

        AdvanceUntil(session, warrant.DueTurn);
        var trace = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_hunter_trace", StringComparison.OrdinalIgnoreCase));
        var arrival = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_hunter", StringComparison.OrdinalIgnoreCase));
        Assert.True(arrival.DueTurn > trace.DueTurn, "Word of the hunt travels ahead of the hunter.");

        // The trace is diegetic: road talk the player can hear about, carried as a real rumor.
        AdvanceUntil(session, trace.DueTurn);
        Assert.Contains(state.Rumors.Records, rumor =>
            rumor.Tags.Contains("witchhunter", StringComparer.OrdinalIgnoreCase));

        AdvanceUntil(session, arrival.DueTurn);
        var hunter = Assert.Single(state.Entities.Values, entity =>
            entity.Id.Value.StartsWith("witchhunter_", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Pursuivant", hunter.Name);
        Assert.DoesNotContain(hunter.Id, session.Engine.Perception().VisibleEntityIds);

        // Named people are finite: a second warrant does not stack a second hunter while the
        // first lives.
        state.ScheduledEvents.Schedule(state.Turn + 1, "empire_warrant", null,
            new System.Collections.Generic.Dictionary<string, object?> { ["factionId"] = "empire" });
        session.Engine.AdvanceTurn();
        Assert.DoesNotContain(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_hunter", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Equals("empire_hunter_trace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SpentPatrolsRegenerateOnASlowLogisticsCadence()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        DisableImperialAi(session);
        state.Factions.AdjustResource("empire", "patrols", -99);

        var expected = 0;
        var max = state.Factions.ResourceValue("empire", "max_patrols");
        for (var step = 0; step < 6; step++)
        {
            session.Engine.AdvanceTurn();
            if (state.Turn % 3 == 0 && expected < max)
            {
                expected++;
            }

            Assert.Equal(expected, state.Factions.ResourceValue("empire", "patrols"));
        }
    }

    private static void RecordKillDeed(GameSession session, string victimFactionTag)
    {
        var player = session.Engine.State.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        var applied = session.Engine.ApplyConsequence(WorldConsequence.RecordDeed(
            "test", player.Id.Value, "kill", magnitude: 4,
            originX: origin.X, originY: origin.Y, effectX: origin.X, effectY: origin.Y,
            tags: new[] { "combat", "violence", victimFactionTag }, sourceEntityId: player.Id.Value));
        Assert.True(applied.Applied, applied.Error);
    }

    private static System.Collections.Generic.List<Sorcerer.Core.Results.StateDelta> AdvanceUntil(GameSession session, int dueTurn)
    {
        var deltas = new System.Collections.Generic.List<Sorcerer.Core.Results.StateDelta>();
        var guard = 0;
        while (session.Engine.State.Turn < dueTurn && guard++ < 200)
        {
            deltas.AddRange(session.Engine.AdvanceTurn());
        }

        return deltas;
    }

    private static bool IsOverdue(ScheduledEventRecord item) =>
        item.Payload.TryGetValue("cause", out var cause)
        && "overdue".Equals(Convert.ToString(cause), StringComparison.OrdinalIgnoreCase);

    private static void DisableImperialAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }

    private static void KillImperialActors(GameSession session)
    {
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            var entity = session.Engine.EntityById(id)!;
            entity.Set(entity.Get<ActorComponent>() with { HitPoints = 0 });
        }
    }

    private static void KillAllNonPlayerActors(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values
            .Where(entity => entity.Id != session.Engine.State.ControlledEntityId))
        {
            if (entity.TryGet<ActorComponent>(out var actor))
            {
                entity.Set(actor with { HitPoints = 0 });
            }
        }
    }
}
