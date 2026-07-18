using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ActorArchetypeTests
{
    [Fact]
    public void CatalogMeetsTheHostileAndPetFloorsWithLegibleFields()
    {
        var catalog = ActorArchetypeCatalog.LoadDefault();

        var hostiles = catalog.Archetypes.Where(a => a.IsHostile).ToArray();
        var pets = catalog.Archetypes.Where(a => a.IsPet).ToArray();
        Assert.True(hostiles.Length >= 12, $"expected >=12 hostile/pressure archetypes, found {hostiles.Length}");
        Assert.True(pets.Length >= 8, $"expected >=8 pet archetypes, found {pets.Length}");

        // Every hostile is tactically legible: a telegraphed intent and a non-damage counter.
        Assert.All(hostiles, hostile =>
        {
            Assert.False(string.IsNullOrWhiteSpace(hostile.Intent), $"{hostile.Id} needs an intent");
            Assert.False(string.IsNullOrWhiteSpace(hostile.Counter), $"{hostile.Id} needs a non-damage counter");
        });

        // Every pet has a temperament and at least two actionable verbs.
        Assert.All(pets, pet =>
        {
            Assert.False(string.IsNullOrWhiteSpace(pet.Temperament), $"{pet.Id} needs a temperament");
            Assert.True(pet.Verbs.Count >= 2, $"{pet.Id} needs >=2 actionable verbs");
        });
    }

    [Fact]
    public void EmbeddedAndLooseActorCatalogsAgree()
    {
        var loose = ActorArchetypeCatalog.LoadDefault();
        var embedded = ActorArchetypeCatalog.LoadEmbedded();
        Assert.Equal(
            loose.Archetypes.Select(a => a.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase),
            embedded.Archetypes.Select(a => a.Id).OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void HollowmerePetsScopeToHollowmereAndAreImportedOrLocal()
    {
        var catalog = ActorArchetypeCatalog.LoadDefault();
        var hollowmerePets = catalog.PetsFor("hollowmere_margin");
        var brallPets = catalog.PetsFor("brall_whaleholds");

        Assert.True(hollowmerePets.Count >= 8);
        Assert.Empty(brallPets); // pets are authored for Hollowmere, not Brall
        Assert.Contains(hollowmerePets, pet => pet.Tags.Contains("imported", StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExoticPetsAppearInHollowmereZones()
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var seed = 1; seed <= 6 && found.Count == 0; seed++)
        {
            var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);
            foreach (var entity in session.Engine.State.Entities.Values.Where(e => e.Has<AiComponent>()))
            {
                entity.Set(new AiComponent("idle"));
            }

            // Walk the near-Hollowmere zones where markets and reeds are.
            foreach (var (dx, dy) in new[] { (1, 0), (1, 1), (0, 1) })
            {
                await TravelToAsync(session, dx, dy);
                foreach (var creature in session.Engine.State.Entities.Values.Where(e =>
                    e.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Contains("pet", StringComparer.OrdinalIgnoreCase)))
                {
                    found.Add(creature.Name);
                }
            }
        }

        Assert.NotEmpty(found);
    }

    private static async Task TravelToAsync(GameSession session, int destX, int destY)
    {
        var (x, y) = Parse(session.Engine.State.CurrentZoneId);
        var guard = 0;
        while ((x != destX || y != destY) && guard++ < 20)
        {
            var dir = x != destX
                ? (x < destX ? Direction.East : Direction.West)
                : (y < destY ? Direction.South : Direction.North);
            await session.ExecuteAsync(new TravelCommand(dir));
            (x, y) = Parse(session.Engine.State.CurrentZoneId);
        }
    }

    private static (int X, int Y) Parse(string zoneId)
    {
        var parts = zoneId.Split(',');
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}
