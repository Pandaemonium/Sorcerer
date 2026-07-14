using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
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
}
