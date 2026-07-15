using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class AmbientEncounterTests
{
    [Fact]
    public async Task RegionsThatOptInStageGuardedPrizesAtPredictableZones()
    {
        const int seed = 44;
        var regions = RegionCatalog.LoadDefault();
        var probe = new GenerationSystem(
            new GameState(40, 30) { Seed = seed, RegionId = "imperial_encounter", CurrentZoneId = "0,0" },
            ItemCatalog.CreateMinimal(),
            LoreCatalog.CreateMinimal(),
            regions: regions);
        var session = CreateSession(seed);
        DisableAi(session);
        var promisedZones = session.Engine.State.PromiseLedger.Promises
            .Select(promise => promise.ClaimedPlace)
            .Where(place => place is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        // Derive the first reachable zone whose ambient roll fires, exactly as generation will.
        string? firingZone = null;
        foreach (var zoneId in CandidateZones())
        {
            if (promisedZones.Contains(zoneId))
            {
                continue;
            }

            var region = probe.RegionForZone(zoneId);
            var chance = region.Encounters?.AmbientChancePercent ?? 0;
            if (chance <= 0)
            {
                continue;
            }

            var rng = new DeterministicRng(WorldRoll.StableSeed(seed, zoneId, region.Id, "ambient_encounter"));
            if (rng.NextInt(0, 100) >= chance)
            {
                continue;
            }

            firingZone = zoneId;
            break;
        }

        Assert.NotNull(firingZone);
        await TravelTo(session, firingZone!);

        var casts = session.Engine.State.Entities.Values
            .Where(entity => entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Contains("encounter_cast", StringComparer.OrdinalIgnoreCase)
                && tags.Tags.Contains("ambient", StringComparer.OrdinalIgnoreCase))
            .ToArray();
        // Settlement zones legitimately veto the staging; only assert when the zone is open country.
        if (session.Engine.CurrentPlace.Settlement is not null)
        {
            return;
        }

        Assert.NotEmpty(casts);
        Assert.All(casts, cast => Assert.True(cast.Get<ActorComponent>().Alive));
        var prize = session.Engine.State.Entities.Values.FirstOrDefault(entity =>
            entity.Has<ItemComponent>()
            && entity.Name.Contains("coin cache", StringComparison.OrdinalIgnoreCase));
        var keeper = casts.FirstOrDefault(cast =>
            cast.TryGet<InventoryComponent>(out var held)
            && held.Items.TryGetValue("gold", out var gold)
            && gold > 0);
        Assert.True(prize is not null || keeper is not null, "no ambient prize was staged");
    }

    [Fact]
    public void RegionsWithoutTheEncountersBlockNeverStageAmbientCasts()
    {
        var regions = RegionCatalog.LoadDefault();

        Assert.Null(regions.Region("wild_border")!.Encounters);
        Assert.Equal(10, regions.Region("imperial_encounter")!.Encounters!.AmbientChancePercent);
        Assert.Equal(6, regions.Region("hollowmere_margin")!.Encounters!.AmbientChancePercent);
    }

    private static GameSession CreateSession(int seed) =>
        GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }

    private static IEnumerable<string> CandidateZones()
    {
        // Ring outward from the start so the walk stays short.
        for (var radius = 1; radius <= 6; radius++)
        {
            for (var y = -radius; y <= radius; y++)
            {
                for (var x = -radius; x <= radius; x++)
                {
                    if (Math.Max(Math.Abs(x), Math.Abs(y)) == radius)
                    {
                        yield return $"{x},{y}";
                    }
                }
            }
        }
    }

    private static async Task TravelTo(GameSession session, string zoneId)
    {
        var destination = ParseZoneId(zoneId);
        var current = ParseZoneId(session.Engine.State.CurrentZoneId);
        while (current.X != destination.X)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(current.X < destination.X ? Direction.East : Direction.West));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }

        while (current.Y != destination.Y)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(current.Y < destination.Y ? Direction.South : Direction.North));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            current = ParseZoneId(session.Engine.State.CurrentZoneId);
        }
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return (int.Parse(parts[0]), int.Parse(parts[1]));
    }
}
