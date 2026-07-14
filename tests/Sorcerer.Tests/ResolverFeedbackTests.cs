using Sorcerer.Magic.Auditing;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// The resolver self-critique corpus parses an optional "feedback" object off the raw resolver
/// answer without affecting resolution. These lock the tolerant extraction: present/absent/empty
/// feedback, snake_case keys, and answers wrapped in stray prose.
/// </summary>
public sealed class ResolverFeedbackTests
{
    [Fact]
    public void ExtractsFeedbackObjectFromResolution()
    {
        const string raw = """
            {"accepted":true,"severity":"minor","outcomeText":"A blue light.","effects":[],"costs":[],
             "rejectedReason":null,
             "feedback":{"missingCapability":"relocate_entity","unusedContext":"lore, promises"}}
            """;

        var feedback = ResolverFeedback.TryExtract(raw);

        Assert.NotNull(feedback);
        Assert.Equal("relocate_entity", feedback!.MissingCapability);
        Assert.Equal("lore, promises", feedback.UnusedContext);
        Assert.True(feedback.HasContent);
    }

    [Fact]
    public void ReturnsNullWhenNoFeedbackObject()
    {
        const string raw = """
            {"accepted":true,"severity":"minor","outcomeText":"A blue light.","effects":[],"costs":[]}
            """;

        Assert.Null(ResolverFeedback.TryExtract(raw));
    }

    [Fact]
    public void ReturnsNullWhenFeedbackFieldsAreEmpty()
    {
        const string raw = """
            {"accepted":true,"feedback":{"missingCapability":"","unusedContext":"   "}}
            """;

        Assert.Null(ResolverFeedback.TryExtract(raw));
    }

    [Fact]
    public void ToleratesSnakeCaseKeys()
    {
        const string raw = """
            {"accepted":true,"feedback":{"missing_capability":"summon_wall","unused_context":""}}
            """;

        var feedback = ResolverFeedback.TryExtract(raw);

        Assert.NotNull(feedback);
        Assert.Equal("summon_wall", feedback!.MissingCapability);
        Assert.Null(feedback.UnusedContext);
    }

    [Fact]
    public void ToleratesProseAroundTheJson()
    {
        const string raw = "Here is the resolution:\n{\"accepted\":true,\"feedback\":{\"missingCapability\":\"teleport\"}}\nDone.";

        var feedback = ResolverFeedback.TryExtract(raw);

        Assert.NotNull(feedback);
        Assert.Equal("teleport", feedback!.MissingCapability);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json at all")]
    public void ReturnsNullForUnusableInput(string? raw)
    {
        Assert.Null(ResolverFeedback.TryExtract(raw));
    }
}
