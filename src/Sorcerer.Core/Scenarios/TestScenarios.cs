using Sorcerer.Core.Characters;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Scenarios;

public static class TestScenarios
{
    public const int TacticalWidth = 40;
    public const int TacticalHeight = 30;
    private static readonly Lazy<RegionInteriorDefinition?> OpeningInterior = new(() =>
        RegionCatalog.LoadDefault()
            .Region("imperial_encounter")?
            .Interiors?
            .Definitions
            .FirstOrDefault(definition => definition.Id.Equals("sealed_registry", StringComparison.OrdinalIgnoreCase)));

    public static GameState ImperialEncounter(
        string? playerOriginId = null,
        IReadOnlyList<RunChronicleRecord>? memorials = null,
        CharacterBuild? build = null)
    {
        var origin = CreationRules.EffectiveOrigin(
            OriginCatalog.LoadDefault().Resolve(build?.OriginId ?? playerOriginId),
            build);
        var playerBody = new BodyStatsComponent(origin.BodyVigor);
        var playerSoul = CharacterMath.SoulFromOrigin("player_soul", origin);
        var state = new GameState(width: TacticalWidth, height: TacticalHeight)
        {
            ControlledEntityId = EntityId.Create("player"),
            Seed = 7,
            Rng = new DeterministicRng(7),
        };

        FactionCatalog.LoadDefault().ApplyTo(state.Factions);
        state.Souls.Set(playerSoul);

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
            .Set(playerBody)
            .Set(CharacterMath.CreateActor(playerBody, playerSoul, "player"))
            .Set(new ControllerComponent(ControllerKind.Player))
            .Set(new SoulComponent("player_soul"))
            .Set(new ProfileComponent(
                origin.PublicName,
                origin.Appearance,
                Origin: origin.Id,
                MagicalSignature: origin.MagicalSignature,
                Backstory: origin.Backstory,
                PortraitPath: build?.PortraitPath ?? ""))
            .Set(StatusContainerComponent.Empty())
            .Set(EquipmentComponent.Empty())
            .Set(new InventoryComponent(
                new Dictionary<string, int>(origin.StartingItems, StringComparer.OrdinalIgnoreCase),
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
                new[] { "fire", "brass", "law", "imperial", "ritual" }))
            .Set(new ClaimSourceComponent(new[]
            {
                new ClaimSeed(
                    "The brazier remembers Vara Nine-Names, whose oath was sealed beneath the walking stone north of here.",
                    "landmark",
                    "walking stone",
                    Salience: 3,
                    Confidence: 70,
                    PlayerVisible: true,
                    BindAsPromise: true,
                    PromiseKind: "rumor",
                    RealizationKind: "site",
                    TriggerHint: "travel",
                    ClaimedPlace: "north of the containment yard",
                    Tags: new[] { "prop", "brazier", "oath", "walking_stone", "north" }),
            })));

