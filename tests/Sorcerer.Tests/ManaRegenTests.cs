using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Magic;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Waiting a turn is a deliberate rest: it recovers 1 mana, never past the maximum. A wait at full
/// mana changes nothing (no regen delta, no message).
/// </summary>
public sealed class ManaRegenTests
{
    private static int Mana(GameSession session) =>
        session.Engine.State.ControlledEntity.Get<ActorComponent>().Mana;

    [Fact]
    public async Task WaitingBelowMaxRegeneratesOneMana()
    {
        var session = NewSessionThatSpendsFiveMana(out var cast);
        await session.ExecuteAsync(cast);
        var manaAfterCast = Mana(session);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(result.Deltas, delta => delta.Operation.Equals("waitManaRegen", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(manaAfterCast + 1, Mana(session));
    }

    [Fact]
    public async Task WaitingAtFullManaDoesNotRegenerate()
    {
        var session = GameSession.CreateImperialEncounter();
        var full = Mana(session);

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.DoesNotContain(result.Deltas, delta => delta.Operation.Equals("waitManaRegen", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(full, Mana(session));
    }

    private static GameSession NewSessionThatSpendsFiveMana(out CastCommand cast)
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "A warding spark.",
            Effects: new[]
            {
                new SpellEffect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["status"] = "warded",
                    ["duration"] = 2,
                }),
            },
            Costs: new[]
            {
                new SpellCost("mana", new Dictionary<string, object?> { ["amount"] = 5 }),
            },
            RejectedReason: null);
        cast = new CastCommand("ward myself");
        return GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
    }

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        private readonly SpellResolution _resolution;

        public FixtureSpellProvider(SpellResolution resolution) => _resolution = resolution;

        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(SpellRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SpellProviderResult("fixture", "{}", _resolution, false, null));
    }
}
