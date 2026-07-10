using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class RegionalPopulationGenerationTests
{
    [Fact]
    public void EveryRegionHasAUsablePopulationGrammar()
    {
        var catalog = RegionCatalog.LoadDefault();

        Assert.Equal(14, catalog.Regions.Count);
        Assert.Equal(44, catalog.Regions.Sum(region => region.Population?.Archetypes.Count ?? 0));
        foreach (var region in catalog.Regions)
        {
            var population = Assert.IsType<RegionPopulationGrammarDefinition>(region.Population);
            Assert.NotEmpty(population.Names.GivenNames);
            Assert.NotEmpty(population.Names.ByNames);
            Assert.InRange(population.MaxResidents, 1, 12);
            Assert.True(population.CenterMean > population.WildMean, region.Id);
            Assert.True(population.NearMean >= population.WildMean, region.Id);
            Assert.True(population.Archetypes.Count >= 2, region.Id);
            Assert.All(population.Archetypes, archetype =>
            {
                Assert.NotEmpty(archetype.Descriptions);
                Assert.NotEmpty(archetype.Wants);
                Assert.True(archetype.CenterWeight + archetype.NearWeight + archetype.WildWeight > 0);
                Assert.InRange(archetype.KnowledgeTier, 1, 3);
            });
        }
    }

    [Fact]
    public void NearbyGeneratedResidentsDoNotRepeatFullNames()
    {
        const int seed = 411;
        var world = WorldRoll.Create(seed);
        foreach (var region in RegionCatalog.LoadDefault().Regions)
        {
            var placement = region.Placement ?? new RegionMapPlacement(0, 0);
            var anchor = (placement.AnchorX, placement.AnchorY);
            var realm = world.RealmFor(region.RealmId);
            var named = new List<(int X, int Y, string Name)>();
            for (var y = anchor.AnchorY - 2; y <= anchor.AnchorY + 2; y++)
            {
                for (var x = anchor.AnchorX - 2; x <= anchor.AnchorX + 2; x++)
                {
                    var batch = RegionPopulationGenerator.Generate(seed, $"{x},{y}", region, realm, anchor);
                    named.AddRange(batch.Residents.Select(resident => (x, y, resident.Name)));
                }
            }

            for (var first = 0; first < named.Count; first++)
            {
                for (var second = first + 1; second < named.Count; second++)
                {
                    if (Math.Max(
                        Math.Abs(named[first].X - named[second].X),
                        Math.Abs(named[first].Y - named[second].Y)) <= 1)
                    {
                        Assert.False(
                            named[first].Name.Equals(named[second].Name, StringComparison.OrdinalIgnoreCase),
                            $"{region.Id} repeated {named[first].Name} in neighboring zones.");
                    }
                }
            }
        }
    }

    [Fact]
    public void PopulationFieldIsDeterministicSparseInWildsAndCrowdedAtItsCenter()
    {
        const int seed = 731;
        var region = RegionCatalog.LoadDefault().Region("stalnaz_highlands")!;
        var realm = WorldRoll.Create(seed).RealmFor(region.RealmId);
        var anchor = (region.Placement!.AnchorX, region.Placement.AnchorY);
        var transect = Enumerable.Range(0, 20)
            .Select(index => $"{anchor.AnchorX + 19 - index},{anchor.AnchorY}")
            .Select(zoneId => RegionPopulationGenerator.Generate(seed, zoneId, region, realm, anchor))
            .ToArray();
        var repeat = Enumerable.Range(0, 20)
            .Select(index => $"{anchor.AnchorX + 19 - index},{anchor.AnchorY}")
            .Select(zoneId => RegionPopulationGenerator.Generate(seed, zoneId, region, realm, anchor))
            .ToArray();

        Assert.True(transect.Take(15).Count(batch => batch.Residents.Count == 0) >= 5);
        Assert.All(transect.Take(15), batch => Assert.Equal(RegionPopulationGenerator.WildHabitat, batch.Habitat));
        Assert.InRange(transect[^1].Residents.Count, 3, region.Population!.MaxResidents);
        Assert.Equal(RegionPopulationGenerator.CenterHabitat, transect[^1].Habitat);
        Assert.True(
            transect[^1].Residents.Count > transect.Take(15).Average(batch => batch.Residents.Count));
        Assert.Equal(
            Signature(transect),
            Signature(repeat));
    }

    [Fact]
    public void CapitalCenterProducesDistinctNamedRolesAndGuaranteedCommerce()
    {
        const int seed = 90;
        var region = RegionCatalog.LoadDefault().Region("vigovian_capital")!;
        var realm = WorldRoll.Create(seed).RealmFor(region.RealmId);
        var batch = RegionPopulationGenerator.Generate(seed, "3,0", region, realm, (3, 0));

        Assert.Equal(RegionPopulationGenerator.CenterHabitat, batch.Habitat);
        Assert.InRange(batch.Residents.Count, 3, 8);
        Assert.Equal(batch.Residents.Count, batch.Residents.Select(resident => resident.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(batch.Residents, resident =>
        {
            Assert.Contains(", ", resident.Name, StringComparison.Ordinal);
            Assert.False(string.IsNullOrWhiteSpace(resident.WantText));
            Assert.Contains("knowledge_", string.Join('|', resident.Tags), StringComparison.OrdinalIgnoreCase);
        });
        Assert.Contains(batch.Residents, resident => resident.ArchetypeId == "bone_broker" && resident.Wares.Count >= 2);
    }

    [Fact]
    public async Task GeneratedCrowdUsesSharedWantTradeServiceDialogueAndBondLanes()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 7,
            dialogueProvider: new MockDialogueProvider());
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var residents = session.Engine.State.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("regional_population", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var apothecary = Assert.Single(residents, entity =>
            entity.Get<TagsComponent>().Tags.Contains("reed_apothecary", StringComparer.OrdinalIgnoreCase));

        Assert.True(travel.Success);
        Assert.InRange(residents.Length, 3, 7);
        Assert.Equal(residents.Length, residents.Select(entity => entity.Get<PositionComponent>().Position).Distinct().Count());
        Assert.All(residents, resident =>
        {
            Assert.True(resident.Has<WantComponent>());
            Assert.True(resident.Has<KnowledgeComponent>());
            Assert.Contains("talk", resident.Get<InteractableComponent>().Verbs, StringComparer.OrdinalIgnoreCase);
        });
        Assert.True(apothecary.Has<MerchantComponent>());
        Assert.True(apothecary.Has<ServiceComponent>());

        var wares = await session.ExecuteAsync(new WaresCommand(apothecary.Name));
        var services = await session.ExecuteAsync(new ServicesCommand(apothecary.Name));
        var give = await session.ExecuteAsync(new GiveCommand("grave salt", apothecary.Name));
        var talk = await session.ExecuteAsync(new TalkCommand("What do you need, and what has the water noticed?"));
        var bonds = await session.ExecuteAsync(new BondsCommand(apothecary.Name));

        Assert.True(wares.Success);
        Assert.Contains(wares.Messages, message => message.Contains("red tincture", StringComparison.OrdinalIgnoreCase));
        Assert.True(services.Success);
        Assert.Contains(services.Messages, message => message.Contains("reed remedy", StringComparison.OrdinalIgnoreCase));
        Assert.True(give.Success);
        Assert.True(talk.Success);
        Assert.True(bonds.Success);
    }

    private static string Signature(IEnumerable<RegionPopulationBatch> batches) =>
        string.Join(
            "||",
            batches.Select(batch => $"{batch.Habitat}:{batch.DistanceToCenter}:{string.Join('|', batch.Residents.Select(resident => $"{resident.Name}/{resident.ArchetypeId}/{resident.HitPoints}/{resident.Attack}"))}"));
}
