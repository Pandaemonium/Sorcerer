using System;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Tactical-mastery pillar: because wild resolution carries uncertainty, deterministic play must
/// supply mastery. Every perceived hostile is telegraphed nearest-first with what it is poised to do
/// under the ordinary pursue-and-strike AI, so a death reads as a wasted turn rather than an ambush.
/// </summary>
public sealed class ThreatTelegraphTests
{
    [Fact]
    public void PerceivedHostilesAreTelegraphedNearestFirst()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;

        var threats = engine.DescribeThreats();

        // The containment soldier and ward-captain are hostile and perceived from the opening.
        Assert.Contains(threats, t => t.Name.Contains("containment soldier", StringComparison.OrdinalIgnoreCase));
        Assert.All(threats, t => Assert.False(t.Imminent)); // none stand adjacent yet
        Assert.True(
            threats.Zip(threats.Skip(1)).All(pair => pair.First.Distance <= pair.Second.Distance),
            "threats must be ordered nearest-first");
    }

    [Fact]
    public void AnAdjacentHostileTelegraphsAnImminentStrike()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var soldier = engine.State.Entities.Values
            .First(e => e.Name.Contains("containment soldier", StringComparison.OrdinalIgnoreCase));

        // Step the soldier next to the sorcerer: the AI strikes at distance 1, so the readout warns.
        soldier.Set(new PositionComponent(new GridPoint(playerPos.X + 1, playerPos.Y)));

        var threat = Assert.Single(engine.DescribeThreats(), t => t.EntityId == soldier.Id.Value);
        Assert.False(threat.Imminent);
        Assert.Equal(1, threat.Distance);
        Assert.Contains("intent", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("compliance writ", threat.Counter!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OpeningSoldierCommitsItsAuthoredIntentBeforeItCanStrike()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var player = engine.State.ControlledEntity;
        var playerPosition = player.Get<PositionComponent>().Position;
        var soldier = engine.State.Entities.Values.First(entity =>
            entity.Name.Contains("containment soldier", StringComparison.OrdinalIgnoreCase));
        soldier.Set(new PositionComponent(playerPosition.Translate(1, 0)));
        var hpBefore = player.Get<ActorComponent>().HitPoints;

        var deltas = engine.RunActorTurns();

        Assert.Equal(hpBefore, player.Get<ActorComponent>().HitPoints);
        Assert.Contains(deltas, delta => delta.Target == soldier.Id.Value && delta.Operation == "telegraphIntent");
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("falls under", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(deltas.PlayerMessages(), message =>
            message.Contains("commits", StringComparison.OrdinalIgnoreCase));
        var threat = Assert.Single(engine.DescribeThreats(), card => card.EntityId == soldier.Id.Value);
        Assert.True(threat.Imminent);
        Assert.Contains("committed", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingIsTelegraphedWhenNoHostileIsInReach()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var player = engine.State.ControlledEntity;

        // Remove every hostile: with nothing to fear, the readout is empty.
        foreach (var entity in engine.State.Entities.Values.ToList())
        {
            if (entity.Id != player.Id
                && entity.TryGet<ActorComponent>(out var actor)
                && engine.IsHostile(player, entity))
            {
                entity.Set(actor with { HitPoints = 0 });
            }
        }

        Assert.Empty(engine.DescribeThreats());
    }

    [Fact]
    public void PersonalSettlementSuppressesThreatTelegraphInTheSameDirectionAsAi()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var player = engine.State.ControlledEntity;
        var soldier = engine.State.Entities.Values
            .First(entity => entity.Name.Contains("containment soldier", StringComparison.OrdinalIgnoreCase));
        soldier.Set(new SoulComponent("settled_soldier_soul"));
        var bond = engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "test",
            soldier.Id.Value,
            player.Get<SoulComponent>().SoulId,
            loyaltyDelta: 5,
            fearDelta: 0,
            admirationDelta: 0,
            resentmentDelta: 0,
            posture: "settled",
            maxDelta: 5));

        Assert.True(bond.Applied);
        Assert.False(engine.IsHostile(soldier, player));
        Assert.DoesNotContain(engine.DescribeThreats(), threat => threat.EntityId == soldier.Id.Value);
    }

    [Fact]
    public void ActiveMimicCompulsionSuppressesTheOrdinaryAttackTelegraph()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var player = engine.State.ControlledEntity;
        var playerPosition = player.Get<PositionComponent>().Position;
        var soldier = engine.State.Entities.Values.First(entity =>
            entity.Name.Contains("containment soldier", StringComparison.OrdinalIgnoreCase));
        soldier.Set(new PositionComponent(playerPosition.Translate(1, 0)));
        var compelled = engine.ApplyConsequence(WorldConsequence.SetBehavior(
            "test",
            soldier.Id.Value,
            "mimic",
            duration: 4,
            sourceEntityId: player.Id.Value));

        var threat = Assert.Single(engine.DescribeThreats(), card => card.EntityId == soldier.Id.Value);

        Assert.True(compelled.Applied, compelled.Error);
        Assert.False(threat.Imminent);
        Assert.Contains("mimic", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("will not", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("striking range", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
    }
}
