using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Operations;
using Xunit;

namespace Sorcerer.Tests;

public sealed class RegionalPropGenerationTests
{
    [Fact]
    public void EveryRegionHasAValidCombinatorialPropGrammar()
    {
        var catalog = RegionCatalog.LoadDefault();

        foreach (var region in catalog.Regions)
        {
            var grammar = Assert.IsType<RegionPropGrammarDefinition>(region.Props);
            Assert.InRange(grammar.MinProps, 0, grammar.MaxProps);
            Assert.InRange(grammar.MaxProps, 1, 16);
            Assert.NotEmpty(grammar.Bases);
            Assert.NotEmpty(grammar.Materials);
            Assert.NotEmpty(grammar.Conditions);
            Assert.All(grammar.Bases, item => Assert.True(item.Weight > 0));
            Assert.All(grammar.Materials, item => Assert.True(item.Weight > 0));
            Assert.All(grammar.Conditions, item => Assert.True(item.Weight > 0));

            foreach (var ensemble in grammar.Ensembles ?? Array.Empty<RegionPropEnsembleDefinition>())
            {
                Assert.NotEmpty(ensemble.Members);
                Assert.All(ensemble.Members, member =>
                {
                    Assert.Contains(grammar.Bases, item => item.Id == member.BaseId);
                    Assert.Contains(grammar.Materials, item => item.Id == member.MaterialId);
                    Assert.Contains(grammar.Conditions, item => item.Id == member.ConditionId);
                });
            }
        }
    }

    [Fact]
    public void TenZoneRegionalTourIsDeterministicVariedAndContainsAnEnsemble()
    {
        var region = RegionCatalog.LoadDefault().Region("hollowmere_margin")!;
        var realm = WorldRoll.Create(71).RealmFor(region.RealmId);
        var first = Enumerable.Range(0, 10)
            .Select(index => RegionPropGenerator.Generate(region, realm, 71, $"tour:{index}"))
            .ToArray();
        var repeat = Enumerable.Range(0, 10)
            .Select(index => RegionPropGenerator.Generate(region, realm, 71, $"tour:{index}"))
            .ToArray();

        Assert.Equal(
            first.SelectMany(batch => batch.Props).Select(PropFingerprint),
            repeat.SelectMany(batch => batch.Props).Select(PropFingerprint));
        Assert.True(first.SelectMany(batch => batch.Props).Select(prop => prop.Name).Distinct().Count() >= 25);
        Assert.Contains(first, batch => batch.EnsembleId is not null);
        Assert.Contains(first.SelectMany(batch => batch.Props), prop => prop.ReadableTitle is not null);
        Assert.Contains(first.SelectMany(batch => batch.Props), prop => prop.CanAnchorMagic);
    }

