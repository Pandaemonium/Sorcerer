using System;
using System.Linq;
using Sorcerer.Core.Lore;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 3 of the WildMagic import: the WildMagic lore cards were converted into Sorcerer's lore
/// format and promoted as canon. These lock that they load, route by subject to the right culture,
/// and gate deeper sections behind access tier.
/// </summary>
public sealed class LoreImportTests
{
    [Theory]
    [InlineData("crystal")]
    [InlineData("stalnaz")]
    [InlineData("gontark")]
    [InlineData("empire")]
    [InlineData("vigovia")]
    [InlineData("shadow_purge")]
    [InlineData("merfolk")]
    [InlineData("rentacosta")]
    public void ImportedWildMagicLoreCardLoads(string id)
    {
        var catalog = LoreCatalog.LoadDefault();

        Assert.Contains(catalog.Cards, card => card.Id == id);
    }

    [Fact]
    public void SubjectRoutingReturnsMatchingCultureAndNotAnUnrelatedOne()
    {
        var catalog = LoreCatalog.LoadDefault();

        var routed = LoreRouter.Select(catalog, new LoreQuery(
            new[] { "crystal" },
            Array.Empty<string>(),
            AccessLevel: 1,
            Limit: 10));

        Assert.Contains(routed, card => card.Id == "crystal");
        Assert.DoesNotContain(routed, card => card.Id == "birdfolk");
    }

    [Fact]
    public void AccessTierGatesDeeperLoreSections()
    {
        var catalog = LoreCatalog.LoadDefault();

        var common = LoreRouter
            .Select(catalog, new LoreQuery(new[] { "crystal" }, Array.Empty<string>(), AccessLevel: 0, Limit: 10))
            .Single(card => card.Id == "crystal");
        var familiar = LoreRouter
            .Select(catalog, new LoreQuery(new[] { "crystal" }, Array.Empty<string>(), AccessLevel: 1, Limit: 10))
            .Single(card => card.Id == "crystal");

        // Level 0 (common) is visible at both tiers; Level 1 (familiar) only at access 1.
        Assert.Contains("signature art", common.Body);
        Assert.DoesNotContain("material edge", common.Body);
        Assert.Contains("material edge", familiar.Body);
    }
}
