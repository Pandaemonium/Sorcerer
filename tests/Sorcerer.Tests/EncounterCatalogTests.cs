using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class EncounterCatalogTests
{
    private const string SampleDocument = """
    {
      "archetypes": [
        {
          "id": "test_guarded_cache",
          "kind": "guarded_cache",
          "minTier": 1,
          "maxTier": 3,
          "requiresInterior": false,
          "ambientEligible": true,
          "formation": "ring",
          "canonPattern": "{item} under {faction} watch.",
          "weight": 3,
          "tags": ["encounter"],
          "casts": [
            {
              "factionId": "empire",
              "weight": 2,
              "minImperialPresence": 40,
              "slots": [
                {
                  "id": "sentry",
                  "role": "guard",
                  "titlePattern": "waystation sentry",
                  "glyph": "g",
                  "aiPolicyId": "guard",
                  "tags": ["objective_guard"],
                  "roles": ["guard"],
                  "wantPattern": "Keep {item} in imperial custody.",
                  "wantStakes": "Stands down for a stamped writ or a superior's word.",
                  "minHitPoints": 8,
                  "maxHitPoints": 12,
                  "minAttack": 1,
                  "maxAttack": 3,
                  "countByTier": [1, 2, 3]
                }
              ]
            }
          ]
        },
        { "id": "", "kind": "broken_missing_id", "casts": [] },
        { "id": "broken_no_casts", "kind": "keeper" }
      ]
    }
    """;

    [Fact]
    public void LoadFromParsesArchetypesAndSkipsMalformedEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"sorcerer_encounters_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "test.json"), SampleDocument);
            var catalog = EncounterTemplateCatalog.LoadFrom(directory);

            var archetype = Assert.Single(catalog.Archetypes);
            Assert.Equal("test_guarded_cache", archetype.Id);
            Assert.Equal("guarded_cache", archetype.Kind);
            Assert.Equal(3, archetype.Weight);
            Assert.Equal("ring", archetype.Formation);
            var cast = Assert.Single(archetype.Casts);
            Assert.Equal("empire", cast.FactionId);
            Assert.Equal(40, cast.MinImperialPresence);
            var slot = Assert.Single(cast.Slots);
            Assert.Equal("guard", slot.Role);
            Assert.Equal(8, slot.MinHitPoints);
            Assert.Equal(12, slot.MaxHitPoints);
            Assert.Equal(new[] { 1, 2, 3 }, slot.CountByTier);
            Assert.Contains("stamped writ", slot.WantStakes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreateMinimalProvidesUsableFallbackIngredients()
    {
        var catalog = EncounterTemplateCatalog.CreateMinimal();

        Assert.True(catalog.Archetypes.Count >= 2);
        Assert.Contains(catalog.Archetypes, archetype => archetype.Kind == "guarded_cache");
        Assert.Contains(catalog.Archetypes, archetype => archetype.Kind == "keeper");
        Assert.All(catalog.Archetypes, archetype =>
        {
            Assert.NotEmpty(archetype.Casts);
            Assert.All(archetype.Casts, cast => Assert.NotEmpty(cast.Slots));
        });
    }

    [Fact]
    public void LoadDefaultResolvesToAuthoredOrFallbackContent()
    {
        var catalog = EncounterTemplateCatalog.LoadDefault();

        Assert.NotEmpty(catalog.Archetypes);
    }
}
