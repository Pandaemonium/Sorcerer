using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GuardAiTests
{
    [Fact]
    public void OffDutyGuardDriftsBackToItsAnchorInsteadOfIdling()
    {
        var session = CreateSession();
        var anchor = FarCorner(session);
        var start = TowardCenter(session, anchor, 3);
        var guard = SpawnGuard(session, anchor, hostileToPlayer: false, at: start);

        session.Engine.RunActorTurns();

        var position = guard.Get<PositionComponent>().Position;
        Assert.True(
            GameEngine.Distance(position, anchor) < GameEngine.Distance(start, anchor),
            $"guard at {position} did not step toward {anchor}");
    }

    [Fact]
    public void GuardAtItsPostHoldsPosition()
    {
        var session = CreateSession();
        var anchor = FarCorner(session);
        var guard = SpawnGuard(session, anchor, hostileToPlayer: false, at: anchor.Translate(1, 0));
        var before = guard.Get<PositionComponent>().Position;

        session.Engine.RunActorTurns();

        Assert.Equal(before, guard.Get<PositionComponent>().Position);
    }

    [Fact]
    public void ProvokedGuardHuntsThePlayerLikeAnyHostile()
    {
        var session = CreateSession();
        var player = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var anchor = player.Translate(4, 0);
        var guard = SpawnGuard(session, anchor, hostileToPlayer: true, at: anchor);
        var before = GameEngine.Distance(guard.Get<PositionComponent>().Position, player);

        session.Engine.RunActorTurns();

        var after = GameEngine.Distance(guard.Get<PositionComponent>().Position, player);
        Assert.True(after < before, $"guard did not close: {before} -> {after}");
    }

    [Fact]
    public void PursuingGuardBreaksOffBeyondTheLeashAndWalksHome()
    {
        var session = CreateSession();
        var player = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var anchor = FarCorner(session);
        // Hostile, near the player, but far past the leash from its post.
        var start = player.Translate(3, 0);
        var guard = SpawnGuard(session, anchor, hostileToPlayer: true, at: start);
        var homeBefore = GameEngine.Distance(start, anchor);

        session.Engine.RunActorTurns();

        var position = guard.Get<PositionComponent>().Position;
        Assert.True(
            GameEngine.Distance(position, anchor) < homeBefore,
            $"guard at {position} did not break off toward {anchor}");
    }

    [Fact]
    public void ConcealedPlayerIsNotNoticedByAHostileGuard()
    {
        var session = CreateSession();
        var player = session.Engine.State.ControlledEntity;
        var anchor = player.Get<PositionComponent>().Position.Translate(5, 0);
        var guard = SpawnGuard(session, anchor, hostileToPlayer: true, at: anchor);
        var concealed = session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test",
            player.Id.Value,
            "concealed",
            duration: 10,
            emitMessage: false));
        Assert.True(concealed.Applied, concealed.Error);
        var before = guard.Get<PositionComponent>().Position;

        session.Engine.RunActorTurns();

        Assert.Equal(before, guard.Get<PositionComponent>().Position);
    }

    [Fact]
    public void GuardWithoutAnchorParametersDegradesToIdle()
    {
        var session = CreateSession();
        var player = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var guard = SpawnGuard(session, anchor: null, hostileToPlayer: false, at: player.Translate(4, 2));
        var before = guard.Get<PositionComponent>().Position;

        session.Engine.RunActorTurns();

        Assert.Equal(before, guard.Get<PositionComponent>().Position);
    }

    private static GameSession CreateSession()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 71);
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }

        // These tests assert movement geometry, not pathing around the yard's walls.
        session.Engine.State.BlockingTerrain.Clear();
        return session;
    }

    private static GridPoint TowardCenter(GameSession session, GridPoint from, int steps)
    {
        var state = session.Engine.State;
        var dx = from.X < state.Width / 2 ? steps : -steps;
        var dy = from.Y < state.Height / 2 ? steps : -steps;
        return new GridPoint(from.X + dx, from.Y + dy);
    }

    private static GridPoint FarCorner(GameSession session)
    {
        var state = session.Engine.State;
        var player = state.ControlledEntity.Get<PositionComponent>().Position;
        // A corner well away from the player so faction hostility, not proximity, drives tests.
        return player.X > state.Width / 2
            ? new GridPoint(2, 2)
            : new GridPoint(state.Width - 3, state.Height - 3);
    }

    private static Entity SpawnGuard(
        GameSession session,
        GridPoint? anchor,
        bool hostileToPlayer,
        GridPoint at)
    {
        if (hostileToPlayer)
        {
            session.Engine.State.Factions.AdjustStanding("test_watch", "hostile:player", 3);
        }

        var spawned = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "test sentry",
            at.X,
            at.Y,
            prefix: "guard",
            glyph: 'g',
            faction: "test_watch",
            hp: 10,
            attack: 1,
            tags: new[] { "npc", "objective_guard" },
            material: "flesh",
            roles: new[] { "guard" },
            controllerKind: "ai",
            aiPolicyId: "guard",
            aiParameters: anchor is { } post
                ? new Dictionary<string, object?> { ["anchorX"] = post.X, ["anchorY"] = post.Y }
                : null,
            summoned: false,
            emitMessage: false));
        Assert.True(spawned.Applied, spawned.Error);
        return session.Engine.State.Entities[EntityId.Create(spawned.TargetId!)];
    }
}
