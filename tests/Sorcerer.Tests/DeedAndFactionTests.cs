using System;
using System.Collections.Generic;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 7 of the WildMagic import: deeds carry visibility and attribution, and factions react
/// through role-based standing and finite resources. These lock the deterministic rules the plan
/// requires: a witnessless deed stays secret and unattributed, an effect-only deed is noticed but
/// not pinned on the actor, one deed moves factions by role, and pressure spends/recovers resources.
/// </summary>
public sealed class DeedAndFactionTests
{
    private static readonly Entity[] NoWitnesses = Array.Empty<Entity>();

    [Fact]
    public void WitnesslessDeedIsSecretAndUnattributed()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var actor = session.Engine.State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;

        var plan = new WorldReactionSystem().PlanDeed(
            session.Engine.State, actor, "wild_magic", magnitude: 3, origin, effectPoint: null,
            actorWitnesses: NoWitnesses, effectWitnesses: NoWitnesses);

        Assert.Equal("secret", plan.Visibility);
        Assert.Equal("secret", plan.AttributionStatus);
        Assert.Null(plan.AttributedSoulId);
        Assert.Empty(plan.Witnesses);
    }

    [Fact]
    public void EffectWitnessedDeedIsNoticedButNotPinnedOnTheActor()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var actor = session.Engine.State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        var effectWitness = session.Engine.EntityById("soldier_1")!;

        var plan = new WorldReactionSystem().PlanDeed(
            session.Engine.State, actor, "wild_magic", magnitude: 3, origin, effectPoint: null,
            actorWitnesses: NoWitnesses, effectWitnesses: new[] { effectWitness });

        Assert.Equal("suspicious", plan.Visibility);
        Assert.Equal("unattributed", plan.AttributionStatus);
        Assert.Null(plan.AttributedSoulId);
    }

    [Fact]
    public void ActorWitnessedDeedIsAttributedToTheActorSoul()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var actor = session.Engine.State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        var witness = session.Engine.EntityById("soldier_1")!;
        var soulId = actor.TryGet<SoulComponent>(out var soul) ? soul.SoulId : actor.Id.Value;

        var plan = new WorldReactionSystem().PlanDeed(
            session.Engine.State, actor, "wild_magic", magnitude: 3, origin, effectPoint: null,
            actorWitnesses: new[] { witness }, effectWitnesses: NoWitnesses);

        Assert.Equal("witnessed", plan.Visibility);
        Assert.Equal("attributed", plan.AttributionStatus);
        Assert.Equal(soulId, plan.AttributedSoulId);
    }

    [Fact]
    public void FreeingPrisonersErodesImperialDefensesOrganically()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var player = state.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;

        var defensesBefore = state.Factions.FactionsByRole("empire_bloc")
            .Sum(faction => state.Factions.ResourceValue(faction.Id, "defenses"));
        Assert.True(defensesBefore > 0);

        // A witnessed liberation is a real anti-imperial victory (reference scenario has soldiers by
        // the yard, so the deed is not secret).
        var deed = session.Engine.ApplyConsequence(WorldConsequence.RecordDeed(
            "test", player.Id.Value, "freed_prisoner", magnitude: 2,
            originX: origin.X, originY: origin.Y, effectX: origin.X, effectY: origin.Y,
            sourceEntityId: player.Id.Value));
        Assert.True(deed.Applied, deed.Error);

        new WorldReactionSystem().ApplyPending(state);

        var defensesAfter = state.Factions.FactionsByRole("empire_bloc")
            .Sum(faction => state.Factions.ResourceValue(faction.Id, "defenses"));
        Assert.True(defensesAfter < defensesBefore, "Freeing prisoners should spend imperial defenses.");
    }

    [Fact]
    public void KillingImperialForcesInTheOpenErodesDefensesButKillingOthersDoesNot()
    {
        static int Defenses(GameSession session) =>
            session.Engine.State.Factions.FactionsByRole("empire_bloc")
                .Sum(faction => session.Engine.State.Factions.ResourceValue(faction.Id, "defenses"));

        static void RecordWitnessedKill(GameSession session, string victimFaction)
        {
            var player = session.Engine.State.ControlledEntity;
            var origin = player.Get<PositionComponent>().Position;
            session.Engine.ApplyConsequence(WorldConsequence.RecordDeed(
                "test", player.Id.Value, "kill", magnitude: 4,
                originX: origin.X, originY: origin.Y, effectX: origin.X, effectY: origin.Y,
                tags: new[] { "combat", "violence", victimFaction }, sourceEntityId: player.Id.Value));
            new WorldReactionSystem().ApplyPending(session.Engine.State);
        }

        // Felling one of the empire's own forces in the open spends a point of imperial defense.
        var imperial = GameSession.CreateImperialEncounter(seed: 7);
        var imperialBefore = Defenses(imperial);
        RecordWitnessedKill(imperial, "empire");
        Assert.True(Defenses(imperial) < imperialBefore);

        // Killing a non-imperial does not touch imperial defenses.
        var other = GameSession.CreateImperialEncounter(seed: 7);
        var otherBefore = Defenses(other);
        RecordWitnessedKill(other, "hollowmere");
        Assert.Equal(otherBefore, Defenses(other));
    }

    [Fact]
    public void OneDeedAdjustsFactionsByRoleWithoutTouchingOtherRoles()
    {
        var ledger = new FactionLedger();
        ledger.AddOrGet("censorate", "Censorate", "law");
        ledger.AddOrGet("reed_folk", "Reed Folk", "folk");

        ledger.AdjustStandingByRole("law", "fear", 3);

        Assert.Equal(3, ledger.StandingValue("censorate", "fear"));
        Assert.Equal(0, ledger.StandingValue("reed_folk", "fear"));
    }

    [Fact]
    public void FactionPressureSpendsAndRecoversFiniteResources()
    {
        var ledger = new FactionLedger();
        ledger.AddOrGet("censorate", "Censorate", "law",
            resources: new Dictionary<string, int> { ["patrols"] = 5 });

        ledger.AdjustResource("censorate", "patrols", -2);
        Assert.Equal(3, ledger.ResourceValue("censorate", "patrols"));

        ledger.AdjustResource("censorate", "patrols", 1, max: 5);
        Assert.Equal(4, ledger.ResourceValue("censorate", "patrols"));

        ledger.AdjustResource("censorate", "patrols", -10);
        Assert.Equal(0, ledger.ResourceValue("censorate", "patrols"));
    }
}
