using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class SettlementGraphTests
{
    [Fact]
    public void EveryRegionHasSettlementDistrictAndLandmarkContent()
    {
        var catalog = RegionCatalog.LoadDefault();

        Assert.Equal(14, catalog.Regions.Count);
        Assert.Equal(42, catalog.Regions.Sum(region => region.Settlement?.Districts.Count ?? 0));
        Assert.All(catalog.Regions, region =>
        {
            var grammar = Assert.IsType<RegionSettlementGrammarDefinition>(region.Settlement);
            Assert.True(grammar.Districts.Count >= 3, region.Id);
            Assert.NotEmpty(grammar.SettlementNames);
            Assert.NotEmpty(grammar.Landmarks);
            Assert.False(string.IsNullOrWhiteSpace(grammar.RoadName));
            Assert.All(grammar.Districts, district =>
            {
                Assert.False(string.IsNullOrWhiteSpace(district.Summary));
                Assert.False(string.IsNullOrWhiteSpace(district.FeatureName));
                Assert.InRange(district.PopulationPercent, 20, 250);
            });
        });
    }

    [Fact]
    public void SameSeedProducesTheSameConnectedPlaceGraph()
    {
        var catalog = RegionCatalog.LoadDefault();
        var first = WorldPlaceGraph.Create(9127, catalog);
        var repeat = WorldPlaceGraph.Create(9127, catalog);

        Assert.Equal(Signature(first), Signature(repeat));
        Assert.Equal(14, first.Settlements.Count(settlement => settlement.IsPrimary));
        Assert.True(first.Settlements.Count >= 25);
        Assert.Equal(14, first.Landmarks.Count);
        Assert.All(first.Roads, road =>
        {
            var from = first.Settlements.Single(settlement => settlement.Id == road.FromSettlementId);
            var to = first.Settlements.Single(settlement => settlement.Id == road.ToSettlementId);
            Assert.Contains($"{from.CenterX},{from.CenterY}", road.ZoneIds);
            Assert.Contains($"{to.CenterX},{to.CenterY}", road.ZoneIds);
            Assert.True(road.ZoneIds.Count >= 2);
        });

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            first.Settlements.First(settlement => settlement.IsPrimary).Id,
        };
        while (true)
        {
            var before = reached.Count;
            foreach (var road in first.Roads)
            {
                if (reached.Contains(road.FromSettlementId))
                {
                    reached.Add(road.ToSettlementId);
                }

                if (reached.Contains(road.ToSettlementId))
                {
                    reached.Add(road.FromSettlementId);
                }
            }

            if (reached.Count == before)
            {
                break;
            }
        }

        Assert.All(first.Settlements, settlement => Assert.Contains(settlement.Id, reached));
    }

    [Fact]
    public void EveryPrimarySettlementCrossSectionHasAtLeastThreeDistricts()
    {
        var catalog = RegionCatalog.LoadDefault();
        var graph = WorldPlaceGraph.Create(77, catalog);

        foreach (var settlement in graph.Settlements.Where(settlement => settlement.IsPrimary))
        {
            var districts = Enumerable.Range(-settlement.Radius, (settlement.Radius * 2) + 1)
                .Select(dx => graph.Profile($"{settlement.CenterX + dx},{settlement.CenterY}", settlement.RegionId).District?.Id)
                .Where(id => id is not null)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.True(districts.Length >= 3, settlement.Name);
        }
    }

    [Fact]
    public void BroadRegionJourneyResolvesToItsPrimarySettlementWhileLandmarksRemainExplicit()
    {
        var graph = WorldPlaceGraph.Create(19, RegionCatalog.LoadDefault());
        var brallRegion = RegionCatalog.LoadDefault().Regions.Single(region =>
            region.Name.Contains("Brall", StringComparison.OrdinalIgnoreCase));
        var brallPrimary = graph.Settlements.Single(settlement =>
            settlement.IsPrimary && settlement.RegionId.Equals(brallRegion.Id, StringComparison.OrdinalIgnoreCase));
        var brallLandmark = graph.Landmarks.Single(landmark =>
            landmark.RegionId.Equals(brallRegion.Id, StringComparison.OrdinalIgnoreCase));

        var broad = Assert.IsType<JourneyDestination>(graph.ResolveDestination("Brall", "0,0", "containment_yard"));
        var explicitLandmark = Assert.IsType<JourneyDestination>(
            graph.ResolveDestination(brallLandmark.Name, "0,0", "containment_yard"));

        Assert.Equal($"{brallPrimary.CenterX},{brallPrimary.CenterY}", broad.ZoneId);
        Assert.Equal(WorldPlaceKinds.Settlement, broad.Kind);
        Assert.Equal(brallLandmark.ZoneId, explicitLandmark.ZoneId);
        Assert.Equal(WorldPlaceKinds.Landmark, explicitLandmark.Kind);
    }

    [Fact]
    public async Task CapitalWalkCrossesNamedDistrictsAndAtlasQuotesTheSameGraph()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        DisableAi(session);

        var hollowmere = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var first = session.View().World!;
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var gate = session.View().World!;
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var court = session.View().World!;
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var archive = session.View().World!;
        var atlas = await session.ExecuteAsync(new AtlasCommand());

        Assert.Contains(hollowmere.Messages, message => message.Contains("Ferryward", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(WorldPlaceKinds.Settlement, first.PlaceKind);
        Assert.Equal("Ferryward", first.DistrictName);
        Assert.Equal("Censor Gate", gate.DistrictName);
        Assert.Equal("Inner Court", court.DistrictName);
        Assert.Equal("Archive Quarter", archive.DistrictName);
        Assert.Equal(3, new[] { gate.DistrictName, court.DistrictName, archive.DistrictName }.Distinct().Count());
        Assert.Contains(atlas.Messages, message => message.Contains(archive.SettlementName!, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(atlas.Messages, message => message.Contains("district: Archive Quarter", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(court.SettlementName);
        Assert.NotNull(court.NearestSettlement);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("place_feature", StringComparer.OrdinalIgnoreCase));
    }

    private static string Signature(WorldPlaceGraph graph) =>
        string.Join(
            "||",
            graph.Settlements.OrderBy(settlement => settlement.Id).Select(settlement =>
                $"S:{settlement.Id}:{settlement.Name}:{settlement.CenterX},{settlement.CenterY}:{settlement.Radius}")
            .Concat(graph.Roads.OrderBy(road => road.Id).Select(road =>
                $"R:{road.Id}:{road.Name}:{road.FromSettlementId}:{road.ToSettlementId}:{string.Join(';', road.ZoneIds)}"))
            .Concat(graph.Landmarks.OrderBy(landmark => landmark.Id).Select(landmark =>
                $"L:{landmark.Id}:{landmark.Name}:{landmark.ZoneId}")));

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
