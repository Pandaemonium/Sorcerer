using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Scenarios;
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
    public void PerceptionHidesActorsBehindClosedSightBlockersAndRemembersExploredTiles()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(12, 5)));

        var closedView = session.View();

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
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);

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

        var suspicion = session.Engine.RecordEffectSuspicion(doorPoint, "test_magic", player);

        var record = Assert.Single(suspicion);
        Assert.Equal("pending", record.Status);
        Assert.Null(record.SuspectedSoulId);

        OpenCellDoorWithoutCommand(session);
        session.Engine.AdvanceTurn();

        var attributed = Assert.Single(session.Engine.State.Suspicions.Records);
        Assert.Equal("attributed", attributed.Status);
        Assert.Equal("player_soul", attributed.SuspectedSoulId);
        Assert.Equal(session.Engine.State.Turn, attributed.AttributedTurn);
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
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);

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

        var deed = session.Engine.RecordDeed(player, "wild_magic", 3, origin, null, new[] { "damage" });
        session.Engine.AdvanceTurn();

        Assert.Equal("secret", deed.Visibility);
        Assert.Empty(session.Engine.State.Legend.Tags);
        Assert.DoesNotContain(session.Engine.State.Factions.Factions, faction =>
            faction.Standing.ContainsKey("imperial-threat"));
    }

    [Fact]
    public void SuspiciousEffectWitnessRemainsUnattributedUntilConnected()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        KillImperialActors(session);
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(12, 5)));

        var deed = session.Engine.RecordDeed(
            player,
            "wild_magic",
            3,
            new GridPoint(12, 5),
            new GridPoint(13, 5),
            new[] { "damage" });
        session.Engine.AdvanceTurn();

        Assert.Equal("suspicious", deed.Visibility);
        Assert.Equal("unattributed", deed.AttributionStatus);
        Assert.Contains(deed.EffectWitnesses!, witness => witness == "lio_soul");
        Assert.Empty(session.Engine.State.Legend.Tags);
        Assert.Contains(session.Engine.State.Factions.Factions, faction =>
            faction.Id == "empire"
            && faction.Standing.TryGetValue("suspicion", out var suspicion)
            && suspicion > 0);
    }

    [Fact]
    public async Task DeedsAfterBodySwapAttachToPlayerSoul()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        var playerBody = session.Engine.State.ControlledEntity;
        var soldier = session.Engine.EntityById("soldier_1")!;
        playerBody.Set(new PositionComponent(new GridPoint(8, 4)));
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);
        await session.ExecuteAsync(new PossessCommand("soldier_1"));

        var controlled = session.Engine.State.ControlledEntity;
        var point = controlled.Get<PositionComponent>().Position;
        session.Engine.RecordDeed(controlled, "wild_magic", 3, point, point, new[] { "damage" });
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
        Assert.Contains(cast.Messages, message => message.Contains("spends a patrol", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(debug.Debug!.Factions!, faction =>
            faction.Id == "empire"
            && faction.Resources.TryGetValue("patrols", out var patrols)
            && patrols == patrolsBefore - 1);

        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new WaitCommand());

        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Id.Value.StartsWith("imperial_patrol_", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<ActorComponent>(out var actor)
            && actor.Faction == "empire");
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
        session.Engine.AdvanceTurn();

        Assert.Equal(0, session.Engine.State.Factions.ResourceValue("empire", "patrols"));
        Assert.Contains(session.Engine.State.ScheduledEvents.Events, item => item.Kind == "empire_patrol");
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
        session.Engine.ApplyStatus(soldier, "webbed", duration: 3);
        await session.ExecuteAsync(new PossessCommand("soldier_1"));
        session.Engine.State.Factions.AdjustResource("empire", "patrols", -99);
        session.Engine.State.Factions.AdjustResource("empire", "warrants", -99);
        var heatBefore = session.Engine.State.Factions.ResourceValue("empire", "heat");

        var controlled = session.Engine.State.ControlledEntity;
        var point = controlled.Get<PositionComponent>().Position;
        session.Engine.RecordDeed(controlled, "wild_magic", 3, point, point, new[] { "damage" });
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
        Assert.True(session.Engine.State.Bonds.TryGet("maren_soul", "player_soul", out var bond));
        Assert.Equal(3, bond.Loyalty);
        Assert.Equal(2, bond.Admiration);
        Assert.Equal("grateful", bond.Posture);
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
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "fine blade" && claim.Status == "promised");
        Assert.Contains(travel.Deltas, delta => delta.Operation == "promiseItem");
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "promised blade"
            && entity.TryGet<PromiseAnchorComponent>(out _));
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
        Assert.True(lio.Get<MerchantComponent>().Wares.TryGetValue("fine blade", out var count));
        Assert.Equal(1, count);
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "fine blade" && claim.Status == "applied");
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
        Assert.Equal("player", lio.Get<ActorComponent>().Faction);
        Assert.Equal("hollowmere", lio.Get<FactionComponent>().FactionId);
        Assert.Contains(lio.Get<FactionComponent>().Roles, role => role == "follower");
        Assert.Contains(followers.Messages, message => message.Contains("Lio of Hollowmere", StringComparison.OrdinalIgnoreCase));
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
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogue" && (bool)delta.Details["generated"]!);
        Assert.Contains(talk.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(session.View().Claims!, claim => claim.Subject == "red bridge" && claim.Status == "promised");
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
        Assert.Equal("talk", entry.ResultAction);
        Assert.True(entry.ResultSuccess);
        Assert.Contains("dialogue", entry.DeltaOperations);
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
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueStepAside");
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
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueActionRejected");
        Assert.DoesNotContain(talk.Deltas, delta => delta.Operation == "dialogueStepAside");
        Assert.Equal(before, witness.Get<PositionComponent>().Position);
    }

    [Fact]
    public async Task GeneratedDialogueCanOpenReachableDoor()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Door keeper says, \"Fine. Open, then.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("open_door", TargetEntityId: "test_door") })));
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
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueOpenDoor");
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
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueOpenDoor");
        Assert.Contains(talk.Deltas, delta => delta.Operation == "realizePromise");
        Assert.Contains(talk.Deltas, delta => delta.Operation == "freePrisoner");
        Assert.Equal("player", session.Engine.EntityById("prisoner_1")!.Get<ActorComponent>().Faction);
    }

    [Fact]
    public async Task GeneratedDialogueCanGiveSpeakerItem()
    {
        var provider = new FixtureDialogueProvider(new DialogueResponse(
            "Quartermaster says, \"Take the brass key.\"",
            Proposals: new DialogueProposalSet(
                Actions: new[] { new DialogueActionProposal("give_item", ItemName: "brass key") })));
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

        var talk = await session.ExecuteAsync(new TalkCommand("quartermaster, may I have the key?"));

        Assert.True(talk.Success);
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueGiveItem");
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
        Assert.Contains(talk.Deltas, delta => delta.Operation == "dialogueCallHelp");
        Assert.Contains(session.Engine.State.ScheduledEvents.Events, item =>
            item.Kind == "empire_patrol"
            && item.SourceEntityId == EntityId.Create("imperial_caller"));
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
        Assert.Contains(travel.Deltas, delta => delta.Operation == "promiseSite");
        Assert.Equal("realized", promise.Status);
        Assert.Equal("1,0", promise.RealizedIn);
        Assert.Contains(session.Engine.State.Entities.Values, entity =>
            entity.Name == "checkpoint beyond this room"
            && entity.TryGet<PromiseAnchorComponent>(out var anchor)
            && anchor.PromiseIds.Contains(promise.Id));
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "site"
            && record.Source == $"promise:{promise.Id}:travel");
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
        Assert.True(sell.Success);
        Assert.Contains(sell.Messages, message => message.Contains("sell red tincture", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(15, inventory.Items["gold"]);
        Assert.DoesNotContain(inventory.Items, pair => pair.Key.Equals("red tincture", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ZoneSnapshotRoundTripsTerrainAndEntities()
    {
        var session = CreateMockSession();
        DisableImperialAi(session);
        session.Engine.SetTerrain(new GridPoint(4, 4), "blue_moss");
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
        Assert.Contains(open.Deltas, delta => delta.Operation == "freePrisoner");

        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
        var talk = await ExecuteTextAsync(session, "talk Lio");
        Assert.True(talk.Success);
        Assert.True(talk.ConsumedTurn);
        Assert.Contains(talk.Messages, message => message.Contains("Lio", StringComparison.OrdinalIgnoreCase));
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
    [InlineData("curse me with debt", "addCurse", "createPromise")]
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
        session.Engine.ApplyStatus(session.Engine.State.ControlledEntity, "marked", duration: 5);

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
        Assert.Contains(result.Messages, message => message.Contains("marble heart", StringComparison.OrdinalIgnoreCase));
    }

    private static GameSession CreateMockSession() =>
        GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));

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

        public FixtureDialogueClaimExtractor(params DialogueClaimProposal[] claims)
        {
            _claims = claims;
        }

        public string Name => "fixture-dialogue-claims";

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
