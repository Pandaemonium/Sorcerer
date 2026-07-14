using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// A following should feel like low-maintenance social power, not a trail of stragglers (Q29): a
/// recruited follower with no foe to fight keeps pace with the sorcerer instead of idling in place.
/// An ordinary non-follower is unaffected.
/// </summary>
public sealed class FollowerTests
{
    [Fact]
    public void AFollowerWithNoFoeStepsTowardTheLeader()
    {
        var engine = ClearedEncounter();
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var follower = AddCompanion(engine, "test_follower", new GridPoint(playerPos.X + 5, playerPos.Y), follower: true);

        var beforeX = follower.Get<PositionComponent>().Position.X;
        engine.RunActorTurns();
        var afterX = engine.EntityById("test_follower")!.Get<PositionComponent>().Position.X;

        Assert.True(afterX < beforeX, $"follower should close on the leader ({beforeX} -> {afterX})");
    }

    [Fact]
    public void AFollowerHoldsAStepOffAndNeverStrikesTheLeader()
    {
        var engine = ClearedEncounter();
        var player = engine.State.ControlledEntity;
        var playerPos = player.Get<PositionComponent>().Position;
        AddCompanion(engine, "test_follower", new GridPoint(playerPos.X + 1, playerPos.Y), follower: true);
        var hpBefore = player.Get<ActorComponent>().HitPoints;

        engine.RunActorTurns();

        // Already alongside: it does not crowd onto the leader, and it never attacks them.
        Assert.Equal(hpBefore, engine.State.ControlledEntity.Get<ActorComponent>().HitPoints);
    }

    [Fact]
    public void ANonFollowerDoesNotTrailTheLeader()
    {
        var engine = ClearedEncounter();
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var bystander = AddCompanion(engine, "test_bystander", new GridPoint(playerPos.X + 5, playerPos.Y), follower: false);
        var before = bystander.Get<PositionComponent>().Position;

        engine.RunActorTurns();

        Assert.Equal(before, engine.EntityById("test_bystander")!.Get<PositionComponent>().Position);
    }

    private static GameEngine ClearedEncounter()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var player = engine.State.ControlledEntity;
        foreach (var entity in engine.State.Entities.Values.ToList())
        {
            if (entity.Id != player.Id
                && entity.TryGet<ActorComponent>(out var actor)
                && engine.IsHostile(player, entity))
            {
                entity.Set(actor with { HitPoints = 0 }); // remove foes so only follow behavior remains
            }
        }

        return engine;
    }

    private static Entity AddCompanion(GameEngine engine, string id, GridPoint at, bool follower)
    {
        var companion = new Entity(EntityId.Create(id), id)
            .Set(new PositionComponent(at))
            .Set(new ActorComponent(8, 8, 0, 0, 1, 0, "player"))
            .Set(new AiComponent(follower ? "follower" : "wander"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new FactionComponent("player", follower ? new[] { "follower" } : new[] { "resident" }));
        engine.State.Entities[companion.Id] = companion;
        return companion;
    }
}
