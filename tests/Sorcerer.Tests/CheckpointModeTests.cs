using System;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Runtime;
using Sorcerer.Magic;
using Sorcerer.Magic.Replay;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 2.5 checkpoint mode: a killing blow rewinds to the last safe rest instead of ending the
/// run. The checkpoint is captured only at an authoritative safe rest -- standing in a settlement
/// place with no hostile the sorcerer can perceive -- so the guard-filled imprisonment start (a
/// settlement place, but not safe) never forms one. Classic mode is untouched: death still ends it.
/// </summary>
public sealed class CheckpointModeTests
{
    private const string DamageSelf = """
        {
          "accepted": true,
          "severity": "major",
          "outcomeText": "The room takes your borrowed breath back.",
          "effects": [
            { "type": "damage", "target": "player", "amount": 50, "damageType": "wild" }
          ],
          "costs": []
        }
        """;

    [Fact]
    public async Task CheckpointModeRewindsToTheLastSafeRestInsteadOfDying()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { DamageSelf })));
        session.Engine.State.RunMode = "checkpoint";
        MakeSafe(session);                               // clear the yard so a safe rest exists

        await session.ExecuteAsync(new WaitCommand());   // resting safely records a checkpoint
        var result = await session.ExecuteAsync(new CastCommand("let the room swallow my current body whole"));

        // The blow lands, but the run rewinds rather than ending; the body is whole again.
        Assert.Equal("running", session.Engine.State.RunStatus);
        Assert.True(session.Engine.State.ControlledEntity.Get<ActorComponent>().Alive);
        Assert.Contains(result.Messages, m => m.Contains("last safe rest", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task CheckpointModeWithoutAnySafeRestStillDies()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { DamageSelf })));
        session.Engine.State.RunMode = "checkpoint";
        PacifyHostiles(session);                         // guards stay present (not safe), but inert

        await session.ExecuteAsync(new WaitCommand());   // at the guarded start: no checkpoint forms
        await session.ExecuteAsync(new CastCommand("let the room swallow my current body whole"));

        // No safe rest was ever reached, so there is nothing to fall back on: the body dies.
        Assert.Equal("defeat", session.Engine.State.RunStatus);
        Assert.False(session.Engine.State.ControlledEntity.Get<ActorComponent>().Alive);
    }

    [Fact]
    public async Task ClassicModeDoesNotRewindEvenFromASafeRest()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { DamageSelf })));
        // RunMode defaults to "classic"; a safe rest must not silently create a fallback.
        MakeSafe(session);

        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new CastCommand("let the room swallow my current body whole"));

        Assert.Equal("defeat", session.Engine.State.RunStatus);
    }

    [Fact]
    public async Task ChronicleRecordsHowManyTimesTheRunWasRestored()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { DamageSelf, DamageSelf })));
        session.Engine.State.RunMode = "checkpoint";
        MakeSafe(session);

        await session.ExecuteAsync(new WaitCommand());                                        // capture
        await session.ExecuteAsync(new CastCommand("let the room swallow my current body whole")); // restore #1
        await session.ExecuteAsync(new CastCommand("let the room swallow my current body whole")); // restore #2

        Assert.Equal("running", session.Engine.State.RunStatus);
        Assert.Equal(2, RunChronicle.Build(session.Engine.State).Restorations);
    }

    // Kill every actor the sorcerer would treat as hostile, directly (no consequence, no reactions),
    // so the current place reads as a safe rest.
    private static void MakeSafe(GameSession session)
    {
        var player = session.Engine.State.ControlledEntity;
        foreach (var entity in session.Engine.State.Entities.Values.ToList())
        {
            if (entity.Id != player.Id
                && entity.TryGet<ActorComponent>(out var actor)
                && session.Engine.IsHostile(player, entity))
            {
                entity.Set(actor with { HitPoints = 0 });
            }
        }
    }

    // Leave hostiles alive and perceivable (so the place is not a safe rest) but strip their AI so
    // they do not act -- letting the test reach the self-inflicted killing blow deliberately.
    private static void PacifyHostiles(GameSession session)
    {
        var player = session.Engine.State.ControlledEntity;
        foreach (var entity in session.Engine.State.Entities.Values.ToList())
        {
            if (entity.Id != player.Id
                && entity.TryGet<ActorComponent>(out _)
                && session.Engine.IsHostile(player, entity))
            {
                entity.Set(new ControllerComponent(ControllerKind.None));
            }
        }
    }
}
