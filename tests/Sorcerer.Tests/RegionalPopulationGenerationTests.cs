using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine;
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
        // WP7 added the Hollowmere Free Folk watching cell, safe-haven provider, Kindled radical,
        // and a pro-Empire refuser (docs/CONTENT_SPRINT_PLAN.md).
        Assert.True(catalog.Regions.Sum(region => region.Population?.Archetypes.Count ?? 0) >= 57);
        Assert.True(catalog.Region("imperial_encounter")!.Population!.Archetypes.Count >= 5);
        Assert.True(catalog.Region("hollowmere_margin")!.Population!.Archetypes.Count >= 10);
        Assert.True(catalog.Region("brall_whaleholds")!.Population!.Archetypes.Count >= 8);
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
    public void RequiredHollowmereMerchantRollsAFreshNonEmptyAssortmentAcrossSeeds()
    {
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!;
        var anchor = (region.Placement!.AnchorX, region.Placement.AnchorY);
        var assortments = Enumerable.Range(1, 20)
            .Select(seed =>
            {
                var realm = WorldRoll.Create(seed).RealmFor(region.RealmId);
                var resident = RegionPopulationGenerator.Generate(
                        seed,
                        $"{anchor.AnchorX},{anchor.AnchorY}",
                        region,
                        realm,
                        anchor)
                    .Residents
                    .Single(item => item.ArchetypeId == "reed_apothecary");
                return resident.Wares.Select(ware => ware.Item).ToHashSet(StringComparer.OrdinalIgnoreCase);
            })
            .ToArray();

        Assert.All(assortments, Assert.NotEmpty);
        Assert.True(assortments.Select(set => string.Join("|", set.OrderBy(item => item))).Distinct().Count() >= 8);
        Assert.All(assortments.SelectMany(set => set).Distinct(StringComparer.OrdinalIgnoreCase), item =>
            Assert.True(assortments.Count(set => set.Contains(item)) < assortments.Length,
                $"{item} should not appear in every seed"));
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

    [Theory]
    [InlineData("hollowmere_margin", "1,0", "reed_apothecary", "freefolk_shelterwright", "hollowmere_loyalist")]
    [InlineData("brall_whaleholds", "16,0", "bone_carver", "tale_witness", "ale_house_host")]
    public void SettlementCentersPreserveTheirAuthoredThreeVoiceEnsemble(
        string regionId,
        string zoneId,
        string first,
        string second,
        string third)
    {
        const int seed = 7;
        var region = RegionCatalog.LoadDefault().Region(regionId)!;
        var realm = WorldRoll.Create(seed).RealmFor(region.RealmId);
        var parts = zoneId.Split(',');
        var anchor = (int.Parse(parts[0]), int.Parse(parts[1]));
        var batch = RegionPopulationGenerator.Generate(seed, zoneId, region, realm, anchor);

        Assert.Equal(RegionPopulationGenerator.CenterHabitat, batch.Habitat);
        Assert.Contains(batch.Residents, resident => resident.ArchetypeId == first);
        Assert.Contains(batch.Residents, resident => resident.ArchetypeId == second);
        Assert.Contains(batch.Residents, resident => resident.ArchetypeId == third);
        if (regionId == "hollowmere_margin")
        {
            var loyalist = Assert.Single(batch.Residents, resident => resident.ArchetypeId == "hollowmere_loyalist");
            Assert.Equal("hollowmere", loyalist.FactionId);
            Assert.Contains("empire", loyalist.Tags, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("stability", loyalist.Tags, StringComparer.OrdinalIgnoreCase);
        }

        if (regionId == "brall_whaleholds")
        {
            var host = Assert.Single(batch.Residents, resident => resident.ArchetypeId == "ale_house_host");
            Assert.True(host.Wares.Count >= 3);
            Assert.Contains(host.Services, service => service.Id == "witness_backed_introduction");
        }
    }

    [Fact]
    public void MerchantAndServiceRolesAlwaysExposeTheirAdvertisedInteraction()
    {
        var regions = RegionCatalog.LoadDefault().Regions.Where(region => region.Population is not null);
        foreach (var region in regions)
        {
            foreach (var archetype in region.Population!.Archetypes)
            {
                if (archetype.Roles.Contains("merchant", StringComparer.OrdinalIgnoreCase))
                {
                    Assert.True(archetype.Wares.Count > 0,
                        $"{region.Id}/{archetype.Id} advertises merchant but has no wares");
                }

                if (archetype.Roles.Contains("service_provider", StringComparer.OrdinalIgnoreCase))
                {
                    Assert.True(archetype.Services.Count > 0,
                        $"{region.Id}/{archetype.Id} advertises service_provider but has no services");
                }
            }
        }
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
        Assert.Contains(wares.Messages, message => message.Contains("marsh febrifuge", StringComparison.OrdinalIgnoreCase));
        Assert.True(services.Success);
        Assert.Contains(services.Messages, message => message.Contains("reed remedy", StringComparison.OrdinalIgnoreCase));
        Assert.True(give.Success);
        Assert.True(talk.Success);
        Assert.True(bonds.Success);
    }

    [Fact]
    public void SettlementArrivalPresentsItsPeopleSignatureAndPetsWithinAReadableWalk()
    {
        var session = GameSession.CreateImperialEncounter(seed: 6);

        session.Engine.Travel(Direction.East);

        var state = session.Engine.State;
        var player = state.ControlledEntity.Get<PositionComponent>().Position;
        var residents = state.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("regional_population", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var features = state.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("place_feature", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var pets = state.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("pet", StringComparer.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(residents.Count(entity => GameEngine.StepDistance(player, entity.Get<PositionComponent>().Position) <= 4) >= 2);
        Assert.Contains(features, entity => GameEngine.StepDistance(player, entity.Get<PositionComponent>().Position) <= 5);
        Assert.NotEmpty(pets);
        Assert.Contains(pets, entity => GameEngine.StepDistance(player, entity.Get<PositionComponent>().Position) <= 6);
    }

    [Fact]
    public void AmbientEncounterGeneratedForArrivalIsNotHiddenAcrossTheMap()
    {
        var session = GameSession.CreateImperialEncounter(seed: 52);
        session.Engine.Travel(Direction.South);

        var state = session.Engine.State;
        var player = state.ControlledEntity.Get<PositionComponent>().Position;
        var encounter = state.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("encounter_cast", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        Assert.NotEmpty(encounter);
        Assert.All(encounter, entity =>
            Assert.InRange(GameEngine.StepDistance(player, entity.Get<PositionComponent>().Position), 1, 8));
    }

    [Fact]
    public void ReferenceTransectDoesNotPutTheCapitalWestOfTheOpeningYard()
    {
        var session = GameSession.CreateImperialEncounter(seed: 15);

        session.Engine.Travel(Direction.West);

        Assert.Equal("hollowmere_margin", session.Engine.State.RegionId);
    }

    private static string Signature(IEnumerable<RegionPopulationBatch> batches) =>
        string.Join(
            "||",
            batches.Select(batch => $"{batch.Habitat}:{batch.DistanceToCenter}:{string.Join('|', batch.Residents.Select(resident => $"{resident.Name}/{resident.ArchetypeId}/{resident.HitPoints}/{resident.Attack}"))}"));
}
