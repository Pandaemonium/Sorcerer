using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Primitives;
using Sorcerer.Magic;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GameSessionTests
{
    [Fact]
    public async Task MoveCommandConsumesTurn()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(0, result.TurnBefore);
        Assert.Equal(1, result.TurnAfter);
    }

    [Fact]
    public async Task TechnicalMagicFailureDoesNotConsumeTurn()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new CastCommand("turn the soldier blue"));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.True(result.TechnicalFailure);
        Assert.Equal(0, result.TurnAfter);
    }

    [Fact]
    public async Task AcceptedMagicConsumesTurnThroughSharedSession()
    {
        var provider = new FixtureSpellProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        var result = await session.ExecuteAsync(new CastCommand("strike the nearest soldier"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal("cast", result.Action);
        Assert.NotEmpty(result.Deltas);
        Assert.Equal(1, result.TurnAfter);
    }

    [Fact]
    public async Task ProtectedItemCostFizzleConsumesTurnWithoutMutation()
    {
        var provider = new ProtectedItemCostProvider();
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpBefore = soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints;

        var result = await session.ExecuteAsync(new CastCommand("shatter the moon pearl to strike"));

        Assert.False(result.Success);
        Assert.False(result.TechnicalFailure);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(1, result.TurnAfter);
        Assert.Equal(hpBefore, soldier.Get<Sorcerer.Core.Entities.ActorComponent>().HitPoints);
    }

    [Fact]
    public async Task ProtectCommandUpdatesReagentView()
    {
        var session = GameSession.CreateImperialEncounter();

        var result = await session.ExecuteAsync(new UnprotectItemCommand("moon pearl"));

        Assert.True(result.Success);
        Assert.Contains(session.View().Reagents!, reagent => reagent.Name == "moon pearl");
    }

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "A test spell snaps into place.",
                Effects: new[]
                {
                    new SpellEffect(
                        "damage",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "nearest_enemy",
                            ["amount"] = 3,
                            ["damageType"] = "arcane",
                        }),
                },
                Costs: Array.Empty<SpellCost>(),
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class ProtectedItemCostProvider : ISpellProvider
    {
        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken)
        {
            var resolution = new SpellResolution(
                Accepted: true,
                Severity: "major",
                OutcomeText: "The pearl tries to break itself into moonlit knives.",
                Effects: new[]
                {
                    new SpellEffect(
                        "damage",
                        new Dictionary<string, object?>
                        {
                            ["target"] = "nearest_enemy",
                            ["amount"] = 8,
                        }),
                },
                Costs: new[]
                {
                    new SpellCost(
                        "item",
                        new Dictionary<string, object?>
                        {
                            ["item"] = "moon pearl",
                            ["quantity"] = 1,
                        }),
                },
                RejectedReason: null);

            return Task.FromResult(new SpellProviderResult(
                Name,
                "",
                resolution,
                TechnicalFailure: false,
                Error: null));
        }
    }
}
