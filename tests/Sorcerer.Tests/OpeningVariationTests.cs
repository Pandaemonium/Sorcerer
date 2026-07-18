using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class OpeningVariationTests
{
    [Fact]
    public void OpeningVariationIsReproducibleForTheSameSeed()
    {
        var first = GameSession.CreateImperialEncounter(seed: 31415).Engine.State;
        var second = GameSession.CreateImperialEncounter(seed: 31415).Engine.State;

        Assert.Equal(OpeningSignature(first), OpeningSignature(second));
        Assert.Equal(first.Messages, second.Messages);
        Assert.Equal(SweepZone(first), SweepZone(second));
        Assert.Equal(WaystationDirection(first), WaystationDirection(second));
    }

    [Fact]
    public void DifferentSeedsProduceSeveralDistinctOpeningsAndRoutes()
    {
        var openings = new HashSet<string>(StringComparer.Ordinal);
        var incidentMessages = new HashSet<string>(StringComparer.Ordinal);
        var sweepZones = new HashSet<string>(StringComparer.Ordinal);
        var routeDirections = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in Enumerable.Range(101, 24))
        {
            var state = GameSession.CreateImperialEncounter(seed: seed).Engine.State;
            openings.Add(OpeningSignature(state));
            incidentMessages.Add(Assert.Single(state.Messages));
            sweepZones.Add(SweepZone(state));
            routeDirections.Add(WaystationDirection(state));

            AssertSeededRouteIsCoherent(state);
        }

        Assert.True(openings.Count >= 6, $"Expected at least six opening tableaux, got {openings.Count}.");
        Assert.True(incidentMessages.Count >= 4, $"Expected at least four incident lines, got {incidentMessages.Count}.");
        Assert.True(sweepZones.Count >= 3,
            $"Expected at least three sweep destinations, got {sweepZones.Count}: {string.Join(" | ", sweepZones)}.");
        Assert.True(routeDirections.Count >= 3, $"Expected at least three first-leg directions, got {routeDirections.Count}.");
    }

    [Fact]
    public void SeedSevenRemainsThePinnedCharacterizationFixture()
    {
        var state = GameSession.CreateImperialEncounter(seed: 7).Engine.State;

        Assert.Empty(OpeningIrregularities(state));
        Assert.Equal("Imperial soldiers move to contain you.", Assert.Single(state.Messages));
        AssertSeededRouteIsCoherent(state);
    }

    [Fact]
    public async Task TheFirstTwoJourneyLegsDoNotCollapseToOneRoute()
    {
        var firstDestinations = new HashSet<string>(StringComparer.Ordinal);
        var secondLegs = new HashSet<string>(StringComparer.Ordinal);
        var routeSignatures = new HashSet<string>(StringComparer.Ordinal);

        foreach (var seed in Enumerable.Range(201, 12))
        {
            var session = GameSession.CreateImperialEncounter(seed: seed);
            DisableAi(session);
            await FreeOpeningCaptive(session);
            var first = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
                promise.SourceSpeakerId == "prisoner_1"
                && promise.RealizationKind == "person");
            firstDestinations.Add(first.ClaimedPlace!);

            await TravelTo(session, first.ClaimedPlace!);
            var contact = Assert.Single(session.Engine.State.Entities.Values, entity =>
                entity.Name.Equals(first.Subject, StringComparison.OrdinalIgnoreCase));
            var talk = await session.ExecuteAsync(new TalkCommand(contact.Name));
            Assert.True(talk.Success, string.Join(" | ", talk.Messages));
            var next = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
                promise.SourceSpeakerId == contact.Id.Value);

            var from = ParseZone(first.ClaimedPlace!);
            var to = ParseZone(next.ClaimedPlace!);
            secondLegs.Add($"{to.X - from.X},{to.Y - from.Y}");
            routeSignatures.Add($"{first.ClaimedPlace}->{next.ClaimedPlace}");
        }

        Assert.True(firstDestinations.Count >= 3,
            $"Expected three opening destinations, got: {string.Join(" | ", firstDestinations)}.");
        Assert.True(secondLegs.Count >= 3,
            $"Expected three second-leg vectors, got: {string.Join(" | ", secondLegs)}.");
        Assert.True(routeSignatures.Count >= 8,
            $"Expected eight two-leg routes, got: {string.Join(" | ", routeSignatures)}.");
    }

    private static void AssertSeededRouteIsCoherent(GameState state)
    {
        var sweepZone = SweepZone(state);
        var graph = WorldPlaceGraph.Create(state.Seed, RegionCatalog.LoadDefault());
        Assert.Contains(graph.Settlements, settlement =>
            $"{settlement.CenterX},{settlement.CenterY}" == sweepZone);

        var coordinates = sweepZone.Split(',').Select(int.Parse).ToArray();
        Assert.Equal(
            $"{Math.Sign(coordinates[0])},{Math.Sign(coordinates[1])}",
            WaystationDirection(state));
    }

    private static string OpeningSignature(GameState state) => string.Join(
        "|",
        OpeningIrregularities(state)
            .OrderBy(entity => entity.Id.Value, StringComparer.Ordinal)
            .Select(entity =>
            {
                var point = entity.Get<PositionComponent>().Position;
                return $"{entity.Name}@{point.X},{point.Y}";
            }));

    private static IEnumerable<Entity> OpeningIrregularities(GameState state) =>
        state.Entities.Values.Where(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("opening_irregularity", StringComparer.OrdinalIgnoreCase));

    private static string SweepZone(GameState state)
    {
        var sweep = Assert.Single(state.ScheduledEvents.Events, item =>
            item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase));
        return Convert.ToString(sweep.Payload["zone"])!;
    }

    private static string WaystationDirection(GameState state)
    {
        var notice = state.Entities[EntityId.Create("notice_1")].Get<ClaimSourceComponent>();
        return Assert.Single(notice.Claims, claim =>
            claim.Subject.Equals("imperial relay waystation", StringComparison.OrdinalIgnoreCase)).ClaimedPlace!;
    }

    private static async Task FreeOpeningCaptive(GameSession session)
    {
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(7, 6)));
        var pickup = await session.ExecuteAsync(new PickupCommand("key"));
        Assert.True(pickup.Success, string.Join(" | ", pickup.Messages));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var opened = await session.ExecuteAsync(new OpenCommand("cell"));
        Assert.True(opened.Success, string.Join(" | ", opened.Messages));
    }

    private static async Task TravelTo(GameSession session, string zoneId)
    {
        var destination = ParseZone(zoneId);
        var current = ParseZone(session.Engine.State.CurrentZoneId);
        while (current.X != destination.X)
        {
            var result = await session.ExecuteAsync(new TravelCommand(
                current.X < destination.X ? Direction.East : Direction.West));
            Assert.True(result.Success, string.Join(" | ", result.Messages));
            current = ParseZone(session.Engine.State.CurrentZoneId);
        }

        while (current.Y != destination.Y)
        {
            var result = await session.ExecuteAsync(new TravelCommand(
                current.Y < destination.Y ? Direction.South : Direction.North));
            Assert.True(result.Success, string.Join(" | ", result.Messages));
            current = ParseZone(session.Engine.State.CurrentZoneId);
        }
    }

    private static (int X, int Y) ParseZone(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
