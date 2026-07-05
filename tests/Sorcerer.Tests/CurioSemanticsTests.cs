using System;
using System.Linq;
using Sorcerer.Core.Items;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 5 of the WildMagic import: ordinary objects are semantic affordances. A generated curio
/// carries semantic tags, a material, and a spell bias for later model interpretation, is region
/// flavored, and is inert (no automatic mechanic) until something invokes it.
/// </summary>
public sealed class CurioSemanticsTests
{
    private static readonly RealmProfile Realm =
        new("hollowmere", "Hollowmere", "contested", "none", "wild_color", 0, new[] { "frontier", "folk" });

    [Fact]
    public void GeneratedCurioCarriesSemanticTagsAndSpellBias()
    {
        var region = RegionRegistry.CreateMinimal().Region("hollowmere_margin")!;

        var curio = CurioGenerator.Generate(region, Realm, new DeterministicRng(42));

        Assert.Contains("curio", curio.Tags);
        Assert.Contains("item", curio.Tags);
        Assert.False(string.IsNullOrWhiteSpace(curio.SpellBias));
        Assert.False(string.IsNullOrWhiteSpace(curio.Material));
        Assert.Equal("inert", curio.ToDefinition().UseProfile);
    }

    [Fact]
    public void CuriosAreRegionFlavoredSoTwoRegionsDiffer()
    {
        var registry = RegionRegistry.CreateMinimal();

        var hollowmere = CurioGenerator.Generate(registry.Region("hollowmere_margin")!, Realm, new DeterministicRng(7));
        var capital = CurioGenerator.Generate(registry.Region("vigovian_capital")!, Realm, new DeterministicRng(7));

        Assert.NotEqual(
            string.Join(",", hollowmere.Tags),
            string.Join(",", capital.Tags));
        Assert.Contains(hollowmere.Tags, tag => tag is "reeds" or "water" or "mud" or "memory");
    }
}
