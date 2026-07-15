using Sorcerer.Core.Status;
using Xunit;

namespace Sorcerer.Tests;

public sealed class StatusRegistryTests
{
    private readonly StatusRegistry _registry = StatusRegistry.CreateDefault();

    [Theory]
    // Resolver-flavored binding phrasings that must land on the mechanical "rooted" status so a
    // spell that vividly locks an enemy in place actually stops it (regression: creative status
    // ids like "fused_to_floor" used to be cosmetic and the enemy kept attacking).
    [InlineData("fused_to_floor", "rooted")]
    [InlineData("legs_fused_to_marble", "rooted")]
    [InlineData("welded_to_the_stones", "rooted")]
    [InlineData("cemented_in_place", "rooted")]
    [InlineData("marble_shackled", "rooted")]
    [InlineData("manacled", "rooted")]
    [InlineData("encased_in_ice", "rooted")]
    [InlineData("stuck_fast", "rooted")]
    [InlineData("Marble-Bound", "rooted")]
    [InlineData("immobilized", "rooted")]
    public void BindingPhrasingsCanonicalizeToRooted(string input, string expected)
    {
        Assert.Equal(expected, _registry.Canonicalize(input));
        Assert.True(_registry.BlocksMovement(input));
        Assert.True(_registry.BlocksAction(input));
    }

    [Theory]
    // "confused" must resolve to stunned (skips its turn), never to rooted via the "fused"
    // substring — a direct alias hit wins before the substring pass runs.
    [InlineData("confused", "stunned")]
    [InlineData("dazed", "stunned")]
    public void ConfusedResolvesToStunnedNotRooted(string input, string expected)
    {
        Assert.Equal(expected, _registry.Canonicalize(input));
        Assert.True(_registry.BlocksAction(input));
        Assert.False(_registry.BlocksMovement(input));
    }
}
