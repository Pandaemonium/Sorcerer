using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Validation;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class InteriorTransitionTests
{
    [Fact]
    public void EveryRegionHasBoundCulturallySpecificInteriorContent()
    {
        var catalog = RegionCatalog.LoadDefault();
        var graph = WorldPlaceGraph.Create(7, catalog);

        Assert.Equal(15, catalog.Regions.Sum(region => region.Interiors?.Definitions.Count ?? 0));
        Assert.All(catalog.Regions, region =>
        {
            var grammar = Assert.IsType<RegionInteriorGrammarDefinition>(region.Interiors);
            Assert.NotEmpty(grammar.Definitions);
            Assert.NotEmpty(grammar.Bindings);
            Assert.All(grammar.Definitions, interior =>
            {
                Assert.False(string.IsNullOrWhiteSpace(interior.Summary));
                Assert.False(string.IsNullOrWhiteSpace(interior.FloorTerrain));
                Assert.False(string.IsNullOrWhiteSpace(interior.WallMaterial));
                Assert.True(interior.Features.Count >= 3, $"{region.Id}:{interior.Id}");
                Assert.Contains(grammar.Bindings, binding => binding.InteriorId == interior.Id);
            });

            var settlement = graph.Settlements.Single(candidate =>
                candidate.IsPrimary && candidate.RegionId.Equals(region.Id, StringComparison.OrdinalIgnoreCase));
            var footprintDistricts = Enumerable.Range(-settlement.Radius, (settlement.Radius * 2) + 1)
                .SelectMany(dx => Enumerable.Range(-settlement.Radius, (settlement.Radius * 2) + 1)
                    .Select(dy => graph.Profile(
                        $"{settlement.CenterX + dx},{settlement.CenterY + dy}",
                        region.Id).District?.Id))
                .Where(id => id is not null)
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.All(grammar.Bindings, binding => Assert.Contains(binding.DistrictId, footprintDistricts));
        });

        var capital = catalog.Region("vigovian_capital")!.Interiors!;
        Assert.Contains(capital.Definitions, interior => interior.Kind == "palace");
        Assert.Contains(capital.Definitions, interior => interior.Kind == "archive");

        var opening = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        Assert.Contains(opening.Engine.State.Entities.Values, entity =>
            entity.TryGet<InteriorEntranceComponent>(out var entrance)
            && entrance.InteriorId == "sealed_registry");
    }

    [Fact]
    public async Task RestrictedInteriorSupportsKeyEntryFollowerTransitAndPersistentReturn()
    {
        var session = await ReachCapitalPalace();
        var entrance = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Has<InteriorEntranceComponent>());
        PlaceControlledAt(session, entrance.Get<PositionComponent>().Position);
        var turnBefore = session.Engine.State.Turn;

        var denied = await session.ExecuteAsync(new EnterCommand(entrance.Id.Value));

        Assert.False(denied.Success);
        Assert.False(denied.ConsumedTurn);
        Assert.Equal(turnBefore, session.Engine.State.Turn);
        Assert.Contains(denied.Messages, message => message.Contains("not an absolute lock", StringComparison.OrdinalIgnoreCase));

        var inventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();
        inventory.Items["imperial cell key"] = 1;
        var follower = session.Engine.State.Entities.Values.First(entity =>
            entity.Id != session.Engine.State.ControlledEntityId
            && entity.Has<ActorComponent>()
            && entity.Has<SoulComponent>());
        var playerSoulId = session.Engine.State.ControlledEntity.Get<SoulComponent>().SoulId;
        var follow = session.Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "test",
            follower.Id.Value,
            playerSoulId,
            loyaltyDelta: 0,
            fearDelta: 0,
            admirationDelta: 0,
            resentmentDelta: 0,
            posture: "follower",
            operation: "testInteriorFollower"));
        Assert.True(follow.Applied);

        var entered = await session.ExecuteAsync(new EnterCommand(entrance.Id.Value));

        Assert.True(entered.Success, string.Join(" | ", entered.Messages));
        Assert.True(entered.ConsumedTurn);
        Assert.Equal(turnBefore + 1, session.Engine.State.Turn);
        Assert.StartsWith("interior:vigovian_capital:odran_palace:", session.Engine.State.CurrentZoneId);
        Assert.Equal(WorldPlaceKinds.Interior, session.View().World!.PlaceKind);
        Assert.Equal("Palace of Reasonable Peace", session.View().World!.InteriorName);
        Assert.Contains(session.Engine.State.Entities.Keys, id => id == follower.Id);
        Assert.True(session.Engine.State.Zones["3,0"].Entities.All(pair => pair.Key != follower.Id));
        Assert.Contains(new GridPoint(0, 0), session.Engine.State.BlockingTerrain);
        Assert.Contains(
            new GridPoint(session.Engine.State.Width - 1, session.Engine.State.Height - 1),
            session.Engine.State.BlockingTerrain);
        Assert.True(session.Engine.State.Entities.Values.Count(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("interior_feature", StringComparer.OrdinalIgnoreCase)) >= 3);

        var feature = session.Engine.State.Entities.Values.First(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("interior_feature", StringComparer.OrdinalIgnoreCase));
        var marked = session.Engine.ApplyConsequence(WorldConsequence.AddTags(
            "test",
            feature.Id.Value,
            new[] { "remembered_stain" },
            operation: "testMarkInterior"));
        Assert.True(marked.Applied);

        var exit = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Has<InteriorExitComponent>());
        PlaceControlledAt(session, exit.Get<PositionComponent>().Position);
        var exitCard = session.View().Entities.Single(entity => entity.Id == exit.Id.Value);
        Assert.Contains(exitCard.Actions ?? Array.Empty<Sorcerer.Core.Views.ContextActionCard>(), action =>
            action.Id == "leave" && action.Command == "leave");

        var left = await session.ExecuteAsync(new LeaveCommand());

        Assert.True(left.Success, string.Join(" | ", left.Messages));
        Assert.Equal("3,0", session.Engine.State.CurrentZoneId);
        Assert.Contains(session.Engine.State.Entities.Keys, id => id == follower.Id);
        Assert.Contains(session.Engine.State.Zones.Values, zone =>
            zone.ZoneId.StartsWith("interior:", StringComparison.OrdinalIgnoreCase));

        var restoredEntrance = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Has<InteriorEntranceComponent>());
        PlaceControlledAt(session, restoredEntrance.Get<PositionComponent>().Position);
        var reentered = await session.ExecuteAsync(new EnterCommand(restoredEntrance.Id.Value));

        Assert.True(reentered.Success, string.Join(" | ", reentered.Messages));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("remembered_stain", StringComparer.OrdinalIgnoreCase));
        Assert.True(StateValidator.Validate(session.Engine.State).IsValid);
    }

    [Fact]
    public async Task SaveLoadInsideInteriorPreservesBothThresholdsAndCanLeave()
    {
        var session = await ReachCapitalPalace();
        var entrance = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Has<InteriorEntranceComponent>());
        PlaceControlledAt(session, entrance.Get<PositionComponent>().Position);
        session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["imperial cell key"] = 1;
        var entered = await session.ExecuteAsync(new EnterCommand(entrance.Id.Value));
        Assert.True(entered.Success, string.Join(" | ", entered.Messages));

        var serialized = GameSaveService.Serialize(
            session.Engine.State,
            savedAt: new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero));
        var loaded = GameSaveService.Deserialize(serialized);
        var resumed = new GameSession(loaded.State, new WildMagicController(new MockSpellProvider()));

        Assert.Equal(WorldPlaceKinds.Interior, resumed.View().World!.PlaceKind);
        Assert.Equal("Palace of Reasonable Peace", resumed.View().World!.InteriorName);
        var exit = Assert.Single(resumed.Engine.State.Entities.Values, entity => entity.Has<InteriorExitComponent>());
        PlaceControlledAt(resumed, exit.Get<PositionComponent>().Position);
        var left = await resumed.ExecuteAsync(new LeaveCommand());

        Assert.True(left.Success, string.Join(" | ", left.Messages));
        Assert.Equal("3,0", resumed.Engine.State.CurrentZoneId);
        Assert.Contains(resumed.Engine.State.Entities.Values, entity => entity.Has<InteriorEntranceComponent>());
        Assert.True(StateValidator.Validate(resumed.Engine.State).IsValid);
    }

    private static async Task<GameSession> ReachCapitalPalace()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 7);
        DisableAi(session);
        for (var index = 0; index < 3; index++)
        {
            var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
            Assert.True(travel.Success, string.Join(" | ", travel.Messages));
            DisableAi(session);
        }

        Assert.Equal("3,0", session.Engine.State.CurrentZoneId);
        return session;
    }

    private static void PlaceControlledAt(GameSession session, GridPoint point) =>
        session.Engine.State.ControlledEntity.Set(new PositionComponent(point));

    private static void DisableAi(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Has<AiComponent>()))
        {
            entity.Set(new AiComponent("idle"));
        }
    }
}
