using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Scenarios;

public static class TestScenarios
{
    public static GameState ImperialEncounter()
    {
        var state = new GameState(width: 16, height: 10)
        {
            ControlledEntityId = EntityId.Create("player"),
        };

        for (var x = 0; x < state.Width; x++)
        {
            state.BlockingTerrain.Add(new GridPoint(x, 0));
            state.BlockingTerrain.Add(new GridPoint(x, state.Height - 1));
        }

        for (var y = 0; y < state.Height; y++)
        {
            state.BlockingTerrain.Add(new GridPoint(0, y));
            state.BlockingTerrain.Add(new GridPoint(state.Width - 1, y));
        }

        Add(state, new Entity(EntityId.Create("player"), "You")
            .Set(new PositionComponent(new GridPoint(3, 5)))
            .Set(new RenderableComponent('@', "wild"))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new ActorComponent(24, 24, 14, 14, 4, 1, "player"))
            .Set(new ControllerComponent(ControllerKind.Player))
            .Set(new SoulComponent("player_soul"))
            .Set(new InventoryComponent(
                new Dictionary<string, int>
                {
                    ["grave salt"] = 2,
                    ["moon pearl"] = 1,
                    ["gold"] = 15,
                },
                new HashSet<string> { "moon pearl" })));

        Add(state, Soldier("soldier_1", "imperial containment soldier", new GridPoint(9, 4)));
        Add(state, Soldier("soldier_2", "imperial ward-captain", new GridPoint(11, 6)));

        Add(state, new Entity(EntityId.Create("brazier_1"), "brass containment brazier")
            .Set(new PositionComponent(new GridPoint(6, 4)))
            .Set(new RenderableComponent('&', "imperial"))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "brass"))
            .Set(new FixtureComponent(
                "brazier",
                new[] { "fire", "brass", "law", "imperial", "ritual" })));

        Add(state, new Entity(EntityId.Create("notice_1"), "posted containment notice")
            .Set(new PositionComponent(new GridPoint(5, 7)))
            .Set(new RenderableComponent('?', "imperial"))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "paper"))
            .Set(new FixtureComponent(
                "notice",
                new[] { "paper", "law", "contract", "imperial", "readable" }))
            .Set(new ReadableComponent("Thaumic Containment Order 7-112")));

        state.AddMessage("Imperial soldiers move to contain you.");
        return state;
    }

    private static Entity Soldier(string id, string name, GridPoint position) =>
        new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('i', "imperial"))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new ActorComponent(10, 10, 0, 0, 3, 1, "empire"))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new SoulComponent($"{id}_soul"));

    private static void Add(GameState state, Entity entity) =>
        state.Entities.Add(entity.Id, entity);
}

