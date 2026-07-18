using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ScenePropTests
{
    [Fact]
    public void ClaimHookProducesActionableFoundDocuments()
    {
        var regions = RegionCatalog.LoadDefault();
        var region = regions.Region("hollowmere_margin")!;
        var realm = WorldRoll.Create(7).RealmFor(region.RealmId);

        var claims = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            for (var x = 0; x <= 6; x++)
            {
                for (var y = 0; y <= 6; y++)
                {
                    var batch = RegionPropGenerator.Generate(region, realm, seed, $"{x},{y}");
                    foreach (var prop in batch.Props.Where(prop => prop.Claim is not null))
                    {
                        claims++;
                        Assert.False(string.IsNullOrWhiteSpace(prop.Claim!.Text));
                        Assert.True(prop.Claim.PlayerVisible);
                        Assert.Contains("found_document", prop.Tags, StringComparer.OrdinalIgnoreCase);
                        Assert.False(string.IsNullOrWhiteSpace(prop.ReadableText));
                    }
                }
            }
        }

        Assert.True(claims > 0, "claim hooks should produce at least one actionable found document across the sample");
    }

    [Fact]
    public void DistrictWeightingScopesBasesToTheirDistrict()
    {
        var regions = RegionCatalog.LoadDefault();
        var region = regions.Region("hollowmere_margin")!;
        var realm = WorldRoll.Create(7).RealmFor(region.RealmId);

        var reedMarketCrates = 0;
        var ferrywardCrates = 0;
        for (var seed = 1; seed <= 20; seed++)
        {
            for (var x = 0; x <= 6; x++)
            {
                reedMarketCrates += RegionPropGenerator.Generate(region, realm, seed, $"{x},1", "reed_market")
                    .Props.Count(prop => prop.Name.Contains("creature crate", StringComparison.OrdinalIgnoreCase));
                ferrywardCrates += RegionPropGenerator.Generate(region, realm, seed, $"{x},2", "ferryward")
                    .Props.Count(prop => prop.Name.Contains("creature crate", StringComparison.OrdinalIgnoreCase));
            }
        }

        // The creature crate weights heavily toward the reed market (6) over the ferryward (2), so
        // across a large sample it appears markedly more often there. (Ensembles stage members
        // regardless of district, so this is a differential, not an absolute, guarantee.)
        Assert.True(reedMarketCrates > 0, "creature crate should appear in the reed market");
        Assert.True(
            reedMarketCrates > ferrywardCrates,
            $"district weighting should favour the reed market: reed {reedMarketCrates} vs ferry {ferrywardCrates}");
    }
}
