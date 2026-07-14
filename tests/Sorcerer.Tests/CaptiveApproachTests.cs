using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Quest coherence: a held captive (a prisoner NPC, e.g. Lio in the opening) must stay put until it
/// is freed, not stroll toward the player because a rumor spread. The NPC-approach world-turn
/// initiative excludes captives; an otherwise-identical free NPC with the same pull still approaches.
/// </summary>
public sealed class CaptiveApproachTests
{
    [Fact]
    public void AHeldCaptiveDoesNotWanderTowardThePlayer()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var spawn = new GridPoint(playerPos.X + 4, playerPos.Y);
        var captive = AddApproachCandidate(engine, "test_captive", spawn, captive: true);

        for (var i = 0; i < 5; i++)
        {
            engine.AdvanceTurn();
        }

        // Excluded from the approach initiative despite meeting every other candidate condition.
        Assert.Equal(spawn, captive.Get<PositionComponent>().Position);
    }

    [Fact]
    public void AFreeInterestedNpcStillApproaches()
    {
        var engine = GameSession.CreateImperialEncounter(seed: 7).Engine;
        ClearHostiles(engine); // remove pursuers so the seeker's path and turn are uncontested
        var playerPos = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var spawn = new GridPoint(playerPos.X, playerPos.Y + 4);
        var seeker = AddApproachCandidate(engine, "test_seeker", spawn, captive: false);

        var moved = false;
        for (var i = 0; i < 10 && !moved; i++)
        {
            engine.AdvanceTurn();
            moved = seeker.Get<PositionComponent>().Position != spawn;
        }

        Assert.True(moved, "a free interested NPC should approach the player");
    }

    private static Entity AddApproachCandidate(GameEngine engine, string id, GridPoint at, bool captive)
    {
        var tags = captive
            ? new[] { "npc", "prisoner", "seeks_player" }
            : new[] { "npc", "seeks_player" };
        var npc = new Entity(EntityId.Create(id), id)
            .Set(new PositionComponent(at))
            .Set(new ActorComponent(8, 8, 0, 0, 1, 0, "hollowmere"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new ControllerComponent(ControllerKind.None))
            .Set(new WantComponent("want_test", "reach the sorcerer", salience: 5));
        if (captive)
        {
            npc.Set(new AiComponent("captive"));
        }

        engine.State.Entities[npc.Id] = npc;
        return npc;
    }

    private static void ClearHostiles(GameEngine engine)
    {
        var player = engine.State.ControlledEntity;
        foreach (var entity in engine.State.Entities.Values.ToList())
        {
            if (entity.Id != player.Id
                && entity.TryGet<ActorComponent>(out var actor)
                && engine.IsHostile(player, entity))
            {
                entity.Set(actor with { HitPoints = 0 });
            }
        }
    }
}