    [Fact]
    public async Task TravelSpawnsSeveralSpecificInspectablePropsAtDistinctPositions()
    {
        var first = CreateSession(seed: 71);
        var repeat = CreateSession(seed: 71);

        await first.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));
        await repeat.ExecuteAsync(new Sorcerer.Core.Commands.TravelCommand(Direction.East));

        var firstProps = GeneratedProps(first).OrderBy(entity => entity.Name).ToArray();
        var repeatProps = GeneratedProps(repeat).OrderBy(entity => entity.Name).ToArray();
        Assert.InRange(firstProps.Length, 5, 13);
        Assert.Equal(firstProps.Select(EntityFingerprint), repeatProps.Select(EntityFingerprint));
        Assert.Equal(firstProps.Length, firstProps.Select(entity => entity.Get<PositionComponent>().Position).Distinct().Count());
        Assert.All(firstProps, entity =>
        {
            Assert.True(entity.Has<DescriptionComponent>());
            Assert.Contains("semantic_prop", entity.Get<TagsComponent>().Tags);
        });

        var prop = firstProps[0];
        var point = prop.Get<PositionComponent>().Position;
        first.Engine.State.ControlledEntity.Set(new PositionComponent(point.Translate(-1, 0)));
        var examine = await first.ExecuteAsync(new Sorcerer.Core.Commands.ExamineCommand(prop.Name));

        Assert.True(examine.Success);
        Assert.Contains(examine.Messages, message =>
            message.Contains(prop.Get<DescriptionComponent>().Text, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ClutterCannotEvictActorsOrHooksFromMagicAndDialogueContext()
    {
        var session = CreateSession(seed: 81);
        var playerPosition = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var sceneryIds = new List<string>();
        for (var index = 0; index < 20; index++)
        {
            var point = playerPosition.Translate((index % 5) - 2, (index / 5) - 2);
            var applied = session.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
                "test",
                $"specific crockery {index}",
                point.X,
                point.Y,
                prefix: "clutter",
                blocksMovement: false,
                material: "painted_clay",
                tags: new[] { "semantic_prop", "scenery", "crockery" },
                canAnchorMagic: false,
                emitMessage: false));
            Assert.True(applied.Applied);
            sceneryIds.Add(applied.TargetId!);
        }

        var hookPoint = playerPosition.Translate(3, 0);
        var hook = session.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            "test",
            "the ferryman's wet ledger",
            hookPoint.X,
            hookPoint.Y,
            prefix: "hook",
            blocksMovement: false,
            material: "reed_paper",
            tags: new[] { "semantic_prop", "context_hook" },
            canAnchorMagic: false,
            readableTitle: "Ferry debts",
            readableText: "Three crossings remain unpaid.",
            emitMessage: false));
        Assert.True(hook.Applied);

        var perception = session.Engine.Perception();
        var actorIds = session.Engine.State.Entities.Values
            .Where(entity => entity.Id == session.Engine.State.ControlledEntityId
                || perception.VisibleEntityIds.Contains(entity.Id))
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Select(entity => entity.Id.Value)
            .ToArray();
        var magic = session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));

        Assert.All(actorIds, id => Assert.Contains(magic.Visible, entity => entity.Id == id));
        Assert.Contains(magic.Visible, entity => entity.Id == hook.TargetId);
        Assert.DoesNotContain(magic.Visible, entity => sceneryIds.Contains(entity.Id));
        Assert.NotNull(magic.Scenery);
        Assert.Equal(10, magic.Scenery!.Count);
        Assert.All(magic.Scenery, entity => Assert.Contains(entity.Id, sceneryIds));

        var speaker = session.Engine.State.Entities.Values.First(entity =>
            entity.Id != session.Engine.State.ControlledEntityId && entity.Has<ActorComponent>());
        var dialogue = DialogueContextAssembler.Build(
            session.Engine,
            new PreparedDialogueTurn(
                session.Engine.State.Turn,
                "What is around us?",
                speaker.Id.Value,
                speaker.Name,
                speaker.Get<TagsComponent>().Tags,
                "player_soul",
                SpeakerHostile: false,
                SpeakerProfile: null,
                SpeakerFaction: speaker.Get<ActorComponent>().Faction,
                BondSummary: null));

        Assert.Contains(dialogue.RouteRequest.Scene.VisibleEntities, line =>
            line.Contains("ferryman's wet ledger", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(dialogue.RouteRequest.Scene.Scenery);
        Assert.Equal(8, dialogue.RouteRequest.Scene.Scenery!.Count);
        Assert.All(dialogue.RouteRequest.Scene.Scenery!, line =>
            Assert.Contains("specific crockery", line, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SelectedSceneryIsPromotedToFullMagicContext()
    {
        var session = CreateSession(seed: 91);
        var playerPosition = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var point = playerPosition.Translate(2, 0);
        var prop = session.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            "test",
            "blue argument bowl",
            point.X,
            point.Y,
            prefix: "selected_scenery",
            blocksMovement: false,
            material: "clay",
            tags: new[] { "semantic_prop", "scenery" },
            canAnchorMagic: false,
            emitMessage: false));
        Assert.True(prop.Applied);
        session.Engine.State.SelectedTarget = point;

        var context = session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));

        Assert.Contains(context.Visible, entity => entity.Id == prop.TargetId);
        Assert.DoesNotContain(context.Scenery ?? Array.Empty<SceneryNote>(), entity => entity.Id == prop.TargetId);
    }

    private static GameSession CreateSession(int seed) =>
        GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);

    private static IEnumerable<Entity> GeneratedProps(GameSession session) =>
        session.Engine.State.Entities.Values.Where(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("semantic_prop", StringComparer.OrdinalIgnoreCase));

    private static string PropFingerprint(GeneratedRegionProp prop) =>
        $"{prop.Name}|{prop.Material}|{prop.EnsembleId}|{prop.OffsetX},{prop.OffsetY}|{prop.ReadableTitle}|{prop.CanAnchorMagic}";

    private static string EntityFingerprint(Entity entity) =>
        $"{entity.Name}|{entity.Get<PhysicalComponent>().Material}|{entity.Get<PositionComponent>().Position.X},{entity.Get<PositionComponent>().Position.Y}";
}