        Add(state, new Entity(EntityId.Create("notice_1"), "posted containment notice")
            .Set(new PositionComponent(new GridPoint(5, 7)))
            .Set(new RenderableComponent('?', "imperial"))
            // The containment order's margins carry a printed charter form: reading the notice
            // teaches it (docs/CHARTER_MAGIC.md - one learnable spell in the opening, proving
            // acquisition through ordinary verbs).
            .Set(new TagsComponent(new[] { "fixture", "paper", "law", "readable", "teaches_charter:binding_writ_1" }))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "paper"))
            .Set(new FixtureComponent(
                "notice",
                new[] { "paper", "law", "contract", "imperial", "readable" }))
            .Set(new ReadableComponent(
                "Thaumic Containment Order 7-112",
                "By marble authority: unauthorized color, oath, dream, bone, rain, name, or prophecy is to be contained until it consents to empire. Confiscated colors and wastewater depart by the southern drainage culvert at dusk."))
            .Set(new ClaimSourceComponent(new[]
            {
                new ClaimSeed(
                    "Confiscated colors and wastewater depart by the southern drainage culvert at dusk.",
                    "escape_route",
                    "southern drainage culvert",
                    Salience: 3,
                    Confidence: 85,
                    PlayerVisible: true,
                    BindAsPromise: true,
                    PromiseKind: "rumor",
                    RealizationKind: "escape_route",
                    TriggerHint: "travel",
                    ClaimedPlace: "south of the containment yard",
                    Tags: new[] { "document", "escape_route", "drainage", "south" }),
            })));

        Add(state, Item("loose_tincture_1", "red tincture", new GridPoint(4, 6), '!', "glass", "red_tincture", 12, new[] { "item", "healing", "blood" }, "heal:6"));
        Add(state, Item("cell_key_1", "imperial cell key", new GridPoint(7, 7), 'k', "iron", "imperial_cell_key", 5, new[] { "item", "key", "imperial" }, "key"));

        var rescuePromiseResult = WorldConsequenceGuard.ApplyWithNewApplier(state, WorldConsequence.CreatePromise(
            "scenario",
            "promise",
            "If the cell door opens, Lio of Hollowmere will owe a dangerous gratitude.",
            playerVisible: true,
            useCurrentRegionAsClaimedPlace: false,
            autoBind: false,
            emitMessage: false,
            operation: "seedPromise"));
        var rescuePromiseId = rescuePromiseResult.TargetId ?? rescuePromiseResult.Deltas.First().Target;
        Add(state, new Entity(EntityId.Create("cell_door_1"), "locked imperial cell door")
            .Set(new PositionComponent(new GridPoint(13, 5)))
            .Set(new RenderableComponent('+', "imperial"))
            .Set(new TagsComponent(new[] { "door", "cell", "iron", "imperial", "lock" }))
            .Set(new PhysicalComponent(BlocksMovement: true, BlocksSight: true, Material: "iron"))
            .Set(new FixtureComponent("door", new[] { "door", "cell", "iron", "imperial" }))
            .Set(new DoorComponent(IsOpen: false, KeyId: "imperial cell key"))
            .Set(new PromiseAnchorComponent(rescuePromiseId)));

        var lio = new Entity(EntityId.Create("prisoner_1"), "Lio of Hollowmere")
            .Set(new PositionComponent(new GridPoint(14, 5)))
            .Set(new RenderableComponent('p', "hollowmere"))
            .Set(new TagsComponent(new[] { "npc", "prisoner", "hollowmere", "ally_candidate" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new BodyStatsComponent(0))
            .Set(new ActorComponent(8, 8, 0, 0, 1, 0, "hollowmere"))
            .Set(new FactionComponent("hollowmere", new[] { "prisoner", "witness" }))
            .Set(new ControllerComponent(ControllerKind.None))
            .Set(new AiComponent("captive"))
            .Set(new SoulComponent("lio_soul"))
            .Set(new ProfileComponent(
                "Lio of Hollowmere",
                "a prisoner with river reeds braided through torn imperial rope",
                Origin: "Hollowmere",
                MagicalSignature: "a name hidden under water",
                Backstory: "Lio is scared, observant, and not a formal quest giver. If trust, gratitude, a gift, or imminent escape loosens his tongue, he tends to disclose concrete leads: a Hollowmere refuge south of the yard, Jimmer the quiet blade-seller, Old Maren's niece Nannerl, a burned oak that marks a hidden road, or an imperial drainage route. He knows folk-magic services exist but treats them as dangerous secrets because Vigovia can execute people for practicing them."))
            .Set(new WantComponent(
                "want_lio_escape",
                "Escape the containment yard and get word to Hollowmere without naming folk-magic helpers too loudly.",
                salience: 5,
                stakes: "If trust or leverage appears, Lio may trade concrete leads for a plausible escape.",
                tags: new[] { "escape", "hollowmere", "promise_source" }))
            .Set(StatusContainerComponent.Empty());
        lio.Set(DialogueKnowledgeProfile.For(
            lio,
            state.RegionId,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["rumors"] = 2,
                ["npc.knowledge.region"] = 2,
                ["zone.current"] = 2,
                ["zone.npcs"] = 2,
                ["scene.object"] = 2,
                ["current_zone"] = 2,
                ["hollowmere"] = 2,
                ["region.travel"] = 2,
                ["people.hollowmere"] = 2,
                ["vigovia.public_law"] = 1,
                ["folk_magic.water"] = 4,
                ["magic.water.hollowmere"] = 4,
                ["wild_magic"] = 3,
                ["services"] = 2,
                ["services.folk_magic"] = 2,
                ["promises.oaths"] = 3,
                ["faction.law"] = 1,
                ["recent.magic_deeds"] = 2,
            }));
        Add(state, lio);

        AddOpeningInteriorThreshold(state);
        AddMemorial(state, memorials);
        WorldConsequenceGuard.ApplyWithNewApplier(state, WorldConsequence.Message(
            "scenario",
            "Imperial soldiers move to contain you.",
            targetEntityId: state.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: state.ControlledEntityId.Value,
            evidence: "Imperial encounter setup.",
            operation: "scenarioMessage",
            details: new Dictionary<string, object?>
            {
                ["scenario"] = "imperial_encounter",
            }));
        return state;
    }

    private static void AddOpeningInteriorThreshold(GameState state)
    {
        var interior = OpeningInterior.Value;
        if (interior is null)
        {
            return;
        }

        var position = new GridPoint(18, 5);
        var tags = interior.Tags
            .Concat(new[]
            {
                "fixture",
                "interior_entrance",
                "significant_site",
                interior.Id,
                interior.Kind,
                interior.AccessPolicy,
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Add(state, new Entity(EntityId.Create("sealed_registry_entrance"), "sealed evidence registry door")
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('D', "imperial"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: false, BlocksSight: false, Material: interior.WallMaterial))
            .Set(new FixtureComponent("interior_entrance", tags, CanAnchorMagic: true))
            .Set(new DescriptionComponent(
                $"A numbered marble door leads into {interior.Name}. {interior.Summary} "
                + $"It is restricted; {interior.RequiredItem ?? "permission, force, or magic"} is one ordinary way through."))
            .Set(new InteractableComponent(new[] { "examine", "enter" }))
            .Set(new InteriorEntranceComponent(
                $"interior:imperial_encounter:{interior.Id}:0,0",
                interior.Id,
                interior.Name,
                interior.Kind,
                interior.Summary,
                interior.AccessPolicy,
                interior.RequiredItem,
                "0,0",
                position.X,
                position.Y)));
    }

    private static void AddMemorial(GameState state, IReadOnlyList<RunChronicleRecord>? memorials)
    {
        var memorial = memorials?
            .Where(record => !string.IsNullOrWhiteSpace(record.Text))
            .LastOrDefault();
        if (memorial is null)
        {
            return;
        }

        var tags = new[] { "memorial", "past_run", "inert", "readable" };
        Add(state, new Entity(EntityId.Create("memorial_1"), "weathered sorcerer's memorial")
            .Set(new PositionComponent(new GridPoint(2, 7)))
            .Set(new RenderableComponent('?', "memory"))
            .Set(new TagsComponent(tags))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "stone"))
            .Set(new DescriptionComponent(memorial.Text))
            .Set(new FixtureComponent("memorial", tags, CanAnchorMagic: false))
            .Set(new ReadableComponent("Weathered Sorcerer's Memorial", memorial.Text)));
    }

    private static Entity Soldier(string id, string name, GridPoint position)
    {
        var soldier = new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('i', "imperial"))
            .Set(new TagsComponent(new[] { "imperial", "soldier", "containment" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "body"))
            .Set(new BodyStatsComponent(1))
            .Set(new ActorComponent(10, 10, 0, 0, 3, 0, "empire"))
            .Set(new FactionComponent("empire", new[] { "empire", "military" }))
            .Set(new ControllerComponent(ControllerKind.Ai))
            .Set(new AiComponent("hostile_guard"))
            .Set(StatusContainerComponent.Empty())
            .Set(new SoulComponent($"{id}_soul"))
            .Set(new ProfileComponent(
                name,
                SoldierAppearance(name),
                Origin: "Vigovia",
                MagicalSignature: "law spoken as if it were weather",
                Backstory: SoldierBackstory(name)))
            .Set(SoldierWant(id, name));
        soldier.Set(DialogueKnowledgeProfile.For(
            soldier,
            "imperial_encounter",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["rumors"] = 1,
                ["npc.knowledge.region"] = 1,
                ["services"] = 1,
                ["faction.law"] = 3,
            }));
        return soldier;
    }

    private static string SoldierAppearance(string name) =>
        name.Contains("captain", StringComparison.OrdinalIgnoreCase)
            ? "a ward-captain with a polished key-ring, tired eyes, and a voice trained to make fear sound procedural"
            : "a containment soldier with chalk dust on the cuffs and a glance that keeps returning to the confiscated-goods crates";

    private static string SoldierBackstory(string name) =>
        name.Contains("captain", StringComparison.OrdinalIgnoreCase)
            ? "The ward-captain is loyal to Vigovia but practical under pressure. In dialogue, they may reveal lawful procedures, warrant routes, patrol timing, an office ledger, or a weakness in the containment-yard schedule. They should treat folk magic as contraband and speak of practitioners carefully, because execution for hidden practice is plausible."
            : "This soldier knows more logistics than doctrine: which clerk counted the confiscated charms, where a blade was sealed, which road leads toward Hollowmere, and which landmark patrols avoid after dusk. They are not friendly, but fear, bargaining, or magical leverage can make them let a concrete lead slip.";

    private static WantComponent SoldierWant(string id, string name) =>
        name.Contains("captain", StringComparison.OrdinalIgnoreCase)
            ? new WantComponent(
                $"want_{id}_order",
                "End the containment incident with the paperwork intact and no public proof that folk-magic practice slipped through imperial hands.",
                salience: 4,
                stakes: "The captain may disclose procedures, schedules, doors, or ledger facts if that seems to preserve order.",
                tags: new[] { "order", "procedure", "empire", "promise_source" })
            : new WantComponent(
                $"want_{id}_shift",
                "Survive the shift without being blamed for missing confiscated goods or a loose prisoner.",
                salience: 3,
                stakes: "Fear, bargaining, or evidence can make the soldier mention routes, stock, clerks, or landmarks.",
                tags: new[] { "survival", "goods", "routes", "promise_source" });

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
