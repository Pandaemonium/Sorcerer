using System;
using System.Linq;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Primitives;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 6 of the WildMagic import: the promise payoff vocabulary the plan names is honored. These
/// lock that alias kinds (route, trade, folk_magic) resolve to their buildable payoff families and
/// realize a bound promise on travel, keeping "a bound promise must be honored" true end to end.
/// </summary>
public sealed class PromiseVocabularyTests
{
    [Theory]
    [InlineData("route")]        // -> escape_route
    [InlineData("trade")]        // -> merchant_stock
    [InlineData("folk_magic")]   // -> service
    public async Task AliasPayoffKindRealizesBoundPromiseOnTravel(string realizationKind)
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var create = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "Something promised waits east of here.",
            triggerHint: "travel",
            realizationKind: realizationKind,
            subject: "a promised thing",
            bindPlace: session.Engine.State.RegionId,
            playerVisible: true,
            salience: 4,
            operation: "testCreatePromise",
            emitMessage: false));
        Assert.True(create.Applied, create.Error);

        var result = await session.ExecuteAsync(new TravelCommand(Direction.East));

        var promise = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == create.TargetId);
        Assert.True(result.Success);
        Assert.Equal("realized", promise.Status);
    }
}
