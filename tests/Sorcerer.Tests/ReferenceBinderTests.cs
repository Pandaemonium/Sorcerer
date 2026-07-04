using Sorcerer.Core;
using Sorcerer.Core.References;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ReferenceBinderTests
{
    [Fact]
    public void BindSelectedTargetWithNoSelectionReportsGuidanceNotBareFailure()
    {
        var session = GameSession.CreateImperialEncounter();

        var bound = ReferenceBinder.Bind(session.Engine, ReferenceBinder.Normalize("target"));

        Assert.False(bound.Success);
        Assert.Equal(ReferenceBinder.NoSelectedTargetMessage, bound.Error);
        Assert.Contains("target <x> <y>", bound.Error);
    }

    [Fact]
    public void BindEntityByNonexistentIdReportsUnifiedNotVisibleMessage()
    {
        var session = GameSession.CreateImperialEncounter();

        var bound = ReferenceBinder.Bind(session.Engine, new EntityReference("no_such_entity_42"));

        Assert.False(bound.Success);
        Assert.Equal(ReferenceBinder.NoVisibleTargetMessage("no_such_entity_42"), bound.Error);
        Assert.Equal("Nothing you can see answers to 'no_such_entity_42'.", bound.Error);
    }

    [Fact]
    public void ResolveSelectedTargetWithNoSelectionMatchesSharedMessage()
    {
        var session = GameSession.CreateImperialEncounter();
        var resolver = new EngineReferenceResolver(session.Engine, session.Engine.State.ControlledEntity);

        var resolved = resolver.Resolve(new EntityRef("selector", "selected_target"));

        Assert.False(resolved.Success);
        Assert.Equal(ReferenceBinder.NoSelectedTargetMessage, resolved.Error);
    }

    [Fact]
    public void ResolveByNonexistentIdAndByNonexistentNameProduceByteIdenticalMessages()
    {
        var session = GameSession.CreateImperialEncounter();
        var resolver = new EngineReferenceResolver(session.Engine, session.Engine.State.ControlledEntity);

        var byId = resolver.Resolve(new EntityRef("id", "phantom_entity_id"));
        var byName = resolver.Resolve(new EntityRef("name", "phantom entity"));

        Assert.False(byId.Success);
        Assert.False(byName.Success);
        Assert.Equal("Nothing you can see answers to 'phantom_entity_id'.", byId.Error);
        Assert.Equal("Nothing you can see answers to 'phantom entity'.", byName.Error);
        // Same template regardless of whether the id/name never existed at all - the wording
        // must not distinguish "does not exist" from "exists but you cannot perceive it."
        Assert.StartsWith("Nothing you can see answers to '", byId.Error);
        Assert.StartsWith("Nothing you can see answers to '", byName.Error);
    }
}
