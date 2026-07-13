using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

public sealed class ForwardMomentumSystemsTests
{
    [Fact]
    public void GeneratedHandoffsExerciseEveryObjectiveFamilyAcrossWorldSeeds()
    {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var regions = RegionCatalog.LoadDefault();
        var catalog = QuestTemplateCatalog.LoadDefault();

        for (var seed = 1; seed <= 160; seed++)
        {
            var session = GameSession.CreateImperialEncounter(seed: seed);
            var state = session.Engine.State;
            var claim = GeneratedObjectiveHandoffFactory.Create(
                seed,
                state.CurrentZoneId,
                state.RegionId,
                WorldPlaceGraph.Create(seed, regions),
                regions,
                catalog,
                session.Engine.EntityById("prisoner_1")!,
                "dialogue");

            Assert.NotNull(claim);
            var kindTag = Assert.Single(claim!.Tags!, tag =>
                tag.StartsWith("objective_kind:", StringComparison.OrdinalIgnoreCase));
            kinds.Add(kindTag["objective_kind:".Length..]);
        }

        Assert.Equal(
            new[] { "delivery", "escort", "fetch", "folk_service", "rumor_verification", "social_leverage", "threat" },
            kinds.OrderBy(kind => kind).ToArray());
    }

    [Fact]
    public async Task AThreatObjectiveAcceptsTransformationAsARealSolution()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        DisableAi(session);
        var state = session.Engine.State;
        var target = session.Engine.EntityById("soldier_1")!;
        var claim = state.Claims.Append(
            state.Turn,
            "test",
            "prisoner_1",
            "player_soul",
            "Resolve the oath-breaker and return.",
            "journey",
            target.Name,
            5,
            100,
            true,
            new[]
            {
                "generated_objective",
                "objective_kind:threat",
                "objective_return_to_giver",
                "objective_giver_name:Lio of Hollowmere",
            },
            status: "promised");
        var created = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "lead",
            claim.Text,
            anchorEntityId: target.Id.Value,
            sourceEntityId: "prisoner_1",
            salience: 5,
            subject: target.Name,
            realizationKind: "threat",
            claimedPlace: state.CurrentZoneId,
            sourceClaimId: claim.Id,
            sourceSpeakerId: "prisoner_1",
            autoBind: true,
            emitMessage: false));
        Assert.True(created.Applied);
        var promise = Assert.Single(state.PromiseLedger.Promises, item => item.SourceClaimId == claim.Id);
        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.UpdatePromise(
            "test",
            promise.Id,
            status: "realized",
            realizedIn: state.CurrentZoneId)).Applied);

        Assert.True(session.Engine.ApplyConsequence(WorldConsequence.AddTags(
            "test",
            target.Id.Value,
            new[] { "transformed" })).Applied);
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(
            "ready_to_return",
            state.PromiseLedger.Promises.Single(item => item.Id == promise.Id).Status);
        Assert.Contains(wait.Messages, message =>
            message.Contains("transformation", StringComparison.OrdinalIgnoreCase)
            && message.Contains("return to Lio", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RumorsCrossRegionsOnlyAlongNamedRoads()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var graph = WorldPlaceGraph.Create(state.Seed, RegionCatalog.LoadDefault());
        var settlements = graph.Settlements.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var road = graph.Roads.First(candidate =>
            !settlements[candidate.FromSettlementId].RegionId.Equals(
                settlements[candidate.ToSettlementId].RegionId,
                StringComparison.OrdinalIgnoreCase));
        var fromRegion = settlements[road.FromSettlementId].RegionId;
        var toRegion = settlements[road.ToSettlementId].RegionId;
        state.RegionId = toRegion;
        state.Rumors.Append(
            state.Turn,
            "test",
            "road_rumor",
            fromRegion,
            fromRegion,
            "A blue orchard has learned to testify.",
            salience: 4,
            carrierIds: new[] { $"region:{fromRegion}" },
            tags: new[] { "rumor", "road_test" });

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 2,
            announce: true,
            applyConsequence: session.Engine.ApplyConsequence);

        var spread = Assert.Single(deltas, delta => delta.Operation == "rumorSpread");
        Assert.Equal(road.Id, spread.Details["roadId"]);
        Assert.Equal(road.Name, spread.Details["roadName"]);
        Assert.Contains(deltas.PlayerMessages(), message =>
            message.Contains(road.Name, StringComparison.OrdinalIgnoreCase)
            && message.Contains("because", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RumorsDoNotTeleportBetweenRegionsWithoutADirectRoad()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var graph = WorldPlaceGraph.Create(state.Seed, RegionCatalog.LoadDefault());
        var settlements = graph.Settlements.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var roadPairs = graph.Roads
            .Select(road => new HashSet<string>(new[]
            {
                settlements[road.FromSettlementId].RegionId,
                settlements[road.ToSettlementId].RegionId,
            }, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var regionIds = settlements.Values
            .Select(settlement => settlement.RegionId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var pair = (from Left in regionIds
                    from Right in regionIds
                    where !Left.Equals(Right, StringComparison.OrdinalIgnoreCase)
                    where !roadPairs.Any(roadPair => roadPair.SetEquals(new[] { Left, Right }))
                    select (Left, Right)).First();
        state.RegionId = pair.Right;
        state.Rumors.Append(
            state.Turn,
            "test",
            "roadless_rumor",
            pair.Left,
            pair.Left,
            "A distant magistrate has been replaced by three argumentative moths.",
            salience: 5,
            carrierIds: new[] { $"region:{pair.Left}" },
            tags: new[] { "rumor", "roadless_test" });

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 2,
            announce: true,
            applyConsequence: session.Engine.ApplyConsequence);

        Assert.DoesNotContain(deltas, delta => delta.Operation == "rumorSpread");
        var rumor = Assert.Single(state.Rumors.Records, item => item.SourceId == "roadless_rumor");
        Assert.Equal(pair.Left, rumor.CurrentRegionId);
        Assert.Equal(0, rumor.Hops);
    }

    [Fact]
    public void InterestedNpcInitiativeMovesOneStepAndNamesItsCause()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        foreach (var faction in state.Factions.Factions)
        {
            faction.Resources["heat"] = 0;
        }

        state.Rumors.ReplaceAll(Array.Empty<RumorRecord>());
        state.PromiseLedger.ReplaceAll(Array.Empty<WorldPromise>());
        var npc = session.Engine.EntityById("prisoner_1")!;
        npc.Set(new TagsComponent(new[] { "objective_contact" }));
        npc.Set(new AiComponent("idle"));
        var player = state.ControlledEntity.Get<PositionComponent>().Position;
        var start = FindApproachPosition(state, player);
        npc.Set(new PositionComponent(start));

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: true,
            applyConsequence: session.Engine.ApplyConsequence);
        var end = npc.Get<PositionComponent>().Position;

        Assert.Equal(1, Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y));
        Assert.Equal(
            Math.Abs(start.X - player.X) + Math.Abs(start.Y - player.Y) - 1,
            Math.Abs(end.X - player.X) + Math.Abs(end.Y - player.Y));
        Assert.Contains(deltas, delta => delta.Operation == "npcApproachMove");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "npc_approach"));
        Assert.Contains(deltas.PlayerMessages(), message =>
            message.Contains(npc.Name, StringComparison.OrdinalIgnoreCase)
            && message.Contains(npc.Get<WantComponent>().Text, StringComparison.OrdinalIgnoreCase));
    }

    private static GridPoint FindApproachPosition(GameState state, GridPoint player)
    {
        foreach (var direction in new[] { new GridPoint(1, 0), new GridPoint(-1, 0), new GridPoint(0, 1), new GridPoint(0, -1) })
        {
            var start = new GridPoint(player.X + direction.X * 4, player.Y + direction.Y * 4);
            var step = new GridPoint(start.X - direction.X, start.Y - direction.Y);
            if (InBounds(state, start)
                && InBounds(state, step)
                && !state.BlockingTerrain.Contains(start)
                && !state.BlockingTerrain.Contains(step)
                && !HasBlockingEntity(state, start)
                && !HasBlockingEntity(state, step))
            {
                return start;
            }
        }

        throw new InvalidOperationException("The encounter has no four-tile straight approach lane.");
    }

    private static bool InBounds(GameState state, GridPoint point) =>
        point.X >= 0 && point.Y >= 0 && point.X < state.Width && point.Y < state.Height;

    private static bool HasBlockingEntity(GameState state, GridPoint point) =>
        state.Entities.Values.Any(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement);

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
