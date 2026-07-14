using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 2.6: a defeat is disposed of in the register of whoever landed the killing blow.
/// Damaging the controlled body records the hand that struck it
/// (<see cref="GameState.LastControlledDamageProvenance"/>), and the chronicle turns that
/// provenance into a death treatment -- an imperial hand files a Censorate incident, wild magic
/// transforms the body, ordinary force simply ends it. These pin both halves: the applier records
/// the killer, and <see cref="RunChronicle"/> selects the treatment only on defeat.
/// </summary>
public sealed class DeathTreatmentTests
{
    [Fact]
    public void EmpireHandDamagingTheBodyIsRecordedAsImperialProvenance()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        var guard = AddAttacker(state, "test_censor_guard", "empire");

        session.Engine.ApplyConsequence(WorldConsequence.Damage(
            source: "test",
            targetEntityId: state.ControlledEntityId.Value,
            amount: 1,
            sourceEntityId: guard.Id.Value));

        Assert.Equal("imperial", state.LastControlledDamageProvenance);
    }

    [Fact]
    public void WildDamageToTheBodyIsRecordedAsWildProvenance()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;

        // The wild register is read from the blow itself -- its damage type or its source label --
        // so an unfactioned or absent hand still reads as wild when the magic is what struck.
        session.Engine.ApplyConsequence(WorldConsequence.Damage(
            source: "wild_surge",
            targetEntityId: state.ControlledEntityId.Value,
            amount: 1,
            damageType: "wild"));

        Assert.Equal("wild", state.LastControlledDamageProvenance);
    }

    [Fact]
    public void OrdinaryForceToTheBodyIsRecordedAsMortalProvenance()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        var brigand = AddAttacker(state, "test_brigand", "outlaw");

        session.Engine.ApplyConsequence(WorldConsequence.Damage(
            source: "test",
            targetEntityId: state.ControlledEntityId.Value,
            amount: 1,
            damageType: "arcane",
            sourceEntityId: brigand.Id.Value));

        Assert.Equal("mortal", state.LastControlledDamageProvenance);
    }

    [Fact]
    public void DamageToAnyoneButTheBodyLeavesTheProvenanceUntouched()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        var bystander = AddAttacker(state, "test_bystander", "neutral");

        session.Engine.ApplyConsequence(WorldConsequence.Damage(
            source: "test",
            targetEntityId: bystander.Id.Value,
            amount: 1));

        Assert.Null(state.LastControlledDamageProvenance);
    }

    [Theory]
    [InlineData("imperial", "imperial")]
    [InlineData("wild", "wild")]
    [InlineData("mortal", "mortal")]
    [InlineData(null, "mortal")]
    public void DefeatChronicleSelectsTheTreatmentFromTheKillersProvenance(string? provenance, string expected)
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        state.RunStatus = "defeat";
        state.LastControlledDamageProvenance = provenance;

        Assert.Equal(expected, RunChronicle.Build(state).Treatment);
    }

    [Fact]
    public void ARunThatDidNotEndInDefeatHasNoTreatment()
    {
        var session = GameSession.CreateImperialEncounter();
        var state = session.Engine.State;
        state.LastControlledDamageProvenance = "imperial";

        state.RunStatus = "victory";
        Assert.Equal("none", RunChronicle.Build(state).Treatment);

        state.RunStatus = "running";
        Assert.Equal("none", RunChronicle.Build(state).Treatment);
    }

    [Theory]
    [InlineData("imperial", "Censorate")]
    [InlineData("wild", "wearing your shape")]
    [InlineData("mortal", "stranger's dawn")]
    public void EachTreatmentNarratesItsOwnDisposition(string treatment, string hallmark)
    {
        var line = DeathTreatment.Disposition(treatment);

        // Every register opens on the same fall, then handles the body in its own way.
        Assert.StartsWith("Your body falls.", line);
        Assert.Contains(hallmark, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AnUnrecognizedRegisterFallsThroughToTheOrdinaryDeath()
    {
        Assert.Equal(DeathTreatment.Mortal, DeathTreatment.ForDefeat(null));
        Assert.Equal(DeathTreatment.Disposition(DeathTreatment.Mortal), DeathTreatment.Disposition("something_unmapped"));
    }

    private static Entity AddAttacker(GameState state, string id, string faction)
    {
        var attacker = new Entity(EntityId.Create(id), id)
            .Set(new ActorComponent(6, 6, 0, 0, 2, 0, faction));
        state.Entities[attacker.Id] = attacker;
        return attacker;
    }
}
