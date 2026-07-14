using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.References;
using Sorcerer.Core.Results;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 1.3 — one failure vocabulary. The reference boundary tags each failure with a stable
/// <see cref="FailureCode"/> family, distinct across missing/out-of-range/unsupported/no-selection,
/// while still carrying a precise player message. A documentation-blind tester can read the code to
/// know how to correct the next command.
/// </summary>
public sealed class FailureVocabularyTests
{
    [Fact]
    public void ReferenceBindingTagsDistinctFailureFamilies()
    {
        var engine = GameSession.CreateImperialEncounter().Engine;

        var missing = ReferenceBinder.Bind(engine, new EntityReference("no_such_entity"));
        Assert.False(missing.Success);
        Assert.Equal(FailureCode.MissingTarget, missing.FailureCode);
        Assert.False(string.IsNullOrWhiteSpace(missing.Error)); // precise message rides alongside the code

        Assert.Equal(FailureCode.OutOfRange,
            ReferenceBinder.Bind(engine, new TileReference(new GridPoint(-9, -9))).FailureCode);

        Assert.Equal(FailureCode.Unsupported,
            ReferenceBinder.Bind(engine, new SelectorReference("not_a_real_selector")).FailureCode);

        Assert.Equal(FailureCode.MissingTarget,
            ReferenceBinder.Bind(engine, new FactionReference("no_such_faction")).FailureCode);

        engine.State.SelectedTarget = null;
        Assert.Equal(FailureCode.NoSelection,
            ReferenceBinder.Bind(engine, new SelectorReference("selected_target")).FailureCode);
    }

    [Fact]
    public void EngineReferenceResolverTagsMalformedAndOutOfRangePoints()
    {
        var engine = GameSession.CreateImperialEncounter().Engine;
        var resolver = new EngineReferenceResolver(engine, engine.State.ControlledEntity, groupCap: 6);

        Assert.Equal(FailureCode.Malformed,
            resolver.Resolve(new EntityRef("point", "not-a-point")).FailureCode);
        Assert.Equal(FailureCode.OutOfRange,
            resolver.Resolve(new EntityRef("point", "-9,-9")).FailureCode);
        Assert.Equal(FailureCode.Unsupported,
            resolver.Resolve(new EntityRef("selector", "not_a_real_selector")).FailureCode);
        Assert.Equal(FailureCode.MissingTarget,
            resolver.Resolve(new EntityRef("id", "no_such_entity")).FailureCode);
    }

    [Fact]
    public async Task TurnSemanticsDistinguishProviderFailureFromIntentionalRejection()
    {
        // Technical provider failure: tagged provider_failure and consumes no turn.
        var technical = await GameSession.CreateImperialEncounter()
            .ExecuteAsync(new CastCommand("turn the soldier blue"));
        Assert.True(technical.TechnicalFailure);
        Assert.False(technical.ConsumedTurn);
        Assert.Equal(FailureCode.ProviderFailure, technical.FailureCode);

        // Intentional in-world rejection: tagged rejected and does consume a turn.
        var rejected = await GameSession
            .CreateImperialEncounter(new WildMagicController(new MockSpellProvider()))
            .ExecuteAsync(new CastCommand("kill the emperor"));
        Assert.False(rejected.TechnicalFailure);
        Assert.True(rejected.ConsumedTurn);
        Assert.Equal(FailureCode.Rejected, rejected.FailureCode);
    }

    [Fact]
    public async Task BlockedMoveIsTaggedBlockedLineAndConsumesNoTurn()
    {
        var session = GameSession.CreateImperialEncounter();
        var engine = session.Engine;
        var origin = engine.State.ControlledEntity.Get<PositionComponent>().Position;

        // Wall the tile the player would step into so the move is blocked (not a travel off the edge).
        var offset = Direction.North.Offset();
        var destination = origin.Translate(offset.X, offset.Y);
        engine.State.BlockingTerrain.Add(destination);
        var turnBefore = engine.State.Turn;

        var result = await session.ExecuteAsync(new MoveCommand(Direction.North));

        Assert.Equal("move", result.Action); // blocked, not resolved as travel
        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(FailureCode.BlockedLine, result.FailureCode);
        Assert.Equal(turnBefore, engine.State.Turn);
    }

    [Fact]
    public async Task BuyingWithoutFundsIsTaggedUnpaidCost()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        await session.ExecuteAsync(new TravelCommand(Direction.East)); // reach the frontier merchant

        // Drain the player's coin so the purchase is unaffordable.
        session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["gold"] = 0;

        var result = await session.ExecuteAsync(new BuyCommand("red tincture"));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(FailureCode.UnpaidCost, result.FailureCode);
        Assert.Contains(result.Messages, message => message.Contains("gold", System.StringComparison.OrdinalIgnoreCase));
    }
}
