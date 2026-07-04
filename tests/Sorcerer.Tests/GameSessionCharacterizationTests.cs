using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Items;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GameSessionCharacterizationTests
{
    [Fact]
    public void InitialImperialEncounterKeepsStableAgentFacingShape()
    {
        var session = CreateMockSession();

        var observation = session.Observation(debug: true);
        var entityCards = observation.View.Entities
            .Select(entity => $"{entity.Id}|{entity.Name}|{entity.X},{entity.Y}|{entity.Glyph}|{entity.BlocksMovement}|{entity.Faction ?? "-"}|{entity.HitPoints?.ToString() ?? "-"}")
            .ToArray();

        Assert.Equal(TestScenarios.TacticalWidth, observation.View.Width);
        Assert.Equal(TestScenarios.TacticalHeight, observation.View.Height);
        Assert.Equal(0, observation.View.Turn);
        Assert.Equal("player", observation.View.ControlledEntityId);
        Assert.Equal(new[]
        {
            "brazier_1|brass containment brazier|6,4|&|True|-|-",
            "cell_key_1|imperial cell key|7,7|k|False|-|-",
            "loose_tincture_1|red tincture|4,6|!|False|-|-",
            "notice_1|posted containment notice|5,7|?|False|-|-",
            "player|You|3,5|@|True|player|24",
            "soldier_1|imperial containment soldier|9,4|i|True|empire|10",
            "soldier_2|imperial ward-captain|11,6|i|True|empire|10",
        }, entityCards);
        Assert.Equal(new[]
        {
            "brazier_1",
            "cell_door_1",
            "cell_key_1",
            "loose_tincture_1",
            "notice_1",
            "player",
            "prisoner_1",
            "soldier_1",
            "soldier_2",
        }, observation.Debug!.EntityIds);
        Assert.Empty(observation.Debug.ValidationIssues!);
        Assert.Contains(observation.View.Inventory!, item => item.Name == "moon pearl" && item.Protected);
        Assert.Contains(observation.View.Inventory!, item => item.Name == "charcoal wand" && !item.Equipped && !item.Focused);
        Assert.Contains(observation.View.Promises, promise =>
            promise.Kind == "promise"
            && promise.Status == "unbound"
            && promise.PlayerVisible
            && promise.Text.Contains("dangerous gratitude", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(observation.View.Tiles!, tile =>
            tile.X == 6
            && tile.Y == 4
            && tile.Visible
            && tile.Explored
            && tile.Terrain == "floor");
        Assert.Contains(observation.View.Tiles!, tile =>
            tile.X == 14
            && tile.Y == 5
            && !tile.Visible
            && !tile.Explored
            && tile.Terrain == "unknown");

        var serialized = JsonSerializer.Serialize(observation);
        Assert.Contains("\"ControlledEntityId\":\"player\"", serialized);
        Assert.Contains("\"ValidationIssues\":[]", serialized);
    }

    [Fact]
    public void OpeningNpcsHaveAuthoredWantsForDialoguePressure()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var captain = session.Engine.EntityById("soldier_2")!;

        Assert.Contains("Escape the containment yard", lio.Get<WantComponent>().Text);
        Assert.Contains("missing confiscated goods", soldier.Get<WantComponent>().Text);
        Assert.Contains("paperwork intact", captain.Get<WantComponent>().Text);
        Assert.Contains("promise_source", lio.Get<WantComponent>().Tags);
    }

    [Fact]
    public void SharedConsequenceCanUpdateNpcWant()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;

        var applied = session.Engine.ApplyConsequence(WorldConsequence.UpdateWant(
            "test",
            lio.Id.Value,
            status: "satisfied",
            stakes: "Lio now needs the escape to stay quiet long enough to matter.",
            addTags: new[] { "satisfied_by_player" },
            removeTags: new[] { "escape" },
            operation: "testUpdateWant"));

        Assert.True(applied.Applied);
        Assert.Contains(applied.Deltas, delta =>
            delta.Operation == "testUpdateWant"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["hadWant"], true)
            && Equals(delta.Details["previousStatus"], "active")
            && Equals(delta.Details["status"], "satisfied"));
        var want = lio.Get<WantComponent>();
        Assert.Equal("satisfied", want.Status);
        Assert.Contains("stay quiet", want.Stakes);
        Assert.Contains("promise_source", want.Tags);
        Assert.Contains("satisfied_by_player", want.Tags);
        Assert.DoesNotContain("escape", want.Tags);
        Assert.Contains(session.Observation(debug: true).Debug!.Wants!, card =>
            card.EntityId == "prisoner_1"
            && card.WantId == "want_lio_escape"
            && card.Status == "satisfied"
            && card.Tags.Contains("satisfied_by_player"));
    }

    [Fact]
    public void UpdateWantCanRecordPrivateMemoryThroughSharedConsequence()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;

        var applied = session.Engine.ApplyConsequence(WorldConsequence.UpdateWant(
            "test",
            lio.Id.Value,
            status: "blocked",
            stakes: "Lio needs another escape route now that the first one drew imperial attention.",
            addTags: new[] { "needs_new_route" },
            removeTags: new[] { "escape" },
            operation: "testUpdateWantWithMemory",
            recordMemory: true,
            memoryText: "Lio remembers that the first escape route is watched.",
            memoryProvenance: "test:want",
            memorySalience: 4,
            memoryShareable: false));

        Assert.True(applied.Applied);
        Assert.Contains(applied.Deltas, delta =>
            delta.Operation == "testUpdateWantWithMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["status"], "blocked"));
        Assert.Contains(applied.Deltas, delta =>
            delta.Operation == "testUpdateWantWithMemoryMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["parentOperation"], "testUpdateWantWithMemory")
            && Equals(delta.Details["provenance"], "test:want"));
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.SubjectId == lio.Id.Value
            && memory.Provenance == "test:want"
            && memory.Text.Contains("escape route", StringComparison.OrdinalIgnoreCase)
            && !memory.Shareable);
        Assert.Contains(lio.Get<MemoryComponent>().Records, memory =>
            memory.Provenance == "test:want"
            && memory.Salience == 4
            && !memory.Shareable);
    }

    [Fact]
    public void SpawnEntityCreatesDefaultWantsForNotableNpcs()
    {
        var session = CreateMockSession();

        var merchant = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "quiet road merchant",
            20,
            20,
            prefix: "quiet_merchant",
            faction: "neutral",
            tags: new[] { "npc", "merchant" },
            roles: new[] { "resident", "merchant" },
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            interactableVerbs: new[] { "talk", "give" },
            emitMessage: false));
        var explicitWant = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "glass oath witness",
            21,
            20,
            prefix: "glass_witness",
            faction: "neutral",
            tags: new[] { "npc", "witness" },
            roles: new[] { "witness" },
            controllerKind: "ai",
            aiPolicyId: "resident",
            summoned: false,
            interactableVerbs: new[] { "talk" },
            wantText: "Remember the exact oath without letting the empire hear it.",
            wantTags: new[] { "oath", "witness" },
            emitMessage: false));
        var creature = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "stone moth",
            22,
            20,
            prefix: "stone_moth",
            faction: "player",
            tags: new[] { "summoned", "moth" },
            summoned: true,
            emitMessage: false));
        var optedOut = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "silent clerk",
            23,
            20,
            prefix: "silent_clerk",
            faction: "empire",
            tags: new[] { "npc", "clerk" },
            roles: new[] { "empire", "functionary" },
            summoned: false,
            autoWant: false,
            emitMessage: false));
        var ordinary = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "blank clay body",
            24,
            20,
            prefix: "blank_body",
            faction: "neutral",
            tags: Array.Empty<string>(),
            roles: Array.Empty<string>(),
            summoned: false,
            emitMessage: false));

        Assert.True(merchant.Applied);
        var merchantEntity = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Name == "quiet road merchant");
        Assert.True(merchantEntity.TryGet<WantComponent>(out var merchantWant));
        Assert.Contains("exchange", merchantWant.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generated_want", merchantWant.Tags);
        Assert.Contains("promise_source", merchantWant.Tags);
        Assert.Contains(merchant.Deltas, delta =>
            Equals(delta.Details["wantGenerated"], true)
            && Equals(delta.Details["wantId"], merchantWant.Id));

        Assert.True(explicitWant.Applied);
        var witness = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Name == "glass oath witness");
        Assert.Equal("Remember the exact oath without letting the empire hear it.", witness.Get<WantComponent>().Text);
        Assert.Contains(explicitWant.Deltas, delta => Equals(delta.Details["wantGenerated"], false));

        Assert.True(creature.Applied);
        var moth = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Name == "stone moth");
        Assert.False(moth.Has<WantComponent>());

        Assert.True(optedOut.Applied);
        var clerk = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Name == "silent clerk");
        Assert.False(clerk.Has<WantComponent>());

        Assert.True(ordinary.Applied);
        var clayBody = Assert.Single(session.Engine.State.Entities.Values, entity => entity.Name == "blank clay body");
        Assert.False(clayBody.Has<WantComponent>());
    }

    [Fact]
    public void SpawnEntityCanUseExplicitEntityIdAndProfileMetadata()
    {
        var session = CreateMockSession();

        var spawned = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "named bridge keeper",
            20,
            21,
            prefix: "keeper",
            faction: "neutral",
            tags: new[] { "npc", "keeper" },
            roles: new[] { "resident" },
            summoned: false,
            entityId: "bridge_keeper",
            details: new Dictionary<string, object?>
            {
                ["profileName"] = "The Bridge Keeper",
                ["profileAppearance"] = "a patient official with reed ink on both hands",
                ["profileOrigin"] = "Hollowmere",
                ["profileMagicalSignature"] = "bridges remembering their builders",
                ["profileBackstory"] = "The keeper notices route rumors before other people do.",
            },
            emitMessage: false));

        Assert.True(spawned.Applied, spawned.Error);
        Assert.Equal("bridge_keeper", spawned.TargetId);
        Assert.Contains(spawned.Deltas, delta =>
            delta.Operation == "summon"
            && delta.Target == "bridge_keeper"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity)
            && Equals(delta.Details["explicitEntityId"], true)
            && Equals(delta.Details["profileOrigin"], "Hollowmere"));
        var entity = session.Engine.EntityById("bridge_keeper")!;
        var profile = entity.Get<ProfileComponent>();
        Assert.Equal("The Bridge Keeper", profile.PublicName);
        Assert.Equal("Hollowmere", profile.Origin);
        Assert.Equal("bridges remembering their builders", profile.MagicalSignature);
        Assert.Contains("route rumors", profile.Backstory, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PerceptionHidesActorsBehindClosedSightBlockersAndRemembersExploredTiles()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var exploredBeforeView = session.Engine.State.ExploredBySoulId["player_soul"].Count;

        var closedView = session.View();

        Assert.Equal(exploredBeforeView, session.Engine.State.ExploredBySoulId["player_soul"].Count);
        Assert.Contains(closedView.Entities, entity => entity.Id == "cell_door_1");
        Assert.DoesNotContain(closedView.Entities, entity => entity.Id == "prisoner_1");
        Assert.Contains(closedView.Tiles!, tile =>
            tile.X == 14
            && tile.Y == 5
            && !tile.Visible
            && !tile.Explored);

        OpenCellDoorWithoutCommand(session);
        var openView = session.View();

        Assert.Contains(openView.Entities, entity => entity.Id == "prisoner_1");
        Assert.Contains(openView.Tiles!, tile =>
            tile.X == 14
            && tile.Y == 5
            && tile.Visible
            && tile.Explored);

        var explorationDeltas = session.Engine.AdvanceTurn();
        Assert.Contains(explorationDeltas, delta =>
            delta.Operation == "recordExploration"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordExploration)
            && Convert.ToInt32(delta.Details["newTileCount"]) > 0
            && Equals(delta.Details["playerVisible"], false));

        CloseCellDoorWithoutCommand(session);
        var rememberedView = session.View();

        Assert.DoesNotContain(rememberedView.Entities, entity => entity.Id == "prisoner_1");
        Assert.Contains(rememberedView.Tiles!, tile =>
            tile.X == 14
            && tile.Y == 5
            && !tile.Visible
            && tile.Explored);
    }

    [Fact]
    public void PerceptionRadiusIsRoundedRatherThanSquare()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(20, 15)));

        var view = session.View();

        Assert.Contains(view.Tiles!, tile =>
            tile.X == 28
            && tile.Y == 15
            && tile.Visible);
        Assert.Contains(view.Tiles!, tile =>
            tile.X == 25
            && tile.Y == 20
            && tile.Visible);
        Assert.Contains(view.Tiles!, tile =>
            tile.X == 28
            && tile.Y == 23
            && !tile.Visible);
    }

    [Fact]
    public async Task ExploredMemoryFollowsSoulAcrossPossession()
    {
        var session = CreateMockSession();
        _ = session.View();
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, soldier, "webbed", duration: 3);

        var possess = await session.ExecuteAsync(new PossessCommand("soldier_1"));
        var viewAfterPossession = session.View();

        Assert.True(possess.Success);
        Assert.Equal("soldier_1", viewAfterPossession.ControlledEntityId);
        Assert.Contains(viewAfterPossession.Tiles!, tile =>
            tile.X == 0
            && tile.Y == 5
            && tile.Explored
            && !tile.Visible);
        Assert.Equal("player_soul", soldier.Get<SoulComponent>().SoulId);
    }

    [Fact]
    public void MagicContextMarksHiddenFactsWithoutRevealingThemToThePlayerView()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));

        var playerView = session.View();
        var context = session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));

        Assert.DoesNotContain(playerView.Entities, entity => entity.Id == "prisoner_1");
        var prisoner = context.Visible.Single(entity => entity.Id == "prisoner_1");
        Assert.Equal("hidden_from_player", prisoner.Visibility);
    }

    [Fact]
    public void WitnessesRespectSightBlockersAndSuspicionCanBecomeAttributedLater()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        KillImperialActors(session);
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(12, 5)));

        var doorPoint = new GridPoint(13, 5);
        var playerPoint = new GridPoint(12, 5);
        var doorWitnesses = session.Engine.WitnessesOf(doorPoint, player.Id);
        var playerWitnesses = session.Engine.WitnessesOf(playerPoint, player.Id);

        Assert.Contains(doorWitnesses, entity => entity.Id.Value == "prisoner_1");
        Assert.DoesNotContain(playerWitnesses, entity => entity.Id.Value == "prisoner_1");

        var suspicionResult = session.Engine.ApplyConsequence(WorldConsequence.RecordSuspicion(
            "test_setup",
            "test_magic",
            doorPoint.X,
            doorPoint.Y,
            player.Id.Value,
            sourceEntityId: player.Id.Value));
        Assert.True(suspicionResult.Applied, suspicionResult.Error ?? "Suspicion was not recorded.");

        var record = Assert.Single(session.Engine.State.Suspicions.Records);
        Assert.Contains(suspicionResult.Deltas, delta =>
            delta.Operation == "recordSuspicion"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordSuspicion)
            && Equals(delta.Details["suspicionId"], record.Id));
        Assert.Equal("pending", record.Status);
        Assert.Null(record.SuspectedSoulId);

        OpenCellDoorWithoutCommand(session);
        var turnDeltas = session.Engine.AdvanceTurn();

        var attributed = Assert.Single(session.Engine.State.Suspicions.Records);
        Assert.Equal("attributed", attributed.Status);
        Assert.Equal("player_soul", attributed.SuspectedSoulId);
        Assert.Equal(session.Engine.State.Turn, attributed.AttributedTurn);
        Assert.Contains(turnDeltas, delta =>
            delta.Operation == "updateSuspicion"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateSuspicion)
            && Equals(delta.Details["suspicionId"], attributed.Id));
    }

    [Fact]
    public async Task CharacterSheetAndOriginsDeriveBodyHpAndSoulMana()
    {
        var session = GameSession.CreateImperialEncounter(originId: "desert_parn");

        var sheet = session.View().Character!;
        var command = await ExecuteTextAsync(session, "character");

        Assert.Equal("desert_parn", sheet.OriginId);
        Assert.Equal("Desert Parn", sheet.OriginName);
        Assert.Equal(5, sheet.Vigor);
        Assert.Equal(3, sheet.Attunement);
        Assert.Equal(2, sheet.Composure);
        Assert.Equal(28, sheet.MaxHitPoints);
        Assert.Equal(12, sheet.MaxMana);
        Assert.Contains(session.View().Inventory!, item => item.Name == "red tincture");
        Assert.True(command.Success);
        Assert.False(command.ConsumedTurn);
        Assert.Contains(command.Messages, message => message.Contains("Desert Parn", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ResolverLensComesFromBodyAndSoulStats()
    {
        var defaultSession = CreateMockSession();
        var boneSession = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            originId: "bone_singers_apprentice");
        var desertSession = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            originId: "desert_parn");

        var defaultLens = MagicContext(defaultSession).ResolverLens!;
        var boneLens = MagicContext(boneSession).ResolverLens!;
        var desertLens = MagicContext(desertSession).ResolverLens!;

        Assert.Equal(0, defaultLens.EffectMagnitudeDelta);
        Assert.Equal("unchanged", defaultLens.Magnitude);
        Assert.Equal("unchanged", defaultLens.Volatility);
        Assert.Equal("unchanged", defaultLens.CostFraming);

        Assert.Equal(1, boneLens.EffectMagnitudeDelta);
        Assert.Contains("high attunement", boneLens.Magnitude);
        Assert.Contains("bone dust", boneLens.Signature);

        Assert.Equal(0, desertLens.EffectMagnitudeDelta);
        Assert.Contains("low composure", desertLens.Volatility);
        Assert.Contains("high vigor", desertLens.CostFraming);
    }

    [Fact]
    public async Task MockProviderHonorsResolverLensDeterministically()
    {
        var boneSession = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            originId: "bone_singers_apprentice");
        var desertSession = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            originId: "desert_parn");
        DisableImperialAi(boneSession);
        DisableImperialAi(desertSession);

        var boneCast = await boneSession.ExecuteAsync(new CastCommand("a plain blue fire"));
        var desertCast = await desertSession.ExecuteAsync(new CastCommand("a plain blue fire"));

        Assert.Contains(boneCast.Deltas, delta =>
            delta.Operation == "damage"
            && Convert.ToInt32(delta.Details["amount"]) == 7);
        Assert.Contains(desertCast.Deltas, delta =>
            delta.Operation == "damage"
            && Convert.ToInt32(delta.Details["amount"]) == 6);
        Assert.Contains(desertCast.Magic!.EffectTypes, effect => effect == "message");
        Assert.Contains(desertCast.Messages, message => message.Contains("low composure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BodySwapAdoptsBodyVigorAndNameButPreservesSoulStatsManaAndMemory()
    {
        var session = CreateMockSession();
        _ = session.View();
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, soldier, "webbed", duration: 3);

        var possess = await session.ExecuteAsync(new PossessCommand("soldier_1"));
        var sheet = session.View().Character!;

        Assert.True(possess.Success);
        Assert.Equal("soldier_1", sheet.BodyEntityId);
        Assert.Equal("imperial containment soldier", sheet.PublicName);
        Assert.Equal(1, sheet.Vigor);
        Assert.Equal(10, sheet.MaxHitPoints);
        Assert.Equal(4, sheet.Attunement);
        Assert.Equal(3, sheet.Composure);
        Assert.Equal(14, sheet.MaxMana);
        Assert.Equal("player_soul", sheet.SoulId);
        Assert.Contains(session.View().Tiles!, tile =>
            tile.X == 0
            && tile.Y == 5
            && tile.Explored
            && !tile.Visible);
        Assert.Contains(playerBody.Get<InventoryComponent>().Items, pair => pair.Key == "moon pearl");
    }

    [Fact]
    public async Task PublicWildMagicBecomesLegendAndStanding()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var cast = await session.ExecuteAsync(new CastCommand("a plain blue fire"));
        var standing = await session.ExecuteAsync(new StandingCommand());

        Assert.True(cast.Success);
        var deed = Assert.Single(session.Engine.State.Deeds.Records, deed => deed.Kind == "wild_magic");
        Assert.Equal("public", deed.Visibility);
        Assert.Equal("attributed", deed.AttributionStatus);
        Assert.Contains(session.Engine.State.Legend.Tags, tag =>
            tag.ActorSoulId == "player_soul"
            && tag.Tag == "uncanny");
        Assert.Contains(session.Engine.State.Rumors.Records, rumor =>
            rumor.SourceKind == "deed"
            && rumor.SourceId == deed.Id
            && rumor.Text.Contains("wild magic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.View().Rumors!, rumor => rumor.SourceId == deed.Id);
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "recordDeed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordDeed)
            && Equals(delta.Details["deedId"], deed.Id)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "deedApplied"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateDeed)
            && Equals(delta.Details["deedId"], deed.Id));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "recordRumor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordRumor)
            && Equals(delta.Details["deedId"], deed.Id));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "deedWitnessMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["deedId"], deed.Id)
            && ((string)delta.Details["provenance"]!).Equals($"deed:{deed.Id}", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.Provenance == $"deed:{deed.Id}"
            && memory.Text.Contains("wild magic", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "addLegend"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddLegend)
            && Equals(delta.Details["tag"], "uncanny")
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "adjustFactionStanding"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionStanding)
            && Equals(delta.Details["axis"], "imperial-threat")
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionResource)
            && Equals(delta.Details["resource"], "heat")
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(cast.Deltas, delta =>
            delta.Operation == "worldReactionMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["deedId"], deed.Id)
            && Equals(delta.Details["deedKind"], "wild_magic")
            && Equals(delta.Details["reactionKind"], "public_wild_magic")
            && delta.Summary.Contains("wild magic", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.StartsWith("Deed recorded", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.Contains("gains legend tag", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.StartsWith("Factions with role", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.Contains("heat shifts", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.StartsWith("Deed marked applied", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.Contains("remembers hearing a rumor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(cast.Messages, message => message.StartsWith("Something is scheduled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(standing.Messages, message => message.Contains("imperial-threat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(standing.Messages, message => message.Contains("Legend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SecretDeedDoesNotBecomeLegendOrStanding()
    {
        var session = CreateMockSession();
        KillAllNonPlayerActors(session);
        var player = session.Engine.State.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;

        var deed = RecordDeed(session, player, "wild_magic", 3, origin, null, new[] { "damage" });
        session.Engine.AdvanceTurn();

        Assert.Equal("secret", deed.Visibility);
        Assert.Empty(session.Engine.State.Legend.Tags);
        Assert.Empty(session.Engine.State.Rumors.Records);
        Assert.DoesNotContain(session.Engine.State.Factions.Factions, faction =>
            faction.Standing.ContainsKey("imperial-threat"));
    }

    [Fact]
    public void WorldReactionRollsBackWholeDeedWhenAChildConsequenceRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var deed = state.Deeds.Append(
            state.Turn,
            "player_soul",
            "wild_magic",
            4,
            $"{state.RegionId}:8,4",
            "public",
            witnesses: Array.Empty<string>(),
            tags: new[] { "damage" });
        var laterDeed = state.Deeds.Append(
            state.Turn + 1,
            "player_soul",
            "body_swap",
            2,
            $"{state.RegionId}:9,4",
            "public",
            witnesses: Array.Empty<string>(),
            tags: new[] { "possession" });
        var reactions = new WorldReactionSystem();
        var rejectNextLegend = true;

        var result = reactions.ApplyPending(state, consequence =>
        {
            if (rejectNextLegend
                && consequence.Type.Equals(WorldConsequenceTypes.AddLegend, StringComparison.OrdinalIgnoreCase))
            {
                rejectNextLegend = false;
                return new WorldConsequenceApplyResult(
                    false,
                    consequence.TargetEntityId,
                    "forced add legend rejection",
                    Array.Empty<string>(),
                    new[]
                    {
                        new StateDelta(
                            "worldConsequenceRejected",
                            consequence.TargetEntityId ?? consequence.Source,
                            "forced add legend rejection",
                            new Dictionary<string, object?>
                            {
                                ["consequenceType"] = consequence.Type,
                                ["playerVisible"] = false,
                            }),
                    },
                    new Dictionary<string, object?>
                    {
                        ["consequenceType"] = consequence.Type,
                        ["error"] = "forced add legend rejection",
                    });
            }

            return WorldConsequenceGuard.ApplyWithNewApplier(state, consequence);
        });

        Assert.True(result.AppliedAny);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddLegend)
            && delta.Summary.Contains("forced add legend rejection", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldReactionSkipped"
            && Equals(delta.Details["deedId"], deed.Id)
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "deedApplied"
            && Equals(delta.Details["deedId"], laterDeed.Id));
        Assert.False(state.Deeds.IsApplied(deed.Id));
        Assert.True(state.Deeds.IsApplied(laterDeed.Id));
        Assert.DoesNotContain(state.Legend.Tags, tag => tag.Source == deed.Id);
        Assert.Contains(state.Legend.Tags, tag => tag.Source == laterDeed.Id);
        Assert.DoesNotContain(state.Rumors.Records, rumor =>
            rumor.SourceKind == "deed"
            && rumor.SourceId == deed.Id);
        Assert.Contains(state.Rumors.Records, rumor =>
            rumor.SourceKind == "deed"
            && rumor.SourceId == laterDeed.Id);
    }

    [Fact]
    public void SuspiciousEffectWitnessRemainsUnattributedUntilConnected()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        KillImperialActors(session);
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(12, 5)));

        var deedResult = session.Engine.ApplyConsequence(WorldConsequence.RecordDeed(
            "test_setup",
            player.Id.Value,
            "wild_magic",
            3,
            12,
            5,
            13,
            5,
            new[] { "damage" },
            sourceEntityId: player.Id.Value));
        Assert.True(deedResult.Applied, deedResult.Error ?? "Deed was not recorded.");
        var deed = Assert.Single(session.Engine.State.Deeds.Records);
        var deltas = session.Engine.AdvanceTurn();

        Assert.Equal("suspicious", deed.Visibility);
        Assert.Equal("unattributed", deed.AttributionStatus);
        Assert.Contains(deed.EffectWitnesses!, witness => witness == "lio_soul");
        Assert.Contains(deedResult.Deltas, delta =>
            delta.Operation == "recordSuspicion"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordSuspicion)
            && Equals(delta.Details["witnessSoulId"], "lio_soul"));
        Assert.Contains(deedResult.Deltas, delta =>
            delta.Operation == "recordDeed"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordDeed)
            && Equals(delta.Details["deedId"], deed.Id));
        Assert.Contains(deltas, delta =>
            delta.Operation == "deedWitnessMemory"
            && delta.Target == "prisoner_1"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["deedId"], deed.Id)
            && Equals(delta.Details["witnessSoulId"], "lio_soul")
            && Equals(delta.Details["attributionStatus"], "unattributed"));
        Assert.Contains(session.Engine.EntityById("prisoner_1")!.Get<MemoryComponent>().Records, memory =>
            memory.Provenance == $"deed:{deed.Id}"
            && memory.Text.Contains("someone unnamed", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(session.Engine.State.Legend.Tags);
        Assert.Contains(session.Engine.State.Factions.Factions, faction =>
            faction.Id == "empire"
            && faction.Standing.TryGetValue("suspicion", out var suspicion)
            && suspicion > 0);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldReactionMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["deedId"], deed.Id)
            && Equals(delta.Details["deedVisibility"], "suspicious")
            && Equals(delta.Details["reactionKind"], "suspicious_wild_magic")
            && delta.Summary.Contains("Someone saw the magic", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeedsAfterBodySwapAttachToPlayerSoul()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, soldier, "webbed", duration: 3);
        await session.ExecuteAsync(new PossessCommand("soldier_1"));

        var controlled = session.Engine.State.ControlledEntity;
        var point = controlled.Get<PositionComponent>().Position;
        RecordDeed(session, controlled, "wild_magic", 3, point, point, new[] { "damage" });
        session.Engine.AdvanceTurn();

        Assert.Contains(session.Engine.State.Legend.Tags, tag =>
            tag.ActorSoulId == "player_soul"
            && tag.Tag == "uncanny");
        Assert.DoesNotContain(session.Engine.State.Legend.Tags, tag =>
            tag.ActorSoulId == "soldier_1_soul");
    }

    [Fact]
    public async Task EmpireHeatSpendsPatrolIntoScheduledPressureAndSpawn()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var patrolsBefore = session.Engine.State.Factions.ResourceValue("empire", "patrols");

        var cast = await session.ExecuteAsync(new CastCommand("a plain blue fire"));
        var debug = session.Observation(debug: true);

        Assert.True(cast.Success);
        Assert.Equal(patrolsBefore - 1, session.Engine.State.Factions.ResourceValue("empire", "patrols"));
        Assert.Contains(session.Engine.State.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
        Assert.Contains(session.Engine.State.WorldTurns.Records, record =>
            record.Kind == "faction_pressure"
            && record.SourceId == "empire"
            && Equals(record.Details["response"], "empire_patrol"));
        Assert.Contains(cast.Messages, message => message.Contains("spends a patrol", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(debug.Debug!.Factions!, faction =>
            faction.Id == "empire"
            && faction.Resources.TryGetValue("patrols", out var patrols)
            && patrols == patrolsBefore - 1);

        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new WaitCommand());

        var patrol = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Id.Value.StartsWith("imperial_patrol_", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<ActorComponent>(out var actor)
            && actor.Faction == "empire");
        Assert.Equal("empire", patrol.Get<FactionComponent>().FactionId);
        Assert.Contains("patrol", patrol.Get<FactionComponent>().Roles);
        Assert.Equal("imperial_patrol", patrol.Get<AiComponent>().PolicyId);
        Assert.Equal("body", patrol.Get<PhysicalComponent>().Material);
        Assert.False(patrol.Has<SummonedComponent>());
    }

    [Fact]
    public async Task EmpirePressureCooldownPreventsImmediatePatrolSpam()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        await session.ExecuteAsync(new CastCommand("a plain blue fire"));
        var scheduledAfterFirst = session.Engine.State.ScheduledEvents.Events.Count(item => item.Kind == "empire_patrol");
        await session.ExecuteAsync(new CastCommand("a second plain blue fire"));
        var scheduledAfterSecond = session.Engine.State.ScheduledEvents.Events.Count(item => item.Kind == "empire_patrol");

        Assert.Equal(1, scheduledAfterFirst);
        Assert.Equal(scheduledAfterFirst, scheduledAfterSecond);
    }

    [Fact]
    public void DepletedEmpireRegeneratesBeforeItCanSpendPressureAgain()
    {
        var session = CreateMockSession();
        session.Engine.State.Factions.AdjustResource("empire", "patrols", -99);
        session.Engine.State.Factions.AdjustResource("empire", "warrants", -99);
        session.Engine.State.Factions.AdjustResource("empire", "heat", 4);

        Assert.Equal(0, session.Engine.State.ScheduledEvents.Events.Count(item => item.Kind == "empire_patrol"));
        var deltas = session.Engine.AdvanceTurn();

        Assert.Equal(0, session.Engine.State.Factions.ResourceValue("empire", "patrols"));
        Assert.Contains(session.Engine.State.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
        Assert.Contains(session.Engine.State.WorldTurns.Records, record =>
            record.Kind == "faction_pressure"
            && record.SourceId == "empire"
            && Equals(record.Details["response"], "empire_patrol"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionResource)
            && Equals(delta.Details["resource"], "patrols")
            && Equals(delta.Details["delta"], 1)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionResource)
            && Equals(delta.Details["resource"], "heat")
            && Equals(delta.Details["delta"], -1)
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("adjust", StringComparison.OrdinalIgnoreCase)
            || message.Contains("shifts by", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void QuietFactionRecoveryDoesNotEmitNoOpResourceDeltas()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var deltas = session.Engine.AdvanceTurn();

        Assert.DoesNotContain(deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionResource));
    }

    [Fact]
    public void DeedTurnsDoNotRechargeFactionPressureBeforeSpending()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var point = player.Get<PositionComponent>().Position;
        session.Engine.State.Factions.AdjustResource("empire", "patrols", -99);
        session.Engine.State.Factions.AdjustResource("empire", "warrants", -99);
        session.Engine.State.Factions.AdjustResource("empire", "heat", 4);

        RecordDeed(session, player, "wild_magic", 2, point, point, new[] { "damage" });
        session.Engine.AdvanceTurn();

        Assert.Equal(0, session.Engine.State.Factions.ResourceValue("empire", "patrols"));
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
        Assert.Contains(session.Engine.State.WorldTurns.Records, record =>
            record.Kind == "faction_pressure_blocked"
            && record.SourceId == "empire"
            && Equals(record.Details["response"], "resource_shortage"));
    }

    [Fact]
    public async Task EmpireBlocRulesApplyToAnyFactionWithTheRole()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.Factions.AddOrGet(
            "charter_office",
            "Charter Office",
            "empire_bloc",
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["heat"] = 0,
                ["patrols"] = 0,
                ["max_patrols"] = 0,
            },
            new[] { "player" });

        var cast = await session.ExecuteAsync(new CastCommand("a plain blue fire"));

        Assert.True(cast.Success);
        Assert.True(session.Engine.State.Factions.StandingValue("charter_office", "imperial-threat") > 0);
        Assert.True(session.Engine.State.Factions.ResourceValue("charter_office", "heat") > 0);
    }

    [Fact]
    public void FactionStandingCanOverrideRoleHostility()
    {
        var session = CreateMockSession();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var player = session.Engine.State.ControlledEntity;

        Assert.True(session.Engine.IsHostile(soldier, player));

        session.Engine.State.Factions.AdjustStanding("empire", "player-alliance", 1);

        Assert.False(session.Engine.IsHostile(soldier, player));
    }

    [Fact]
    public async Task EmpireHeatAfterBodySwapStillFollowsThePlayerSoul()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        ApplyStatus(session, soldier, "webbed", duration: 3);
        await session.ExecuteAsync(new PossessCommand("soldier_1"));
        session.Engine.State.Factions.AdjustResource("empire", "patrols", -99);
        session.Engine.State.Factions.AdjustResource("empire", "warrants", -99);
        var heatBefore = session.Engine.State.Factions.ResourceValue("empire", "heat");

        var controlled = session.Engine.State.ControlledEntity;
        var point = controlled.Get<PositionComponent>().Position;
        RecordDeed(session, controlled, "wild_magic", 3, point, point, new[] { "damage" });
        session.Engine.AdvanceTurn();

        Assert.True(session.Engine.State.Factions.ResourceValue("empire", "heat") > heatBefore);
        Assert.Contains(session.Engine.State.Legend.Tags, tag =>
            tag.ActorSoulId == "player_soul"
            && tag.Tag == "uncanny");
    }

    [Fact]
    public void EmperorReachabilityUsesImperialDefensePool()
    {
        var session = CreateMockSession();

        Assert.False(session.Engine.EmperorReachable());

        session.Engine.State.Factions.AdjustResource("empire", "defenses", -99);

        Assert.True(session.Engine.EmperorReachable());
    }

    [Fact]
    public void PersonalBondsDoNotConflateCombatFactionOrMembership()
    {
        var session = CreateMockSession();
        var soldier = session.Engine.EntityById("soldier_1")!;
        var player = session.Engine.State.ControlledEntity;

        session.Engine.State.Bonds.Adjust("soldier_1_soul", "player_soul", admiration: 6, posture: "admiring");

        Assert.True(session.Engine.IsHostile(soldier, player));
        Assert.Equal("empire", soldier.Get<ActorComponent>().Faction);
        Assert.Equal("empire", soldier.Get<FactionComponent>().FactionId);

        session.Engine.State.Bonds.Adjust("soldier_1_soul", "player_soul", loyalty: 5, posture: "protective");

        Assert.False(session.Engine.IsHostile(soldier, player));
        Assert.Equal("empire", soldier.Get<ActorComponent>().Faction);
        Assert.Equal("empire", soldier.Get<FactionComponent>().FactionId);
    }

    [Fact]
    public async Task GiftsWriteMemoryWithoutImmediateBondShift()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var give = await session.ExecuteAsync(new GiveCommand("grave salt", "Lio"));
        var debug = session.Observation(debug: true);

        Assert.True(give.Success);
        Assert.Equal(1, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["grave salt"]);
        Assert.Contains(give.Deltas, delta =>
            delta.Operation == "giveItem"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransferItem)
            && Equals(delta.Details["mode"], "give")
            && Equals(delta.Details["item"], "grave salt")
            && Equals(delta.Details["recipientEntityId"], "prisoner_1"));
        Assert.Contains(give.Deltas, delta =>
            delta.Operation == "giftMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
        Assert.DoesNotContain(give.Deltas, delta => delta.Operation == "giftSkipped");
        Assert.Equal(1, session.Engine.EntityById("prisoner_1")!.Get<InventoryComponent>().Items["grave salt"]);
        Assert.DoesNotContain(debug.Debug!.Bonds!, bond =>
            bond.SubjectSoulId == "lio_soul"
            && bond.TargetSoulId == "player_soul");
        Assert.Contains(session.Engine.EntityById("prisoner_1")!.Get<MemoryComponent>().Records, memory =>
            memory.Text.Contains("grave salt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DialogueClaimExtractionCanUseRecentGiftToShiftBondBetweenTurns()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Old Maren decides the gift was a real kindness.",
            "bond",
            "Old Maren",
            Salience: 3,
            Confidence: 90,
            PlayerVisible: true,
            UpdateBond: true,
            LoyaltyDelta: 3,
            AdmirationDelta: 2,
            BondPosture: "grateful",
            Tags: new[] { "gift", "bond" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        var maren = new Entity(EntityId.Create("old_maren"), "Old Maren")
            .Set(new PositionComponent(new GridPoint(12, 5)))
            .Set(new RenderableComponent('p', "neutral"))
            .Set(new TagsComponent(new[] { "resident" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(6, 6, 0, 0, 1, 0, "neutral"))
            .Set(new ControllerComponent(ControllerKind.None))
            .Set(new SoulComponent("maren_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new InteractableComponent(new[] { "talk", "give" }))
            .Set(new ProfileComponent("Old Maren", "a careful witness with river mud on her cuffs"));
        session.Engine.State.Entities[maren.Id] = maren;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new GiveCommand("grave salt", "Maren"));
        var talk = await session.ExecuteAsync(new TalkCommand("Maren, this was meant kindly"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "claimBondShift");
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimBondShift");
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimApplicationSkipped");
        Assert.True(session.Engine.State.Bonds.TryGet("maren_soul", "player_soul", out var bond));
        Assert.Equal(2, bond.Loyalty);
        Assert.Equal(2, bond.Admiration);
        Assert.Equal("grateful", bond.Posture);
    }

    [Fact]
    public async Task DialogueClaimBondShiftRollsBackOnMissingEntityFailure()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio lowers his voice. \"The kindness was real; I will remember it.\"",
            Delivery: "soft",
            Intent: "inform"));
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Lio decides the kindness was real.",
            "bond",
            "Lio",
            Salience: 3,
            Confidence: 90,
            PlayerVisible: true,
            UpdateBond: true,
            LoyaltyDelta: 4,
            AdmirationDelta: 4,
            BondPosture: "grateful"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, I meant the gift kindly."));
        var bondsBefore = session.Engine.State.Bonds.Bonds.Count;
        session.Engine.State.Entities.Remove(EntityId.Create("prisoner_1"));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        Assert.Equal(bondsBefore, session.Engine.State.Bonds.Bonds.Count);
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimRecorded");
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimMemory");
        Assert.Contains(wait.Deltas, delta => delta.Operation == "rumorMinted");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimApplicationSkipped"
            && delta.Target == "claim_1"
            && Equals(delta.Details["proposalType"], "bond")
            && Equals(delta.Details["claimId"], "claim_1")
            && Equals(delta.Details["failure"], "missing_entity")
            && Equals(delta.Details["rejectedCount"], 1));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimApplied");
    }

    [Fact]
    public async Task DialogueClaimExtractionBindsVisiblePromiseForLaterTravelRealization()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Jimmer hid a fine blade beyond the next road.",
            "item",
            "fine blade",
            Salience: 3,
            Confidence: 75,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "item",
            TriggerHint: "travel",
            ItemName: "fine blade",
            Tags: new[] { "item", "blade" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.True(talk.Success);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromiseStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateClaim)
            && Equals(delta.Details["status"], "promised"));
        Assert.Contains(wait.Deltas, delta => delta.Operation == "rumorMinted");
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "fine blade" && claim.Status == "promised");
        Assert.Contains(session.View().Rumors!, rumor => rumor.SourceKind == "claim" && rumor.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "rumor_spread"));
        var itemPromise = session.Engine.State.PromiseLedger.Promises.Single(promise =>
            promise.Subject == "fine blade");
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseItem"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnItem)
            && Equals(delta.Details["promiseId"], itemPromise.Id)
            && Equals(delta.Details["stackPolicy"], "unique"));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "promised blade"
            && entity.TryGet<PromiseAnchorComponent>(out _));
    }

    [Fact]
    public async Task CastingAtAGenericPromiseThreatByNameDoesNotTripThePromiseEffectGuard()
    {
        // Regression: WildMagicController.PromiseIntentNeedsLedger scans the player's raw spell
        // text for words like "promise"/"promised" to require a real createPromise/scheduleEvent/
        // createTrigger effect. The generic threat-promise fallback name used to be literally
        // "promised threat", so any ordinary combat spell that named the entity back ("attack the
        // promised threat") tripped that guard as a false positive -- the player was never trying
        // to bind a new promise, just referring to the thing standing in front of them by name.
        // The generated name is now region-flavored (ThreatArchetypeGenerator) rather than a
        // single fixed fallback string, so target by selector -- the point under test is the
        // guard token check, not any specific generated name.
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Boiling acid arcs through the air.",
            Effects: new[]
            {
                new SpellEffect("damage", new Dictionary<string, object?> { ["target"] = "nearest_enemy", ["amount"] = 4 }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "Someone dangerous is coming to find you.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "old grudge",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            null,
            triggerHint: "travel",
            realizationKind: "threat");
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var threat = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor) && anchor.PromiseIds.Contains(promise.Id));
        Assert.DoesNotContain(new[]
            {
                "promise", "promises", "promised", "prophecy", "prophecies", "omen", "omens", "oath", "oaths",
            },
            token => threat.Name.Contains(token, StringComparison.OrdinalIgnoreCase));

        var result = await session.ExecuteAsync(new CastCommand($"I spew boiling acid onto the {threat.Name}"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.False(result.TechnicalFailure);
        Assert.Null(result.Magic?.Error);
    }

    [Fact]
    public async Task TravelThreatPromiseUsesGeneratedSpawnEntityConsequence()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "A debt collector waits beyond the next road.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "debt collector",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            null,
            triggerHint: "travel",
            realizationKind: "threat");

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseThreat"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity)
            && Equals(delta.Details["promiseId"], promise.Id)
            && Equals(delta.Details["summoned"], false)
            && Equals(delta.Details["aiPolicyId"], "hostile")
            && Equals(delta.Details["wantGenerated"], true));
        var threat = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name.Contains("debt collector", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        // A private debt collector is not the Empire; spawning it under the empire faction would
        // feed Censorate heat/warrant pressure for a threat that has nothing to do with it. It
        // must still be a real, engine-recognized hostile toward the player, not just tagged so.
        Assert.Equal("independent", threat.Get<ActorComponent>().Faction);
        Assert.Equal("hostile", threat.Get<AiComponent>().PolicyId);
        Assert.True(session.Engine.IsHostile(threat, session.Engine.State.ControlledEntity));
        Assert.Contains("leverage", threat.Get<WantComponent>().Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("generated_want", threat.Get<WantComponent>().Tags);

        // The regression this guards: FactionLedger.IsHostile(actor, target) checks the ACTOR's
        // own hostileRoles against the TARGET's role, so it is not automatically symmetric.
        // Giving the threat's faction hostileRoles:["player"] makes it attack the player, but
        // the player's own faction must separately list the threat's role as hostile too, or
        // bumping into it reads as "blocks the way" instead of a bump-attack.
        var hpBefore = threat.Get<ActorComponent>().HitPoints;
        var threatPosition = threat.Get<PositionComponent>().Position;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(threatPosition.Translate(-1, 0)));
        var bump = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.DoesNotContain(bump.Messages, message => message.Contains("blocks the way", StringComparison.OrdinalIgnoreCase));
        Assert.True(threat.Get<ActorComponent>().HitPoints < hpBefore, string.Join(" | ", bump.Messages));
    }

    [Fact]
    public async Task TravelPromiseSelectionHonorsDirectionalClaims()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "There is a burned-oak sword south of here.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "burned-oak sword",
            triggerHint: "travel",
            realizationKind: "item");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            null,
            triggerHint: "travel",
            realizationKind: "item");

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.South));

        var realization = Assert.Single(travel.Deltas, delta =>
            delta.Operation == "realizePromise"
            && delta.Target == promise.Id);
        Assert.Equal("South", realization.Details["contextDirection"]);
        Assert.Equal("0,1", realization.Details["contextZoneId"]);
        Assert.Equal("hollowmere_margin", realization.Details["contextRegionId"]);
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseItem"
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Single(travel.Messages, message =>
            message.Contains("promised object is waiting", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(travel.Messages, message =>
            message.StartsWith("A promise changes", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("A promise stirs awake", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(
            travel.Messages.Count,
            travel.Messages.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public async Task TravelPromiseSelectionDoesNotSpendContradictoryDirection()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "There is a burned-oak sword north of here.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "burned-oak sword",
            triggerHint: "travel",
            realizationKind: "item");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            null,
            triggerHint: "travel",
            realizationKind: "item");

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.South));

        Assert.DoesNotContain(travel.Deltas, delta =>
            delta.Operation == "realizePromise"
            && delta.Target == promise.Id);
        Assert.DoesNotContain(travel.Deltas, delta =>
            delta.Operation == "promiseItem"
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Equal("bound", session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id).Status);
    }

    [Fact]
    public void PromiseCaptureRecordsSourceMemoryThroughSharedMemoryLedger()
    {
        var session = CreateMockSession();
        var system = new PromiseSystem();

        var promise = system.Capture(
            session.Engine.State,
            new PromiseCapture(
                "prophecy",
                "red bell",
                "A red bell will answer when the locked gate hears rain.",
                "test_prophecy",
                4,
                new[] { "prophecy", "bell" }));

        var memory = Assert.Single(session.Engine.State.Memories.Records, record =>
            record.SubjectId == "red bell");
        Assert.Equal("prophecy", promise.Kind);
        Assert.Equal("red bell", promise.Subject);
        Assert.Equal("A red bell will answer when the locked gate hears rain.", memory.Text);
        Assert.Equal("test_prophecy", memory.Provenance);
        Assert.Equal(4, memory.Salience);
        Assert.True(memory.Shareable);
    }

    [Fact]
    public void PromiseCaptureUsesProvidedConsequenceSink()
    {
        var session = CreateMockSession();
        var system = new PromiseSystem();
        var submittedTypes = new List<string>();

        system.Capture(
            session.Engine.State,
            new PromiseCapture(
                "prophecy",
                "black bridge",
                "The black bridge will fold when a debtor speaks its true name.",
                "test_prophecy",
                4,
                new[] { "prophecy", "bridge" }),
            consequence =>
            {
                submittedTypes.Add(consequence.Type);
                return session.Engine.ApplyConsequence(consequence);
            });

        Assert.Equal(
            new[] { WorldConsequenceTypes.CreatePromise, WorldConsequenceTypes.RecordMemory },
            submittedTypes);
    }

    [Fact]
    public void PromiseCaptureRollsBackPromiseIfSourceMemoryRejects()
    {
        var session = CreateMockSession();
        var system = new PromiseSystem();

        var error = Assert.Throws<InvalidOperationException>(() => system.Capture(
            session.Engine.State,
            new PromiseCapture(
                "prophecy",
                "glass ford",
                "The glass ford will open after the moon buys a knife.",
                "test_prophecy",
                4,
                new[] { "prophecy", "ford" }),
            consequence => consequence.Type.Equals(WorldConsequenceTypes.RecordMemory, StringComparison.OrdinalIgnoreCase)
                ? RejectConsequence(consequence, "forced_memory_rejection")
                : session.Engine.ApplyConsequence(consequence)));

        Assert.Contains("forced_memory_rejection", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Text.Contains("glass ford", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Memories.Records, memory =>
            memory.Text.Contains("glass ford", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void PromiseCaptureRejectsInvalidInputBeforeMutation()
    {
        var session = CreateMockSession();
        var system = new PromiseSystem();
        var submittedTypes = new List<string>();

        Assert.Throws<ArgumentException>(() => system.Capture(
            session.Engine.State,
            new PromiseCapture(
                "prophecy",
                "",
                "A nameless prophecy should not enter the ledger.",
                "test_prophecy",
                4,
                new[] { "prophecy" }),
            consequence =>
            {
                submittedTypes.Add(consequence.Type);
                return session.Engine.ApplyConsequence(consequence);
            }));

        Assert.Empty(submittedTypes);
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Text.Contains("nameless prophecy", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Memories.Records, memory =>
            memory.Text.Contains("nameless prophecy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BoundedWorldTurnAuditsOnlyBudgetedMovesAndCoolsPromiseStirring()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.PromiseLedger.Add(
            "rumor",
            "There is a magical sword in a burned-out oak north of here.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "magical sword",
            triggerHint: "travel",
            realizationKind: "item");
        for (var i = 0; i < 3; i++)
        {
            state.Rumors.Append(
                state.Turn,
                "test",
                $"source_{i}",
                "hollowmere_margin",
                "hollowmere_margin",
                $"A road rumor number {i} is looking for a carrier.",
                salience: 4,
                carrierIds: new[] { $"source_carrier_{i}" },
                tags: new[] { "rumor", "test" });
        }

        var firstTurnDeltas = session.Engine.AdvanceTurn();
        var firstTurnMoves = state.WorldTurns.Records.Where(record => record.Turn == state.Turn).ToArray();
        session.Engine.AdvanceTurn();

        Assert.True(firstTurnMoves.Length <= 2);
        Assert.Contains(firstTurnDeltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn));
        Assert.Contains(firstTurnMoves, record =>
            record.Kind == "rumor_spread"
            && Equals(record.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor));
        Assert.Contains(firstTurnMoves, record => record.Kind == "promise_stir");
        Assert.Single(state.WorldTurns.Records, record => record.Kind == "promise_stir");
        var debug = session.Observation(debug: true).Debug!;
        Assert.Equal(state.WorldTurns.Records.Count, debug.Ledgers!.WorldTurns);
        Assert.Equal(state.WorldTurns.Records.Count, debug.WorldTurns!.Count);
        Assert.Contains(debug.WorldTurns, card =>
            card.Kind == "rumor_spread"
            && card.Summary.Length > 0
            && Equals(card.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor));
        Assert.Contains(debug.WorldTurns, card =>
            card.Kind == "promise_stir"
            && card.SourceId.StartsWith("promise_", StringComparison.Ordinal)
            && card.Summary.Length > 0);
    }

    [Fact]
    public void BoundedWorldTurnCanPrivatelyStirHighSalienceNpcWant()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            var entity = session.Engine.EntityById(id)!;
            session.Engine.ApplyConsequence(WorldConsequence.UpdateWant(
                "test_setup",
                entity.Id.Value,
                status: "satisfied",
                operation: "testSilenceWant"));
        }

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 2,
            announce: true,
            applyConsequence: session.Engine.ApplyConsequence);
        var secondDeltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 2,
            announce: true,
            applyConsequence: session.Engine.ApplyConsequence);

        Assert.Contains(deltas, delta =>
            delta.Operation == "wantStirMemory"
            && delta.Target == "prisoner_1"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["provenance"], "want:want_lio_escape")
            && Equals(delta.Details["shareable"], false));
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "want_stir")
            && Equals(delta.Details["sourceId"], "want_lio_escape")
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(state.Memories.Records, memory =>
            memory.SubjectId == "prisoner_1"
            && memory.Provenance == "want:want_lio_escape"
            && memory.Text.Contains("Escape the containment yard", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("active want stirs", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secondDeltas, delta =>
            delta.Operation == "wantStirMemory"
            && delta.Target == "prisoner_1");
    }

    [Fact]
    public async Task ConsumedActionMessagesHideWorldTurnAuditWrappers()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.PromiseLedger.Add(
            "rumor",
            "There is a magical sword in a burned-out oak north of here.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "magical sword",
            triggerHint: "travel",
            realizationKind: "item");
        state.Rumors.Append(
            state.Turn,
            "test",
            "audit_wrapper_rumor",
            state.RegionId,
            state.RegionId,
            "A road rumor is looking for one more carrier.",
            salience: 4,
            carrierIds: new[] { "source_carrier" },
            tags: new[] { "rumor", "test" });

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(1, result.Messages.Count(message => message.StartsWith("A rumor reaches", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, result.Messages.Count(message => message.StartsWith("A lead tugs", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "rumorSpread"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor)
            && Equals(delta.Details["visibility"], WorldConsequenceVisibility.Hidden)
            && !delta.IsPlayerVisible());
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "rumor_spread")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "promise_stir")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task RumorSpreadWritesHeardMemoryForNewNpcCarrier()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var regionCarrier = $"region:{state.RegionId}";
        state.Rumors.Append(
            state.Turn,
            "test",
            "memory_rumor_source",
            state.RegionId,
            state.RegionId,
            "The walking stone remembers a sealed oath.",
            salience: 4,
            carrierIds: new[] { regionCarrier },
            tags: new[] { "rumor", "memory_test" });

        var result = await session.ExecuteAsync(new WaitCommand());

        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "rumorSpread"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor)
            && delta.Details["carrierIds"] is IEnumerable<string> carrierIds
            && carrierIds.Contains("lio_soul"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "rumorHeardMemory"
            && delta.Target == "prisoner_1"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["sourceId"], "memory_rumor_source"));
        Assert.Contains(state.Memories.Records, memory =>
            memory.SubjectId == "prisoner_1"
            && memory.Text.Contains("walking stone", StringComparison.OrdinalIgnoreCase)
            && memory.Provenance.StartsWith("rumor:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.EntityById("prisoner_1")!.Get<MemoryComponent>().Records, memory =>
            memory.Text.Contains("walking stone", StringComparison.OrdinalIgnoreCase)
            && memory.Source == "world_turn");
        Assert.Single(state.WorldTurns.Records, record => record.Kind == "rumor_spread");
    }

    [Fact]
    public void RumorPropagationPrefersCarriersWhoseWantsMatchTheRumor()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var regionCarrier = $"region:{state.RegionId}";
        state.Rumors.Append(
            state.Turn,
            "test",
            "procedure_rumor_source",
            state.RegionId,
            state.RegionId,
            "A Censorate procedure says the ward-captain must keep the paperwork intact.",
            salience: 4,
            carrierIds: new[] { regionCarrier },
            tags: new[] { "rumor", "procedure", "empire" });

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 1,
            announce: false,
            applyConsequence: session.Engine.ApplyConsequence);

        var rumor = Assert.Single(state.Rumors.Records, item => item.SourceId == "procedure_rumor_source");
        Assert.Contains("soldier_2_soul", rumor.CarrierIds);
        Assert.DoesNotContain("lio_soul", rumor.CarrierIds);
        Assert.Contains(deltas, delta =>
            delta.Operation == "rumorSpread"
            && delta.Details["newCarriers"] is IEnumerable<string> carriers
            && carriers.Contains("soldier_2_soul"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "rumorHeardMemory"
            && delta.Target == "soldier_2"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
        Assert.Contains(state.Memories.Records, memory =>
            memory.SubjectId == "soldier_2"
            && memory.Text.Contains("procedure", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RumorPropagationRollsBackRumorUpdateWhenHeardMemoryRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var regionCarrier = $"region:{state.RegionId}";
        state.Rumors.Append(
            state.Turn,
            "test",
            "rollback_rumor",
            state.RegionId,
            state.RegionId,
            "A rollback rumor wants to land in someone's memory.",
            salience: 4,
            carrierIds: new[] { regionCarrier },
            tags: new[] { "rumor", "rollback" });

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 1,
            announce: false,
            applyConsequence: consequence =>
                consequence.Type.Equals(WorldConsequenceTypes.RecordMemory, StringComparison.OrdinalIgnoreCase)
                    ? RejectConsequence(consequence, "test rejected heard memory")
                    : session.Engine.ApplyConsequence(consequence));

        var rumor = Assert.Single(state.Rumors.Records, item => item.SourceId == "rollback_rumor");
        Assert.Equal(new[] { regionCarrier }, rumor.CarrierIds);
        Assert.Equal(0, rumor.Hops);
        Assert.Equal(4, rumor.Salience);
        Assert.Equal("active", rumor.Status);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "rumorSpread");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "rumorHeardMemory");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
        Assert.Contains(deltas, delta =>
            delta.Operation == "rumorPropagationSkipped"
            && delta.Target == rumor.Id
            && Equals(delta.Details["sourceId"], "rollback_rumor")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(state.Memories.Records, memory =>
            memory.Text.Contains("rollback rumor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void RumorPropagationReportsSilentNonApplyWithoutMutatingRumor()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var regionCarrier = $"region:{state.RegionId}";
        state.Rumors.Append(
            state.Turn,
            "test",
            "silent_rumor",
            state.RegionId,
            state.RegionId,
            "A silent rumor should leave an audit when its update refuses to apply.",
            salience: 4,
            carrierIds: new[] { regionCarrier },
            tags: new[] { "rumor", "silent" });

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 1,
            announce: false,
            applyConsequence: consequence =>
                consequence.Type.Equals(WorldConsequenceTypes.UpdateRumor, StringComparison.OrdinalIgnoreCase)
                    ? WorldConsequenceApplyResult.Empty("silent rumor update refusal")
                    : session.Engine.ApplyConsequence(consequence));

        var rumor = Assert.Single(state.Rumors.Records, item => item.SourceId == "silent_rumor");
        Assert.Equal(new[] { regionCarrier }, rumor.CarrierIds);
        Assert.Equal(0, rumor.Hops);
        Assert.DoesNotContain(deltas, delta => delta.Operation == "rumorSpread");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.Contains(deltas, delta =>
            delta.Operation == "rumorPropagationSkipped"
            && delta.Target == rumor.Id
            && Equals(delta.Details["sourceId"], "silent_rumor")
            && Equals(delta.Details["failure"], "silent rumor update refusal")
            && Equals(delta.Details["rejectedCount"], 0)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
    }

    [Fact]
    public void RumorPropagationSkipsSaturatedRumorsBeforeSpendingSpreadBudget()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var regionCarrier = $"region:{state.RegionId}";
        var allLocalCarriers = state.Entities.Values
            .Where(entity => entity.Id != state.ControlledEntityId)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value)
            .Append(regionCarrier)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        state.Rumors.Append(
            state.Turn,
            "test",
            "saturated_rumor",
            state.RegionId,
            state.RegionId,
            "Everyone nearby has already heard the first rumor.",
            salience: 5,
            carrierIds: allLocalCarriers,
            tags: new[] { "rumor", "saturated" });
        state.Rumors.Append(
            state.Turn,
            "test",
            "eligible_rumor",
            state.RegionId,
            state.RegionId,
            "The second rumor still needs a local carrier.",
            salience: 4,
            carrierIds: new[] { "distant_source" },
            tags: new[] { "rumor", "eligible" });

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 1,
            announce: false,
            applyConsequence: session.Engine.ApplyConsequence);

        Assert.DoesNotContain(deltas, delta =>
            delta.Operation == "rumorSpread"
            && Equals(delta.Details["sourceId"], "saturated_rumor"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "rumorSpread"
            && delta.Target == "rumor_2"
            && Equals(delta.Details["sourceId"], "eligible_rumor"));
        var eligible = Assert.Single(state.Rumors.Records, rumor => rumor.SourceId == "eligible_rumor");
        Assert.Contains(regionCarrier, eligible.CarrierIds);
    }

    [Fact]
    public void RumorPropagationDecaysOldRumorsThroughSharedUpdate()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Rumors.Append(
            state.Turn,
            "test",
            "old_rumor",
            state.RegionId,
            state.RegionId,
            "An old rumor is down to its last useful telling.",
            salience: 3,
            carrierIds: new[] { "distant_source" },
            tags: new[] { "rumor", "old" },
            hops: 2);

        var deltas = RumorSystem.Propagate(
            state,
            "test",
            maxRumors: 1,
            maxCarriersPerRumor: 1,
            announce: false,
            applyConsequence: session.Engine.ApplyConsequence);

        Assert.Contains(deltas, delta =>
            delta.Operation == "rumorSpread"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor)
            && Equals(delta.Details["sourceId"], "old_rumor")
            && Equals(delta.Details["salienceBefore"], 3)
            && Equals(delta.Details["salienceAfter"], 2)
            && Equals(delta.Details["statusAfter"], "stale"));
        var rumor = Assert.Single(state.Rumors.Records, rumor => rumor.SourceId == "old_rumor");
        Assert.Equal(2, rumor.Salience);
        Assert.Equal("stale", rumor.Status);
        Assert.Equal(3, rumor.Hops);
        Assert.Empty(RumorSystem.VisibleToPlayer(state, limit: 12));
    }

    [Fact]
    public async Task ConsumedItemActionsReturnWorldTurnDeltas()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var promise = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "rumor",
            "There is a magical sword in a burned-out oak north of here.",
            playerVisible: true,
            salience: 4,
            subject: "magical sword",
            triggerHint: "travel",
            realizationKind: "item",
            operation: "testItemActionPromise",
            emitMessage: false));
        var rumor = session.Engine.ApplyConsequence(WorldConsequence.RecordRumor(
            "test",
            "claim",
            "item_action_rumor_source",
            state.RegionId,
            state.RegionId,
            "A road rumor is looking for one more carrier.",
            4,
            carrierIds: new[] { "item_action_source_carrier" },
            tags: new[] { "rumor", "test" },
            operation: "testItemActionRumor"));

        var result = await session.ExecuteAsync(new EquipCommand("charcoal wand"));

        Assert.True(promise.Applied);
        Assert.True(rumor.Applied);
        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Contains(result.Messages, message => message.StartsWith("A rumor reaches", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Messages, message => message.StartsWith("A lead tugs", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "equip"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateEquipment));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "rumor_spread")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "promise_stir")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task TravelMessagesDoNotPersistHiddenWorldTurnAuditWrappers()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Rumors.Append(
            state.Turn,
            "test",
            "travel_audit_wrapper_rumor",
            state.RegionId,
            state.RegionId,
            "A road rumor wants to cross the hill.",
            salience: 4,
            carrierIds: new[] { "travel_source_carrier" },
            tags: new[] { "rumor", "test" });

        var result = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.True(result.Success);
        Assert.Equal(1, result.Messages.Count(message => message.StartsWith("A rumor reaches", StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(1, state.Messages.Count(message => message.StartsWith("A rumor reaches", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "rumorSpread"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor)
            && Equals(delta.Details["visibility"], WorldConsequenceVisibility.Hidden)
            && !delta.IsPlayerVisible());
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "rumor_spread")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public void BoundedWorldTurnAuditsFactionPressureAndSpendsBudget()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Factions.AdjustResource("empire", "heat", 4);
        state.Rumors.Append(
            state.Turn,
            "test",
            "budget_rumor",
            "hollowmere_margin",
            "hollowmere_margin",
            "A budget rumor waits behind imperial pressure.",
            salience: 4,
            carrierIds: new[] { "budget_source" },
            tags: new[] { "rumor", "test" });
        var patrolsBefore = state.Factions.ResourceValue("empire", "patrols");

        var deltas = new WorldTurnSystem().Apply(state, "test", budget: 1, announce: false);

        Assert.Single(deltas, delta => delta.Operation == "worldTurn");
        Assert.Contains(deltas, delta =>
            delta.Operation == "scheduleEvent"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ScheduleEvent)
            && Equals(delta.Details["eventType"], "empire_patrol")
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionResource)
            && Equals(delta.Details["resource"], "patrols")
            && Equals(delta.Details["delta"], -1));
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "faction_pressure")
            && Equals(delta.Details["response"], "empire_patrol"));
        Assert.Equal(patrolsBefore - 1, state.Factions.ResourceValue("empire", "patrols"));
        Assert.Contains(state.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
        Assert.DoesNotContain(state.WorldTurns.Records, record => record.Kind == "rumor_spread");
    }

    [Fact]
    public void FactionPressureRollsBackWhenAChildConsequenceRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Factions.AdjustResource("empire", "heat", 4);
        var patrolsBefore = state.Factions.ResourceValue("empire", "patrols");
        var heatBefore = state.Factions.ResourceValue("empire", "heat");
        var cooldownBefore = state.Factions.ResourceValue("empire", "response_cooldown_until");

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            applyConsequence: consequence =>
            {
                if (consequence.Type.Equals(WorldConsequenceTypes.ScheduleEvent, StringComparison.OrdinalIgnoreCase))
                {
                    var details = new Dictionary<string, object?>
                    {
                        ["consequenceType"] = consequence.Type,
                        ["error"] = "forced schedule rejection",
                    };
                    return new WorldConsequenceApplyResult(
                        false,
                        consequence.TargetEntityId,
                        "forced schedule rejection",
                        Array.Empty<string>(),
                        new[]
                        {
                            new StateDelta(
                                "worldConsequenceRejected",
                                consequence.TargetEntityId ?? consequence.Source,
                                "forced schedule rejection",
                                details),
                        },
                        details);
                }

                return session.Engine.ApplyConsequence(consequence);
            });

        Assert.Equal(patrolsBefore, state.Factions.ResourceValue("empire", "patrols"));
        Assert.Equal(heatBefore, state.Factions.ResourceValue("empire", "heat"));
        Assert.Equal(cooldownBefore, state.Factions.ResourceValue("empire", "response_cooldown_until"));
        Assert.DoesNotContain(state.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
        Assert.DoesNotContain(state.WorldTurns.Records, record => record.Kind == "faction_pressure");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "adjustFactionResource");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "scheduleEvent");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ScheduleEvent));
        AssertWorldTurnSkipped(deltas, "faction_pressure", "empire");
    }

    [Fact]
    public void FactionPressureRollsBackWhenWorldTurnAuditRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Factions.AdjustResource("empire", "heat", 4);
        var patrolsBefore = state.Factions.ResourceValue("empire", "patrols");
        var heatBefore = state.Factions.ResourceValue("empire", "heat");
        var cooldownBefore = state.Factions.ResourceValue("empire", "response_cooldown_until");

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            applyConsequence: consequence =>
                consequence.Type.Equals(WorldConsequenceTypes.RecordWorldTurn, StringComparison.OrdinalIgnoreCase)
                    ? RejectConsequence(consequence, "forced world-turn audit rejection")
                    : session.Engine.ApplyConsequence(consequence));

        Assert.Equal(patrolsBefore, state.Factions.ResourceValue("empire", "patrols"));
        Assert.Equal(heatBefore, state.Factions.ResourceValue("empire", "heat"));
        Assert.Equal(cooldownBefore, state.Factions.ResourceValue("empire", "response_cooldown_until"));
        Assert.DoesNotContain(state.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
        Assert.DoesNotContain(state.WorldTurns.Records, record => record.Kind == "faction_pressure");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "adjustFactionResource");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "scheduleEvent");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn));
        AssertWorldTurnSkipped(deltas, "faction_pressure", "empire");
    }

    [Fact]
    public void RumorSpreadRollsBackWhenWorldTurnAuditRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.PromiseLedger.ReplaceAll(Array.Empty<WorldPromise>());
        foreach (var entity in state.Entities.Values)
        {
            if (entity.Has<WantComponent>())
            {
                var applied = session.Engine.ApplyConsequence(WorldConsequence.UpdateWant(
                    "test_setup",
                    entity.Id.Value,
                    status: "satisfied",
                    operation: "testSilenceWant"));
                Assert.True(applied.Applied, applied.Error ?? $"Want for {entity.Id.Value} was not silenced.");
            }
        }

        var rumor = state.Rumors.Append(
            state.Turn,
            "test",
            "audit_reject_rumor",
            state.RegionId,
            state.RegionId,
            "A rumor should vanish back into the ledger if its world-turn audit fails.",
            salience: 4,
            carrierIds: new[] { "remote_carrier" },
            tags: new[] { "rumor", "test" });

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            applyConsequence: consequence =>
                consequence.Type.Equals(WorldConsequenceTypes.RecordWorldTurn, StringComparison.OrdinalIgnoreCase)
                    ? RejectConsequence(consequence, "forced world-turn audit rejection")
                    : session.Engine.ApplyConsequence(consequence));

        var restored = Assert.Single(state.Rumors.Records, item => item.Id == rumor.Id);
        Assert.Equal(rumor.LastTurn, restored.LastTurn);
        Assert.Equal(rumor.Hops, restored.Hops);
        Assert.Equal(rumor.Salience, restored.Salience);
        Assert.Equal(rumor.CarrierIds, restored.CarrierIds);
        Assert.Empty(restored.DistortionHistory);
        Assert.DoesNotContain(state.WorldTurns.Records, record => record.Kind == "rumor_spread");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "rumorSpread");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "rumorHeardMemory");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn));
        AssertWorldTurnSkipped(deltas, "rumor_spread", "rumor");
    }

    [Fact]
    public void WantStirRollsBackMemoryWhenWorldTurnAuditRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.PromiseLedger.ReplaceAll(Array.Empty<WorldPromise>());
        state.Rumors.ReplaceAll(Array.Empty<RumorRecord>());
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            var entity = session.Engine.EntityById(id)!;
            session.Engine.ApplyConsequence(WorldConsequence.UpdateWant(
                "test_setup",
                entity.Id.Value,
                status: "satisfied",
                operation: "testSilenceWant"));
        }

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            applyConsequence: consequence =>
                consequence.Type.Equals(WorldConsequenceTypes.RecordWorldTurn, StringComparison.OrdinalIgnoreCase)
                    ? RejectConsequence(consequence, "forced world-turn audit rejection")
                    : session.Engine.ApplyConsequence(consequence));

        Assert.DoesNotContain(state.Memories.Records, memory =>
            memory.SubjectId == "prisoner_1"
            && memory.Provenance == "want:want_lio_escape");
        Assert.DoesNotContain(state.WorldTurns.Records, record => record.Kind == "want_stir");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "wantStirMemory");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn));
        AssertWorldTurnSkipped(deltas, "want_stir", "want_lio_escape");
    }

    [Fact]
    public void FactionRecoveryRollsBackResourcesWhenWorldTurnAuditRejects()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Factions.AdjustResource("empire", "patrols", -1);
        state.Factions.AdjustResource("empire", "heat", 1);
        var patrolsBefore = state.Factions.ResourceValue("empire", "patrols");
        var heatBefore = state.Factions.ResourceValue("empire", "heat");

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            allowFactionRecovery: true,
            applyConsequence: consequence =>
                consequence.Type.Equals(WorldConsequenceTypes.RecordWorldTurn, StringComparison.OrdinalIgnoreCase)
                    ? RejectConsequence(consequence, "forced world-turn audit rejection")
                    : session.Engine.ApplyConsequence(consequence));

        Assert.Equal(patrolsBefore, state.Factions.ResourceValue("empire", "patrols"));
        Assert.Equal(heatBefore, state.Factions.ResourceValue("empire", "heat"));
        Assert.DoesNotContain(state.WorldTurns.Records, record => record.Kind == "faction_recovery");
        Assert.DoesNotContain(deltas, delta => delta.Operation == "adjustFactionResource");
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn));
        AssertWorldTurnSkipped(deltas, "faction_recovery", "empire");
    }

    [Fact]
    public void BoundedWorldTurnUsesProvidedConsequenceSink()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Factions.AdjustResource("empire", "heat", 4);
        var submittedTypes = new List<string>();

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            applyConsequence: consequence =>
            {
                submittedTypes.Add(consequence.Type);
                return WorldConsequenceGuard.ApplyWithNewApplier(state, consequence);
            });

        Assert.Contains(WorldConsequenceTypes.AdjustFactionResource, submittedTypes);
        Assert.Contains(WorldConsequenceTypes.ScheduleEvent, submittedTypes);
        Assert.Contains(WorldConsequenceTypes.RecordWorldTurn, submittedTypes);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "faction_pressure"));
    }

    [Fact]
    public void BoundedWorldTurnAuditsQuietFactionRecovery()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        state.Factions.AdjustResource("empire", "patrols", -1);
        state.Factions.AdjustResource("empire", "heat", 1);
        var patrolsBefore = state.Factions.ResourceValue("empire", "patrols");

        var deltas = new WorldTurnSystem().Apply(
            state,
            "test",
            budget: 1,
            announce: false,
            allowFactionRecovery: true,
            applyConsequence: session.Engine.ApplyConsequence);

        Assert.Equal(patrolsBefore + 1, state.Factions.ResourceValue("empire", "patrols"));
        Assert.Equal(0, state.Factions.ResourceValue("empire", "heat"));
        Assert.Contains(deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AdjustFactionResource)
            && Equals(delta.Details["resource"], "patrols")
            && Equals(delta.Details["delta"], 1)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "adjustFactionResource"
            && Equals(delta.Details["resource"], "heat")
            && Equals(delta.Details["delta"], -1)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["kind"], "faction_recovery")
            && Equals(delta.Details["sourceId"], "empire")
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(state.WorldTurns.Records, record =>
            record.Kind == "faction_recovery"
            && record.SourceId == "empire"
            && record.Details.TryGetValue("adjustments", out var rawAdjustments)
            && rawAdjustments is IEnumerable<string> adjustments
            && adjustments.Contains("heat:-1")
            && adjustments.Contains("patrols:1"));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("recovers", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DialogueClaimExtractionCanInstantlyAddExistingMerchantStock()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Lio can sell you a fine blade.",
            "merchant_stock",
            "fine blade",
            Salience: 3,
            Confidence: 80,
            PlayerVisible: true,
            MerchantId: "prisoner_1",
            ItemName: "fine blade",
            Tags: new[] { "merchant", "blade" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Set(new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), Gold: 10));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, what can you sell me?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimMerchantStock");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimApplied"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateClaim)
            && Equals(delta.Details["status"], "applied"));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimApplicationSkipped");
        Assert.True(lio.Get<MerchantComponent>().Wares.TryGetValue("fine blade", out var count));
        Assert.Equal(1, count);
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "fine blade" && claim.Status == "applied");
    }

    [Fact]
    public void SharedWorldConsequencesApplyMemoryBondAndStock()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Set(new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), Gold: 10));

        var memory = session.Engine.ApplyConsequence(WorldConsequence.RecordMemory(
            "test",
            "prisoner_1",
            "Lio remembers the gift and weighs the sorcerer differently.",
            "test",
            3,
            shareable: true,
            operation: "testMemory"));
        var bond = session.Engine.ApplyConsequence(WorldConsequence.UpdateBond(
            "test",
            "prisoner_1",
            "player_soul",
            loyaltyDelta: 9,
            fearDelta: -9,
            admirationDelta: 4,
            resentmentDelta: 0,
            posture: "moved",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testBond",
            maxDelta: 2));
        var stock = session.Engine.ApplyConsequence(WorldConsequence.AddMerchantStock(
            "test",
            "prisoner_1",
            "fine blade",
            visibility: WorldConsequenceVisibility.Message,
            operation: "testStock"));

        Assert.True(memory.Applied);
        Assert.Contains(memory.Deltas, delta => delta.Operation == "testMemory");
        Assert.Contains(lio.Get<MemoryComponent>().Records, record =>
            record.Text.Contains("gift", StringComparison.OrdinalIgnoreCase));
        Assert.True(bond.Applied);
        Assert.Contains(bond.Deltas, delta => delta.Operation == "testBond");
        Assert.True(session.Engine.State.Bonds.TryGet("lio_soul", "player_soul", out var bondRecord));
        Assert.Equal(2, bondRecord.Loyalty);
        Assert.Equal(-2, bondRecord.Fear);
        Assert.Equal(2, bondRecord.Admiration);
        Assert.True(stock.Applied);
        Assert.Contains(stock.Deltas, delta => delta.Operation == "testStock");
        Assert.True(lio.Get<MerchantComponent>().Wares.TryGetValue("fine blade", out var count));
        Assert.Equal(1, count);
    }

    [Fact]
    public void SharedWorldConsequencesRecordRumorsAndSkipDuplicateSources()
    {
        var session = CreateMockSession();

        var rumor = session.Engine.ApplyConsequence(WorldConsequence.RecordRumor(
            "test",
            "claim",
            "claim_test_1",
            session.Engine.State.RegionId,
            session.Engine.State.RegionId,
            "Lio says the wet road remembers debts.",
            4,
            carrierIds: new[] { "lio_soul", "player_soul" },
            tags: new[] { "rumor", "claim", "road" },
            operation: "testRumor"));
        var duplicate = session.Engine.ApplyConsequence(WorldConsequence.RecordRumor(
            "test",
            "claim",
            "claim_test_1",
            session.Engine.State.RegionId,
            session.Engine.State.RegionId,
            "A duplicate version should not enter the ledger.",
            5,
            operation: "testRumor"));

        Assert.True(rumor.Applied);
        var delta = Assert.Single(rumor.Deltas, item => item.Operation == "testRumor");
        Assert.Equal(WorldConsequenceTypes.RecordRumor, delta.Details["consequenceType"]);
        var record = Assert.Single(session.Engine.State.Rumors.Records, item => item.SourceId == "claim_test_1");
        Assert.Equal("claim", record.SourceKind);
        Assert.Equal(4, record.Salience);
        Assert.Contains("player_soul", record.CarrierIds);
        var update = session.Engine.ApplyConsequence(WorldConsequence.UpdateRumor(
            "test",
            record.Id,
            currentRegionId: "vigovian_capital",
            addCarrierIds: new[] { "capital_listener" },
            addTags: new[] { "capital" },
            appendDistortionHistory: new[] { "The road rumor reached a capital listener." },
            incrementHops: true,
            operation: "testRumorUpdate"));

        Assert.True(update.Applied);
        var updateDelta = Assert.Single(update.Deltas, item => item.Operation == "testRumorUpdate");
        Assert.Equal(WorldConsequenceTypes.UpdateRumor, updateDelta.Details["consequenceType"]);
        var updated = Assert.Single(session.Engine.State.Rumors.Records, item => item.SourceId == "claim_test_1");
        Assert.Equal("vigovian_capital", updated.CurrentRegionId);
        Assert.Contains("capital_listener", updated.CarrierIds);
        Assert.Contains("capital", updated.Tags);
        Assert.Contains(updated.DistortionHistory, line => line.Contains("capital listener", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, updated.Hops);
        Assert.False(duplicate.Applied);
        Assert.Equal("duplicate_rumor_source", duplicate.Error);
        Assert.Single(session.Engine.State.Rumors.Records, item => item.SourceId == "claim_test_1");
    }

    [Fact]
    public async Task RumorDistortionRunsAsBackgroundUpdateRumorConsequence()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var state = session.Engine.State;
        state.Rumors.Append(
            state.Turn,
            "test",
            "distortion_source",
            state.RegionId,
            state.RegionId,
            "South of here, there is a town called Hollowmere.",
            salience: 4,
            carrierIds: new[] { $"region:{state.RegionId}", "prisoner_1_soul", "soldier_1_soul", "soldier_2_soul" },
            tags: new[] { "rumor", "claim", "town" },
            hops: 2);

        var wait = await session.ExecuteAsync(new WaitCommand());
        var distorted = Assert.Single(state.Rumors.Records, rumor => rumor.SourceId == "distortion_source");

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "queueBackgroundJob"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.QueueBackgroundJob)
            && Equals(delta.Details["purpose"], "rumor_distortion")
            && Equals(delta.Details["targetKind"], "rumor")
            && Equals(delta.Details["targetId"], distorted.Id)
            && Equals(delta.Details["rumorId"], distorted.Id)
            && Equals(delta.Details["queued"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundRumorDistortion"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRumor)
            && Equals(delta.Details["visibility"], WorldConsequenceVisibility.Hidden));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "backgroundJobApplied"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBackgroundJob)
            && Equals(delta.Details["purpose"], "rumor_distortion")
            && Equals(delta.Details["targetId"], distorted.Id)
            && Equals(delta.Details["state"], "Applied")
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn)
            && Equals(delta.Details["kind"], "background_job_queued")
            && Equals(delta.Details["rumorId"], distorted.Id)
            && Equals(delta.Details["purpose"], "rumor_distortion")
            && Equals(delta.Details["queued"], true)
            && Equals(delta.Details["auditOnly"], true));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "worldTurn"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordWorldTurn)
            && Equals(delta.Details["kind"], "background_rumor_distortion")
            && Equals(delta.Details["rumorId"], distorted.Id)
            && Equals(delta.Details["purpose"], "rumor_distortion")
            && Equals(delta.Details["auditOnly"], true));
        Assert.Contains(state.WorldTurns.Records, record =>
            record.Kind == "background_job_queued"
            && record.SourceId == distorted.Id);
        Assert.Contains(state.WorldTurns.Records, record =>
            record.Kind == "background_rumor_distortion"
            && record.SourceId == distorted.Id);
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("changes in retelling", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Messages, message =>
            message.StartsWith("Background job", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("Somewhere south of here", distorted.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"region:{state.RegionId}", distorted.CarrierIds);
        Assert.Contains("town", distorted.Tags);
        Assert.Contains("distorted", distorted.Tags);
        Assert.Contains(distorted.DistortionHistory, entry =>
            entry.StartsWith("distortion:", StringComparison.OrdinalIgnoreCase));
        var visibleRumor = Assert.Single(session.View().Rumors!, rumor => rumor.Id == distorted.Id);
        Assert.Equal("active", visibleRumor.Status);
        Assert.Equal("South of here, there is a town called Hollowmere.", visibleRumor.OriginalText);
        Assert.Contains(visibleRumor.DistortionHistory, entry =>
            entry.StartsWith("distortion:", StringComparison.OrdinalIgnoreCase));
        var debug = session.Observation(debug: true).Debug!;
        Assert.Contains(debug.BackgroundJobs!, job =>
            job.Purpose == "rumor_distortion"
            && job.TargetId == distorted.Id
            && job.State == "Applied");
        Assert.Contains(debug.Rumors!, rumor =>
            rumor.Id == distorted.Id
            && rumor.Status == "active"
            && rumor.CarrierIds.Contains($"region:{state.RegionId}")
            && rumor.DistortionHistory.Any(entry => entry.StartsWith("distortion:", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task ScheduledEventMessagesUseSharedMessageConsequenceWhenDue()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "moth_arrival",
            1,
            new Dictionary<string, object?>
            {
                ["text"] = "A moth arrives with ash on its feet.",
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(scheduled.Applied);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateScheduledEvent)
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["eventType"], "moth_arrival")
            && Equals(delta.Details["action"], "due"));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledEventMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["scheduledEventId"], scheduled.TargetId)
            && Equals(delta.Details["eventType"], "moth_arrival"));
        Assert.DoesNotContain(wait.Deltas, delta =>
            delta.Operation == "turnEvent"
            && delta.Summary.Contains("moth arrives", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wait.Messages, message => message.Contains("moth arrives", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HiddenScheduledEventMessageDoesNotWritePlayerMessageWhenDue()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "hidden_moth_arrival",
            1,
            new Dictionary<string, object?>
            {
                ["text"] = "A hidden moth arrives with ash on its feet.",
                ["playerVisible"] = false,
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(scheduled.Applied);
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Id == scheduled.TargetId);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledEventMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["scheduledEventId"], scheduled.TargetId)
            && Equals(delta.Details["eventType"], "hidden_moth_arrival")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("hidden moth", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("hidden moth", StringComparison.OrdinalIgnoreCase));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task ScheduledEventCanDeliverTypedConsequenceWhenDue()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "delayed_tag",
            1,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "addTags",
                ["targetEntityId"] = "brazier_1",
                ["operation"] = "scheduledTag",
                ["tags"] = new[] { "delayed_blossom" },
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(scheduled.Applied);
        Assert.Contains(session.Engine.EntityById("brazier_1")!.Get<TagsComponent>().Tags, tag => tag == "delayed_blossom");
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Id == scheduled.TargetId);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledTag"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Target, "brazier_1"));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["action"], "due"));
    }

    [Fact]
    public async Task ScheduledConsequenceMergesTopLevelFieldsIntoNestedPayload()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "delayed_status",
            1,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "apply_status",
                ["targetEntityId"] = "brazier_1",
                ["status"] = "scheduled-blue",
                ["duration"] = 2,
                ["displayName"] = "scheduled blue",
                ["consequencePayload"] = new Dictionary<string, object?>
                {
                    ["operation"] = "scheduledTopLevelStatus",
                },
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());
        var brazier = session.Engine.EntityById("brazier_1")!;

        Assert.True(scheduled.Applied);
        Assert.Contains(brazier.Get<StatusContainerComponent>().Statuses, status =>
            status.Id.Equals("scheduled_blue", StringComparison.OrdinalIgnoreCase)
            && status.DisplayName.Equals("scheduled blue", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Id == scheduled.TargetId);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledTopLevelStatus"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Target, "brazier_1")
            && Equals(delta.Details["status"], "scheduled_blue")
            && Equals(delta.Details["duration"], 2));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["action"], "due"));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task RejectedScheduledConsequenceRollsBackAndExpiresEvent()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "bad_delayed_tag",
            1,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "addTags",
                ["targetEntityId"] = "missing_fixture",
                ["operation"] = "scheduledMissingTag",
                ["tags"] = new[] { "impossible" },
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(scheduled.Applied);
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Id == scheduled.TargetId);
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "scheduledMissingTag");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, "missing_fixture")
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledEventSkipped"
            && Equals(delta.Target, scheduled.TargetId)
            && Equals(delta.Details["eventType"], "bad_delayed_tag")
            && Equals(delta.Details["scheduledConsequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["action"], "expire"));
    }

    [Fact]
    public async Task MalformedGenericScheduledConsequenceSkipsAsHiddenAuditAndExpires()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "nameless_scheduled_consequence",
            1,
            new Dictionary<string, object?>
            {
                ["effectType"] = "consequence",
                ["targetEntityId"] = "brazier_1",
                ["operation"] = "scheduledMissingConsequenceType",
                ["tags"] = new[] { "should_not_land" },
                ["playerVisible"] = true,
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());
        var secondWait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(scheduled.Applied);
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Id == scheduled.TargetId);
        Assert.DoesNotContain(session.Engine.EntityById("brazier_1")!.Get<TagsComponent>().Tags, tag =>
            tag.Equals("should_not_land", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "scheduledMissingConsequenceType");
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "scheduledEventMessage");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, scheduled.TargetId)
            && Equals(delta.Details["consequenceType"], "scheduled_event_consequence")
            && Equals(delta.Details["effectType"], "consequence")
            && ((string)delta.Details["error"]!).Contains("consequenceType", StringComparison.OrdinalIgnoreCase)
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledEventSkipped"
            && Equals(delta.Target, scheduled.TargetId)
            && Equals(delta.Details["eventType"], "nameless_scheduled_consequence")
            && Equals(delta.Details["scheduledConsequenceType"], "scheduled_event_consequence")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["action"], "expire")
            && !delta.IsPlayerVisible());
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("nameless", StringComparison.OrdinalIgnoreCase)
            || message.Contains("scheduled consequence", StringComparison.OrdinalIgnoreCase)
            || message.Contains("consequenceType", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(secondWait.Deltas, delta =>
            delta.Operation == "scheduledEventSkipped"
            && Equals(delta.Target, scheduled.TargetId));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task ScheduledSilentNonApplyConsequenceRollsBackAndExpiresEvent()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var player = session.Engine.State.ControlledEntity;
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Set(new PositionComponent(new GridPoint(1, 1)));
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "remote_ward_breaking",
            "remote ward-breaking",
            "Break a lock, but only if one is close enough to touch.",
            "open_or_unlock",
            targetHint: "no nearby door",
            revealed: true,
            operation: "testRemoteWardService"));
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "bad_delayed_service",
            1,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "request_service",
                ["targetEntityId"] = lio.Id.Value,
                ["providerId"] = lio.Id.Value,
                ["service"] = "remote ward-breaking",
                ["actorEntityId"] = player.Id.Value,
                ["operation"] = "scheduledRemoteService",
            }));

        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(offer.Applied, offer.Error);
        Assert.True(scheduled.Applied, scheduled.Error);
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Id == scheduled.TargetId);
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "scheduledRemoteService");
        Assert.DoesNotContain(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["action"], "due"));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Target, lio.Id.Value)
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "scheduledEventSkipped"
            && Equals(delta.Target, scheduled.TargetId)
            && Equals(delta.Details["eventType"], "bad_delayed_service")
            && Equals(delta.Details["scheduledConsequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "updateScheduledEvent"
            && Equals(delta.Details["eventId"], scheduled.TargetId)
            && Equals(delta.Details["action"], "expire"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public void SharedWorldConsequencesRecordClaims()
    {
        var session = CreateMockSession();

        var claim = session.Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            "test_dialogue",
            "prisoner_1",
            "player_soul",
            "Lio says a red bridge waits below the cistern.",
            "site",
            "red bridge",
            salience: 8,
            confidence: 120,
            playerVisible: true,
            tags: new[] { "dialogue", "site", "dialogue" },
            operation: "testClaim"));

        Assert.True(claim.Applied);
        Assert.Equal("claim_1", claim.TargetId);
        var delta = Assert.Single(claim.Deltas, item => item.Operation == "testClaim");
        Assert.Equal(WorldConsequenceTypes.RecordClaim, delta.Details["consequenceType"]);
        Assert.Equal("claim_1", delta.Details["claimId"]);
        var record = Assert.Single(session.Engine.State.Claims.Records);
        Assert.Equal("prisoner_1", record.SpeakerId);
        Assert.Equal("player_soul", record.ListenerSoulId);
        Assert.Equal("site", record.Category);
        Assert.Equal("red bridge", record.Subject);
        Assert.Equal(5, record.Salience);
        Assert.Equal(100, record.Confidence);
        Assert.True(record.PlayerVisible);
        Assert.Contains("dialogue", record.Tags);
        Assert.Contains("site", record.Tags);

        var duplicate = session.Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            "test_dialogue",
            "prisoner_1",
            "player_soul",
            "Lio says a red bridge waits below the cistern!",
            "site",
            "red bridge",
            salience: 3,
            confidence: 50,
            playerVisible: true,
            tags: new[] { "dialogue", "site" },
            operation: "testClaim"));

        Assert.True(duplicate.Applied);
        Assert.Equal(record.Id, duplicate.TargetId);
        Assert.Single(session.Engine.State.Claims.Records);
        Assert.Contains(duplicate.Deltas, delta =>
            delta.Operation == "claimDuplicate"
            && Equals(delta.Details["claimId"], record.Id)
            && Equals(delta.Details["duplicate"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public void SharedTypedConsequencesApplyImmediateTacticalEffects()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpBefore = soldier.Get<ActorComponent>().HitPoints;

        var damage = session.Engine.ApplyConsequence(WorldConsequence.Damage(
            "test",
            soldier.Id.Value,
            amount: 10,
            damageType: "fire",
            sourceEntityId: "player"));
        var heal = session.Engine.ApplyConsequence(WorldConsequence.Heal(
            "test",
            soldier.Id.Value,
            amount: 2,
            sourceEntityId: "player"));
        var restoreMana = session.Engine.ApplyConsequence(WorldConsequence.RestoreMana(
            "test",
            player.Id.Value,
            amount: 1,
            sourceEntityId: "player"));
        var move = session.Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "test",
            soldier.Id.Value,
            8,
            5,
            operation: "push",
            sourceEntityId: "player"));
        var terrain = session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test",
            4,
            4,
            "blue_moss",
            duration: 2,
            sourceEntityId: "player"));
        var applyStatus = session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test",
            soldier.Id.Value,
            "webbed",
            duration: 3,
            sourceEntityId: "player"));
        var summon = session.Engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "test",
            "glass helper",
            4,
            6,
            prefix: "glass_helper",
            faction: "player",
            sourceEntityId: "player",
            wantText: "Protect the caster until the glass cracks.",
            wantTags: new[] { "summoned", "guardian" }));
        var item = session.Engine.ApplyConsequence(WorldConsequence.SpawnItem(
            "test",
            "red glass knife",
            5,
            6,
            prefix: "red_glass_knife",
            itemType: "weapon",
            material: "glass",
            tags: new[] { "weapon", "knife" },
            sourceEntityId: "player"));
        var fixture = session.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            "test",
            "moon shrine",
            6,
            6,
            prefix: "moon_shrine",
            fixtureType: "shrine",
            material: "stone",
            tags: new[] { "shrine", "moon" },
            interactableVerbs: new[] { "examine" },
            sourceEntityId: "player",
            operation: "testSpawnFixture",
            emitMessage: false));
        var manaBefore = player.Get<ActorComponent>().Mana;
        var resource = session.Engine.ApplyConsequence(WorldConsequence.AdjustActorResource(
            "test",
            player.Id.Value,
            "mana",
            -2,
            min: 0,
            sourceEntityId: "player",
            operation: "test:manaCost",
            message: "The test spends two mana."));
        var promise = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "omen",
            "A red bell will answer when the door opens.",
            triggerHint: "open",
            sourceEntityId: "player",
            sourceClaimId: "claim_unit_1",
            sourceSpeakerId: "notice_1",
            sourceListenerSoulId: "player_soul",
            sourceConfidence: 77));

        Assert.True(damage.Applied);
        Assert.Equal(WorldConsequenceTypes.Damage, damage.Details["consequenceType"]);
        Assert.Equal(WorldConsequenceTiming.Immediate, damage.Details["timing"]);
        var damageDelta = damage.Deltas.Single(delta => delta.Operation == "damage");
        Assert.Equal(WorldConsequenceTypes.Damage, damageDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == damageDelta.Summary);
        Assert.True(soldier.Get<ActorComponent>().HitPoints < hpBefore);
        Assert.True(heal.Applied);
        var healDelta = heal.Deltas.Single(delta => delta.Operation == "heal");
        Assert.Equal(WorldConsequenceTypes.Heal, healDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == healDelta.Summary);
        Assert.True(restoreMana.Applied);
        var restoreManaDelta = restoreMana.Deltas.Single(delta => delta.Operation == "restoreMana");
        Assert.Equal(WorldConsequenceTypes.RestoreMana, restoreManaDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == restoreManaDelta.Summary);
        Assert.True(move.Applied);
        Assert.Equal(new GridPoint(8, 5), soldier.Get<PositionComponent>().Position);
        Assert.Contains(move.Deltas, delta => delta.Operation == "push");
        var moveDelta = move.Deltas.Single(delta => delta.Operation == "push");
        Assert.Equal(WorldConsequenceTypes.MoveEntity, moveDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == moveDelta.Summary);
        Assert.True(terrain.Applied);
        Assert.Equal("blue_moss", session.Engine.State.Terrain[new GridPoint(4, 4)]);
        Assert.Equal(session.Engine.State.Turn + 2, session.Engine.State.TerrainExpirations[new GridPoint(4, 4)]);
        var terrainDelta = terrain.Deltas.Single(delta => delta.Operation == "createTile");
        Assert.Equal(WorldConsequenceTypes.SetTerrain, terrainDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == terrainDelta.Summary);
        Assert.True(applyStatus.Applied);
        Assert.Contains(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "rooted");
        var applyStatusDelta = applyStatus.Deltas.Single(delta => delta.Operation == "addStatus");
        Assert.Equal(WorldConsequenceTypes.ApplyStatus, applyStatusDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == applyStatusDelta.Summary);
        var removeStatus = session.Engine.ApplyConsequence(WorldConsequence.RemoveStatus(
            "test",
            soldier.Id.Value,
            "webbed",
            sourceEntityId: "player"));
        Assert.True(removeStatus.Applied);
        Assert.DoesNotContain(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "rooted");
        var removeStatusDelta = removeStatus.Deltas.Single(delta => delta.Operation == "removeStatus");
        Assert.Equal(WorldConsequenceTypes.RemoveStatus, removeStatusDelta.Details["consequenceType"]);
        Assert.Contains(session.Engine.State.Messages, message => message == removeStatusDelta.Summary);
        Assert.True(summon.Applied);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "glass helper"
            && entity.TryGet<SummonedComponent>(out var summoned)
            && summoned.Source == "player"
            && entity.TryGet<WantComponent>(out var summonWant)
            && summonWant.Text.Contains("Protect the caster", StringComparison.OrdinalIgnoreCase));
        Assert.True(item.Applied);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "red glass knife"
            && entity.TryGet<ItemComponent>(out var itemComponent)
            && itemComponent.ItemType == "weapon");
        Assert.True(fixture.Applied);
        Assert.Contains(fixture.Deltas, delta =>
            delta.Operation == "testSpawnFixture"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "moon shrine"
            && entity.TryGet<FixtureComponent>(out var fixtureComponent)
            && fixtureComponent.FixtureType == "shrine"
            && entity.TryGet<InteractableComponent>(out var interactable)
            && interactable.Verbs.Contains("examine"));
        Assert.True(resource.Applied);
        Assert.Contains(resource.Deltas, delta => delta.Operation == "test:manaCost");
        Assert.Equal(manaBefore - 2, player.Get<ActorComponent>().Mana);
        Assert.Equal(player.Get<ActorComponent>().Mana, session.Engine.State.Souls.Get("player_soul").Mana);
        Assert.True(promise.Applied);
        Assert.Contains(session.Engine.State.PromiseLedger.Promises, record =>
            record.Text.Contains("red bell", StringComparison.OrdinalIgnoreCase)
            && record.SourceClaimId == "claim_unit_1"
            && record.SourceSpeakerId == "notice_1"
            && record.SourceListenerSoulId == "player_soul"
            && record.SourceConfidence == 77);
        Assert.Contains(promise.Deltas, delta =>
            delta.Operation == "createPromise"
            && Equals(delta.Details["sourceClaimId"], "claim_unit_1")
            && Equals(delta.Details["sourceSpeakerId"], "notice_1")
            && Equals(delta.Details["sourceListenerSoulId"], "player_soul")
            && Equals(delta.Details["sourceConfidence"], 77));
        var notice = session.Engine.EntityById("notice_1")!;
        var promiseId = promise.TargetId ?? promise.Deltas.First().Target;
        var promiseUpdate = session.Engine.ApplyConsequence(WorldConsequence.UpdatePromise(
            "test",
            promiseId,
            status: "bound",
            boundTargetId: notice.Id.Value,
            triggerHint: "read",
            realizationKind: "memory",
            sourceEntityId: "player"));
        Assert.True(promiseUpdate.Applied);
        Assert.Contains(promiseUpdate.Deltas, delta => delta.Operation == "updatePromise");
        var updatedPromise = session.Engine.State.PromiseLedger.Promises.Single(record => record.Id == promiseId);
        Assert.Equal("bound", updatedPromise.Status);
        Assert.Equal(notice.Id.Value, updatedPromise.BoundTargetId);
        Assert.Equal("read", updatedPromise.TriggerHint);
        Assert.Equal("memory", updatedPromise.RealizationKind);
        Assert.Contains(notice.Get<PromiseAnchorComponent>().PromiseIds, id => id == promiseId);

        var curseOne = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "curse",
            "Wild Debt: a red bell answers twice.",
            sourceEntityId: "player",
            operation: "addCurse",
            stackExisting: true));
        var curseTwo = session.Engine.ApplyConsequence(WorldConsequence.CreatePromise(
            "test",
            "curse",
            "Wild Debt: a red bell answers twice.",
            sourceEntityId: "player",
            operation: "addCurse",
            stackExisting: true));
        Assert.True(curseOne.Applied);
        Assert.True(curseTwo.Applied);
        Assert.Contains(curseTwo.Deltas, delta => delta.Operation == "addCurse");
        Assert.Equal(2, session.Engine.State.PromiseLedger.Promises.Single(record =>
            record.Text == "Wild Debt: a red bell answers twice.").Stacks);
    }

    [Fact]
    public void MoveEntityAllowsEntityToRemainOnItsOwnTile()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;

        var move = session.Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "test",
            player.Id.Value,
            position.X,
            position.Y,
            operation: "selfPlacement",
            emitMessage: false,
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = false,
            }));

        Assert.True(move.Applied);
        var delta = Assert.Single(move.Deltas);
        Assert.Equal("selfPlacement", delta.Operation);
        Assert.Equal(player.Id.Value, delta.Target);
        Assert.False(delta.Details.ContainsKey("blocked"));
        Assert.Equal(position, player.Get<PositionComponent>().Position);
    }

    [Fact]
    public void MoveEntityOwnsControlledMovementMetadataWhenRequested()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var start = player.Get<PositionComponent>().Position;

        var unrecorded = session.Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "test",
            player.Id.Value,
            start.X + 1,
            start.Y,
            operation: "forcedMove",
            emitMessage: false));

        Assert.True(unrecorded.Applied);
        Assert.Null(session.Engine.State.LastControlledMoveDelta);
        Assert.Contains(unrecorded.Deltas, delta =>
            delta.Operation == "forcedMove"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["recordControlledMovement"], false));

        var recorded = session.Engine.ApplyConsequence(WorldConsequence.MoveEntity(
            "test",
            player.Id.Value,
            start.X + 2,
            start.Y,
            operation: "playerLikeMove",
            emitMessage: false,
            recordControlledMovement: true));

        Assert.True(recorded.Applied);
        Assert.Equal(new GridPoint(1, 0), session.Engine.State.LastControlledMoveDelta);
        Assert.Contains(recorded.Deltas, delta =>
            delta.Operation == "playerLikeMove"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["recordControlledMovement"], true)
            && Equals(delta.Details["dx"], 1)
            && Equals(delta.Details["dy"], 0));
    }

    [Fact]
    public void GameTransactionRollbackRestoresExpandedFlywheelState()
    {
        var session = CreateMockSession();
        var state = session.Engine.State;
        var flowPoint = new GridPoint(2, 2);
        state.BackgroundSettings = new Sorcerer.Core.Runtime.BackgroundJobSettings(
            Enabled: false,
            MaxQueuedJobs: 2,
            JobsPerTurn: 0);
        state.LastControlledMoveDelta = new GridPoint(0, 1);
        state.WorldFlags["baseline"] = true;
        state.TileFlows[flowPoint] = new TileFlow(1, 0, 99);

        var transaction = GameTransaction.Begin(state);
        state.BackgroundSettings = new Sorcerer.Core.Runtime.BackgroundJobSettings(
            Enabled: true,
            MaxQueuedJobs: 9,
            JobsPerTurn: 3);
        state.LastControlledMoveDelta = new GridPoint(4, 4);
        session.Engine.ApplyConsequence(WorldConsequence.RecordClaim(
            "rollback_test",
            "prisoner_1",
            "player_soul",
            "A rollback claim should not survive.",
            "rumor",
            "rollback claim",
            salience: 4,
            confidence: 90,
            playerVisible: true));
        session.Engine.ApplyConsequence(WorldConsequence.SetWorldFlag(
            "rollback_test",
            "baseline",
            false));
        session.Engine.ApplyConsequence(WorldConsequence.SetWorldFlag(
            "rollback_test",
            "new_flag",
            true));
        session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "rollback_test",
            "player",
            "turn_start",
            "damage",
            new Dictionary<string, object?>
            {
                ["amount"] = 1,
                ["damageType"] = "arcane",
            },
            uses: 2,
            playerVisible: false));
        session.Engine.ApplyConsequence(WorldConsequence.CreateFlow(
            "rollback_test",
            3,
            3,
            radius: 0,
            dx: 1,
            dy: 0,
            duration: 5));
        session.Engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "rollback_test",
            "player",
            "entity_detail",
            priority: 1));

        transaction.Rollback();

        Assert.Equal(new Sorcerer.Core.Runtime.BackgroundJobSettings(
            Enabled: false,
            MaxQueuedJobs: 2,
            JobsPerTurn: 0), state.BackgroundSettings);
        Assert.Equal(new GridPoint(0, 1), state.LastControlledMoveDelta);
        Assert.True((bool)state.WorldFlags["baseline"]!);
        Assert.False(state.WorldFlags.ContainsKey("new_flag"));
        Assert.DoesNotContain(state.Claims.Records, claim =>
            claim.Text.Contains("rollback claim", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(state.PersistentEffects.Records);
        Assert.Empty(state.BackgroundJobs.Jobs);
        Assert.Single(state.TileFlows);
        Assert.Equal(new TileFlow(1, 0, 99), state.TileFlows[flowPoint]);
    }

    [Fact]
    public void CreateRouteCanUseExplicitCoordinatesWithoutLoadedAnchorEntity()
    {
        var session = CreateMockSession();

        var route = session.Engine.ApplyConsequence(WorldConsequence.CreateRoute(
            "test",
            "generated_zone",
            "dry culvert",
            "A culvert route exists because an off-map promise became concrete.",
            "escape_route",
            tags: new[] { "route", "test" },
            promiseIds: new[] { "promise_test_route" },
            sourceEntityId: "player",
            operation: "testRoute",
            details: new Dictionary<string, object?>
            {
                ["x"] = 3,
                ["y"] = 4,
            }));

        Assert.True(route.Applied);
        var delta = Assert.Single(route.Deltas, item => item.Operation == "testRoute");
        Assert.Equal(WorldConsequenceTypes.CreateRoute, delta.Details["consequenceType"]);
        Assert.Equal("generated_zone", delta.Details["anchorEntityId"]);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "dry culvert"
            && entity.TryGet<PositionComponent>(out var position)
            && position.Position == new GridPoint(3, 4)
            && entity.TryGet<FixtureComponent>(out var fixture)
            && fixture.FixtureType == "escape_route"
            && entity.TryGet<PromiseAnchorComponent>(out var promiseAnchor)
            && promiseAnchor.PromiseIds.Contains("promise_test_route"));
    }

    [Fact]
    public void SharedTypedConsequencesApplyGeneralStateMutations()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;

        var inventory = session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test",
            player.Id.Value,
            "red pepper",
            amount: 3,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var addTags = session.Engine.ApplyConsequence(WorldConsequence.AddTags(
            "test",
            soldier.Id.Value,
            new[] { "moon-marked", "fragile" },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var removeTags = session.Engine.ApplyConsequence(WorldConsequence.RemoveTags(
            "test",
            soldier.Id.Value,
            new[] { "fragile" },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var faction = session.Engine.ApplyConsequence(WorldConsequence.ChangeFaction(
            "test",
            soldier.Id.Value,
            "player",
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var control = session.Engine.ApplyConsequence(WorldConsequence.UpdateControl(
            "test",
            soldier.Id.Value,
            "ai",
            aiPolicyId: "follower",
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var flag = session.Engine.ApplyConsequence(WorldConsequence.SetWorldFlag(
            "test",
            "bridge collapsed",
            true,
            "bridge collapsed",
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var scheduled = session.Engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "test",
            "bell_return",
            4,
            new Dictionary<string, object?> { ["text"] = "The bell comes back." },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var trigger = session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test",
            "bell ward",
            "delay",
            delay: 2,
            interval: 1,
            uses: 1,
            duration: null,
            effectType: "message",
            effectFields: new Dictionary<string, object?> { ["text"] = "The bell rings." },
            description: "The bell waits to ring.",
            anchorEntityId: soldier.Id.Value,
            sourceEntityId: "player"));
        var standing = session.Engine.ApplyConsequence(WorldConsequence.AdjustFactionStanding(
            "test",
            "empire",
            "suspicion",
            3,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var resource = session.Engine.ApplyConsequence(WorldConsequence.AdjustFactionResource(
            "test",
            "empire",
            "heat",
            2,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var legend = session.Engine.ApplyConsequence(WorldConsequence.AddLegend(
            "test",
            "player_soul",
            "bell-touched",
            2,
            "test_deed",
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var canon = session.Engine.ApplyConsequence(WorldConsequence.AddCanon(
            "test",
            "censorate_memo",
            "empire",
            "Censorate memorandum: bells are to be watched.",
            "Censorate watches bells.",
            new[] { "empire", "bell" },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));

        Assert.True(inventory.Applied);
        Assert.Equal(3, player.Get<InventoryComponent>().Items["red_pepper"]);
        Assert.True(addTags.Applied);
        Assert.True(removeTags.Applied);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "moon_marked");
        Assert.DoesNotContain(soldier.Get<TagsComponent>().Tags, tag => tag == "fragile");
        Assert.True(faction.Applied);
        Assert.Equal("player", soldier.Get<ActorComponent>().Faction);
        Assert.Equal("player", soldier.Get<FactionComponent>().FactionId);
        Assert.True(control.Applied);
        Assert.Equal(ControllerKind.Ai, soldier.Get<ControllerComponent>().Kind);
        Assert.Equal("follower", soldier.Get<AiComponent>().PolicyId);
        Assert.Equal(WorldConsequenceTypes.UpdateControl, control.Deltas.Single().Details["consequenceType"]);
        Assert.True(flag.Applied);
        Assert.True((bool)session.Engine.State.WorldFlags["bridge_collapsed"]!);
        Assert.True(scheduled.Applied);
        Assert.Contains(session.Engine.State.ScheduledEvents.Events, item =>
            item.Kind == "bell_return"
            && item.DueTurn == session.Engine.State.Turn + 4);
        Assert.True(trigger.Applied);
        Assert.Contains(session.Engine.State.Triggers.Records, item =>
            item.Name == "bell ward"
            && item.AnchorEntityId == soldier.Id.Value
            && item.EffectType == "message");
        Assert.Contains(trigger.Deltas, delta =>
            delta.Operation == "createTrigger"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreateTrigger)
            && Equals(delta.Details["triggerId"], session.Engine.State.Triggers.Records.Single().Id)
            && Equals(delta.Details["nextTurn"], session.Engine.State.Turn + 2));
        Assert.True(standing.Applied);
        Assert.Equal(3, session.Engine.State.Factions.StandingValue("empire", "suspicion"));
        Assert.True(resource.Applied);
        Assert.True(session.Engine.State.Factions.ResourceValue("empire", "heat") >= 2);
        Assert.True(legend.Applied);
        Assert.Contains(session.Engine.State.Legend.Tags, item =>
            item.ActorSoulId == "player_soul"
            && item.Tag == "bell-touched"
            && item.Weight == 2);
        Assert.True(canon.Applied);
        Assert.Contains(session.Engine.State.Canon.Records, item =>
            item.Kind == "censorate_memo"
            && item.Summary == "Censorate watches bells.");
    }

    [Fact]
    public void ModifyInventoryRejectsInsufficientConsumes()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var inventory = player.Get<InventoryComponent>();

        var consume = session.Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "test",
            player.Id.Value,
            "missing salt",
            op: "consume",
            amount: 1,
            operation: "testConsumeMissing"));

        Assert.False(consume.Applied);
        Assert.False(inventory.Items.ContainsKey("missing_salt"));
        var rejected = Assert.Single(consume.Deltas);
        Assert.Equal("worldConsequenceRejected", rejected.Operation);
        Assert.Equal(WorldConsequenceTypes.ModifyInventory, rejected.Details["consequenceType"]);
        Assert.Contains("not carrying enough missing salt", rejected.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MagicModifyInventoryRejectsInsufficientConsumesBeforeApplication()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The pouch eats a moon that is not there.",
            Effects: new[]
            {
                new SpellEffect(
                    "modifyInventory",
                    new Dictionary<string, object?>
                    {
                        ["item"] = "missing salt",
                        ["op"] = "consume",
                        ["amount"] = 1,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("spend missing salt"));

        Assert.False(result.Success);
        Assert.False(result.TechnicalFailure);
        Assert.True(result.ConsumedTurn);
        Assert.Contains(result.Messages, message =>
            message.Contains("not carrying enough missing salt", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Deltas, delta =>
            Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ModifyInventory));
        Assert.False(session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items.ContainsKey("missing_salt"));
    }

    [Fact]
    public void ActorResourceHealthAtZeroUsesSharedDefeatCleanup()
    {
        var session = CreateMockSession();
        var soldier = session.Engine.EntityById("soldier_1")!;

        var drain = session.Engine.ApplyConsequence(WorldConsequence.AdjustActorResource(
            "test",
            soldier.Id.Value,
            "health",
            -99,
            min: 0,
            operation: "testDrain",
            sourceEntityId: "player"));

        Assert.True(drain.Applied);
        Assert.Equal(WorldConsequenceTypes.AdjustActorResource, drain.Deltas.Single().Details["consequenceType"]);
        Assert.Equal(0, soldier.Get<ActorComponent>().HitPoints);
        Assert.False(soldier.Get<PhysicalComponent>().BlocksMovement);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "defeated");
    }

    [Fact]
    public async Task MagicOperationsUseSharedConsequencesForGeneralStateMutations()
    {
        var provider = new FixtureSpellProvider(new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The bookkeeping becomes part of the world.",
            Effects: new[]
            {
                new SpellEffect(
                    "modifyInventory",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["item"] = "dream salt",
                        ["amount"] = 2,
                    }),
                new SpellEffect(
                    "addTag",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["tags"] = "temporary,bridge-bent",
                    }),
                new SpellEffect(
                    "removeTag",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["tag"] = "temporary",
                    }),
                new SpellEffect(
                    "changeFaction",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["faction"] = "player",
                    }),
                new SpellEffect(
                    "setFlag",
                    new Dictionary<string, object?>
                    {
                        ["flag"] = "bell remembered",
                        ["value"] = true,
                        ["description"] = "bell remembered",
                    }),
                new SpellEffect(
                    "scheduleEvent",
                    new Dictionary<string, object?>
                    {
                        ["turns"] = 3,
                        ["eventType"] = "moth_arrival",
                        ["text"] = "A moth arrives.",
                    }),
                new SpellEffect(
                    "createTrigger",
                    new Dictionary<string, object?>
                    {
                        ["name"] = "moth signal",
                        ["kind"] = "delay",
                        ["delay"] = 2,
                        ["anchor"] = "self",
                        ["effectType"] = "message",
                        ["text"] = "The moth signal chirps.",
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;

        var result = await session.ExecuteAsync(new CastCommand("make the ledgers sing"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Equal(
            new[] { "modifyInventory", "addTag", "removeTag", "changeFaction", "setFlag", "scheduleEvent", "createTrigger" },
            result.Magic!.EffectTypes);
        Assert.Equal(2, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["dream_salt"]);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "bridge_bent");
        Assert.DoesNotContain(soldier.Get<TagsComponent>().Tags, tag => tag == "temporary");
        Assert.Equal("player", soldier.Get<ActorComponent>().Faction);
        Assert.True((bool)session.Engine.State.WorldFlags["bell_remembered"]!);
        Assert.Contains(result.Deltas, delta => delta.Operation == "scheduleEvent");
        Assert.Contains(result.Deltas, delta => delta.Operation == "createTrigger");
    }

    [Fact]
    public void SharedTypedConsequencesApplyAdvancedMagicStateMutations()
    {
        var session = CreateMockSession();
        var player = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;

        var transform = session.Engine.ApplyConsequence(WorldConsequence.TransformEntity(
            "test",
            soldier.Id.Value,
            name: "silver soldier",
            material: "silver",
            description: "A soldier made into a mirror-bright warning.",
            tags: new[] { "mirror-bright" },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var resistance = session.Engine.ApplyConsequence(WorldConsequence.SetResistance(
            "test",
            player.Id.Value,
            "fire",
            40,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var weakness = session.Engine.ApplyConsequence(WorldConsequence.SetWeakness(
            "test",
            soldier.Id.Value,
            "cold",
            75,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var delayed = session.Engine.ApplyConsequence(WorldConsequence.DelayIncomingDamage(
            "test",
            player.Id.Value,
            4,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var memory = session.Engine.ApplyConsequence(WorldConsequence.EditMemory(
            "test",
            soldier.Id.Value,
            "add",
            "The caster spared me once.",
            "caster",
            4,
            aboutCaster: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var forget = session.Engine.ApplyConsequence(WorldConsequence.EditMemory(
            "test",
            soldier.Id.Value,
            "remove",
            subject: "caster",
            aboutCaster: true,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var persistent = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            soldier.Id.Value,
            "on_hit",
            "damage",
            new Dictionary<string, object?> { ["amount"] = 2, ["damageType"] = "mirror" },
            uses: 2,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        ApplyStatus(session, soldier, "burning", duration: 3);
        var hpBeforeAcceleration = soldier.Get<ActorComponent>().HitPoints;
        var accelerated = session.Engine.ApplyConsequence(WorldConsequence.AccelerateStatus(
            "test",
            soldier.Id.Value,
            "burning",
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var behavior = session.Engine.ApplyConsequence(WorldConsequence.SetBehavior(
            "test",
            soldier.Id.Value,
            "coward",
            duration: 3,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));
        var flow = session.Engine.ApplyConsequence(WorldConsequence.CreateFlow(
            "test",
            4,
            4,
            radius: 1,
            dx: 1,
            dy: 0,
            duration: 5,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player"));

        Assert.True(transform.Applied);
        Assert.Equal("silver soldier", soldier.Name);
        Assert.Equal("silver", soldier.Get<PhysicalComponent>().Material);
        Assert.Equal("A soldier made into a mirror-bright warning.", soldier.Get<DescriptionComponent>().Text);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "mirror_bright");
        Assert.True(resistance.Applied);
        Assert.Equal(40, player.Get<ResistanceComponent>().Resistances["fire"]);
        Assert.True(weakness.Applied);
        Assert.Equal(75, soldier.Get<ResistanceComponent>().Weaknesses["cold"]);
        Assert.True(delayed.Applied);
        Assert.Equal(session.Engine.State.Turn + 4, player.Get<DelayedDamageComponent>().ReleaseTurn);
        Assert.True(memory.Applied);
        Assert.Contains(memory.Deltas, delta =>
            delta.Operation == "editMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.EditMemory));
        Assert.True(forget.Applied);
        Assert.Contains(forget.Deltas, delta =>
            delta.Operation == "memoryBondFloor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.EditMemory));
        Assert.DoesNotContain(soldier.Get<MemoryComponent>().Records, record =>
            record.Text.Contains("caster", StringComparison.OrdinalIgnoreCase));
        Assert.True(persistent.Applied);
        Assert.Contains(session.Engine.State.PersistentEffects.Records, record =>
            record.AnchorEntityId == soldier.Id.Value
            && record.Hook == "on_hit"
            && record.EffectType == "damage");
        Assert.True(accelerated.Applied);
        Assert.True(soldier.Get<ActorComponent>().HitPoints < hpBeforeAcceleration);
        Assert.DoesNotContain(soldier.Get<StatusContainerComponent>().Statuses, status => status.Id == "burning");
        Assert.True(behavior.Applied);
        Assert.Equal(session.Engine.State.Turn + 3, soldier.Get<BehaviorTagsComponent>().Tags["coward"]);
        Assert.True(flow.Applied);
        Assert.True(session.Engine.State.TileFlows.TryGetValue(new GridPoint(4, 4), out var tileFlow));
        Assert.Equal(1, tileFlow.Dx);
    }

    [Fact]
    public void TransformEntityCanRetuneFixturePhysicalRenderAndInteractionState()
    {
        var session = CreateMockSession();
        var bridge = AddBridgeFixture(session, "old_bridge", new GridPoint(6, 6));

        var transform = session.Engine.ApplyConsequence(WorldConsequence.TransformEntity(
            "test",
            bridge.Id.Value,
            name: "collapsed bell bridge",
            material: "splintered wood",
            description: "A bridge folded into nail-bright kindling.",
            tags: new[] { "collapsed", "loud" },
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: "player",
            operation: "testCollapseFixture",
            blocksMovement: true,
            blocksSight: false,
            glyph: '=',
            palette: "ruin",
            fixtureType: "collapsed_bridge",
            removeTags: new[] { "standing" },
            interactableVerbs: new[] { "examine", "climb" }));

        Assert.True(transform.Applied);
        Assert.Equal("collapsed bell bridge", bridge.Name);
        Assert.Equal("splintered_wood", bridge.Get<PhysicalComponent>().Material);
        Assert.True(bridge.Get<PhysicalComponent>().BlocksMovement);
        Assert.False(bridge.Get<PhysicalComponent>().BlocksSight);
        Assert.Equal('=', bridge.Get<RenderableComponent>().Glyph);
        Assert.Equal("ruin", bridge.Get<RenderableComponent>().Palette);
        Assert.Equal("collapsed_bridge", bridge.Get<FixtureComponent>().FixtureType);
        Assert.Contains("collapsed", bridge.Get<TagsComponent>().Tags);
        Assert.DoesNotContain("standing", bridge.Get<TagsComponent>().Tags);
        Assert.Contains("climb", bridge.Get<InteractableComponent>().Verbs);
        Assert.Contains(transform.Deltas, delta =>
            delta.Operation == "testCollapseFixture"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransformEntity));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GenericMagicConsequenceCanTransformFixtureProp()
    {
        var provider = new FixtureSpellProvider(new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The old bridge folds into a passless ladder of bells and splinters.",
            Effects: new[]
            {
                new SpellEffect(
                    "consequence",
                    new Dictionary<string, object?>
                    {
                        ["consequenceType"] = "transform_entity",
                        ["targetEntityId"] = "old_bridge_1",
                        ["name"] = "collapsed bell bridge",
                        ["material"] = "splintered wood",
                        ["blocksMovement"] = true,
                        ["tags"] = new[] { "collapsed" },
                        ["removeTags"] = new[] { "standing" },
                        ["consequencePayload"] = new Dictionary<string, object?>
                        {
                            ["operation"] = "wildMagicCollapseBridge",
                            ["fixtureType"] = "collapsed_bridge",
                            ["glyph"] = "=",
                            ["palette"] = "ruin",
                            ["interactableVerbs"] = new[] { "examine", "climb" },
                        },
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        var bridge = AddBridgeFixture(session, "old_bridge", new GridPoint(6, 6));

        var result = await session.ExecuteAsync(new CastCommand("collapse the old bell bridge into a tangle of ringing wood"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Contains("consequence", result.Magic!.EffectTypes);
        Assert.Equal("collapsed bell bridge", bridge.Name);
        Assert.True(bridge.Get<PhysicalComponent>().BlocksMovement);
        Assert.Equal("collapsed_bridge", bridge.Get<FixtureComponent>().FixtureType);
        Assert.Equal('=', bridge.Get<RenderableComponent>().Glyph);
        Assert.Contains("collapsed", bridge.Get<TagsComponent>().Tags);
        Assert.DoesNotContain("standing", bridge.Get<TagsComponent>().Tags);
        Assert.Contains("climb", bridge.Get<InteractableComponent>().Verbs);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicCollapseBridge"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransformEntity));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task TransformItemOperationCanRetuneFixtureProp()
    {
        var provider = new FixtureSpellProvider(new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "The bridge remembers being debris.",
            Effects: new[]
            {
                new SpellEffect(
                    "transformItem",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "old_bridge_1",
                        ["name"] = "folded bell bridge",
                        ["material"] = "bent brass",
                        ["blocksMovement"] = true,
                        ["fixtureType"] = "folded_bridge",
                        ["glyph"] = "=",
                        ["palette"] = "brass",
                        ["removeTags"] = new[] { "standing" },
                        ["add_tags"] = new[] { "folded" },
                        ["interactableVerbs"] = new[] { "examine", "climb" },
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        var bridge = AddBridgeFixture(session, "old_bridge", new GridPoint(6, 6));

        var result = await session.ExecuteAsync(new CastCommand("fold the old bridge into brass debris"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Contains("transformItem", result.Magic!.EffectTypes);
        Assert.Equal("folded bell bridge", bridge.Name);
        Assert.Equal("bent_brass", bridge.Get<PhysicalComponent>().Material);
        Assert.True(bridge.Get<PhysicalComponent>().BlocksMovement);
        Assert.Equal("folded_bridge", bridge.Get<FixtureComponent>().FixtureType);
        Assert.Equal('=', bridge.Get<RenderableComponent>().Glyph);
        Assert.Equal("brass", bridge.Get<RenderableComponent>().Palette);
        Assert.Contains("folded", bridge.Get<TagsComponent>().Tags);
        Assert.DoesNotContain("standing", bridge.Get<TagsComponent>().Tags);
        Assert.Contains("climb", bridge.Get<InteractableComponent>().Verbs);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "transformItem"
            && delta.Target == bridge.Id.Value
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransformEntity));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task MagicOperationsUseSharedConsequencesForAdvancedStateMutations()
    {
        var provider = new FixtureSpellProvider(new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The soldier becomes a walking side effect.",
            Effects: new[]
            {
                new SpellEffect(
                    "transformEntity",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["name"] = "thistle soldier",
                        ["material"] = "thorn",
                        ["addTags"] = "bristling",
                    }),
                new SpellEffect(
                    "addResistance",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["damageType"] = "fire",
                        ["amount"] = 30,
                    }),
                new SpellEffect(
                    "addWeakness",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["damageType"] = "cold",
                        ["amount"] = 60,
                    }),
                new SpellEffect(
                    "delayIncoming",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "self",
                        ["turns"] = 2,
                    }),
                new SpellEffect(
                    "editMemory",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["op"] = "add",
                        ["subject"] = "caster",
                        ["text"] = "The caster gave me a thistle name.",
                        ["strength"] = 4,
                    }),
                new SpellEffect(
                    "createPersistentEffect",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["hook"] = "on_hit",
                        ["effectType"] = "message",
                        ["text"] = "The thistles answer.",
                    }),
                new SpellEffect(
                    "setBehavior",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "soldier_1",
                        ["tag"] = "coward",
                        ["duration"] = 2,
                    }),
                new SpellEffect(
                    "createFlow",
                    new Dictionary<string, object?>
                    {
                        ["x"] = 4,
                        ["y"] = 4,
                        ["radius"] = 0,
                        ["dx"] = 1,
                        ["dy"] = 0,
                        ["duration"] = 4,
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        var soldier = session.Engine.EntityById("soldier_1")!;

        var result = await session.ExecuteAsync(new CastCommand("make the soldier a thistle engine"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.Equal(
            new[] { "transformEntity", "addResistance", "addWeakness", "delayIncoming", "editMemory", "createPersistentEffect", "setBehavior", "createFlow" },
            result.Magic!.EffectTypes);
        Assert.Equal("thistle soldier", soldier.Name);
        Assert.Equal("thorn", soldier.Get<PhysicalComponent>().Material);
        Assert.Contains(soldier.Get<TagsComponent>().Tags, tag => tag == "bristling");
        Assert.Equal(30, session.Engine.State.ControlledEntity.Get<ResistanceComponent>().Resistances["fire"]);
        Assert.Equal(60, soldier.Get<ResistanceComponent>().Weaknesses["cold"]);
        Assert.Equal(result.TurnBefore + 2, session.Engine.State.ControlledEntity.Get<DelayedDamageComponent>().ReleaseTurn);
        Assert.Contains(soldier.Get<MemoryComponent>().Records, record =>
            record.Text.Contains("thistle name", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.PersistentEffects.Records, record => record.AnchorEntityId == soldier.Id.Value);
        Assert.Contains(result.Deltas, delta => delta.Operation == "setBehavior");
        Assert.True(session.Engine.State.TileFlows.ContainsKey(new GridPoint(4, 4)));
    }

    [Fact]
    public async Task DialogueClaimCanRevealServiceAndRequestCanOpenLockedDoor()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Lio can break the ward on the cell door.",
            "service",
            "ward-breaking",
            Salience: 3,
            Confidence: 85,
            PlayerVisible: true,
            TargetEntityId: "prisoner_1",
            ItemName: "ward-breaking",
            TriggerHint: "cell door",
            Tags: new[] { "service", "folk_magic", "door" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, can you help with the cell door?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var lio = session.Engine.EntityById("prisoner_1")!;
        var offers = lio.Get<ServiceComponent>().Offers
            .Select(service => service with { ItemCost = "grave salt" })
            .ToArray();
        lio.Set(new ServiceComponent(offers));
        var services = await session.ExecuteAsync(new ServicesCommand("Lio"));
        var request = await session.ExecuteAsync(new RequestServiceCommand("ward-breaking", "Lio"));
        var door = session.Engine.EntityById("cell_door_1")!;

        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimOfferService");
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimApplicationSkipped");
        Assert.Contains(services.Messages, message => message.Contains("ward-breaking", StringComparison.OrdinalIgnoreCase));
        Assert.True(request.Success);
        Assert.Equal(1, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["grave salt"]);
        Assert.Contains(request.Deltas, delta =>
            delta.Operation == "requestService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.Contains(request.Deltas, delta =>
            delta.Operation == "serviceCost"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ModifyInventory)
            && Equals(delta.Details["item"], "grave salt"));
        Assert.Contains(request.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.Contains(request.Deltas, delta =>
            delta.Operation == "freeCaptive"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.FreeCaptive)
            && Equals(delta.Details["captiveId"], "prisoner_1"));
        Assert.Contains(request.Deltas, delta =>
            delta.Operation == "freeCaptiveWant"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["status"], "satisfied"));
        Assert.Contains(request.Messages, message =>
            message.Contains("free enough to choose you", StringComparison.OrdinalIgnoreCase));
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Null(door.Get<DoorComponent>().KeyId);
        Assert.Equal("player", lio.Get<ActorComponent>().Faction);
    }

    [Fact]
    public async Task RequestServiceCanSatisfyProviderWantThroughSharedConsequence()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.ApplyConsequence(WorldConsequence.UpdateWant(
            "test",
            lio.Id.Value,
            text: "Perform one quiet ward-breaking without giving the Censorate a name to execute.",
            status: "active",
            stakes: "Success proves the service can help; failure makes the cell feel smaller.",
            tags: new[] { "service_provider", "urgent" },
            operation: "testWant"));
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "ward_breaking",
            "ward-breaking",
            "A hush-hush folk charm that worries a lock open.",
            "open_or_unlock",
            targetHint: "cell door",
            tags: new[] { "service", "folk_magic", "door" },
            wantStatusOnComplete: "satisfied",
            wantStakesOnComplete: "The ward service was performed; future danger now comes from who heard about it.",
            wantAddTagsOnComplete: new[] { "service_completed", "satisfied_by_player" },
            wantRemoveTagsOnComplete: new[] { "urgent" },
            operation: "testOfferService"));

        var result = await session.ExecuteAsync(new RequestServiceCommand("ward-breaking", "Lio"));
        var want = lio.Get<WantComponent>();

        Assert.True(offer.Applied);
        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceWantCompletion"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceWantCompletionMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["parentOperation"], "serviceWantCompletion")
            && Equals(delta.Details["provenance"], "service:ward_breaking"));
        Assert.Equal("satisfied", want.Status);
        Assert.Contains("ward service", want.Stakes, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service_completed", want.Tags);
        Assert.Contains("satisfied_by_player", want.Tags);
        Assert.DoesNotContain("urgent", want.Tags);
    }

    [Fact]
    public async Task ServicePaymentFailureRollsBackServiceEffect()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "bad_payment_ward_breaking",
            "ward-breaking",
            "Break the cell ward if paid with a vague handful of grave.",
            "open_or_unlock",
            itemCost: "grave",
            targetHint: "cell door",
            revealed: true,
            operation: "testOfferService"));

        var result = await session.ExecuteAsync(new RequestServiceCommand("ward-breaking", "Lio"));
        var restoredDoor = session.Engine.EntityById("cell_door_1")!;

        Assert.True(offer.Applied);
        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Contains(result.Messages, message =>
            message.Contains("not carrying enough grave", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ModifyInventory));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceRequestSkipped"
            && Equals(delta.Details["effectKind"], "open_or_unlock")
            && Equals(delta.Details["rejectedCount"], 1));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "requestService");
        Assert.False(restoredDoor.Get<DoorComponent>().IsOpen);
        Assert.Equal("imperial cell key", restoredDoor.Get<DoorComponent>().KeyId);
        Assert.Equal(2, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["grave salt"]);
    }

    [Fact]
    public async Task ServiceWantCompletionFailureRollsBackServiceEffectAndPayment()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Remove<WantComponent>();
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "want_locked_ward_breaking",
            "ward-breaking",
            "Break the cell ward, but only if the provider's want can be satisfied.",
            "open_or_unlock",
            itemCost: "grave salt",
            targetHint: "cell door",
            revealed: true,
            wantStatusOnComplete: "satisfied",
            wantStakesOnComplete: "The service was completed.",
            wantAddTagsOnComplete: new[] { "service_completed" },
            operation: "testOfferService"));

        var result = await session.ExecuteAsync(new RequestServiceCommand("ward-breaking", "Lio"));
        var restoredDoor = session.Engine.EntityById("cell_door_1")!;

        Assert.True(offer.Applied);
        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Contains(result.Messages, message =>
            message.Contains("provider_has_no_want", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceWantSkipped"
            && Equals(delta.Details["reason"], "provider_has_no_want"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "serviceRequestSkipped"
            && Equals(delta.Details["failure"], "provider_has_no_want")
            && Equals(delta.Details["rejectedCount"], 0));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "serviceCost");
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "requestService");
        Assert.False(restoredDoor.Get<DoorComponent>().IsOpen);
        Assert.Equal("imperial cell key", restoredDoor.Get<DoorComponent>().KeyId);
        Assert.Equal(2, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["grave salt"]);
    }

    [Fact]
    public async Task DialogueRemoteServiceClaimDoesNotAttachServiceToSpeaker()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Old Maren's niece Nannerl can mend wards if you bring moon pearl.",
            "service",
            "ward_mender_nannerl",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "person",
            TriggerHint: "buy,trade",
            TargetEntityId: "prisoner_1",
            ItemName: "moon pearl",
            Tags: new[] { "service", "folk_magic", "ward" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, who can mend wards?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var lio = session.Engine.EntityById("prisoner_1")!;
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimOfferService");
        Assert.False(lio.Has<ServiceComponent>());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "service")
            && Equals(delta.Details["triggerHint"], "travel"));
        Assert.Equal("bound", promise.Status);
        Assert.Equal("service", promise.RealizationKind);
        Assert.Equal("travel", promise.TriggerHint);
    }

    [Fact]
    public async Task DialogueRemoteServiceRealizationUsesTravelEvenWhenCategorizedAsPerson()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Old Maren's niece Nannerl can break door wards quietly.",
            "person",
            "Nannerl",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "service",
            TriggerHint: "talk",
            TargetEntityId: "nannerl_wardbreaker_1",
            Tags: new[] { "service", "folk_magic", "ward" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, who can break wards?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase));
        var claim = Assert.Single(session.Engine.State.Claims.Records, item =>
            item.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase));

        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimOfferService");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "service")
            && Equals(delta.Details["triggerHint"], "travel"));
        Assert.Equal("bound", promise.Status);
        Assert.Equal("service", promise.RealizationKind);
        Assert.Equal("travel", promise.TriggerHint);
        Assert.Equal(claim.Id, promise.SourceClaimId);
        Assert.Equal("prisoner_1", promise.SourceSpeakerId);
        Assert.Equal("player_soul", promise.SourceListenerSoulId);
        Assert.Equal(85, promise.SourceConfidence);

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseRealizationPlan"
            && delta.Target == promise.Id
            && Equals(delta.Details["sourceClaimId"], claim.Id)
            && Equals(delta.Details["sourceSpeakerId"], "prisoner_1")
            && Equals(delta.Details["sourceListenerSoulId"], "player_soul")
            && Equals(delta.Details["sourceConfidence"], 85));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferService));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Contains("Nannerl", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<ServiceComponent>(out var services)
            && services.Offers.Any(service => service.Name.Contains("ward", StringComparison.OrdinalIgnoreCase))
            && entity.TryGet<WantComponent>(out var want)
            && want.Tags.Contains("promise_source"));
    }

    [Fact]
    public async Task DialoguePersonPromiseTravelUsesGeneratedSpawnEntityConsequence()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Old Maren waits by the river gate.",
            "person",
            "Old Maren",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "person",
            TriggerHint: "travel",
            Tags: new[] { "person", "guide" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, who waits by the river gate?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Contains("Old Maren", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "person"));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promisePerson"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Old Maren"
            && entity.TryGet<ProfileComponent>(out _)
            && entity.TryGet<MemoryComponent>(out _)
            && entity.TryGet<WantComponent>(out var want)
            && want.Text.Contains("promise", StringComparison.OrdinalIgnoreCase)
            && want.Tags.Contains("promise_source")
            && entity.TryGet<InteractableComponent>(out var interactable)
            && interactable.Verbs.Contains("recruit")
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
    }

    [Fact]
    public async Task DialogueClaimCanOfferTradeWithoutCompletingTransaction()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Lio can trade you a fine blade.",
            "trade",
            "fine blade",
            Salience: 3,
            Confidence: 80,
            PlayerVisible: true,
            TargetEntityId: "prisoner_1",
            ItemName: "fine blade",
            Tags: new[] { "trade", "blade" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, can we trade?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var wares = await session.ExecuteAsync(new WaresCommand("Lio"));
        var lio = session.Engine.EntityById("prisoner_1")!;

        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimOfferTrade");
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimApplicationSkipped");
        Assert.True(lio.Has<MerchantComponent>());
        Assert.True(lio.Get<MerchantComponent>().Wares.TryGetValue("fine blade", out var count));
        Assert.Equal(1, count);
        Assert.Contains(wares.Messages, message => message.Contains("fine blade", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TravelCanRealizeEscapeRoutePromiseAsRouteFixture()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "An imperial drainage route runs east of the yard.",
            playerVisible: true,
            salience: 4,
            subject: "imperial drainage route",
            claimedPlace: session.Engine.State.RegionId,
            triggerHint: "travel",
            realizationKind: "escape_route");
        session.Engine.State.PromiseLedger.Bind(promise.Id, session.Engine.State.RegionId, null, "travel", "escape_route");

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseRoute"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreateRoute));
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name.Contains("drainage", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<FixtureComponent>(out var fixture)
            && fixture.FixtureType == "escape_route");
    }

    [Fact]
    public async Task DialogueRouteLikeLandmarkPromiseNormalizesAndDeduplicates()
    {
        var text = "a burned oak marks a hidden road south of this yard";
        var extractor = new FixtureDialogueClaimExtractor(
            new DialogueClaimProposal(
                text,
                "landmark",
                "burned_oak_route_marker",
                Salience: 4,
                Confidence: 90,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "site",
                TriggerHint: "travel"),
            new DialogueClaimProposal(
                text,
                "landmark",
                "burned_oak_route_marker",
                Salience: 4,
                Confidence: 90,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "escape_route",
                TriggerHint: "travel, inspect"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, where is the hidden road?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Equals(text, StringComparison.OrdinalIgnoreCase));
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Equal("escape_route", promise.RealizationKind);
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromiseLinked"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateClaim)
            && Equals(delta.Details["status"], "promised"));
        Assert.Contains(travel.Deltas, delta => delta.Operation == "promiseRoute");
        Assert.DoesNotContain(travel.Deltas, delta => delta.Operation == "promiseSite");
    }

    [Fact]
    public async Task HighSalienceActionableDialogueClaimAutoBindsPromise()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "A burned oak marks a hidden road leading to Hollowmere refuge.",
            "landmark",
            "burned oak",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: false));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, give me one lead."));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Contains("burned oak", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "escape_route"));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromiseMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Equal("bound", promise.Status);
        Assert.Equal("travel", promise.TriggerHint);
    }

    [Fact]
    public async Task DialogueClaimPromiseBindingCommitsPromiseAndClaimStatusTogether()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "A silver knife waits inside the burned oak north of the checkpoint.",
            "item",
            "silver knife",
            Salience: 4,
            Confidence: 90,
            PlayerVisible: true,
            BindAsPromise: true,
            RealizationKind: "item",
            TriggerHint: "travel"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, give me a concrete lead."));
        var wait = await session.ExecuteAsync(new WaitCommand());

        var claim = Assert.Single(session.Engine.State.Claims.Records, item =>
            item.Text.Contains("silver knife", StringComparison.OrdinalIgnoreCase));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Contains("silver knife", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("promised", claim.Status);
        Assert.Equal(promise.Id, claim.BoundPromiseId);
        Assert.Equal(claim.Id, promise.SourceClaimId);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromiseStatus"
            && Equals(delta.Details["claimId"], claim.Id)
            && Equals(delta.Details["boundPromiseId"], promise.Id)
            && Equals(delta.Details["status"], "promised"));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimPromiseSkipped");
    }

    [Fact]
    public async Task VisibleNonActionableDialogueClaimUsesMessageConsequence()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Ricky has a brother named Taylor who remembers the old market songs.",
            "memory",
            "Ricky's brother Taylor",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: false));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, tell me something local."));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Text.Contains("Taylor", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimJournalMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["category"], "memory"));
        Assert.Contains(wait.Messages, message =>
            message.Contains("Taylor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RepeatedPlaceAndRouteClaimsMergeIntoOnePromise()
    {
        var text = "a refuge exists south of the yard for Hollowmere folk to hide in";
        var extractor = new FixtureDialogueClaimExtractor(
            new DialogueClaimProposal(
                text,
                "site",
                "refuge_location",
                Salience: 4,
                Confidence: 85,
                PlayerVisible: true,
                BindAsPromise: false,
                RealizationKind: "site"),
            new DialogueClaimProposal(
                text,
                "escape_route",
                "refuge_location",
                Salience: 4,
                Confidence: 85,
                PlayerVisible: true,
                BindAsPromise: false,
                RealizationKind: "escape_route"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, where is the refuge?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Equals(text, StringComparison.OrdinalIgnoreCase));
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.South));

        Assert.Equal("escape_route", promise.RealizationKind);
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimPromiseLinked");
        Assert.Contains(travel.Deltas, delta => delta.Operation == "promiseRoute");
        Assert.Single(travel.Deltas, delta => delta.Operation == "realizePromise");
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "path to Hollowmere refuge"
            && entity.TryGet<FixtureComponent>(out var fixture)
            && fixture.FixtureType == "escape_route");
    }

    [Fact]
    public async Task RewordedSameSpeakerClaimLinksToExistingPromiseInsteadOfForkingAThread()
    {
        // Reproduces FEEL_LOG [06]: the model restated the same fact ("Jimmer sells blades that
        // don't sing to ward-captains") in different words across two turns. The second claim
        // should link back to the first promise rather than minting a parallel one.
        var extractor = new SequentialDialogueClaimExtractor(
            new DialogueClaimProposal(
                "Jimmer is a quiet blade-seller in the lower markets who sells blades that do not sing to ward-captains.",
                "person",
                "jimmer",
                Salience: 4,
                Confidence: 85,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "person"),
            new DialogueClaimProposal(
                "Jimmer in the lower markets sells blades that do not sing to ward-captains.",
                "person",
                "jimmer",
                Salience: 4,
                Confidence: 85,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "person"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, tell me about Jimmer."));
        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new TalkCommand("Lio, do you trust me?"));
        var secondWait = await session.ExecuteAsync(new WaitCommand());

        var jimmerPromises = session.Engine.State.PromiseLedger.Promises
            .Where(promise => promise.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Single(jimmerPromises);
        Assert.Contains(secondWait.Deltas, delta => delta.Operation == "claimPromiseLinked");
        var claims = session.Engine.State.Claims.Records
            .Where(claim => claim.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(2, claims.Length);
        Assert.All(claims, claim => Assert.Equal(jimmerPromises[0].Id, claim.BoundPromiseId));
    }

    [Fact]
    public async Task SameSpeakerUnrelatedFactsCreateSeparatePromisesRatherThanMerging()
    {
        var extractor = new SequentialDialogueClaimExtractor(
            new DialogueClaimProposal(
                "Jimmer is a quiet blade-seller in the lower markets who sells blades that do not sing to ward-captains.",
                "person",
                "jimmer",
                Salience: 4,
                Confidence: 85,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "person"),
            new DialogueClaimProposal(
                "An old burned oak marks a hidden road east of the yard.",
                "site",
                "burned_oak",
                Salience: 4,
                Confidence: 85,
                PlayerVisible: true,
                BindAsPromise: true,
                PromiseKind: "rumor",
                RealizationKind: "site"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, tell me about Jimmer."));
        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new TalkCommand("Lio, what else do you know?"));
        var secondWait = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(2, session.Engine.State.PromiseLedger.Promises.Count(promise =>
            promise.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase)
            || promise.Text.Contains("oak", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(secondWait.Deltas, delta => delta.Operation == "claimPromiseLinked");
    }

    [Fact]
    public async Task DifferentSpeakersWithNoSharedSubjectCreateSeparatePromisesDespiteSimilarText()
    {
        // Same near-duplicate wording as the merge test above (high token overlap), but from two
        // different speakers with two different subjects - the provenance guardrail must block
        // the merge even though the text alone would otherwise qualify.
        var extractorForLio = new DialogueClaimProposal(
            "Jimmer is a quiet blade-seller in the lower markets who sells blades that do not sing to ward-captains.",
            "person",
            "jimmer",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "person");
        var extractorForStranger = new DialogueClaimProposal(
            "Jimmer in the lower markets sells blades that do not sing to ward-captains.",
            "person",
            "market_gossip",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "person");
        var extractor = new SequentialDialogueClaimExtractor(extractorForLio, extractorForStranger);
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        AddTestNpc(session, "market_stranger_1", "a passing stranger", new GridPoint(13, 6));

        await session.ExecuteAsync(new TalkCommand("Lio, tell me about Jimmer."));
        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new TalkCommand("stranger, what do you know of Jimmer?"));
        var secondWait = await session.ExecuteAsync(new WaitCommand());

        Assert.Equal(2, session.Engine.State.PromiseLedger.Promises.Count(promise =>
            promise.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(secondWait.Deltas, delta => delta.Operation == "claimPromiseLinked");
    }

    [Fact]
    public async Task GeneratedDialogueSupportAllowsInflectedMerchantClaim()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Jimmer hides beneath the wet market; if you buy from him before dawn, he will sell you a lockpick.",
            Delivery: "hushed",
            Intent: "confide",
            Proposals: new DialogueProposalSet(
                Claims: new[]
                {
                    new DialogueClaimProposal(
                        "Jimmer sells a special lockpick to anyone who buys his wares by morning.",
                        "merchant_stock",
                        "lockpick",
                        Salience: 4,
                        Confidence: 85,
                        PlayerVisible: true,
                        BindAsPromise: true,
                        PromiseKind: "rumor",
                        RealizationKind: "merchant_stock",
                        TriggerHint: "travel",
                        ItemName: "lockpick"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, tell me a market secret."));

        Assert.Contains(talk.Deltas, delta => delta.Operation == "claimPromise");
        Assert.DoesNotContain(talk.Deltas, delta =>
            delta.Operation == "dialogueProposalSkipped"
            && Equals(delta.Details["proposalType"], "claim"));
        Assert.Contains(session.View().Claims!, claim =>
            claim.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase)
            && claim.Status == "promised");
    }

    [Fact]
    public async Task SellerSiteClaimRealizesAsMerchantStock()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Jimmer is a quiet blade-seller who hides in the shadowed alley behind the market stalls.",
            "site",
            "shadowed alley",
            Salience: 4,
            Confidence: 85,
            PlayerVisible: true,
            BindAsPromise: false,
            RealizationKind: "site"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, where is Jimmer?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var wares = await session.ExecuteAsync(new WaresCommand("Jimmer"));

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "merchant_stock"));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.True(wares.Success);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Jimmer"
            && entity.TryGet<MerchantComponent>(out var merchant)
            && merchant.Wares.ContainsKey("promised blade")
            && entity.TryGet<WantComponent>(out var want)
            && want.Tags.Contains("promise_source"));
    }

    [Fact]
    public void ServiceComponentSurvivesSaveRoundTrip()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;
        session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "quiet_mending",
            "quiet mending",
            "Lio can mend a torn cloak without imperial eyes noticing.",
            "record_memory",
            wantStatusOnComplete: "satisfied",
            wantStakesOnComplete: "The quiet service was completed without becoming paperwork.",
            wantAddTagsOnComplete: new[] { "service_completed" },
            wantRemoveTagsOnComplete: new[] { "urgent" },
            visibility: WorldConsequenceVisibility.Message));

        var saved = Sorcerer.Core.Persistence.GameSaveService.Serialize(session.Engine.State);
        var loaded = Sorcerer.Core.Persistence.GameSaveService.Deserialize(saved);
        var loadedLio = loaded.State.Entities[lio.Id];

        Assert.True(loadedLio.TryGet<ServiceComponent>(out var services));
        var offer = Assert.Single(services.Offers, service => service.Id == "quiet_mending");
        Assert.Equal("satisfied", offer.WantStatusOnComplete);
        Assert.Contains("completed", offer.WantStakesOnComplete, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("service_completed", offer.WantAddTagsOnComplete!);
        Assert.Contains("urgent", offer.WantRemoveTagsOnComplete!);
    }

    [Fact]
    public void WantComponentSurvivesSaveRoundTrip()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;

        var saved = Sorcerer.Core.Persistence.GameSaveService.Serialize(session.Engine.State);
        var loaded = Sorcerer.Core.Persistence.GameSaveService.Deserialize(saved);
        var loadedLio = loaded.State.Entities[lio.Id];

        Assert.True(loadedLio.TryGet<WantComponent>(out var want));
        Assert.Equal("want_lio_escape", want.Id);
        Assert.Contains("Hollowmere", want.Text);
        Assert.Contains("promise_source", want.Tags);
    }

    [Fact]
    public void ClaimSourceComponentSurvivesSaveRoundTrip()
    {
        var session = CreateMockSession();
        var notice = session.Engine.EntityById("notice_1")!;

        var saved = Sorcerer.Core.Persistence.GameSaveService.Serialize(session.Engine.State);
        var loaded = Sorcerer.Core.Persistence.GameSaveService.Deserialize(saved);
        var loadedNotice = loaded.State.Entities[notice.Id];

        Assert.True(loadedNotice.TryGet<ClaimSourceComponent>(out var claimSource));
        var seed = Assert.Single(claimSource.Claims);
        Assert.Equal("escape_route", seed.Category);
        Assert.Equal("southern drainage culvert", seed.Subject);
        Assert.True(seed.BindAsPromise);
        Assert.Equal("escape_route", seed.RealizationKind);
        Assert.Contains("drainage", seed.Tags!);

        var loadedBrazier = loaded.State.Entities[EntityId.Create("brazier_1")];
        Assert.True(loadedBrazier.TryGet<ClaimSourceComponent>(out var brazierClaimSource));
        var brazierSeed = Assert.Single(brazierClaimSource.Claims);
        Assert.Equal("landmark", brazierSeed.Category);
        Assert.Equal("walking stone", brazierSeed.Subject);
        Assert.Equal("site", brazierSeed.RealizationKind);
        Assert.Contains("brazier", brazierSeed.Tags!);
    }

    [Fact]
    public async Task DialogueClaimExtractionCanBindFutureMerchantStockForTravel()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Jimmer can sell you a fine blade.",
            "merchant_stock",
            "fine blade",
            Salience: 3,
            Confidence: 80,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "merchant_stock",
            TriggerHint: "travel",
            ItemName: "fine blade",
            Tags: new[] { "merchant", "stock", "blade" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, who can sell me a blade?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var wares = await session.ExecuteAsync(new WaresCommand("Jimmer"));
        var merchant = session.Engine.State.Entities.Values.Single(entity =>
            entity.Name == "Jimmer"
            && entity.TryGet<MerchantComponent>(out _));

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "merchant_stock"));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.True(wares.Success);
        Assert.Contains(wares.Messages, message => message.Contains("promised blade", StringComparison.OrdinalIgnoreCase));
        Assert.True(merchant.Get<MerchantComponent>().Wares.TryGetValue("promised blade", out var count));
        Assert.Equal(1, count);
        Assert.Contains(merchant.Get<PromiseAnchorComponent>().PromiseIds, promiseId =>
            session.Engine.State.PromiseLedger.Promises.Any(promise =>
                promise.Id == promiseId
                && promise.Status == "realized"
                && promise.RealizationKind == "merchant_stock"));
    }

    [Fact]
    public async Task DialogueMerchantStockCategoryOverridesMismatchedPersonRealization()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Jimmer sells blades in the lower market.",
            "merchant_stock",
            "blade",
            Salience: 4,
            Confidence: 90,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "person",
            TriggerHint: "travel"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        await session.ExecuteAsync(new TalkCommand("Lio, who can sell me a blade?"));
        var wait = await session.ExecuteAsync(new WaitCommand());
        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimPromise"
            && Equals(delta.Details["realizationKind"], "merchant_stock"));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseMerchantStock"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade));
        Assert.DoesNotContain(travel.Deltas, delta => delta.Operation == "promisePerson");
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Jimmer"
            && entity.TryGet<MerchantComponent>(out var merchant)
            && merchant.Wares.ContainsKey("promised blade"));
    }

    [Fact]
    public async Task JournalSeparatesHighSaliencePromisesIntoLeads()
    {
        var session = CreateMockSession();
        session.Engine.State.PromiseLedger.Add(
            "rumor",
            "There is a magical sword in a burned-out oak tree north of here.",
            playerVisible: true,
            salience: 4,
            subject: "magical sword",
            triggerHint: "travel",
            realizationKind: "item",
            sourceClaimId: "claim_77",
            sourceSpeakerId: "prisoner_1",
            sourceConfidence: 92);
        session.Engine.State.Claims.Append(
            session.Engine.State.Turn,
            "dialogue",
            "prisoner_1",
            "player_soul",
            "The ward-breaker owes Lio a door opened in silence.",
            "service",
            "ward-breaker",
            salience: 4,
            confidence: 85,
            playerVisible: true,
            tags: new[] { "service", "door" });
        session.Engine.State.Claims.Append(
            session.Engine.State.Turn,
            "test",
            "ricky",
            "player_soul",
            "Ricky has a brother named Taylor.",
            "person",
            "Taylor",
            salience: 2,
            confidence: 80,
            playerVisible: true,
            tags: new[] { "family" });

        var journal = await session.ExecuteAsync(new JournalCommand());

        Assert.Equal(journal.Messages, session.View().Journal);
        Assert.Contains(journal.Messages, message =>
            message.StartsWith("Lead:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("magical sword", StringComparison.OrdinalIgnoreCase)
            && message.Contains("heard from Lio of Hollowmere", StringComparison.OrdinalIgnoreCase)
            && message.Contains("92% confidence", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(journal.Messages, message =>
            message.StartsWith("Claim:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("ward-breaker", StringComparison.OrdinalIgnoreCase)
            && message.Contains("heard from Lio of Hollowmere", StringComparison.OrdinalIgnoreCase)
            && message.Contains("85% confidence", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(journal.Messages, message =>
            message.StartsWith("Lead:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("Taylor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(journal.Messages, message =>
            message.Contains("Taylor", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RumorsCommandShowsOnlyRumorsHeardByPlayer()
    {
        var session = CreateMockSession();
        var regionId = session.Engine.State.RegionId;
        session.Engine.State.PromiseLedger.Add(
            "rumor",
            "There is a magical sword in a burned-out oak tree north of here.",
            playerVisible: true,
            salience: 4,
            subject: "magical sword",
            triggerHint: "travel",
            realizationKind: "item");
        session.Engine.State.Claims.Append(
            session.Engine.State.Turn,
            "test",
            "ricky",
            "player_soul",
            "Ricky has a brother named Taylor.",
            "person",
            "Taylor",
            salience: 4,
            confidence: 80,
            playerVisible: true,
            tags: new[] { "family" });
        session.Engine.State.Rumors.Append(
            session.Engine.State.Turn,
            "claim",
            "heard_claim",
            regionId,
            regionId,
            "Lio knows a lock-whisperer who trades in grave salt.",
            salience: 4,
            carrierIds: new[] { "player_soul" },
            tags: new[] { "rumor", "service" });
        session.Engine.State.Rumors.Append(
            session.Engine.State.Turn,
            "claim",
            "unheard_claim",
            regionId,
            regionId,
            "A distant rumor has not reached the player.",
            salience: 4,
            carrierIds: new[] { "distant_soul" },
            tags: new[] { "rumor", "distant" });

        var rumors = await session.ExecuteAsync(new RumorsCommand());

        Assert.True(rumors.Success);
        Assert.False(rumors.ConsumedTurn);
        Assert.Equal("rumors", rumors.Action);
        Assert.Equal(RumorViewBuilder.BuildLines(session.Engine.State, limit: 12), rumors.Messages);
        Assert.Equal(
            RumorViewBuilder.Visible(session.Engine.State, limit: 12).Select(rumor => rumor.Id),
            session.View().Rumors!.Select(rumor => rumor.Id));
        Assert.Contains(rumors.Messages, message =>
            message.StartsWith("Rumor:", StringComparison.OrdinalIgnoreCase)
            && message.Contains("lock-whisperer", StringComparison.OrdinalIgnoreCase)
            && message.Contains("status active", StringComparison.OrdinalIgnoreCase)
            && message.Contains("hops 0", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.View().Rumors!, rumor =>
            rumor.SourceId == "heard_claim"
            && rumor.Status == "active"
            && rumor.OriginalText.Contains("lock-whisperer", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rumors.Messages, message =>
            message.Contains("distant rumor", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(rumors.Messages, message =>
            message.StartsWith("Lead:", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("Claim:", StringComparison.OrdinalIgnoreCase)
            || message.StartsWith("Promise:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecruitmentUsesBondThresholdAndPreservesMembership()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        await session.ExecuteAsync(new GiveCommand("grave salt", "Lio"));
        session.Engine.State.Bonds.Adjust("lio_soul", "player_soul", loyalty: 5, posture: "grateful");

        var recruit = await session.ExecuteAsync(new RecruitCommand("Lio"));
        var followers = await session.ExecuteAsync(new FollowersCommand());
        var lio = session.Engine.EntityById("prisoner_1")!;

        Assert.True(recruit.Success);
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruitFaction"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ChangeFaction)
            && Equals(delta.Details["faction"], "player")
            && Equals(delta.Details["membershipFactionId"], "hollowmere"));
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruitControl"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateControl)
            && Equals(delta.Details["controllerKind"], ControllerKind.Ai.ToString())
            && Equals(delta.Details["aiPolicyId"], "follower"));
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruitMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruit"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["posture"], "follower"));
        Assert.DoesNotContain(recruit.Deltas, delta => delta.Operation == "recruitmentSkipped");
        Assert.Equal("player", lio.Get<ActorComponent>().Faction);
        Assert.Equal("hollowmere", lio.Get<FactionComponent>().FactionId);
        Assert.Contains(lio.Get<FactionComponent>().Roles, role => role == "follower");
        Assert.Contains(followers.Messages, message => message.Contains("Lio of Hollowmere", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RecruitmentRefusalUsesMessageConsequenceAndBondUpdate()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var recruit = await session.ExecuteAsync(new RecruitCommand("Lio"));

        Assert.False(recruit.Success);
        Assert.True(recruit.ConsumedTurn);
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruitRefused"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["recruitScore"], 0));
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruitBond"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond));
    }

    [Fact]
    public async Task RecruitmentRefusalRollsBackIfBondUpdateRejects()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        session.Engine.State.ControlledEntity.Set(new SoulComponent(""));
        var turnBefore = session.Engine.State.Turn;
        var messageCount = session.Engine.State.Messages.Count;

        var recruit = await session.ExecuteAsync(new RecruitCommand("Lio"));

        Assert.False(recruit.Success);
        Assert.False(recruit.ConsumedTurn);
        Assert.Equal(turnBefore, session.Engine.State.Turn);
        Assert.Equal(messageCount, session.Engine.State.Messages.Count);
        Assert.Contains(recruit.Messages, message =>
            message.Contains("target soul id", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(recruit.Deltas, delta => delta.Operation == "recruitRefused");
        Assert.DoesNotContain(recruit.Deltas, delta => delta.Operation == "recruitBond");
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond));
        Assert.Contains(recruit.Deltas, delta =>
            delta.Operation == "recruitmentSkipped"
            && Equals(delta.Target, "prisoner_1")
            && Equals(delta.Details["failure"], "Bond consequence did not include a target soul id.")
            && Equals(delta.Details["rejectedCount"], 1)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task BondInspectionDoesNotCreateNeutralBondRecords()
    {
        var session = CreateMockSession();
        var before = session.Engine.State.Bonds.Bonds.Count;

        var result = await session.ExecuteAsync(new BondsCommand());

        Assert.True(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(before, session.Engine.State.Bonds.Bonds.Count);
        Assert.DoesNotContain(result.Deltas, delta =>
            delta.Details.TryGetValue("consequenceType", out var type)
            && Equals(type, WorldConsequenceTypes.UpdateBond));
    }

    [Fact]
    public async Task SecretPhraseDoesNotBindPromiseWithoutExtractor()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, trust me with a secret"));

        Assert.True(talk.Success);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialoguePromise");
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Source == "dialogue"
            || promise.Source.StartsWith("dialogue_claim:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeterministicPrisonerTalkDoesNotHardcodeBondShift()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, are you all right?"));

        Assert.True(talk.Success);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueBond");
        Assert.False(session.Engine.State.Bonds.TryGet("lio_soul", "player_soul", out _));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueExchangeMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
    }

    [Fact]
    public async Task GeneratedDialogueAppliesProviderClaimsImmediately()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio whispers, \"Past the wet road, Hollowmere keeps a red bridge for people in trouble.\"",
            Delivery: "hushed",
            Intent: "inform",
            Proposals: new DialogueProposalSet(
                Claims: new[]
                {
                    new DialogueClaimProposal(
                        "Past the wet road, Hollowmere keeps a red bridge for people in trouble.",
                        "landmark",
                        "red bridge",
                        Salience: 3,
                        Confidence: 78,
                        PlayerVisible: true,
                        BindAsPromise: true,
                        PromiseKind: "rumor",
                        RealizationKind: "site",
                        TriggerHint: "travel",
                        ClaimedPlace: "Hollowmere",
                        Tags: new[] { "hollowmere", "bridge", "landmark" }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));

        Assert.True(talk.Success);
        Assert.True(talk.ConsumedTurn);
        Assert.Contains(talk.Messages, message => message.Contains("red bridge", StringComparison.OrdinalIgnoreCase));
        var dialogue = Assert.Single(talk.Deltas, delta => delta.Operation == "dialogue");
        Assert.True((bool)dialogue.Details["generated"]!);
        Assert.False(dialogue.IsPlayerVisible());
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueMessage"
            && delta.IsPlayerVisible());
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "claimRecorded"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordClaim));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "claimMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "rumorMinted"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordRumor)
            && Equals(delta.Details["claimId"], "claim_1"));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "claimIntakeSkipped");
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueExchangeMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["speakerId"], "prisoner_1"));
        Assert.Contains(talk.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "red bridge" && claim.Status == "promised");
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.Provenance.StartsWith("claim:", StringComparison.OrdinalIgnoreCase)
            && memory.Text.Contains("red bridge", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.Provenance.Equals("conversation", StringComparison.OrdinalIgnoreCase)
            && memory.Text.Contains("red bridge", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueWithoutProviderClaimsStillQueuesClaimExtraction()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "The river flows south, past the burned oak that marks where soldiers rarely tread.",
            Delivery: "hushed",
            Intent: "inform",
            Proposals: null));
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "The burned oak marks a quiet southern route.",
            "landmark",
            "burned oak",
            Salience: 4,
            Confidence: 76,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "rumor",
            RealizationKind: "route",
            TriggerHint: "travel",
            ClaimedPlace: "south road",
            Tags: new[] { "landmark", "route", "burned_oak" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor,
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogue" && (bool)delta.Details["generated"]!);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "claimRecorded");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimRecorded"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordClaim));
        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "burned oak" && claim.Status == "promised");
    }

    [Fact]
    public async Task DialogueClaimExtractionFailureStaysAuditOnly()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "The river flows south, past the burned oak that marks where soldiers rarely tread.",
            Delivery: "hushed",
            Intent: "inform",
            Proposals: null));
        var extractor = new TechnicalFailureDialogueClaimExtractor();
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor,
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        var failure = Assert.Single(wait.Deltas, delta => delta.Operation == "claimExtractionFailed");
        Assert.Equal(true, failure.Details["auditOnly"]);
        Assert.Equal(false, failure.Details["playerVisible"]);
        Assert.False(failure.IsPlayerVisible());
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("claim extraction failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Deltas.PlayerMessages(), message =>
            message.Contains("claim extraction failed", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimRecorded");
    }

    [Fact]
    public async Task FaultedDialogueClaimExtractionMaterializesForReplay()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "The river flows south, past the burned oak that marks where soldiers rarely tread.",
            Delivery: "hushed",
            Intent: "inform",
            Proposals: null));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: new FaultingDialogueClaimExtractor(),
            dialogueProvider: provider,
            seed: 19);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        Assert.NotNull(talk.Dialogue);
        var record = Assert.Single(wait.DialogueClaimExtractions);
        Assert.True(record.TechnicalFailure);
        Assert.Equal("faulting-dialogue-claims", record.Provider);
        Assert.Contains("fixture task fault", record.Error);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimExtractionFailed"
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));

        var replay = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: new ReplayDialogueClaimExtractor(wait.DialogueClaimExtractions),
            dialogueProvider: new ReplayDialogueProvider(new[] { talk.Dialogue! }),
            seed: 19);
        DisableImperialAi(replay);
        OpenCellDoorWithoutCommand(replay);
        replay.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var replayTalk = await replay.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));
        var replayWait = await replay.ExecuteAsync(new WaitCommand());

        Assert.True(replayTalk.Success);
        var replayRecord = Assert.Single(replayWait.DialogueClaimExtractions);
        Assert.True(replayRecord.TechnicalFailure);
        Assert.Contains("fixture task fault", replayRecord.Error);
        Assert.Contains(replayWait.Deltas, delta =>
            delta.Operation == "claimExtractionFailed"
            && Equals(delta.Details["provider"], "faulting-dialogue-claims"));
        Assert.DoesNotContain(replayWait.Deltas, delta => delta.Operation == "claimRecorded");
    }

    [Fact]
    public async Task CanceledDialogueClaimExtractionMaterializesAsTechnicalFailure()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "The river flows south, past the burned oak that marks where soldiers rarely tread.",
            Delivery: "hushed",
            Intent: "inform",
            Proposals: null));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: new CanceledDialogueClaimExtractor(),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits outside?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        var record = Assert.Single(wait.DialogueClaimExtractions);
        Assert.True(record.TechnicalFailure);
        Assert.Equal("canceled-dialogue-claims", record.Provider);
        Assert.Equal("canceled", record.Error);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimExtractionFailed"
            && Equals(delta.Details["error"], "canceled")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimRecorded");
    }

    [Fact]
    public async Task GeneratedDialogueSkipsClaimsNotSupportedBySpokenText()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Jimmer sells blades in the lower quarter if you can slip past the ward-captain.",
            Delivery: "hushed",
            Intent: "inform",
            Proposals: new DialogueProposalSet(
                Claims: new[]
                {
                    new DialogueClaimProposal(
                        "Jimmer sells blades in the lower quarter.",
                        "merchant_stock",
                        "blades",
                        Salience: 4,
                        Confidence: 90,
                        PlayerVisible: true,
                        BindAsPromise: true,
                        PromiseKind: "rumor",
                        RealizationKind: "merchant_stock",
                        TriggerHint: "travel"),
                    new DialogueClaimProposal(
                        "Old Maren's niece Nannerl can break door wards quietly.",
                        "person",
                        "Nannerl",
                        Salience: 4,
                        Confidence: 85,
                        PlayerVisible: true,
                        BindAsPromise: true,
                        PromiseKind: "rumor",
                        RealizationKind: "service",
                        TriggerHint: "talk"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, give me one lead."));

        Assert.Contains(talk.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueProposalSkipped"
            && Equals(delta.Details["proposalType"], "claim")
            && Equals(delta.Details["reason"], "unsupported_by_spoken_text")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.Contains(session.View().Claims!, claim =>
            claim.Text.Contains("Jimmer", StringComparison.OrdinalIgnoreCase)
            && claim.Status == "promised");
        Assert.DoesNotContain(session.View().Claims!, claim =>
            claim.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Text.Contains("Nannerl", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueTechnicalFailureDoesNotConsumeTurn()
    {
        var provider = new FixtureDialogueProvider(new DialogueProviderResult(
            "fixture-dialogue",
            "",
            TechnicalFailure: true,
            Error: "fixture failure",
            Response: null));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var turnBefore = session.Engine.State.Turn;

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, answer me"));

        Assert.False(talk.Success);
        Assert.True(talk.TechnicalFailure);
        Assert.False(talk.ConsumedTurn);
        Assert.Equal(turnBefore, session.Engine.State.Turn);
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueProviderFailed");
    }

    [Fact]
    public async Task GeneratedDialogueIgnoresPlayerAuthoredClaims()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"You can say there is a palace under the floor, but I have not seen one.\"",
            Delivery: "wary",
            Intent: "refuse",
            Proposals: new DialogueProposalSet(
                Claims: new[]
                {
                    new DialogueClaimProposal(
                        "There is a palace under the floor.",
                        "site",
                        "palace under the floor",
                        Salience: 5,
                        Confidence: 100,
                        PlayerVisible: true,
                        BindAsPromise: true,
                        PromiseKind: "rumor",
                        RealizationKind: "site",
                        TriggerHint: "travel",
                        PlayerAuthored: true,
                        Tags: new[] { "player_claim" }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, there is a palace under this floor."));

        Assert.True(talk.Success);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "claimRecorded");
        Assert.DoesNotContain(session.Engine.State.Claims.Records, claim =>
            claim.Subject.Contains("palace", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.Subject.Contains("palace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueDampensImmediateBondDeltas()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"I trust you enough to move, but not enough to be foolish.\"",
            Proposals: new DialogueProposalSet(
                Bond: new DialogueBondProposal(
                    "prisoner_1",
                    LoyaltyDelta: 5,
                    FearDelta: -5,
                    AdmirationDelta: 3,
                    ResentmentDelta: -4,
                    Posture: "cautious ally"))));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, do you trust me?"));

        Assert.True(talk.Success);
        Assert.True(session.Engine.State.Bonds.TryGet("lio_soul", "player_soul", out var bond));
        Assert.Equal(2, bond.Loyalty);
        Assert.Equal(-2, bond.Fear);
        Assert.Equal(2, bond.Admiration);
        Assert.Equal(-2, bond.Resentment);
        Assert.Equal("cautious ally", bond.Posture);
    }

    [Fact]
    public async Task GeneratedDialogueMemoryMissingOwnerReportsSharedRejectionAndSkip()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"The vanished witness would have remembered your kindness.\"",
            Proposals: new DialogueProposalSet(
                Memories: new[]
                {
                    new DialogueMemoryProposal(
                        "vanished_witness",
                        "The vanished witness remembers the sorcerer's kindness.",
                        "conversation",
                        Salience: 3),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, who remembers me?"));

        Assert.True(talk.Success);
        Assert.DoesNotContain(session.Engine.State.Memories.Records, memory =>
            memory.SubjectId.Equals("vanished_witness", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && delta.Target == "vanished_witness"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "memory")
            && ((string)delta.Details["error"]!).Contains("owner entity does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueProposalSkipped"
            && delta.Target == "vanished_witness"
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "memory")
            && ((string)delta.Details["reason"]!).Contains("owner entity does not exist", StringComparison.OrdinalIgnoreCase)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task GeneratedDialogueBondMissingTargetReportsSharedRejectionAndSkip()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"The vanished witness would have trusted you.\"",
            Proposals: new DialogueProposalSet(
                Bond: new DialogueBondProposal(
                    "vanished_witness",
                    LoyaltyDelta: 3,
                    AdmirationDelta: 3,
                    Posture: "grateful",
                    Reason: "The model proposed a bond for a non-present entity."))));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var bondCountBefore = session.Engine.State.Bonds.Bonds.Count;

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, who else trusts me?"));

        Assert.True(talk.Success);
        Assert.Equal(bondCountBefore, session.Engine.State.Bonds.Bonds.Count);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && delta.Target == "vanished_witness"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond)
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "bond"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueProposalSkipped"
            && delta.Target == "vanished_witness"
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "bond")
            && Equals(delta.Details["reason"], "missing_entity")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
    }

    [Fact]
    public async Task GeneratedDialogueCanUpdateWantThroughSharedConsequence()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"If you get me through that door, I only want a quiet road south now.\"",
            Proposals: new DialogueProposalSet(
                Want: new DialogueWantProposal(
                    "prisoner_1",
                    Text: "Reach a quiet road south before Vigovia can name the helper.",
                    Salience: 4,
                    Status: "active",
                    Stakes: "Lio's escape has shifted from leaving the cell to moving quietly beyond the yard.",
                    AddTags: new[] { "road", "south" },
                    RemoveTags: new[] { "escape" },
                    Reason: "Lio reframed the immediate desire in spoken dialogue."))));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, the door is handled. What now?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueWantShift"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["proposalType"], "want")
            && Equals(delta.Details["previousStatus"], "active")
            && Equals(delta.Details["status"], "active"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueWantShiftMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["parentOperation"], "dialogueWantShift")
            && Equals(delta.Details["provenance"], "conversation"));
        var want = session.Engine.EntityById("prisoner_1")!.Get<WantComponent>();
        Assert.Contains("quiet road south", want.Text);
        Assert.Equal(4, want.Salience);
        Assert.Contains("promise_source", want.Tags);
        Assert.Contains("road", want.Tags);
        Assert.Contains("south", want.Tags);
        Assert.DoesNotContain("escape", want.Tags);
    }

    [Fact]
    public async Task GeneratedDialogueWantMissingTargetReportsSharedRejection()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"Someone who is not here wanted the quiet road too.\"",
            Proposals: new DialogueProposalSet(
                Want: new DialogueWantProposal(
                    "vanished_witness",
                    Text: "Reach a quiet road south before Vigovia writes the name down.",
                    Salience: 3,
                    AddTags: new[] { "road" },
                    Reason: "The model proposed a want for a non-present entity."))));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, who else needs the road?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && delta.Target == "vanished_witness"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "want")
            && ((string)delta.Details["error"]!).Contains("Want target does not exist", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueProposalSkipped"
            && delta.Target == "vanished_witness"
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "want")
            && ((string)delta.Details["reason"]!).Contains("Want target does not exist", StringComparison.OrdinalIgnoreCase)
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<WantComponent>(out var want)
            && want.Text.Contains("quiet road south", StringComparison.OrdinalIgnoreCase)
            && entity.Id.Value.Equals("vanished_witness", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeterministicThreatDialogueUsesSharedBondAndMemoryConsequences()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, obey me or fear what follows."));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "bondShift"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "threatMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["requireOwnerEntity"], true));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "threatDialogueSkipped");
        Assert.True(session.Engine.State.Bonds.TryGet("lio_soul", "player_soul", out var bond));
        Assert.Equal("afraid", bond.Posture);
        Assert.Equal(2, bond.Fear);
        Assert.Equal(1, bond.Resentment);
        Assert.Contains(session.Engine.State.Memories.Records, memory =>
            memory.SubjectId == "prisoner_1"
            && memory.Text.Contains("remembers the shape", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueWritesAuditEntry()
    {
        var audit = new FixtureDialogueAuditSink();
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"I heard you.\"",
            Delivery: "plain",
            Intent: "answer"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            dialogueAudit: audit);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, can you hear me?"));

        var entry = Assert.Single(audit.Entries);
        Assert.True(talk.Success);
        Assert.Equal("fixture-dialogue", entry.Provider);
        Assert.Equal("prisoner_1", entry.SpeakerId);
        Assert.Contains("Escape the containment yard", entry.Request.Speaker.Want);
        Assert.Contains(entry.Request.CapabilityCards, card =>
            card.Contains("recruit", StringComparison.OrdinalIgnoreCase)
            && card.Contains("offer_trade", StringComparison.OrdinalIgnoreCase)
            && card.Contains("reveal_service", StringComparison.OrdinalIgnoreCase)
            && card.Contains("canonize_fact", StringComparison.OrdinalIgnoreCase)
            && card.Contains("spawn_fixture", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("talk", entry.ResultAction);
        Assert.True(entry.ResultSuccess);
        Assert.Contains("dialogue", entry.DeltaOperations);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["speakerId"], "prisoner_1")
            && Equals(delta.Details["generated"], true)
            && Equals(delta.Details["provider"], "fixture-dialogue"));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueFallbackMessage");
        Assert.Single(
            session.Engine.State.Messages,
            message => message.Equals("Lio says, \"I heard you.\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GeneratedDialogueRequestIncludesWorldTurnStirredWantMemory()
    {
        var audit = new FixtureDialogueAuditSink();
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"The want has not left me.\""));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            dialogueAudit: audit);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var stir = new WorldTurnSystem().Apply(
            session.Engine.State,
            "test",
            budget: 2,
            announce: true,
            applyConsequence: session.Engine.ApplyConsequence);
        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what are you holding onto?"));

        var entry = Assert.Single(audit.Entries);
        Assert.Contains(stir, delta =>
            delta.Operation == "wantStirMemory"
            && delta.Target == "prisoner_1");
        Assert.True(talk.Success);
        Assert.Contains(entry.Request.RecentMemories, memory =>
            memory.Contains("want:want_lio_escape", StringComparison.OrdinalIgnoreCase)
            && memory.Contains("Escape the containment yard", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRequestIncludesWitnessedDeedMemory()
    {
        var audit = new FixtureDialogueAuditSink();
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"I saw enough.\""));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            dialogueAudit: audit);
        DisableImperialAi(session);
        KillImperialActors(session);
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(12, 5)));
        RecordDeed(
            session,
            player,
            "wild_magic",
            3,
            new GridPoint(12, 5),
            new GridPoint(13, 5),
            new[] { "damage" });
        session.Engine.AdvanceTurn();
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, what did you see?"));

        var entry = Assert.Single(audit.Entries);
        Assert.True(talk.Success);
        Assert.Contains(entry.Request.RecentMemories, memory =>
            memory.Contains("deed:", StringComparison.OrdinalIgnoreCase)
            && memory.Contains("wild magic", StringComparison.OrdinalIgnoreCase)
            && memory.Contains("someone unnamed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRequestIncludesRumorsHeardBySpeaker()
    {
        var audit = new FixtureDialogueAuditSink();
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "The soldier says, \"People talk.\"",
            Delivery: "wary",
            Intent: "inform"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            dialogueAudit: audit);
        DisableImperialAi(session);
        session.Engine.State.Rumors.Append(
            session.Engine.State.Turn,
            "test",
            "test_source",
            session.Engine.State.RegionId,
            session.Engine.State.RegionId,
            "The blue fire in the yard answered to a fugitive's hand.",
            salience: 4,
            carrierIds: new[] { "soldier_1_soul" },
            tags: new[] { "rumor", "wild_magic" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(8, 4)));

        var talk = await session.ExecuteAsync(new TalkCommand("soldier, what have you heard?"));

        var entry = Assert.Single(audit.Entries);
        Assert.True(talk.Success);
        Assert.Contains(entry.Request.RecentRumors!, rumor =>
            rumor.Contains("blue fire", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueCanMoveSpeakerAside()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test witness says, \"I will get out of your way.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("step_aside") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var witness = AddTestNpc(session, "test_witness", "Test witness", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));
        var before = witness.Get<PositionComponent>().Position;

        var talk = await session.ExecuteAsync(new TalkCommand("witness, please move aside"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueStepAside"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity));
        Assert.NotEqual(before, witness.Get<PositionComponent>().Position);
    }

    [Fact]
    public async Task GeneratedDialogueRefusalDoesNotApplyCooperativeAction()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test witness says, \"I will not move for you.\"",
            Intent: "refuse",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("step_aside") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var witness = AddTestNpc(session, "test_witness", "Test witness", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));
        var before = witness.Get<PositionComponent>().Position;

        var talk = await session.ExecuteAsync(new TalkCommand("witness, please move aside"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueActionRejected"
            && Equals(delta.Details["type"], "step_aside")
            && Equals(delta.Details["normalizedType"], "step_aside")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueStepAside");
        Assert.Equal(before, witness.Get<PositionComponent>().Position);
    }

    [Fact]
    public async Task GeneratedDialogueCanOpenReachableDoor()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Door keeper says, \"Fine. Open, then.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("open", TargetEntityId: "test_door") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "door_keeper", "Door keeper", new GridPoint(5, 5));
        var door = new Entity(EntityId.Create("test_door"), "test door")
            .Set(new PositionComponent(new GridPoint(6, 5)))
            .Set(new RenderableComponent('+', "door"))
            .Set(new TagsComponent(new[] { "door" }))
            .Set(new PhysicalComponent(BlocksMovement: true, BlocksSight: true, Material: "wood"))
            .Set(new DoorComponent(IsOpen: false));
        session.Engine.State.Entities[door.Id] = door;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(4, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("keeper, open the door"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueOpenDoor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OpenOrUnlock)
            && Equals(delta.Details["open"], true));
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.False(door.Get<PhysicalComponent>().BlocksMovement);
    }

    [Fact]
    public async Task GeneratedDialogueOpenDoorUsesSharedDoorConsequences()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Cell keeper says, \"I will open it.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("open_door", TargetEntityId: "cell_door_1") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var keeper = AddTestNpc(session, "cell_keeper", "Cell keeper", new GridPoint(12, 5));
        keeper.Set(new InventoryComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["imperial cell key"] = 1,
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(11, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("keeper, open the cell"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueOpenDoor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OpenOrUnlock));
        Assert.Contains(talk.Deltas, delta => delta.Operation == "realizePromise");
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "freeCaptiveFaction"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ChangeFaction)
            && Equals(delta.Details["membershipFactionId"], "hollowmere"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "freeCaptiveControl"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateControl)
            && Equals(delta.Details["aiPolicyId"], "follower"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "freeCaptiveWant"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["status"], "satisfied")
            && ((IReadOnlyList<string>)delta.Details["tags"]!).Contains("satisfied_by_player"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "freeCaptiveWantMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory)
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.UpdateWant)
            && Equals(delta.Details["parentOperation"], "freeCaptiveWant")
            && Equals(delta.Details["provenance"], "free_captive"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "freeCaptiveMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["faction"], "player"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "freeCaptive"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.FreeCaptive)
            && Equals(delta.Details["captiveId"], "prisoner_1"));
        Assert.Single(
            session.Engine.State.Messages,
            message => message.Contains("free enough to choose you", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("player", session.Engine.EntityById("prisoner_1")!.Get<ActorComponent>().Faction);
        var want = session.Engine.EntityById("prisoner_1")!.Get<WantComponent>();
        Assert.Equal("satisfied", want.Status);
        Assert.Contains("satisfied_by_player", want.Tags);
        Assert.Contains("rescued", want.Tags);
        Assert.DoesNotContain("escape", want.Tags);
    }

    [Fact]
    public void FreeCaptiveConsequenceRollsBackIfDeedCannotBeRecorded()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var prisoner = session.Engine.EntityById("prisoner_1")!;
        var prisonerFactionBefore = prisoner.Get<ActorComponent>().Faction;
        var deedCountBefore = session.Engine.State.Deeds.Records.Count;
        prisoner.Remove<PositionComponent>();

        var result = session.Engine.ApplyConsequence(WorldConsequence.FreeCaptive(
            "test",
            prisoner.Id.Value,
            session.Engine.State.ControlledEntityId.Value,
            evidence: "Test release without a captive position.",
            operation: "testFreeCaptive"));
        var restoredPrisoner = session.Engine.EntityById("prisoner_1")!;

        Assert.False(result.Applied);
        Assert.Equal(prisonerFactionBefore, restoredPrisoner.Get<ActorComponent>().Faction);
        Assert.Equal(deedCountBefore, session.Engine.State.Deeds.Records.Count);
        Assert.DoesNotContain(result.Messages, message =>
            message.Contains("free enough to choose you", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "testFreeCaptiveFaction");
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "testFreeCaptiveControl");
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "testFreeCaptiveMessage");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "freeCaptiveSkipped"
            && Equals(delta.Details["failure"], "missing_deed_position")
            && !delta.IsPlayerVisible());
    }

    [Fact]
    public void OpenDoorRollsBackWhenCaptiveReleaseChildFails()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var door = session.Engine.EntityById("cell_door_1")!;
        var prisoner = session.Engine.EntityById("prisoner_1")!;
        var prisonerFactionBefore = prisoner.Get<ActorComponent>().Faction;
        var deedCountBefore = session.Engine.State.Deeds.Records.Count;
        session.Engine.State.ControlledEntity.Remove<PositionComponent>();

        var result = session.Engine.ApplyConsequence(WorldConsequence.OpenOrUnlock(
            "test",
            door.Id.Value,
            visibility: WorldConsequenceVisibility.Message,
            operation: "testOpenDoor"));
        var restoredDoor = session.Engine.EntityById("cell_door_1")!;
        var restoredPrisoner = session.Engine.EntityById("prisoner_1")!;

        Assert.False(result.Applied);
        Assert.Equal("missing_deed_position", result.Error);
        Assert.False(restoredDoor.Get<DoorComponent>().IsOpen);
        Assert.Equal("imperial cell key", restoredDoor.Get<DoorComponent>().KeyId);
        Assert.True(restoredDoor.Get<PhysicalComponent>().BlocksMovement);
        Assert.Equal(prisonerFactionBefore, restoredPrisoner.Get<ActorComponent>().Faction);
        Assert.Equal(deedCountBefore, session.Engine.State.Deeds.Records.Count);
        Assert.Empty(result.Messages);
        Assert.DoesNotContain(session.Engine.State.Messages, message =>
            message.Contains("free enough to choose", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "testOpenDoor");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "freeCaptiveSkipped"
            && Equals(delta.Details["failure"], "missing_deed_position")
            && !delta.IsPlayerVisible());
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "openOrUnlockSkipped"
            && Equals(delta.Details["operation"], "testOpenDoor")
            && Equals(delta.Details["failure"], "missing_deed_position")
            && Equals(delta.Details["rolledBackDeltaCount"], 1)
            && !delta.IsPlayerVisible());
    }

    [Fact]
    public async Task GeneratedDialogueCanGiveSpeakerItem()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Quartermaster says, \"Take the brass key.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("give", ItemName: "brass key") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var giver = AddTestNpc(session, "quartermaster", "Quartermaster", new GridPoint(5, 5));
        giver.Set(new InventoryComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["brass key"] = 1,
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("quartermaster, can you give the brass key to me?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueGiveItem"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransferItem)
            && Equals(delta.Details["mode"], "give")
            && Equals(delta.Details["recipientEntityId"], "player"));
        Assert.False(giver.Get<InventoryComponent>().Items.ContainsKey("brass key"));
        Assert.Equal(1, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["brass key"]);
    }

    [Fact]
    public async Task GeneratedDialogueCanCallHelp()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Imperial caller says, \"Clerk! To me!\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("call_help") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(
            session,
            "imperial_caller",
            "Imperial caller",
            new GridPoint(5, 5),
            faction: "empire",
            tags: new[] { "imperial", "caller" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("caller, call help"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueCallHelp"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ScheduleEvent)
            && Equals(delta.Details["eventType"], "empire_patrol"));
        Assert.Contains(session.Engine.State.ScheduledEvents.Events, item =>
            item.Kind == "empire_patrol"
            && item.SourceEntityId == EntityId.Create("imperial_caller"));
    }

    [Fact]
    public async Task GeneratedDialogueCanRecruitThroughSharedRecruitmentRules()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"If the road has earned that much trust, I will come.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal("recruit", Reason: "The speaker agreed to follow in dialogue."),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        session.Engine.State.Bonds.Adjust("lio_soul", "player_soul", loyalty: 5, posture: "grateful");
        var turnBefore = session.Engine.State.Turn;

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, follow me"));
        var followers = await session.ExecuteAsync(new FollowersCommand());
        var lio = session.Engine.EntityById("prisoner_1")!;

        Assert.True(talk.Success);
        Assert.True(talk.ConsumedTurn);
        Assert.Equal(turnBefore + 1, talk.TurnAfter);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruitFaction"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ChangeFaction)
            && Equals(delta.Details["faction"], "player")
            && Equals(delta.Details["membershipFactionId"], "hollowmere")
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "recruit"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruitControl"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateControl)
            && Equals(delta.Details["aiPolicyId"], "follower"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruitBond"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruitMemory"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordMemory));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruit"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["posture"], "follower"));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "recruitmentSkipped");
        Assert.Equal("player", lio.Get<ActorComponent>().Faction);
        Assert.Equal("hollowmere", lio.Get<FactionComponent>().FactionId);
        Assert.Contains(lio.Get<FactionComponent>().Roles, role => role == "follower");
        Assert.Contains(followers.Messages, message => message.Contains("Lio of Hollowmere", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRecruitRefusalUsesSharedRecruitmentConsequences()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio says, \"No. I will not make a life out of one conversation.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal("follow_me", Reason: "The speaker declined to follow."),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var turnBefore = session.Engine.State.Turn;

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, follow me"));
        var lio = session.Engine.EntityById("prisoner_1")!;

        Assert.True(talk.Success);
        Assert.True(talk.ConsumedTurn);
        Assert.Equal(turnBefore + 1, talk.TurnAfter);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruitRefused"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "recruit")
            && Equals(delta.Details["recruitScore"], 0));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRecruitBond"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateBond));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueRecruitFaction");
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueRecruitControl");
        Assert.Equal("hollowmere", lio.Get<ActorComponent>().Faction);
        Assert.NotEqual("player", lio.Get<ActorComponent>().Faction);
        Assert.Equal("hollowmere", lio.Get<FactionComponent>().FactionId);
    }

    [Fact]
    public async Task GeneratedDialogueCanAttackAdjacentTarget()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test duelist says, \"Then take the blow.\"",
            Intent: "threaten",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("attack", TargetEntityId: "player") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_duelist", "Test duelist", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));
        var hpBefore = session.Engine.State.ControlledEntity.Get<ActorComponent>().HitPoints;

        var talk = await session.ExecuteAsync(new TalkCommand("duelist, fight me"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueAttack"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Damage)
            && Equals(delta.Details["attacker"], "test_duelist"));
        Assert.True(session.Engine.State.ControlledEntity.Get<ActorComponent>().HitPoints < hpBefore);
        Assert.Contains(talk.Messages, message =>
            message.Contains("Test duelist strikes you", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.Engine.State.Messages, message =>
            message.Contains("Test duelist strikes you", StringComparison.OrdinalIgnoreCase));
        // The "Your step becomes a strike." framing beat is specific to a *move* resolving as an
        // attack; an explicit dialogue-triggered attack is not a walk-into-a-fight moment and
        // must not get it.
        Assert.DoesNotContain(talk.Messages, message => message.Contains("becomes a strike", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueAttackHidesPersistentEffectLifecycleBookkeeping()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test duelist says, \"Then take the blow.\"",
            Intent: "threaten",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("attack", TargetEntityId: "player") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var duelist = AddTestNpc(session, "test_duelist", "Test duelist", new GridPoint(5, 5));
        var player = session.Engine.State.ControlledEntity;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));
        var ward = session.Engine.ApplyConsequence(WorldConsequence.CreatePersistentEffect(
            "test",
            player.Id.Value,
            "on_hit",
            "damage",
            new Dictionary<string, object?>
            {
                ["target"] = "other",
                ["amount"] = 2,
                ["damageType"] = "thorns",
            },
            uses: 1,
            playerVisible: false,
            sourceEntityId: player.Id.Value,
            operation: "testThornWard"));
        Assert.True(ward.Applied);
        var duelistHpBefore = duelist.Get<ActorComponent>().HitPoints;

        var talk = await session.ExecuteAsync(new TalkCommand("duelist, fight me"));

        Assert.True(talk.Success);
        Assert.True(duelist.Get<ActorComponent>().HitPoints < duelistHpBefore);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "persistentDamage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Damage));
        var lifecycle = Assert.Single(talk.Deltas, delta => delta.Operation == "updatePersistentEffect");
        Assert.False(lifecycle.IsPlayerVisible());
        Assert.DoesNotContain(talk.Messages, message =>
            message.StartsWith("Persistent effect", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(talk.Deltas.PlayerMessages(), message =>
            message.StartsWith("Persistent effect", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRejectsNonAdjacentAttack()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Distant duelist says, \"I would strike you if I could.\"",
            Intent: "threaten",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("attack", TargetEntityId: "distant_target") })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "distant_duelist", "Distant duelist", new GridPoint(5, 5));
        var distantTarget = AddTestNpc(session, "distant_target", "Distant target", new GridPoint(8, 8));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));
        var hpBefore = distantTarget.Get<ActorComponent>().HitPoints;

        var talk = await session.ExecuteAsync(new TalkCommand("duelist, fight me"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueActionRejected"
            && Equals(delta.Details["type"], "attack")
            && ((string)delta.Details["reason"]!).Contains("not adjacent", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueAttack");
        Assert.Equal(hpBefore, distantTarget.Get<ActorComponent>().HitPoints);
    }

    [Fact]
    public async Task GeneratedDialogueCanOfferTrade()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test peddler says, \"I can sell you silver pins.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal("offer_trade", ItemName: "silver pin", Quantity: 2, Gold: 9),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var peddler = AddTestNpc(session, "test_peddler", "Test peddler", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("peddler, show me your wares"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueOfferTrade"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade)
            && Equals(delta.Details["item"], "silver pin"));
        var stock = peddler.Get<MerchantComponent>();
        Assert.Equal(2, stock.Wares["silver pin"]);
        Assert.Contains(peddler.Get<InteractableComponent>().Verbs, verb =>
            verb.Equals("buy", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueCanApplyGenericTypedConsequence()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"I will wear the helpful cord where anyone can see it.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "consequence",
                        TargetEntityId: "test_clerk",
                        Reason: "The clerk agreed to take on a visible helpful sign.",
                        ConsequenceType: "addTags",
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["tags"] = new[] { "helpful" },
                        }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var clerk = AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, will you mark yourself as helpful?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueConsequence"
            && delta.Target == "test_clerk"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "consequence"));
        Assert.Contains("helpful", clerk.Get<TagsComponent>().Tags);
    }

    [Fact]
    public async Task GeneratedDialogueGenericConsequenceAcceptsNestedPayloadAliases()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"Give me a breath and I will tie the alias mark quietly.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "world_consequence",
                        Reason: "The clerk agreed to take on a quiet delayed mark.",
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["world_consequence_type"] = "addTags",
                            ["target_id"] = "test_clerk",
                            ["consequence_timing"] = WorldConsequenceTiming.Deferred,
                            ["consequence_visibility"] = WorldConsequenceVisibility.Hidden,
                            ["tags"] = new[] { "alias_marked" },
                            ["operation"] = "dialogueNestedAliasAddTags",
                            ["delay"] = 2,
                        }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var clerk = AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, mark yourself quietly in a moment"));

        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "scheduleTimedConsequence"
            && Equals(delta.Details["scheduledConsequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["scheduledTiming"], WorldConsequenceTiming.Deferred));
        Assert.False(clerk.TryGet<TagsComponent>(out var beforeTags)
            && beforeTags.Tags.Contains("alias_marked", StringComparer.OrdinalIgnoreCase));

        var waitDeltas = new List<StateDelta>();
        for (var index = 0; index < 3; index++)
        {
            var wait = await session.ExecuteAsync(new WaitCommand());
            Assert.True(wait.Success);
            waitDeltas.AddRange(wait.Deltas);
            if (clerk.Get<TagsComponent>().Tags.Contains("alias_marked", StringComparer.OrdinalIgnoreCase))
            {
                break;
            }
        }

        Assert.Contains("alias_marked", clerk.Get<TagsComponent>().Tags);
        Assert.Contains(waitDeltas, delta =>
            delta.Operation == "dialogueNestedAliasAddTags"
            && delta.Target == "test_clerk"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["visibility"], WorldConsequenceVisibility.Hidden));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueGenericConsequenceCanUseSharedTiming()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"Give me a breath and I will mark the ledger.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "consequence",
                        TargetEntityId: "test_clerk",
                        Reason: "The clerk agreed to mark the ledger after a pause.",
                        ConsequenceType: "addTags",
                        ConsequenceTiming: WorldConsequenceTiming.Deferred,
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["tags"] = new[] { "ledger_marked" },
                            ["operation"] = "dialogueTimedAddTags",
                            ["delay"] = 2,
                        }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var clerk = AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, mark the ledger in a moment"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "scheduleTimedConsequence"
            && Equals(delta.Details["scheduledConsequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["scheduledTiming"], WorldConsequenceTiming.Deferred));
        Assert.False(clerk.TryGet<TagsComponent>(out var beforeTags)
            && beforeTags.Tags.Contains("ledger_marked", StringComparer.OrdinalIgnoreCase));

        var waitDeltas = new List<StateDelta>();
        for (var index = 0; index < 3; index++)
        {
            var wait = await session.ExecuteAsync(new WaitCommand());
            Assert.True(wait.Success);
            waitDeltas.AddRange(wait.Deltas);
            if (clerk.Get<TagsComponent>().Tags.Contains("ledger_marked", StringComparer.OrdinalIgnoreCase))
            {
                break;
            }
        }

        Assert.Contains("ledger_marked", clerk.Get<TagsComponent>().Tags);
        Assert.Contains(waitDeltas, delta =>
            delta.Operation == "dialogueTimedAddTags"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueGenericConsequenceCanTransformFixtureProp()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"I know the word that folds that bridge.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "consequence",
                        TargetEntityId: "old_bridge_1",
                        Reason: "The clerk used the shared grammar to collapse a fixture.",
                        ConsequenceType: "transform_entity",
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["operation"] = "dialogueCollapseBridge",
                            ["name"] = "collapsed bell bridge",
                            ["material"] = "splintered wood",
                            ["description"] = "A bridge folded by a clerk's whispered route-word.",
                            ["blocksMovement"] = true,
                            ["fixtureType"] = "collapsed_bridge",
                            ["glyph"] = "=",
                            ["palette"] = "ruin",
                            ["tags"] = new[] { "collapsed" },
                            ["removeTags"] = new[] { "standing" },
                            ["interactableVerbs"] = new[] { "examine", "climb" },
                        }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var clerk = AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5));
        var bridge = AddBridgeFixture(session, "old_bridge", new GridPoint(4, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, fold that old bridge"));

        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.Equal("collapsed bell bridge", bridge.Name);
        Assert.True(bridge.Get<PhysicalComponent>().BlocksMovement);
        Assert.Equal("collapsed_bridge", bridge.Get<FixtureComponent>().FixtureType);
        Assert.Equal('=', bridge.Get<RenderableComponent>().Glyph);
        Assert.Contains("collapsed", bridge.Get<TagsComponent>().Tags);
        Assert.DoesNotContain("standing", bridge.Get<TagsComponent>().Tags);
        Assert.Contains("climb", bridge.Get<InteractableComponent>().Verbs);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueCollapseBridge"
            && delta.Target == bridge.Id.Value
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.TransformEntity)
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "consequence"));
        Assert.Equal("Test clerk", clerk.Name);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueGenericConsequenceCanRequestServiceForPlayer()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio whispers, \"Give me the salt, and I will worry the lock open.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "consequence",
                        Reason: "Lio agreed to perform the known ward-breaking service now.",
                        ConsequenceType: "requestService",
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["service"] = "ward-breaking",
                            ["operation"] = "dialogueRequestService",
                        }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        var door = session.Engine.EntityById("cell_door_1")!;
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "ward_breaking",
            "ward-breaking",
            "A hush-hush folk charm that worries a lock open.",
            "open_or_unlock",
            itemCost: "grave salt",
            targetHint: "cell door",
            operation: "testOfferService"));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, can you break the ward now?"));

        Assert.True(offer.Applied, offer.Error);
        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRequestService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["serviceId"], "ward_breaking")
            && Equals(delta.Details["actorEntityId"], "player"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "serviceCost"
            && Equals(delta.Details["parentConsequenceType"], WorldConsequenceTypes.RequestService));
        Assert.Contains(talk.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Equal(1, session.Engine.State.ControlledEntity.Get<InventoryComponent>().Items["grave salt"]);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "serviceRequestSkipped");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueCanUseDirectConsequenceTypeAction()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio whispers, \"Give me the salt, and I will worry the lock open.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "request_service",
                        Reason: "Lio named the service as an immediate action.",
                        ConsequencePayload: new Dictionary<string, object?>
                        {
                            ["service"] = "ward-breaking",
                            ["operation"] = "dialogueDirectRequestService",
                        }),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        var door = session.Engine.EntityById("cell_door_1")!;
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "ward_breaking",
            "ward-breaking",
            "A hush-hush folk charm that worries a lock open.",
            "open_or_unlock",
            itemCost: "grave salt",
            targetHint: "cell door",
            operation: "testOfferService"));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, do the ward-breaking now."));

        Assert.True(offer.Applied, offer.Error);
        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueDirectRequestService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["proposalType"], "consequence")
            && Equals(delta.Details["serviceId"], "ward_breaking")
            && Equals(delta.Details["actorEntityId"], "player"));
        Assert.Contains(talk.Deltas, delta => delta.Operation == "serviceOpenOrUnlock");
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.DoesNotContain(talk.Deltas, delta =>
            delta.Operation == "dialogueActionRejected"
            && Equals(delta.Details["normalizedType"], "request_service"));
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueDirectConsequencePromotesTopLevelActionFields()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Lio whispers, \"I will mark myself helpful and worry the lock open.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "add_tags",
                        TargetEntityId: "prisoner_1",
                        Reason: "Lio accepts the helpful mark.",
                        Tags: new[] { "helpful" }),
                    new DialogueActionProposal(
                        "request_service",
                        Reason: "Lio agreed to perform the known ward-breaking service now.",
                        ServiceId: "ward_breaking"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;
        var door = session.Engine.EntityById("cell_door_1")!;
        var offer = session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            lio.Id.Value,
            "ward_breaking",
            "ward-breaking",
            "A hush-hush folk charm that worries a lock open.",
            "open_or_unlock",
            itemCost: "grave salt",
            targetHint: "cell door",
            operation: "testOfferService"));

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, mark yourself and do the ward-breaking now."));

        Assert.True(offer.Applied, offer.Error);
        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.Contains("helpful", lio.Get<TagsComponent>().Tags);
        Assert.True(door.Get<DoorComponent>().IsOpen);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueConsequence"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddTags)
            && Equals(delta.Details["proposalType"], "consequence"));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueConsequence"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RequestService)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueDirectConsequenceAppliesParsedTopLevelPayloadFields()
    {
        var rawDialogue = """
            {"spokenText":"Lio whispers, \"Hold still; I can mark myself for two breaths.\"","proposals":{"actions":[{"type":"apply_status","targetEntityId":"prisoner_1","status":"oath-marked","duration":2,"displayName":"oath marked"}]}}
            """;
        var provider = new OllamaDialogueProvider(
            httpClient: new HttpClient(new QueueHttpHandler(ChatResponse(rawDialogue))),
            model: "test-model");
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var lio = session.Engine.EntityById("prisoner_1")!;

        var talk = await session.ExecuteAsync(new TalkCommand("Lio, can you mark yourself?"));

        Assert.True(talk.Success, string.Join(" | ", talk.Messages));
        Assert.Contains(lio.Get<StatusContainerComponent>().Statuses, status =>
            status.Id.Equals("oath_marked", StringComparison.OrdinalIgnoreCase)
            && status.DisplayName.Equals("oath marked", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueConsequence"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.ApplyStatus)
            && Equals(delta.Details["status"], "oath_marked")
            && Equals(delta.Details["duration"], 2));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "worldConsequenceRejected");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task GeneratedDialogueCanRevealService()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test mender says, \"I can worry a lock open if you bring grave salt.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "reveal_service",
                        Name: "ward-breaking",
                        Description: "A hush-hush folk charm that worries a lock open.",
                        Tags: new[] { "service", "folk_magic", "door" },
                        ServiceId: "ward_breaking",
                        EffectKind: "open_or_unlock",
                        TargetHint: "cell door",
                        ItemCost: "grave salt"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var mender = AddTestNpc(session, "test_mender", "Test mender", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("mender, what service can you offer?"));
        var services = await session.ExecuteAsync(new ServicesCommand("mender"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueRevealService"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferService)
            && Equals(delta.Details["serviceId"], "ward_breaking")
            && Equals(delta.Details["effectKind"], "open_or_unlock")
            && Equals(delta.Details["itemCost"], "grave salt"));
        var offer = Assert.Single(mender.Get<ServiceComponent>().Offers);
        Assert.Equal("ward_breaking", offer.Id);
        Assert.Equal("grave salt", offer.ItemCost);
        Assert.Contains(mender.Get<InteractableComponent>().Verbs, verb =>
            verb.Equals("services", StringComparison.OrdinalIgnoreCase));
        Assert.True(services.Success);
        Assert.Contains(services.Messages, message =>
            message.Contains("ward-breaking", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueServiceClaimReusesRevealedServiceAction()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test mender says, \"I can worry a lock open if you bring grave salt.\"",
            Proposals: new DialogueProposalSet(
                Claims: new[]
                {
                    new DialogueClaimProposal(
                        "Test mender can worry a lock open if you bring grave salt.",
                        "service",
                        "ward-breaking",
                        Salience: 3,
                        Confidence: 85,
                        PlayerVisible: true,
                        TargetEntityId: "test_mender",
                        ItemName: "ward-breaking",
                        TriggerHint: "cell door",
                        Tags: new[] { "service", "folk_magic", "door" }),
                },
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "reveal_service",
                        Name: "ward-breaking",
                        Description: "A hush-hush folk charm that worries a lock open.",
                        Tags: new[] { "service", "folk_magic", "door" },
                        ServiceId: "ward_breaking",
                        EffectKind: "open_or_unlock",
                        TargetHint: "cell door",
                        ItemCost: "grave salt"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        var mender = AddTestNpc(session, "test_mender", "Test mender", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("mender, what service can you offer?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueRevealService");
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "claimOfferService");
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "claimApplied"
            && Equals(delta.Details["appliedTo"], "test_mender")
            && Equals(delta.Details["matchedExistingService"], true)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        var offer = Assert.Single(mender.Get<ServiceComponent>().Offers);
        Assert.Equal("ward_breaking", offer.Id);
        Assert.Contains(session.View().Claims!, claim =>
            claim.Subject == "ward-breaking"
            && claim.Status == "applied"
            && claim.AppliedTo == "test_mender");
    }

    [Fact]
    public async Task ExtractedDialogueServiceClaimReusesRevealedServiceActionBetweenTurns()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test mender says, \"I can worry a lock open if you bring grave salt.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "reveal_service",
                        Name: "ward-breaking",
                        Description: "A hush-hush folk charm that worries a lock open.",
                        Tags: new[] { "service", "folk_magic", "door" },
                        ServiceId: "ward_breaking",
                        EffectKind: "open_or_unlock",
                        TargetHint: "cell door",
                        ItemCost: "grave salt"),
                })));
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Test mender can worry a lock open if you bring grave salt.",
            "service",
            "ward-breaking",
            Salience: 3,
            Confidence: 85,
            PlayerVisible: true,
            TargetEntityId: "test_mender",
            ItemName: "ward-breaking",
            TriggerHint: "cell door",
            Tags: new[] { "service", "folk_magic", "door" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            claimExtractor: extractor);
        DisableImperialAi(session);
        var mender = AddTestNpc(session, "test_mender", "Test mender", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("mender, what service can you offer?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueRevealService");
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimOfferService");
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimApplied"
            && Equals(delta.Details["appliedTo"], "test_mender")
            && Equals(delta.Details["matchedExistingService"], true)
            && Equals(delta.Details["serviceId"], "ward_breaking"));
        Assert.Single(mender.Get<ServiceComponent>().Offers);
    }

    [Fact]
    public async Task GeneratedDialogueCanCreatePromiseAction()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test prophet says, \"I swear the north bell will answer when you travel toward it.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "create_promise",
                        PromiseKind: "rumor",
                        PromiseText: "The north bell will answer when you travel toward it.",
                        TriggerHint: "travel",
                        RealizationKind: "site",
                        ClaimedPlace: "north of here",
                        Subject: "north bell",
                        PlayerVisible: true,
                        Salience: 4),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_prophet", "Test prophet", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("prophet, promise me"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueCreatePromise"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.CreatePromise)
            && Equals(delta.Details["kind"], "rumor")
            && Equals(delta.Details["triggerHint"], "travel")
            && Equals(delta.Details["realizationKind"], "site")
            && Equals(delta.Details["playerVisible"], true)
            && Equals(delta.Details["salience"], 4));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Source.Equals("dialogue:fixture-dialogue", StringComparison.OrdinalIgnoreCase)
            && item.Text.Contains("north bell", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("north of here", promise.ClaimedPlace);
        Assert.Equal("unbound", promise.Status);
        Assert.Contains(session.Engine.State.Messages, message =>
            message.Contains("north bell will answer", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRefusalDoesNotCreatePromiseAction()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test prophet says, \"No. I swear nothing to you.\"",
            Intent: "refuse",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "create_promise",
                        PromiseKind: "rumor",
                        PromiseText: "The false bell will answer.",
                        TriggerHint: "travel"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_prophet", "Test prophet", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));
        var before = session.Engine.State.PromiseLedger.Promises.Count;

        var talk = await session.ExecuteAsync(new TalkCommand("prophet, promise me"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueActionRejected"
            && Equals(delta.Details["type"], "create_promise"));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueCreatePromise");
        Assert.Equal(before, session.Engine.State.PromiseLedger.Promises.Count);
    }

    [Fact]
    public async Task GeneratedDialogueCanCanonizeSupportedLocalFact()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"The local law is simple: folk-magic practice is punishable by execution here.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "canonize_fact",
                        Tags: new[] { "folk_magic", "vigovia" },
                        CanonKind: "local_law",
                        CanonText: "Folk-magic practice is punishable by execution here.",
                        CanonSummary: "Folk magic is a capital crime"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5), tags: new[] { "npc", "functionary" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, what is the local law?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueAddCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["kind"], "local_law")
            && Equals(delta.Details["attachedTo"], "test_clerk")
            && Equals(delta.Details["provider"], "fixture-dialogue")
            && Equals(delta.Details["proposalType"], "add_canon")
            && Equals(delta.Details["playerVisible"], false)
            && !delta.IsPlayerVisible());
        var canon = Assert.Single(session.Engine.State.Canon.Records, record =>
            record.Kind == "local_law"
            && record.AttachedTo == "test_clerk"
            && record.Text.Contains("punishable by execution", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("folk_magic", canon.Tags);
        Assert.Contains("canonized", canon.Tags);
        Assert.DoesNotContain(talk.Messages, message =>
            message.Contains("Folk magic is a capital crime", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractedDialogueCanCanonizeDurableFactBetweenTurns()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"The local law is simple: folk-magic practice is punishable by execution here.\""));
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Folk-magic practice is punishable by execution here.",
            "local_law",
            "folk magic law",
            Salience: 2,
            Confidence: 90,
            Tags: new[] { "folk_magic", "vigovia" },
            BindAsCanon: true,
            CanonKind: "local_law",
            CanonSummary: "Folk magic is a capital crime"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            claimExtractor: extractor);
        DisableImperialAi(session);
        AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5), tags: new[] { "npc", "functionary" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, what is the local law?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.True(talk.Success);
        Assert.True(wait.Success);
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimAddCanon"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.AddCanon)
            && Equals(delta.Details["kind"], "local_law")
            && Equals(delta.Details["attachedTo"], "test_clerk")
            && Equals(delta.Details["provider"], "fixture-dialogue-claims")
            && Equals(delta.Details["proposalType"], "add_canon")
            && !delta.IsPlayerVisible());
        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimApplied"
            && Equals(delta.Details["proposalType"], "add_canon")
            && Equals(delta.Details["canonKind"], "local_law")
            && Equals(delta.Details["appliedTo"], "test_clerk"));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimApplicationSkipped");
        var canon = Assert.Single(session.Engine.State.Canon.Records, record =>
            record.Kind == "local_law"
            && record.AttachedTo == "test_clerk"
            && record.Text.Contains("punishable by execution", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("folk_magic", canon.Tags);
        Assert.Contains("dialogue_claim", canon.Tags);
        Assert.Contains("canonized", canon.Tags);
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("Folk magic is a capital crime", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractedPlayerAuthoredCanonClaimIsIgnored()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"You said it, not me.\""));
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Folk-magic practice is punishable by execution here.",
            "local_law",
            "folk magic law",
            PlayerAuthored: true,
            BindAsCanon: true,
            CanonKind: "local_law"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            claimExtractor: extractor);
        DisableImperialAi(session);
        AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5), tags: new[] { "npc", "functionary" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        await session.ExecuteAsync(new TalkCommand("folk magic is a capital crime here, right?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimAddCanon");
        Assert.DoesNotContain(session.Engine.State.Canon.Records, record =>
            record.Text.Contains("punishable by execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExtractedUnsupportedCanonClaimIsSkipped()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"I know nothing about the southern road.\""));
        var extractor = new FixtureDialogueClaimExtractor(
            true,
            new DialogueClaimProposal(
                "Folk-magic practice is punishable by execution here.",
                "local_law",
                "folk magic law",
                Salience: 2,
                Confidence: 90,
                BindAsCanon: true,
                CanonKind: "local_law"));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider,
            claimExtractor: extractor);
        DisableImperialAi(session);
        AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5), tags: new[] { "npc", "functionary" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        await session.ExecuteAsync(new TalkCommand("clerk, what is the local law?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(wait.Deltas, delta =>
            delta.Operation == "claimExtractionSkipped"
            && Equals(delta.Details["provider"], "fixture-dialogue-claims")
            && Equals(delta.Details["reason"], "unsupported_by_spoken_text")
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(wait.Deltas, delta => delta.Operation == "claimAddCanon");
        Assert.DoesNotContain(wait.Deltas, delta =>
            delta.Operation == "recordClaim"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.RecordClaim));
        Assert.DoesNotContain(session.Engine.State.Canon.Records, record =>
            record.Text.Contains("punishable by execution", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Claims.Records, record =>
            record.Text.Contains("punishable by execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRejectsUnsupportedCanonFact()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test clerk says, \"I know nothing about the southern road.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "canonize_fact",
                        CanonKind: "local_law",
                        CanonText: "Folk-magic practice is punishable by execution here.",
                        CanonSummary: "Folk magic is a capital crime"),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_clerk", "Test clerk", new GridPoint(5, 5), tags: new[] { "npc", "functionary" });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("clerk, what is the local law?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueActionRejected"
            && Equals(delta.Details["type"], "canonize_fact")
            && ((string)delta.Details["reason"]!).Contains("not supported", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueAddCanon");
        Assert.DoesNotContain(session.Engine.State.Canon.Records, record =>
            record.Text.Contains("punishable by execution", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialoguePromiseActionTreatsUnresolvedTargetAsHint()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test prophet says, \"The north bell will answer when you travel toward it.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "create_promise",
                        TargetEntityId: "north bell",
                        PromiseKind: "rumor",
                        PromiseText: "The north bell will answer when you travel toward it.",
                        RealizationKind: "site",
                        PlayerVisible: true,
                        Salience: 4),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_prophet", "Test prophet", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("prophet, promise me about the north bell"));

        Assert.True(talk.Success);
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueActionRejected");
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueCreatePromise"
            && Equals(delta.Details["unresolvedTargetHint"], "north bell"));
        var promise = Assert.Single(session.Engine.State.PromiseLedger.Promises, item =>
            item.Text.Contains("north bell", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("north bell", promise.ClaimedPlace);
        Assert.Equal("north bell", promise.Subject);
    }

    [Fact]
    public async Task GeneratedDialogueCanMarkLocalFixture()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test witness says, \"Here. This board is the one.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "mark_location",
                        Name: "loose floorboard",
                        Description: "A floorboard with fresh nail-scars.",
                        FixtureType: "marker",
                        Material: "wood",
                        Tags: new[] { "floorboard", "marked" },
                        InteractableVerbs: new[] { "examine" },
                        X: 4,
                        Y: 5,
                        BlocksMovement: false),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_witness", "Test witness", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("witness, mark this place"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueMarkLocation"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture)
            && Equals(delta.Details["fixtureType"], "marker")
            && Equals(delta.Details["x"], 4)
            && Equals(delta.Details["y"], 5)
            && Equals(delta.Details["blocksMovement"], false));
        var marker = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name == "loose floorboard");
        Assert.Equal(new GridPoint(4, 5), marker.Get<PositionComponent>().Position);
        Assert.Equal("marker", marker.Get<FixtureComponent>().FixtureType);
        Assert.Contains(marker.Get<InteractableComponent>().Verbs, verb =>
            verb.Equals("examine", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GeneratedDialogueRejectsRemoteLocationMarker()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Test witness says, \"Far away, I can mark it from here.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[]
                {
                    new DialogueActionProposal(
                        "mark_location",
                        Name: "impossible marker",
                        X: 8,
                        Y: 8,
                        BlocksMovement: false),
                })));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            dialogueProvider: provider);
        DisableImperialAi(session);
        AddTestNpc(session, "test_witness", "Test witness", new GridPoint(5, 5));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 5)));

        var talk = await session.ExecuteAsync(new TalkCommand("witness, mark the far place"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogueActionRejected"
            && Equals(delta.Details["type"], "mark_location")
            && ((string)delta.Details["reason"]!).Contains("nearby tile", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(session.Engine.State.Entities.Values, entity =>
            entity.Name == "impossible marker");
    }

    [Fact]
    public async Task TravelRealizesExtractorBoundSitePromiseAsGeneratedPlace()
    {
        var extractor = new FixtureDialogueClaimExtractor(new DialogueClaimProposal(
            "Lio knows of a checkpoint beyond this room.",
            "site",
            "checkpoint beyond this room",
            Salience: 3,
            Confidence: 80,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "quest",
            RealizationKind: "site",
            TriggerHint: "travel",
            ClaimedPlace: "checkpoint beyond this room",
            Tags: new[] { "checkpoint", "site" }));
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            claimExtractor: extractor);
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        await session.ExecuteAsync(new TalkCommand("Lio, what do you know beyond this room?"));
        var wait = await session.ExecuteAsync(new WaitCommand());

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var promise = Assert.Single(
            session.Engine.State.PromiseLedger.Promises,
            item => item.Source.StartsWith("dialogue_claim:", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(wait.Deltas, delta => delta.Operation == "claimPromise");
        Assert.True(travel.Success);
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "realizePromise"
            && delta.Target == promise.Id
            && Equals(delta.Details["trigger"], "travel"));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "promiseSite"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture));
        Assert.Equal("realized", promise.Status);
        Assert.Equal("1,0", promise.RealizedIn);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "checkpoint beyond this room"
            && entity.TryGet<FixtureComponent>(out var fixture)
            && fixture.FixtureType == "promise_site"
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "site"
            && record.Source == $"promise:{promise.Id}:travel");
    }

    [Fact]
    public async Task ExamineRealizesInspectAnchoredPromise()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var notice = session.Engine.EntityById("notice_1")!;
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "The posted notice hides a promised blade for the person who examines it.",
            playerVisible: true,
            source: "test",
            salience: 3,
            subject: "promised blade",
            triggerHint: "inspect",
            realizationKind: "item");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            notice.Id.Value,
            triggerHint: "inspect",
            realizationKind: "item");
        notice.Set(new PromiseAnchorComponent(promise.Id));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));

        var examine = await session.ExecuteAsync(new ExamineCommand("notice"));
        var realized = session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == promise.Id);

        Assert.True(examine.Success);
        Assert.False(examine.ConsumedTurn);
        Assert.Equal("realized", realized.Status);
        Assert.Equal("inspect:notice_1", realized.RealizedIn);
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "realizePromise"
            && Equals(delta.Details["trigger"], "inspect"));
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "promiseItem"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnItem)
            && Equals(delta.Details["stackPolicy"], "unique"));
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "promiseAwakened"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "promiseItemMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        Assert.DoesNotContain(examine.Deltas, delta => delta.Operation == "examineFallbackMessage");
        Assert.Single(session.Engine.State.Messages, message =>
            message.Contains("A promise stirs awake", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Contains("promised blade appears", StringComparison.OrdinalIgnoreCase));
        var item = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name == "promised blade"
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        Assert.Equal("promise", item.Get<ItemComponent>().Material);
        Assert.Equal("unique", item.Get<ItemComponent>().StackPolicy);
    }

    [Fact]
    public async Task ExamineRealizesInspectThreatPromiseThroughSpawnConsequence()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var notice = session.Engine.EntityById("notice_1")!;
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "The posted notice has a debt collector folded into its margins.",
            playerVisible: true,
            source: "test",
            salience: 3,
            subject: "debt collector",
            triggerHint: "inspect",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            notice.Id.Value,
            triggerHint: "inspect",
            realizationKind: "threat");
        notice.Set(new PromiseAnchorComponent(promise.Id));
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));

        var examine = await session.ExecuteAsync(new ExamineCommand("notice"));

        Assert.True(examine.Success);
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "promiseThreat"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity)
            && Equals(delta.Details["aiPolicyId"], "hostile")
            && Equals(delta.Details["summoned"], false));
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "promiseThreatMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["promiseId"], promise.Id));
        var threat = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name.Contains("debt collector", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        // A private debt collector is not the Empire; spawning it under the empire faction would
        // feed Censorate heat/warrant pressure for a threat that has nothing to do with it. It
        // must still be a real, engine-recognized hostile toward the player, not just tagged so.
        Assert.Equal("independent", threat.Get<ActorComponent>().Faction);
        Assert.Equal("hostile", threat.Get<AiComponent>().PolicyId);
        Assert.True(session.Engine.IsHostile(threat, session.Engine.State.ControlledEntity));
    }

    [Fact]
    public async Task TravelThreatPromiseNamingTheEmpireSpawnsUnderTheEmpireFaction()
    {
        // The other side of the fix: a threat promise whose own text names the Empire (a
        // soldier, an imperial patrol) should still spawn as "empire", so Censorate heat and
        // warrant pressure keep responding to threats that are actually the Empire's doing.
        var session = CreateMockSession();
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "An imperial soldier waits beyond the next road.",
            playerVisible: true,
            source: "test",
            salience: 4,
            subject: "imperial soldier",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(
            promise.Id,
            session.Engine.State.RegionId,
            null,
            triggerHint: "travel",
            realizationKind: "threat");

        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var threat = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        Assert.Equal("empire", threat.Get<ActorComponent>().Faction);
    }

    [Fact]
    public async Task TravelRealizesAtMostOneThreatPerBatchWhenAnotherKindIsEligible()
    {
        // Regression: SelectTravelPromises previously took the raw top-2 scored candidates with
        // no kind restriction, so two "threat" promises could both realize in one travel. Give
        // two threats a much higher salience than a competing "person" promise (so, without
        // kind-diversity, both threats would win on raw score) and assert the batch still only
        // ever contains one threat.
        var session = CreateMockSession();
        DisableImperialAi(session);
        var threatA = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "A debt collector waits beyond the next road.",
            playerVisible: true,
            source: "test",
            salience: 5,
            subject: "debt collector",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(threatA.Id, session.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");
        var threatB = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "Another debt collector waits too.",
            playerVisible: true,
            source: "test",
            salience: 5,
            subject: "second debt collector",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(threatB.Id, session.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");
        var person = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "A stranger waits beyond the next road.",
            playerVisible: true,
            source: "test",
            salience: 1,
            subject: "stranger",
            triggerHint: "travel",
            realizationKind: "person");
        session.Engine.State.PromiseLedger.Bind(person.Id, session.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "person");

        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var realizedKinds = new[] { threatA.Id, threatB.Id, person.Id }
            .Select(id => session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == id))
            .Where(item => item.Status.Equals("realized", StringComparison.OrdinalIgnoreCase))
            .Select(item => item.RealizationKind)
            .ToArray();
        Assert.Equal(2, realizedKinds.Length);
        Assert.Single(realizedKinds, kind => kind!.Equals("threat", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(realizedKinds, kind => kind!.Equals("person", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ThreatRealizationCooldownDelaysASecondThreatAcrossTravels()
    {
        // Regression: there was no pacing between separate travels either -- a threat could
        // realize on every single travel that had one eligible. Realize one threat, confirm a
        // second high-scoring bound threat does NOT realize on the very next travel, then confirm
        // it does once the global cooldown window has passed.
        var session = CreateMockSession();
        DisableImperialAi(session);
        var first = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "A debt collector waits beyond the next road.",
            playerVisible: true,
            source: "test",
            salience: 5,
            subject: "debt collector",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(first.Id, session.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        Assert.Equal(
            "realized",
            session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == first.Id).Status);

        var second = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "Another debt collector waits too.",
            playerVisible: true,
            source: "test",
            salience: 5,
            subject: "second debt collector",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(second.Id, session.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        Assert.Equal(
            "bound",
            session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == second.Id).Status);

        for (var i = 0; i < 12; i++)
        {
            await session.ExecuteAsync(new WaitCommand());
        }

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        Assert.Equal(
            "realized",
            session.Engine.State.PromiseLedger.Promises.Single(item => item.Id == second.Id).Status);
    }

    [Fact]
    public async Task ThreatStatsScaleWithPromiseSalience()
    {
        // Regression: every promise-realized threat spawned with the exact same fixed hp:8/
        // attack:3 regardless of how dramatic the promise was. A salience-5 threat should hit
        // harder and survive longer than a salience-1 one.
        var lowSession = CreateMockSession();
        DisableImperialAi(lowSession);
        var lowPromise = lowSession.Engine.State.PromiseLedger.Add(
            "rumor",
            "Someone minor is coming to find you.",
            playerVisible: true,
            source: "test",
            salience: 1,
            subject: "someone",
            triggerHint: "travel",
            realizationKind: "threat");
        lowSession.Engine.State.PromiseLedger.Bind(lowPromise.Id, lowSession.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");
        await lowSession.ExecuteAsync(new TravelCommand(Direction.East));
        var lowThreat = Assert.Single(lowSession.Engine.State.Entities.Values, entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor) && anchor.PromiseIds.Contains(lowPromise.Id));

        var highSession = CreateMockSession();
        DisableImperialAi(highSession);
        var highPromise = highSession.Engine.State.PromiseLedger.Add(
            "rumor",
            "Someone dreadful is coming to find you.",
            playerVisible: true,
            source: "test",
            salience: 5,
            subject: "someone",
            triggerHint: "travel",
            realizationKind: "threat");
        highSession.Engine.State.PromiseLedger.Bind(highPromise.Id, highSession.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");
        await highSession.ExecuteAsync(new TravelCommand(Direction.East));
        var highThreat = Assert.Single(highSession.Engine.State.Entities.Values, entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor) && anchor.PromiseIds.Contains(highPromise.Id));

        Assert.True(
            highThreat.Get<ActorComponent>().MaxHitPoints > lowThreat.Get<ActorComponent>().MaxHitPoints,
            $"expected high-salience HP ({highThreat.Get<ActorComponent>().MaxHitPoints}) > low-salience HP ({lowThreat.Get<ActorComponent>().MaxHitPoints})");
        Assert.True(
            highThreat.Get<ActorComponent>().Attack > lowThreat.Get<ActorComponent>().Attack,
            $"expected high-salience attack ({highThreat.Get<ActorComponent>().Attack}) > low-salience attack ({lowThreat.Get<ActorComponent>().Attack})");
    }

    [Fact]
    public async Task GenericThreatPromiseRealizesWithARegionFlavoredNameNotTheBareFallback()
    {
        // Regression: any threat promise that didn't literally mention "collector" or an
        // imperial keyword used to spawn under the single bland fallback name every time. That
        // was the common case -- most threat promises don't name either -- so this is what
        // actually fixes the "every threat looks the same" complaint.
        var session = CreateMockSession();
        DisableImperialAi(session);
        var promise = session.Engine.State.PromiseLedger.Add(
            "rumor",
            "Someone with an old grudge is coming to find you.",
            playerVisible: true,
            source: "test",
            salience: 3,
            subject: "old grudge",
            triggerHint: "travel",
            realizationKind: "threat");
        session.Engine.State.PromiseLedger.Bind(promise.Id, session.Engine.State.RegionId, null, triggerHint: "travel", realizationKind: "threat");

        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var threat = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.TryGet<PromiseAnchorComponent>(out var anchor) && anchor.PromiseIds.Contains(promise.Id));
        Assert.NotEqual("unnamed claimant", threat.Name);
        Assert.DoesNotContain(new[]
            {
                "promise", "promises", "promised", "prophecy", "prophecies", "omen", "omens", "oath", "oaths",
            },
            token => threat.Name.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MissingDialogueTargetDoesNotMutateBonds()
    {
        var session = CreateMockSession();
        var bondsBefore = session.Engine.State.Bonds.Bonds.Count;

        var talk = await session.ExecuteAsync(new TalkCommand("tell the absent glass duke a secret"));

        Assert.False(talk.Success);
        Assert.False(talk.ConsumedTurn);
        Assert.Equal(bondsBefore, session.Engine.State.Bonds.Bonds.Count);
    }

    [Fact]
    public async Task TravelGeneratesZoneAndAtlasWorldCard()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));
        var atlas = await session.ExecuteAsync(new AtlasCommand());
        var view = session.View();
        var context = MagicContext(session);

        Assert.True(travel.Success);
        Assert.Contains(travel.Messages, message =>
            message.Contains("You travel east into Hollowmere Margin.", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Contains("You travel east into Hollowmere Margin.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "travel"
            && Equals(delta.Details["auditOnly"], true)
            && Equals(delta.Details["playerVisible"], false)
            && Equals(delta.Details["fromZone"], "0,0")
            && Equals(delta.Details["toZone"], "1,0"));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "travelMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["fromZone"], "0,0")
            && Equals(delta.Details["toZone"], "1,0")
            && Equals(delta.Details["regionId"], "hollowmere_margin")
            && Equals(delta.Details["direction"], Direction.East.ToString()));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "travelPlaceTraveler"
            && delta.Target == "player"
            && delta.Summary == "You enter the destination zone."
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["zoneId"], "1,0")
            && Equals(delta.Details["regionId"], "hollowmere_margin")
            && Equals(delta.Details["direction"], Direction.East.ToString())
            && Equals(delta.Details["playerVisible"], false));
        Assert.Equal("1,0", session.Engine.State.CurrentZoneId);
        Assert.Equal("hollowmere_margin", session.Engine.State.RegionId);
        Assert.NotNull(view.World);
        Assert.Equal("Hollowmere Margin", view.World!.RegionName);
        Assert.Contains(view.Entities, entity => entity.Name.Contains("reed", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(atlas.Messages, message => message.Contains("reed_cover", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(context.ResolverLens!.Notes, note => note.Contains("Hollowmere Margin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(context.ResolverLens!.Notes, note => note.Contains("reed_cover", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TravelGenerationUsesSharedSpawnAndTradeConsequences()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.True(travel.Success);
        var terrainDeltas = travel.Deltas
            .Where(delta => delta.Operation == "generateZoneTerrain")
            .ToArray();
        Assert.Equal(5, terrainDeltas.Length);
        Assert.All(terrainDeltas, delta =>
        {
            Assert.Equal(WorldConsequenceTypes.SetTerrain, delta.Details["consequenceType"]);
            Assert.Equal("hollowmere_margin", delta.Details["regionId"]);
            Assert.Equal("shallow_water", delta.Details["terrain"]);
            Assert.Equal("region_generation", delta.Details["terrainSource"]);
            Assert.False(delta.IsPlayerVisible());
        });
        Assert.Equal(5, session.Engine.State.Terrain.Values.Count(terrain =>
            terrain.Equals("shallow_water", StringComparison.OrdinalIgnoreCase)));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "generateZoneFeature"
            && delta.Target.StartsWith("zone_prop_", StringComparison.OrdinalIgnoreCase)
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture)
            && Equals(delta.Details["regionId"], "hollowmere_margin")
            && !delta.IsPlayerVisible());
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "generateZoneItem"
            && delta.Target.StartsWith("zone_item_", StringComparison.OrdinalIgnoreCase)
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnItem)
            && Equals(delta.Details["regionId"], "hollowmere_margin")
            && !delta.IsPlayerVisible());
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "generateResident"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity)
            && Equals(delta.Details["summoned"], false)
            && Equals(delta.Details["aiPolicyId"], "resident")
            && Equals(delta.Details["wantGenerated"], false)
            && !delta.IsPlayerVisible());
        Assert.Equal(2, travel.Deltas.Count(delta =>
            delta.Operation == "generateResidentWares"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.OfferTrade)
            && Equals(delta.Details["stockSource"], "resident_generation")));
        var resident = Assert.Single(session.Engine.State.Entities.Values, entity =>
            entity.Name == "Hollowmere reed-keeper");
        Assert.True(resident.TryGet<MerchantComponent>(out var merchant));
        Assert.Equal(1, merchant.Wares["red tincture"]);
        Assert.Equal(2, merchant.Wares["grave salt"]);
    }

    [Fact]
    public void ZoneGenerationSkipsRejectedGeneratedConsequenceWithoutThrowing()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var generation = new GenerationSystem(
            session.Engine.State,
            ItemCatalog.CreateMinimal(),
            LoreCatalog.CreateMinimal(),
            session.Engine.ApplyConsequence,
            (generatedState, consequence) =>
                consequence.Type.Equals(WorldConsequenceTypes.SpawnFixture, StringComparison.OrdinalIgnoreCase)
                    ? RejectConsequence(consequence, "test rejected generated fixture")
                    : WorldConsequenceGuard.ApplyWithNewApplier(generatedState, consequence));

        var deltas = generation.Travel(Direction.East);

        Assert.Equal("1,0", session.Engine.State.CurrentZoneId);
        Assert.Equal("hollowmere_margin", session.Engine.State.RegionId);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "generationConsequenceSkipped"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnFixture)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
        Assert.Contains(deltas, delta =>
            delta.Operation == "generateZoneItem"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnItem));
        Assert.Contains(deltas, delta =>
            delta.Operation == "generateResident"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.SpawnEntity));
        Assert.Contains(deltas, delta =>
            delta.Operation == "travelPlaceTraveler"
            && delta.Target == "player"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("test rejected generated fixture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TravelPlacementRejectionLeavesTravelerCoherentlyPlacedAndAudited()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var startingPosition = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var generation = new GenerationSystem(
            session.Engine.State,
            ItemCatalog.CreateMinimal(),
            LoreCatalog.CreateMinimal(),
            consequence => Operation(consequence).Equals("travelPlaceTraveler", StringComparison.OrdinalIgnoreCase)
                ? RejectConsequence(consequence, "test rejected traveler placement")
                : session.Engine.ApplyConsequence(consequence));

        var deltas = generation.Travel(Direction.East);
        var playerPosition = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;

        Assert.Equal("1,0", session.Engine.State.CurrentZoneId);
        Assert.NotEqual(startingPosition, playerPosition);
        Assert.InRange(playerPosition.X, 0, session.Engine.State.Width - 1);
        Assert.InRange(playerPosition.Y, 0, session.Engine.State.Height - 1);
        Assert.Contains(deltas, delta =>
            delta.Operation == "worldConsequenceRejected"
            && delta.Target == "player"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity));
        Assert.Contains(deltas, delta =>
            delta.Operation == "travelPlacementSkipped"
            && delta.Target == "player"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["auditOnly"], true)
            && !delta.IsPlayerVisible());
    }

    [Fact]
    public async Task ZoneEntryRumorNarrationUsesTypedMessageConsequence()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var legend = session.Engine.ApplyConsequence(WorldConsequence.AddLegend(
            "test",
            "player_soul",
            "bell-touched",
            3,
            "test_deed",
            sourceEntityId: "player"));

        var travel = await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.True(legend.Applied);
        Assert.True(travel.Success);
        Assert.Contains(travel.Messages, message =>
            message.Contains("Hollowmere Margin", StringComparison.OrdinalIgnoreCase)
            && message.Contains("bell-touched", StringComparison.OrdinalIgnoreCase));
        Assert.Single(session.Engine.State.Messages, message =>
            message.Contains("bell-touched", StringComparison.OrdinalIgnoreCase)
            && message.Contains("Hollowmere Margin", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(travel.Deltas, delta =>
            delta.Operation == "zone_entry_rumor"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["legendTag"], "bell-touched")
            && Equals(delta.Details["regionId"], "hollowmere_margin"));
    }

    [Fact]
    public async Task WalkingAcrossTheZoneEdgeAutoTravels()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var edgeX = session.Engine.State.Width - 1;
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(edgeX - 1, 5)));

        var edgeView = session.View();
        var edgeTile = Assert.Single(edgeView.Tiles!, tile => tile.X == edgeX && tile.Y == 5);
        Assert.False(edgeTile.BlocksMovement);
        Assert.Equal("floor", edgeTile.Terrain);

        var stepOntoEdge = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(stepOntoEdge.Success);
        Assert.Equal("move", stepOntoEdge.Action);
        Assert.Equal(new GridPoint(edgeX, 5), session.Engine.State.ControlledEntity.Get<PositionComponent>().Position);

        var movePastEdge = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(movePastEdge.Success);
        Assert.Equal("travel", movePastEdge.Action);
        Assert.Equal("1,0", session.Engine.State.CurrentZoneId);
    }

    [Fact]
    public async Task WalkingIntoACornerStaysBlockedInstead()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(session.Engine.State.Width - 1, 1)));

        var move = await session.ExecuteAsync(new MoveCommand(Direction.NorthEast));

        Assert.False(move.Success);
        Assert.Equal("0,0", session.Engine.State.CurrentZoneId);
    }

    [Fact]
    public async Task SeededWorldRollIsDeterministicAndVisible()
    {
        var first = await TravelWorldForSeed(7);
        var repeat = await TravelWorldForSeed(7);
        var different = await TravelWorldForSeed(8);

        Assert.Equal(first.RealmStatus, repeat.RealmStatus);
        Assert.Equal(first.RealmRuler, repeat.RealmRuler);
        Assert.Equal(first.ImperialPresence, repeat.ImperialPresence);
        Assert.Equal("occupied", first.RealmStatus);
        Assert.Equal("the Bent Council", first.RealmRuler);
        Assert.Equal(57, first.ImperialPresence);
        Assert.NotEqual(
            $"{first.RealmStatus}|{first.RealmRuler}|{first.ImperialPresence}",
            $"{different.RealmStatus}|{different.RealmRuler}|{different.ImperialPresence}");
    }

    [Fact]
    public async Task GeneratedRegionSpawnsResidentPopulationSeed()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        DisableImperialAi(session);

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var resident = Assert.Single(
            session.Engine.State.Entities.Values,
            entity => entity.Name == "Hollowmere reed-keeper");

        Assert.Equal("hollowmere", resident.Get<ActorComponent>().Faction);
        Assert.Contains("resident", resident.Get<FactionComponent>().Roles);
        Assert.Contains("occupied", resident.Get<TagsComponent>().Tags);
        Assert.Contains("Hollowmere is occupied", resident.Get<DescriptionComponent>().Text);
        Assert.Contains("quiet shelters", resident.Get<WantComponent>().Text);
        Assert.Contains("promise_source", resident.Get<WantComponent>().Tags);
        Assert.False(session.Engine.IsHostile(resident, session.Engine.State.ControlledEntity));
    }

    [Fact]
    public async Task GeneratedZonesCreateDeterministicCuriosWithPreservedReagentMetadata()
    {
        var first = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var second = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        DisableImperialAi(first);
        DisableImperialAi(second);

        await first.ExecuteAsync(new TravelCommand(Direction.East));
        await second.ExecuteAsync(new TravelCommand(Direction.East));

        var firstCurio = Assert.Single(first.Engine.State.Entities.Values, IsGeneratedCurio);
        var secondCurio = Assert.Single(second.Engine.State.Entities.Values, IsGeneratedCurio);
        Assert.Equal(firstCurio.Name, secondCurio.Name);
        Assert.Equal(firstCurio.Get<ItemComponent>().Value, secondCurio.Get<ItemComponent>().Value);
        Assert.Contains("curio", firstCurio.Get<TagsComponent>().Tags);
        Assert.True(firstCurio.Has<DescriptionComponent>());

        first.Engine.State.ControlledEntity.Set(firstCurio.Get<PositionComponent>());
        var pickup = await first.ExecuteAsync(new PickupCommand(firstCurio.Name));

        Assert.True(pickup.Success);
        Assert.Contains(first.View().Reagents!, reagent =>
            reagent.Name == firstCurio.Name
            && reagent.Material == firstCurio.Get<ItemComponent>().Material
            && reagent.TotalValue == firstCurio.Get<ItemComponent>().Value
            && !string.IsNullOrWhiteSpace(reagent.SpellBias));
    }

    [Fact]
    public async Task GeneratedResidentsSupportExplicitTradeCommands()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        DisableImperialAi(session);

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        var wares = await session.ExecuteAsync(new WaresCommand());
        var buy = await session.ExecuteAsync(new BuyCommand("red tincture"));
        var sell = await session.ExecuteAsync(new SellCommand("red tincture"));
        var inventory = session.Engine.State.ControlledEntity.Get<InventoryComponent>();

        Assert.True(wares.Success);
        Assert.Contains(wares.Messages, message => message.Contains("red tincture", StringComparison.OrdinalIgnoreCase));
        Assert.True(buy.Success);
        Assert.Contains(buy.Messages, message => message.Contains("buy red tincture", StringComparison.OrdinalIgnoreCase));
        var buyTrade = Assert.Single(buy.Deltas, delta => delta.Operation == "executeTrade");
        Assert.Equal(WorldConsequenceTypes.ExecuteTrade, buyTrade.Details["consequenceType"]);
        Assert.Equal("buy", buyTrade.Details["mode"]);
        Assert.Equal("red tincture", buyTrade.Details["item"]);
        Assert.True(sell.Success);
        Assert.Contains(sell.Messages, message => message.Contains("sell red tincture", StringComparison.OrdinalIgnoreCase));
        var sellTrade = Assert.Single(sell.Deltas, delta => delta.Operation == "executeTrade");
        Assert.Equal(WorldConsequenceTypes.ExecuteTrade, sellTrade.Details["consequenceType"]);
        Assert.Equal("sell", sellTrade.Details["mode"]);
        Assert.Equal("red tincture", sellTrade.Details["item"]);
        Assert.Equal(15, inventory.Items["gold"]);
        Assert.DoesNotContain(inventory.Items, pair => pair.Key.Equals("red tincture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void SellingUnderADifferentlyFormattedNameReusesTheExistingWareKey()
    {
        var session = CreateMockSession();
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Set(new MerchantComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["red_tincture"] = 2 },
            Gold: 50));
        var player = session.Engine.State.ControlledEntity;
        player.Set(new InventoryComponent(
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["gold"] = 0,
                ["Red Tincture"] = 1,
            },
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)));

        // A dialogue/LLM-authored sell can name the item differently than the merchant's own
        // stock key ("Red Tincture" vs "red_tincture"). Selling must land on the existing ware
        // entry, not fragment stock into a second, unreconciled key for the same item.
        var result = session.Engine.ApplyConsequence(WorldConsequence.ExecuteTrade(
            "test",
            lio.Id.Value,
            player.Id.Value,
            "sell",
            "Red Tincture",
            "Red Tincture",
            price: 5));

        Assert.True(result.Applied, result.Error);
        var wares = lio.Get<MerchantComponent>().Wares;
        var ware = Assert.Single(wares);
        Assert.Equal("red_tincture", ware.Key);
        Assert.Equal(3, ware.Value);
    }

    [Fact]
    public async Task ZoneSnapshotRoundTripsTerrainAndEntities()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.ApplyConsequence(WorldConsequence.SetTerrain(
            "test_setup",
            4,
            4,
            "blue_moss",
            emitMessage: false));
        var flowPoint = new GridPoint(4, 5);
        session.Engine.State.TileFlows[flowPoint] = new TileFlow(1, 0, 99);

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        Assert.Empty(session.Engine.State.TileFlows);

        var generatedEntity = session.Engine.State.Entities.Values.First(entity => entity.Id.Value.StartsWith("zone_prop_", StringComparison.OrdinalIgnoreCase));
        generatedEntity.Name = "renamed reed shrine";
        await session.ExecuteAsync(new TravelCommand(Direction.West));

        Assert.Equal("0,0", session.Engine.State.CurrentZoneId);
        Assert.Equal("blue_moss", session.Engine.State.Terrain[new GridPoint(4, 4)]);
        Assert.True(session.Engine.State.TileFlows.ContainsKey(flowPoint));
        var occupied = session.Engine.State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _)
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement)
            .Select(entity => entity.Get<PositionComponent>().Position)
            .ToArray();
        Assert.Equal(occupied.Length, occupied.Distinct().Count());

        await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Contains(session.Engine.State.Entities.Values, entity => entity.Name == "renamed reed shrine");
    }

    [Fact]
    public void ExpiredTileFlowsUseSharedLifecycleConsequence()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var flowPoint = new GridPoint(4, 5);
        session.Engine.State.TileFlows[flowPoint] = new TileFlow(1, 0, session.Engine.State.Turn + 1);

        var deltas = session.Engine.AdvanceTurn();

        Assert.False(session.Engine.State.TileFlows.ContainsKey(flowPoint));
        Assert.Contains(deltas, delta =>
            delta.Operation == "updateFlow"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateFlow)
            && Equals(delta.Details["x"], flowPoint.X)
            && Equals(delta.Details["y"], flowPoint.Y)
            && Equals(delta.Details["action"], "expire")
            && Equals(delta.Details["playerVisible"], false));
        Assert.DoesNotContain(deltas.PlayerMessages(), message =>
            message.Contains("tile flow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BondFollowerTravelsWithControlledSoul()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        await session.ExecuteAsync(new GiveCommand("grave salt", "Lio"));
        session.Engine.State.Bonds.Adjust("lio_soul", "player_soul", loyalty: 5, posture: "grateful");
        await session.ExecuteAsync(new RecruitCommand("Lio"));

        await session.ExecuteAsync(new TravelCommand(Direction.East));

        Assert.Equal("1,0", session.Engine.State.CurrentZoneId);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Id.Value == "prisoner_1"
            && entity.TryGet<ActorComponent>(out var actor)
            && actor.Faction == "player");
    }

    [Fact]
    public async Task FollowerBlockingMovementYieldsBySwappingPlaces()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        OpenCellDoorWithoutCommand(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        session.Engine.State.Bonds.Adjust("lio_soul", "player_soul", loyalty: 5, posture: "grateful");
        await session.ExecuteAsync(new RecruitCommand("Lio"));
        var playerPoint = new GridPoint(10, 10);
        var followerPoint = new GridPoint(11, 10);
        session.Engine.State.BlockingTerrain.Remove(playerPoint);
        session.Engine.State.BlockingTerrain.Remove(followerPoint);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(playerPoint));
        var lio = session.Engine.EntityById("prisoner_1")!;
        lio.Set(new PositionComponent(followerPoint));

        var move = await session.ExecuteAsync(new MoveCommand(Direction.East));

        Assert.True(move.Success);
        Assert.True(move.ConsumedTurn);
        Assert.Equal(followerPoint, session.Engine.State.ControlledEntity.Get<PositionComponent>().Position);
        Assert.Equal(playerPoint, lio.Get<PositionComponent>().Position);
        Assert.Contains(move.Messages, message =>
            message.Contains("trade places", StringComparison.OrdinalIgnoreCase)
            && message.Contains("Lio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(move.Deltas, delta =>
            delta.Operation == "followerYieldSwap"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.MoveEntity)
            && Equals(delta.Details["swappedWithEntityId"], "prisoner_1"));
    }

    [Fact]
    public async Task ScriptedTextCommandsKeepInventoryTalkAndReadOpenFlowStable()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var move = await ExecuteTextAsync(session, "east");
        var pickupTincture = await ExecuteTextAsync(session, "pickup red tincture");
        var useTincture = await ExecuteTextAsync(session, "use red tincture");
        var equip = await ExecuteTextAsync(session, "equip charcoal wand");
        var focus = await ExecuteTextAsync(session, "focus charcoal wand");
        var unprotect = await ExecuteTextAsync(session, "unprotect moon pearl");
        var reagents = await ExecuteTextAsync(session, "reagents");

        Assert.True(move.Success);
        Assert.Equal("move", move.Action);
        Assert.Equal(1, move.TurnAfter);
        Assert.True(pickupTincture.Success);
        Assert.True(useTincture.Success);
        Assert.True(equip.Success);
        Assert.True(focus.Success);
        Assert.True(unprotect.Success);
        Assert.False(unprotect.ConsumedTurn);
        Assert.Contains(reagents.Messages, message => message.Contains("moon pearl", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(session.View().Inventory!, item => item.Name == "charcoal wand" && item.Equipped && item.Focused);
        Assert.Contains(session.View().Inventory!, item => item.Name == "moon pearl" && !item.Protected);
        Assert.DoesNotContain(session.View().Inventory!, item => item.Name == "red tincture");

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(6, 7)));
        var read = await ExecuteTextAsync(session, "read notice");
        Assert.True(read.Success);
        Assert.True(read.ConsumedTurn);
        Assert.Contains(read.Messages, message => message.Contains("marble authority", StringComparison.OrdinalIgnoreCase));

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(7, 6)));
        var pickupKey = await ExecuteTextAsync(session, "pickup key");
        Assert.True(pickupKey.Success);

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));
        var open = await ExecuteTextAsync(session, "open cell");
        Assert.True(open.Success);
        Assert.Contains(open.Deltas, delta =>
            delta.Operation == "freeCaptiveMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var talk = await ExecuteTextAsync(session, "talk Lio");
        Assert.True(talk.Success);
        Assert.True(talk.ConsumedTurn);
        Assert.Contains(talk.Messages, message => message.Contains("Lio", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(talk.Deltas, delta =>
            delta.Operation == "dialogue"
            && Equals(delta.Details["playerText"], "I approach and wait for you to speak."));
        Assert.DoesNotContain(talk.Deltas, delta =>
            delta.Operation == "dialogueExchangeMemory"
            && delta.Summary.Contains("Player: Lio", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("call a brass moth", "summon", "summon")]
    [InlineData("heal my wounds", "heal", "heal")]
    [InlineData("restore mana", "restoreMana", "restoreMana")]
    [InlineData("bind the nearest enemy in sticky web", "addStatus", "addStatus")]
    [InlineData("turn the floor to ice", "createTiles", "createTile")]
    [InlineData("turn the soldier into glass", "transformEntity", "transformEntity")]
    [InlineData("push the soldier away", "push", "push")]
    [InlineData("pull the soldier toward me", "pull", "pull")]
    [InlineData("teleport me", "teleport", "teleport")]
    [InlineData("charm the soldier", "changeFaction", "changeFaction")]
    [InlineData("curse me with debt", "addCurse", "addCurse")]
    [InlineData("blast all enemies with lightning", "areaDamage", "damage")]
    [InlineData("a plain blue fire", "damage", "damage")]
    [InlineData("send a debt collector in three turns", "scheduleEvent", "scheduleEvent")]
    public async Task MockProviderKeepsSpellOperationFamiliesReachable(
        string spell,
        string expectedEffectType,
        string expectedDeltaOperation)
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        InjureAndDrainPlayer(session);

        var result = await session.ExecuteAsync(new CastCommand(spell));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.False(result.TechnicalFailure);
        Assert.Equal("cast", result.Action);
        Assert.NotNull(result.Magic);
        Assert.Equal("mock", result.Magic!.Provider);
        Assert.Contains(expectedEffectType, result.Magic.EffectTypes);
        Assert.Contains(result.Deltas, delta => delta.Operation == expectedDeltaOperation);
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task FixtureOperationBundleKeepsNonMockOperationFamiliesReachable()
    {
        var provider = new FixtureSpellProvider(new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "The room agrees to be annotated.",
            Effects: new[]
            {
                new SpellEffect(
                    "createTile",
                    new Dictionary<string, object?>
                    {
                        ["x"] = 4,
                        ["y"] = 4,
                        ["terrain"] = "shallow water",
                        ["duration"] = 2,
                    }),
                new SpellEffect(
                    "removeStatus",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "player",
                        ["status"] = "marked",
                    }),
                new SpellEffect(
                    "transformItem",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "brazier_1",
                        ["name"] = "singing containment brazier",
                        ["material"] = "brass",
                        ["addTags"] = "singing,witness",
                    }),
                new SpellEffect(
                    "addTrait",
                    new Dictionary<string, object?>
                    {
                        ["target"] = "notice_1",
                        ["trait"] = "eavesdropper",
                    }),
                new SpellEffect(
                    "message",
                    new Dictionary<string, object?>
                    {
                        ["text"] = "The room takes notes.",
                    }),
            },
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null));
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));
        DisableImperialAi(session);
        ApplyStatus(session, session.Engine.State.ControlledEntity, "marked", duration: 5);

        var result = await session.ExecuteAsync(new CastCommand("make the room remember itself"));

        Assert.True(result.Success);
        Assert.Equal(new[] { "createTile", "removeStatus", "transformItem", "addTrait", "message" }, result.Magic!.EffectTypes);
        Assert.Equal("shallow_water", session.Engine.State.Terrain[new GridPoint(4, 4)]);
        Assert.DoesNotContain(
            session.Engine.State.ControlledEntity.Get<StatusContainerComponent>().Statuses,
            status => status.Id == "marked");
        Assert.Equal("singing containment brazier", session.Engine.EntityById("brazier_1")!.Name);
        Assert.Contains(
            session.Engine.EntityById("brazier_1")!.Get<TagsComponent>().Tags,
            tag => tag == "singing");
        Assert.Contains(
            session.Engine.EntityById("notice_1")!.Get<TagsComponent>().Tags,
            tag => tag == "eavesdropper");
        Assert.Contains(result.Messages, message => message == "The room takes notes.");
        Assert.True(session.Engine.ValidateState().IsValid);
    }

    [Fact]
    public async Task ScheduledEventsAndBackgroundJobsPumpOnTurnBoundaries()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var cast = await session.ExecuteAsync(new CastCommand("send a debt collector in three turns"));
        Assert.True(cast.Success);
        Assert.Contains(cast.Deltas, delta => delta.Operation == "scheduleEvent");
        Assert.Single(session.Engine.State.ScheduledEvents.Events, item => item.Kind == "wild_debt_arrival");

        await session.ExecuteAsync(new WaitCommand());
        Assert.DoesNotContain(session.Engine.State.Messages, message => message == "send a debt collector in three turns");

        await session.ExecuteAsync(new WaitCommand());
        Assert.DoesNotContain(session.Engine.State.ScheduledEvents.Events, item => item.Kind == "wild_debt_arrival");
        Assert.Contains(session.Engine.State.Messages, message => message == "send a debt collector in three turns");

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 4)));
        var examine = await ExecuteTextAsync(session, "examine brazier");
        var debugBefore = session.Observation(debug: true);

        Assert.True(examine.Success);
        Assert.False(examine.ConsumedTurn);
        Assert.Contains(examine.Deltas, delta =>
            delta.Operation == "queueBackgroundJob"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.QueueBackgroundJob)
            && Equals(delta.Details["purpose"], "entity_detail")
            && Equals(delta.Details["targetEntityId"], "brazier_1"));
        Assert.Contains(debugBefore.Debug!.BackgroundJobs!, job => job.Purpose == "entity_detail" && job.State == "Queued");

        await session.ExecuteAsync(new WaitCommand());
        var debugAfter = session.Observation(debug: true);

        Assert.Contains(debugAfter.Debug!.BackgroundJobs!, job => job.Purpose == "entity_detail" && job.State == "Applied");
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "entity_detail"
            && record.AttachedTo == "brazier_1"
            && record.Text.Contains("waiting for a spell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task IntentionalMagicRejectionConsumesTurnButIsNotTechnicalFailure()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);

        var result = await session.ExecuteAsync(new CastCommand("kill the emperor"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.False(result.TechnicalFailure);
        Assert.Equal(1, result.TurnAfter);
        Assert.NotNull(result.Magic);
        Assert.False(result.Magic!.Accepted);
        Assert.False(result.Magic.TechnicalFailure);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "wildMagicRejected"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message));
        Assert.Contains(result.Messages, message => message.Contains("marble heart", StringComparison.OrdinalIgnoreCase));
    }

    private static GameSession CreateMockSession() =>
        GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));

    private static StateDelta ApplyStatus(
        GameSession session,
        Entity target,
        string status,
        int duration,
        string displayName = "",
        string operation = "addStatus",
        string? messageOverride = null)
    {
        var applied = session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test_setup",
            target.Id.Value,
            status,
            duration,
            displayName,
            operation: operation,
            emitMessage: false,
            message: messageOverride));

        Assert.True(applied.Applied, applied.Error ?? $"Status {status} was not applied.");
        return Assert.Single(applied.Deltas);
    }

    private static DeedRecord RecordDeed(
        GameSession session,
        Entity actor,
        string kind,
        int magnitude,
        GridPoint origin,
        GridPoint? effectPoint,
        IEnumerable<string>? tags = null)
    {
        var applied = session.Engine.ApplyConsequence(WorldConsequence.RecordDeed(
            "test_setup",
            actor.Id.Value,
            kind,
            magnitude,
            origin.X,
            origin.Y,
            effectPoint?.X,
            effectPoint?.Y,
            tags?.ToArray(),
            sourceEntityId: actor.Id.Value));

        Assert.True(applied.Applied, applied.Error ?? $"Deed {kind} was not recorded.");
        var deedId = applied.Deltas
            .Select(delta => delta.Details.TryGetValue("deedId", out var value) ? value?.ToString() : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        Assert.False(string.IsNullOrWhiteSpace(deedId), "Deed consequence did not report a deed id.");
        return Assert.Single(session.Engine.State.Deeds.Records, deed =>
            deed.Id.Equals(deedId, StringComparison.OrdinalIgnoreCase));
    }

    private static string Operation(WorldConsequence consequence) =>
        consequence.Payload is not null && consequence.Payload.TryGetValue("operation", out var operation)
            ? operation?.ToString() ?? string.Empty
            : string.Empty;

    private static WorldConsequenceApplyResult RejectConsequence(WorldConsequence consequence, string error)
    {
        var details = new Dictionary<string, object?>
        {
            ["consequenceType"] = consequence.Type,
            ["error"] = error,
        };
        return new WorldConsequenceApplyResult(
            false,
            consequence.TargetEntityId,
            error,
            Array.Empty<string>(),
            new[]
            {
                new StateDelta(
                    "worldConsequenceRejected",
                    consequence.TargetEntityId ?? consequence.Source,
                    error,
                    details),
            },
            details);
    }

    private static StateDelta AssertWorldTurnSkipped(
        IEnumerable<StateDelta> deltas,
        string expectedKind,
        string expectedSourceId)
    {
        var skipped = Assert.Single(deltas, delta =>
            delta.Operation == "worldTurnSkipped"
            && Equals(delta.Details["kind"], expectedKind)
            && Equals(delta.Details["sourceId"], expectedSourceId));
        Assert.Equal(expectedKind, skipped.Details["kind"]);
        Assert.Equal(expectedSourceId, skipped.Details["sourceId"]);
        Assert.True(skipped.Details.TryGetValue("auditOnly", out var auditOnly) && auditOnly is true);
        Assert.True(skipped.Details.TryGetValue("playerVisible", out var playerVisible) && playerVisible is false);
        Assert.False(skipped.IsPlayerVisible());
        return skipped;
    }

    private static async Task<WorldCard> TravelWorldForSeed(int seed)
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: seed);
        DisableImperialAi(session);
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        return session.View().World!;
    }

    private static Task<Sorcerer.Core.Results.ActionResult> ExecuteTextAsync(GameSession session, string text) =>
        session.ExecuteAsync(TextCommandParser.Parse(text));

    private static MagicContextView MagicContext(GameSession session) =>
        session.Engine.MagicContext(new OperationIndex(
            Array.Empty<string>(),
            Array.Empty<OperationCardView>()));

    private static bool IsGeneratedCurio(Entity entity) =>
        entity.TryGet<ItemComponent>(out _)
        && entity.TryGet<TagsComponent>(out var tags)
        && tags.Tags.Contains("curio", StringComparer.OrdinalIgnoreCase)
        && tags.Tags.Contains("generated", StringComparer.OrdinalIgnoreCase);

    private static void DisableImperialAi(GameSession session)
    {
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            session.Engine.EntityById(id)!.Set(new ControllerComponent(ControllerKind.None));
        }
    }

    private static void KillImperialActors(GameSession session)
    {
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            var entity = session.Engine.EntityById(id)!;
            var actor = entity.Get<ActorComponent>();
            entity.Set(actor with { HitPoints = 0 });
        }
    }

    private static void KillAllNonPlayerActors(GameSession session)
    {
        foreach (var entity in session.Engine.State.Entities.Values.Where(entity => entity.Id != session.Engine.State.ControlledEntityId))
        {
            if (!entity.TryGet<ActorComponent>(out var actor))
            {
                continue;
            }

            entity.Set(actor with { HitPoints = 0 });
        }
    }

    private static void InjureAndDrainPlayer(GameSession session)
    {
        var player = session.Engine.State.ControlledEntity;
        var actor = player.Get<ActorComponent>();
        player.Set(actor with { HitPoints = Math.Min(actor.HitPoints, 12), Mana = Math.Min(actor.Mana, 4) });
    }

    private static void OpenCellDoorWithoutCommand(GameSession session)
    {
        var door = session.Engine.EntityById("cell_door_1")!;
        door.Set(door.Get<DoorComponent>() with { IsOpen = true });
        door.Set(door.Get<PhysicalComponent>() with { BlocksMovement = false, BlocksSight = false });
    }

    private static void CloseCellDoorWithoutCommand(GameSession session)
    {
        var door = session.Engine.EntityById("cell_door_1")!;
        door.Set(door.Get<DoorComponent>() with { IsOpen = false });
        door.Set(door.Get<PhysicalComponent>() with { BlocksMovement = true, BlocksSight = true });
    }

    private static Entity AddBridgeFixture(GameSession session, string prefix, GridPoint position)
    {
        var spawned = session.Engine.ApplyConsequence(WorldConsequence.SpawnFixture(
            "test",
            "old bell bridge",
            position.X,
            position.Y,
            prefix: prefix,
            glyph: '-',
            palette: "bridge",
            fixtureType: "bridge",
            material: "wood",
            tags: new[] { "bridge", "standing" },
            blocksMovement: false,
            description: "A narrow bridge with bell-wire along the rail.",
            interactableVerbs: new[] { "examine", "cross" },
            sourceEntityId: "player",
            operation: "testSpawnBridgeFixture"));
        Assert.True(spawned.Applied, string.Join(" | ", spawned.Messages));
        return session.Engine.EntityById(spawned.TargetId!)!;
    }

    private static Entity AddTestNpc(
        GameSession session,
        string id,
        string name,
        GridPoint position,
        string faction = "neutral",
        IReadOnlyList<string>? tags = null)
    {
        var npc = new Entity(EntityId.Create(id), name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('p', "neutral"))
            .Set(new TagsComponent(tags ?? new[] { "resident" }))
            .Set(new PhysicalComponent(BlocksMovement: true, Material: "flesh"))
            .Set(new ActorComponent(6, 6, 0, 0, 1, 0, faction))
            .Set(new ControllerComponent(ControllerKind.None))
            .Set(new SoulComponent($"{id}_soul"))
            .Set(new BodyStatsComponent(3))
            .Set(StatusContainerComponent.Empty())
            .Set(MemoryComponent.Empty())
            .Set(new InteractableComponent(new[] { "talk", "give" }))
            .Set(new ProfileComponent(name, "a test dialogue participant"));
        session.Engine.State.Entities[npc.Id] = npc;
        return npc;
    }

    private sealed class FixtureDialogueClaimExtractor : IDialogueClaimExtractor
    {
        private readonly IReadOnlyList<DialogueClaimProposal> _claims;
        private readonly bool _requiresSpokenTextSupport;

        public FixtureDialogueClaimExtractor(params DialogueClaimProposal[] claims)
            : this(requiresSpokenTextSupport: false, claims)
        {
        }

        public FixtureDialogueClaimExtractor(bool requiresSpokenTextSupport, params DialogueClaimProposal[] claims)
        {
            _claims = claims;
            _requiresSpokenTextSupport = requiresSpokenTextSupport;
        }

        public string Name => "fixture-dialogue-claims";

        public bool RequiresSpokenTextSupport => _requiresSpokenTextSupport;

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DialogueClaimExtractionResult(
                Name,
                RawText: "",
                TechnicalFailure: false,
                Error: null,
                Claims: _claims));
    }

    // Returns one single-proposal extraction result per call, in order (clamped to the last
    // entry once exhausted), so tests can simulate a speaker restating a claim across separate
    // `talk` turns rather than a single turn producing several proposals at once.
    private sealed class SequentialDialogueClaimExtractor : IDialogueClaimExtractor
    {
        private readonly IReadOnlyList<DialogueClaimProposal> _claimsInOrder;
        private int _callCount;

        public SequentialDialogueClaimExtractor(params DialogueClaimProposal[] claimsInOrder)
        {
            _claimsInOrder = claimsInOrder;
        }

        public string Name => "sequential-fixture-dialogue-claims";

        public bool RequiresSpokenTextSupport => false;

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken)
        {
            var index = Math.Min(_callCount, _claimsInOrder.Count - 1);
            _callCount++;
            return Task.FromResult(new DialogueClaimExtractionResult(
                Name,
                RawText: "",
                TechnicalFailure: false,
                Error: null,
                Claims: new[] { _claimsInOrder[index] }));
        }
    }

    private sealed class TechnicalFailureDialogueClaimExtractor : IDialogueClaimExtractor
    {
        public string Name => "technical-failure-dialogue-claims";

        public bool RequiresSpokenTextSupport => false;

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DialogueClaimExtractionResult(
                Name,
                RawText: "",
                TechnicalFailure: true,
                Error: "fixture extractor failure",
                Claims: Array.Empty<DialogueClaimProposal>()));
    }

    private sealed class FaultingDialogueClaimExtractor : IDialogueClaimExtractor
    {
        public string Name => "faulting-dialogue-claims";

        public bool RequiresSpokenTextSupport => false;

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken) =>
            Task.FromException<DialogueClaimExtractionResult>(
                new InvalidOperationException("fixture task fault"));
    }

    private sealed class CanceledDialogueClaimExtractor : IDialogueClaimExtractor
    {
        public string Name => "canceled-dialogue-claims";

        public bool RequiresSpokenTextSupport => false;

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken)
        {
            using var source = new CancellationTokenSource();
            source.Cancel();
            return Task.FromCanceled<DialogueClaimExtractionResult>(source.Token);
        }
    }

    private static string ChatResponse(string content) =>
        JsonSerializer.Serialize(new { message = new { content } });

    private sealed class QueueHttpHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueueHttpHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(_responses.Dequeue()),
            });
    }

    private sealed class FixtureDialogueProvider : IDialogueProvider
    {
        private readonly DialogueProviderResult _result;

        public FixtureDialogueProvider(DialogueResponse response)
            : this(new DialogueProviderResult(
                "fixture-dialogue",
                RawText: "",
                TechnicalFailure: false,
                Error: null,
                Response: response))
        {
        }

        public FixtureDialogueProvider(DialogueProviderResult result)
        {
            _result = result;
        }

        public string Name => "fixture-dialogue";

        public Task<DialogueProviderResult> ResolveAsync(
            DialogueRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    private sealed class FixtureDialogueAuditSink : IDialogueAuditSink
    {
        private readonly List<DialogueAuditEntry> _entries = new();

        public IReadOnlyList<DialogueAuditEntry> Entries => _entries;

        public void Record(DialogueAuditEntry entry) => _entries.Add(entry);
    }

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        private readonly SpellResolution _resolution;

        public FixtureSpellProvider(SpellResolution resolution)
        {
            _resolution = resolution;
        }

        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SpellProviderResult(
                Name,
                RawText: "",
                Resolution: _resolution,
                TechnicalFailure: false,
                Error: null));
    }
}
