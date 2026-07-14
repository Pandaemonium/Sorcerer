using System;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
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
        Assert.True(threat.Imminent);
        Assert.Equal(1, threat.Distance);
        Assert.Contains("striking range", threat.Telegraph, StringComparison.OrdinalIgnoreCase);
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
}
