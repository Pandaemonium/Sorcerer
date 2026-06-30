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
            Seed = 7,
            Rng = new DeterministicRng(7),
        };

        state.Factions.AddOrGet("player", "The Sorcerer", "player");
        state.Factions.AddOrGet("empire", "Grand Empire", "empire");
        state.Factions.AddOrGet("hollowmere", "Hollowmere", "region");

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

        foreach (var point in new[]
        {
            new GridPoint(13, 4),
            new GridPoint(14, 4),
            new GridPoint(13, 6),
            new GridPoint(14, 6),
        })
        {
            state.BlockingTerrain.Add(point);
            state.Terrain[point] = "cell_wall";
        }

        Add(state, new Entity(EntityId.Create("player"), "You")
            .Set(new PositionComponent(new GridPoint(3, 5)))
            .Set(new RenderableComponent('@', "wild"))
            .Set(new TagsComponent(new[] { "sorcerer", "wild_magic", "wanted" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new ActorComponent(24, 24, 14, 14, 4, 1, "player"))
            .Set(new ControllerComponent(ControllerKind.Player))
            .Set(new SoulComponent("player_soul"))
            .Set(new ProfileComponent(
                "the sorcerer",
                "a fugitive bright with badly behaved magic",
                Origin: "unknown",
                MagicalSignature: "color leaking through marble law"))
            .Set(StatusContainerComponent.Empty())
            .Set(EquipmentComponent.Empty())
            .Set(new InventoryComponent(
                new Dictionary<string, int>
                {
                    ["grave salt"] = 2,
                    ["moon pearl"] = 1,
                    ["charcoal wand"] = 1,
                    ["gold"] = 15,
                },
                new HashSet<string> { "moon pearl" })));

        Add(state, Soldier("soldier_1", "imperial containment soldier", new GridPoint(9, 4)));
        Add(state, Soldier("soldier_2", "imperial ward-captain", new GridPoint(11, 6)));

        Add(state, new Entity(EntityId.Create("brazier_1"), "brass containment brazier")
            .Set(new PositionComponent(new GridPoint(6, 4)))
            .Set(new RenderableComponent('&', "imperial"))
            .Set(new TagsComponent(new[] { "fixture", "fire", "law", "anchor" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "brass"))
            .Set(new FixtureComponent(
                "brazier",
                new[] { "fire", "brass", "law", "imperial", "ritual" })));

        Add(state, new Entity(EntityId.Create("notice_1"), "posted containment notice")
            .Set(new PositionComponent(new GridPoint(5, 7)))
            .Set(new RenderableComponent('?', "imperial"))
            .Set(new TagsComponent(new[] { "fixture", "paper", "law", "readable" }))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "paper"))
            .Set(new FixtureComponent(
                "notice",
                new[] { "paper", "law", "contract", "imperial", "readable" }))
            .Set(new ReadableComponent(
                "Thaumic Containment Order 7-112",
                "By marble authority: unauthorized color, oath, dream, bone, rain, name, or prophecy is to be contained until it consents to empire.")));

        Add(state, Item("loose_tincture_1", "red tincture", new GridPoint(4, 6), '!', "glass", "red_tincture", 12, new[] { "item", "healing", "blood" }, "heal:6"));
        Add(state, Item("cell_key_1", "imperial cell key", new GridPoint(7, 7), 'k', "iron", "imperial_cell_key", 5, new[] { "item", "key", "imperial" }, "key"));

        var rescuePromise = state.PromiseLedger.Add(
            "promise",
            "If the cell door opens, Lio of Hollowmere will owe a dangerous gratitude.",
            playerVisible: true);
        Add(state, new Entity(EntityId.Create("cell_door_1"), "locked imperial cell door")
            .Set(new PositionComponent(new GridPoint(13, 5)))
            .Set(new RenderableComponent('+', "imperial"))
            .Set(new TagsComponent(new[] { "door", "cell", "iron", "imperial", "lock" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "iron"))
            .Set(new FixtureComponent("door", new[] { "door", "cell", "iron", "imperial" }))
            .Set(new DoorComponent(IsOpen: false, KeyId: "imperial cell key"))
            .Set(new PromiseAnchorComponent(rescuePromise.Id)));

        Add(state, new Entity(EntityId.Create("prisoner_1"), "Lio of Hollowmere")
            .Set(new PositionComponent(new GridPoint(14, 5)))
            .Set(new RenderableComponent('p', "hollowmere"))
            .Set(new TagsComponent(new[] { "npc", "prisoner", "hollowmere", "ally_candidate" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new ActorComponent(8, 8, 0, 0, 1, 0, "hollowmere"))
            .Set(new FactionComponent("hollowmere", new[] { "prisoner", "witness" }))
            .Set(new ControllerComponent(ControllerKind.None))
            .Set(new AiComponent("captive"))
            .Set(new SoulComponent("lio_soul"))
            .Set(new ProfileComponent(
                "Lio of Hollowmere",
                "a prisoner with river reeds braided through torn imperial rope",
                Origin: "Hollowmere",
                MagicalSignature: "a name hidden under water"))
            .Set(StatusContainerComponent.Empty()));

        state.AddMessage("Imperial soldiers move to contain you.");
        return state;
    }

    private static Entity Soldier(string id, string name, GridPoint position) =>
        new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('i', "imperial"))
            .Set(new TagsComponent(new[] { "imperial", "soldier", "containment" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new ActorComponent(10, 10, 0, 0, 3, 1, "empire"))
            .Set(new FactionComponent("empire", new[] { "empire", "military" }))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile_guard"))
            .Set(StatusContainerComponent.Empty())
            .Set(new SoulComponent($"{id}_soul"));

    private static Entity Item(
        string id,
        string name,
        GridPoint position,
        char glyph,
        string material,
        string itemType,
        int value,
        IReadOnlyList<string> tags,
        string useProfile) =>
        new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent(glyph, "item"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: material))
            .Set(new ItemComponent(itemType, value, material, tags, UseProfile: useProfile))
            .Set(new StackComponent(1));

    private static void Add(GameState state, Entity entity) =>
        state.Entities.Add(entity.Id, entity);
}
