using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class CostProfileTests
{
    [Fact]
    public void CostProfilesMeetTheFloorAndCarryCounterplay()
    {
        var catalog = CostProfileCatalog.LoadDefault();

        Assert.True(catalog.Profiles.Count >= 9, $"expected >=9 cost profiles, found {catalog.Profiles.Count}");
        Assert.True(catalog.OfKind("curse").Count >= 4);
        Assert.True(catalog.OfKind("debt").Count >= 3);
        Assert.True(catalog.OfKind("altered_item").Count >= 3);

        // Every profile has a mechanical condition, a cause, a journal surface, and at least one
        // route to clear / transfer / exploit / bargain / endure it.
        Assert.All(catalog.Profiles, profile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(profile.Condition), $"{profile.Id} needs a condition");
            Assert.False(string.IsNullOrWhiteSpace(profile.Cause), $"{profile.Id} needs a cause");
            Assert.False(string.IsNullOrWhiteSpace(profile.JournalSurface), $"{profile.Id} needs a journal surface");
            Assert.True(profile.ClearRoutes.Count >= 1, $"{profile.Id} needs at least one counterplay route");
        });
    }

    [Fact]
    public void LooseAndEmbeddedCostProfilesAgree()
    {
        var loose = CostProfileCatalog.LoadDefault();
        var embedded = CostProfileCatalog.LoadEmbedded();
        Assert.Equal(
            loose.Profiles.Select(p => p.Id).OrderBy(id => id, System.StringComparer.OrdinalIgnoreCase),
            embedded.Profiles.Select(p => p.Id).OrderBy(id => id, System.StringComparer.OrdinalIgnoreCase));
    }
}
