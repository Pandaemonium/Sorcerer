using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class EncounterGrammarTests
{
    [Fact]
    public void EncounterLibraryMeetsTheIngredientFloor()
    {
        var catalog = EncounterTemplateCatalog.LoadDefault();
        Assert.True(catalog.Archetypes.Count >= 10, $"expected >=10 encounter ingredients, found {catalog.Archetypes.Count}");
    }

    [Fact]
    public void ArchetypeReferencedCastDrawsStatsAndCounterFromTheActorCatalog()
    {
        var regions = RegionCatalog.LoadDefault();
        var region = regions.Region("imperial_encounter")!;

        var catalog = new EncounterTemplateCatalog();
        catalog.Add(new EncounterArchetypeDefinition(
            "test_patrol",
            EncounterAssembler.KindGuardedCache,
            MinTier: 1,
            MaxTier: 3,
            RequiresInterior: false,
            AmbientEligible: true,
            Formation: "ring",
            CanonPattern: "{item} under patrol at {place}.",
            Weight: 1,
            Tags: new[] { "encounter" },
            Casts: new[]
            {
                new EncounterFactionCastDefinition("empire", 1, 0, new[]
                {
                    new EncounterCastSlotDefinition(
                        "warden", "guard", "yard warden", 'p', "guard",
                        new[] { "encounter_cast" }, new[] { "guard" },
                        "Guard {item}.", "",
                        CountByTier: new[] { 1, 1, 1 },
                        ArchetypeId: "yard_warden"),
                }),
            }));

        var request = new EncounterRequest(
            WorldSeed: 7,
            ZoneId: "1,0",
            Purpose: "ambient",
            Discriminator: "test",
            Region: region,
            ObjectiveName: "a sealed crate",
            PromiseSalience: 4,
            FactionPressure: 0,
            InteriorAvailable: false);

        var plan = EncounterAssembler.Assemble(request, catalog);
        Assert.NotNull(plan);
        var warden = Assert.Single(plan!.Casts);
        // Glyph and identity come from the yard_warden actor archetype (glyph 'W'), not the slot 'p'.
        Assert.Equal('W', warden.Glyph);
        Assert.Contains("actor_archetype", warden.Tags);
        // The archetype's non-damage counter is folded into the want-stakes teaching surface.
        Assert.Contains("counter:", warden.WantStakes, System.StringComparison.OrdinalIgnoreCase);
        Assert.True(warden.HitPoints >= 12, "warden should use the archetype's sturdier HP range");
    }
}
