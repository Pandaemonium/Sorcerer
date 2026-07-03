using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Validation;
using Sorcerer.Core.World;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Replay;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

public sealed class PersistenceTests
{
    [Fact]
    public async Task SaveLoadSaveRoundTripIsByteStable()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 13);
        await session.ExecuteAsync(new ExamineCommand("tincture"));
        await session.ExecuteAsync(new WaitCommand());
        await session.ExecuteAsync(new CastCommand("make the floor between me and the soldier slick with moonlit ice"));
        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var savedAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var before = GameSaveService.Serialize(session.Engine.State, savedAt: savedAt);

        var loaded = GameSaveService.Deserialize(before);
        var after = GameSaveService.Serialize(
            loaded.State,
            loaded.PendingCast,
            loaded.PendingCastSerial,
            savedAt);

        Assert.Equal(before, after);
        Assert.True(StateValidator.Validate(loaded.State).IsValid);
        Assert.Equal(session.Engine.State.Turn, loaded.State.Turn);
        Assert.Equal(session.Engine.State.CurrentZoneId, loaded.State.CurrentZoneId);
        Assert.Equal(session.Engine.State.Canon.Records.Count, loaded.State.Canon.Records.Count);
        Assert.Equal(session.Engine.State.BackgroundJobs.Jobs.Count, loaded.State.BackgroundJobs.Jobs.Count);
        Assert.Equal(session.Engine.State.Rumors.Records.Count, loaded.State.Rumors.Records.Count);
        Assert.Equal(session.Engine.State.WorldTurns.Records.Count, loaded.State.WorldTurns.Records.Count);
    }

    [Fact]
    public async Task SaveLoadRoundTripsNewWildMagicPortComponentsAndLedgers()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 17);
        await session.ExecuteAsync(new CastCommand("harden my skin against the coming flame"));

        var player = session.Engine.State.ControlledEntity;
        await session.ExecuteAsync(new CastCommand("delay my wounds so they land later"));
        Assert.True(player.TryGet<Sorcerer.Core.Entities.DelayedDamageComponent>(out _));

        await session.ExecuteAsync(new CastCommand("mark the world with a debt that must someday be paid"));
        await session.ExecuteAsync(new CastCommand("wrap me in thorns that answer anyone who strikes me"));
        await session.ExecuteAsync(new CastCommand("compel the soldier to make them dance helplessly"));
        await session.ExecuteAsync(new CastCommand("open a gravity well that pulls everything standing on it"));

        // Delayed damage releases 3 turns after it was cast, so by now (several casts later) it
        // has legitimately expired; this test only needs the still-live lanes for the round trip.
        Assert.True(player.TryGet<Sorcerer.Core.Entities.ResistanceComponent>(out _));
        Assert.NotEmpty(session.Engine.State.WorldFlags);
        Assert.NotEmpty(session.Engine.State.PersistentEffects.Records);
        Assert.NotEmpty(session.Engine.State.TileFlows);

        var savedAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var before = GameSaveService.Serialize(session.Engine.State, savedAt: savedAt);
        var loaded = GameSaveService.Deserialize(before);
        var after = GameSaveService.Serialize(loaded.State, loaded.PendingCast, loaded.PendingCastSerial, savedAt);

        Assert.Equal(before, after);
        Assert.True(StateValidator.Validate(loaded.State).IsValid);
        var reloadedPlayer = loaded.State.ControlledEntity;
        Assert.True(reloadedPlayer.TryGet<Sorcerer.Core.Entities.ResistanceComponent>(out var reloadedResistance));
        Assert.Equal(40, reloadedResistance.Resistances["fire"]);
        Assert.Equal(session.Engine.State.WorldFlags.Count, loaded.State.WorldFlags.Count);
        Assert.Equal(session.Engine.State.PersistentEffects.Records.Count, loaded.State.PersistentEffects.Records.Count);
        Assert.Equal(session.Engine.State.TileFlows.Count, loaded.State.TileFlows.Count);

        var soldier = session.Engine.EntityById("soldier_1")!;
        if (soldier.TryGet<Sorcerer.Core.Entities.BehaviorTagsComponent>(out _))
        {
            var reloadedSoldier = loaded.State.Entities[soldier.Id];
            Assert.True(reloadedSoldier.TryGet<Sorcerer.Core.Entities.BehaviorTagsComponent>(out _));
        }
    }

    [Fact]
    public async Task SaveLoadPreservesDistortedRumorsAndBackgroundJobs()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 19);
        session.Engine.EntityById("soldier_1")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        session.Engine.EntityById("soldier_2")!.Set(new ActorComponent(0, 10, 0, 0, 3, 1, "empire"));
        var state = session.Engine.State;
        state.Rumors.Append(
            state.Turn,
            "test",
            "save_distortion_source",
            state.RegionId,
            state.RegionId,
            "South of here, there is a town called Hollowmere.",
            salience: 4,
            carrierIds: new[] { $"region:{state.RegionId}", "prisoner_1_soul" },
            tags: new[] { "rumor", "town" },
            hops: 2);

        await session.ExecuteAsync(new WaitCommand());

        var distorted = Assert.Single(state.Rumors.Records, rumor => rumor.SourceId == "save_distortion_source");
        Assert.Contains("distorted", distorted.Tags);
        Assert.Contains(distorted.DistortionHistory, entry =>
            entry.StartsWith("distortion:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(state.BackgroundJobs.Jobs, job =>
            job.Purpose == "rumor_distortion"
            && job.TargetId == distorted.Id
            && job.State == Sorcerer.Core.Runtime.BackgroundJobState.Applied);

        var savedAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var before = GameSaveService.Serialize(state, savedAt: savedAt);
        var loaded = GameSaveService.Deserialize(before);
        var after = GameSaveService.Serialize(loaded.State, loaded.PendingCast, loaded.PendingCastSerial, savedAt);

        Assert.Equal(before, after);
        var loadedRumor = Assert.Single(loaded.State.Rumors.Records, rumor => rumor.SourceId == "save_distortion_source");
        Assert.Equal(distorted.Text, loadedRumor.Text);
        Assert.Contains("distorted", loadedRumor.Tags);
        Assert.Contains(loadedRumor.DistortionHistory, entry =>
            entry.StartsWith("distortion:", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(loaded.State.BackgroundJobs.Jobs, job =>
            job.Purpose == "rumor_distortion"
            && job.TargetId == loadedRumor.Id
            && job.State == Sorcerer.Core.Runtime.BackgroundJobState.Applied);
    }

    [Fact]
    public void SaveLoadRoundTripsFullFlywheelStateSurface()
    {
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider()),
            seed: 23);
        var state = session.Engine.State;
        state.BackgroundSettings = new BackgroundJobSettings(Enabled: true, MaxQueuedJobs: 20, JobsPerTurn: 2);
        state.Zones["debug_zone"] = new ZoneSnapshot(
            "debug_zone",
            "hollowmere_margin",
            Generated: true,
            new Dictionary<EntityId, Entity>(),
            new HashSet<GridPoint>(),
            new Dictionary<GridPoint, string> { [new GridPoint(1, 1)] = "blue_moss" },
            new Dictionary<GridPoint, int> { [new GridPoint(1, 1)] = state.Turn + 12 },
            new Dictionary<GridPoint, TileFlow> { [new GridPoint(2, 2)] = new(1, 0, state.Turn + 9) },
            new Dictionary<string, IReadOnlySet<GridPoint>>(StringComparer.OrdinalIgnoreCase)
            {
                ["player_soul"] = new HashSet<GridPoint> { new(1, 1), new(2, 2) },
            },
            new[] { "test room profile" },
            new[] { "test promise hook" });

        var playerId = state.ControlledEntityId.Value;
        var playerSoulId = state.ControlledEntity.Get<SoulComponent>().SoulId;
        var lio = session.Engine.EntityById("prisoner_1")!;
        var lioSoulId = lio.Get<SoulComponent>().SoulId;

        var playerStart = state.ControlledEntity.Get<PositionComponent>().Position;
        ApplyRequired(session, WorldConsequence.MoveEntity(
            "persistence_test",
            playerId,
            playerStart.X + 1,
            playerStart.Y,
            operation: "testControlledMove",
            emitMessage: false,
            recordControlledMovement: true));
        ApplyRequired(session, WorldConsequence.RecordExploration(
            "persistence_test",
            playerSoulId,
            new[] { new GridPoint(33, 21), new GridPoint(34, 21) },
            operation: "testRecordExploration"));
        ApplyRequired(session, WorldConsequence.Message(
            "persistence_test",
            "The save ledger hears itself breathe.",
            targetEntityId: playerId,
            operation: "testMessage"));
        var deed = ApplyRequired(session, WorldConsequence.RecordDeed(
            "persistence_test",
            playerId,
            "bridge_collapse",
            4,
            3,
            5,
            effectX: 9,
            effectY: 4,
            tags: new[] { "magic", "bridge" },
            operation: "testRecordDeed"));
        var deedId = Assert.IsType<string>(deed.Details["deedId"]);
        ApplyRequired(session, WorldConsequence.UpdateDeed(
            "persistence_test",
            deedId,
            operation: "testUpdateDeed"));
        ApplyRequired(session, WorldConsequence.AddLegend(
            "persistence_test",
            playerSoulId,
            "bridge-breaker",
            3,
            deedId,
            operation: "testAddLegend"));
        ApplyRequired(session, WorldConsequence.AdjustFactionStanding(
            "persistence_test",
            "empire",
            "alarm",
            2,
            operation: "testFactionStanding"));
        ApplyRequired(session, WorldConsequence.AdjustFactionResource(
            "persistence_test",
            "empire",
            "heat",
            3,
            max: 99,
            operation: "testFactionResource"));
        ApplyRequired(session, WorldConsequence.RecordMemory(
            "persistence_test",
            lio.Id.Value,
            "Lio saw the bridge fold like wet paper.",
            "test",
            salience: 4,
            shareable: true,
            operation: "testRecordMemory"));
        var promise = ApplyRequired(session, WorldConsequence.CreatePromise(
            "persistence_test",
            "rumor",
            "There is a moonlit blade under the burned oak north of here.",
            anchorEntityId: "notice_1",
            triggerHint: "travel",
            operation: "testCreatePromise",
            playerVisible: true,
            salience: 4,
            subject: "moonlit blade",
            claimedPlace: "north road",
            realizationKind: "item",
            sourceClaimId: "claim_seed_persistence",
            sourceSpeakerId: lio.Id.Value,
            sourceListenerSoulId: playerSoulId,
            sourceConfidence: 82,
            bindPlace: state.RegionId,
            emitMessage: false));
        var promiseId = Assert.IsType<string>(promise.TargetId);
        var promiseEligibilityTurn = state.Turn;
        ApplyRequired(session, WorldConsequence.UpdatePromise(
            "persistence_test",
            promiseId,
            lastEligibilityFailure: "direction_mismatch",
            lastEligibilityContext: "trigger=travel;region=hollowmere_margin;zone=1,0;direction=east",
            lastEligibilityTurn: promiseEligibilityTurn,
            operation: "testUpdatePromiseEligibility",
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = false,
                ["auditOnly"] = true,
            }));
        var claim = ApplyRequired(session, WorldConsequence.RecordClaim(
            "persistence_test",
            lio.Id.Value,
            playerSoulId,
            "Lio says a moonlit blade waits under the burned oak.",
            "item",
            "moonlit blade",
            salience: 4,
            confidence: 82,
            playerVisible: true,
            tags: new[] { "lead", "item" },
            operation: "testRecordClaim"));
        var claimId = Assert.IsType<string>(claim.Details["claimId"]);
        ApplyRequired(session, WorldConsequence.UpdateClaim(
            "persistence_test",
            claimId,
            status: "promised",
            boundPromiseId: promiseId,
            operation: "testUpdateClaim"));
        var rumor = ApplyRequired(session, WorldConsequence.RecordRumor(
            "persistence_test",
            "claim",
            claimId,
            state.RegionId,
            "hollowmere_margin",
            "Someone in chains spoke of a moonlit blade.",
            salience: 4,
            carrierIds: new[] { $"region:{state.RegionId}", lioSoulId },
            tags: new[] { "item", "blade" },
            hops: 2,
            operation: "testRecordRumor"));
        var rumorId = Assert.IsType<string>(rumor.Details["rumorId"]);
        ApplyRequired(session, WorldConsequence.UpdateRumor(
            "persistence_test",
            rumorId,
            addCarrierIds: new[] { "region:hollowmere_margin" },
            addTags: new[] { "distorted" },
            appendDistortionHistory: new[] { "distortion: persistence test" },
            incrementHops: true,
            operation: "testUpdateRumor"));
        ApplyRequired(session, WorldConsequence.RecordWorldTurn(
            "persistence_test",
            "turn",
            "rumor_spread",
            rumorId,
            "A test rumor crossed the save boundary.",
            new Dictionary<string, object?>
            {
                ["fromRegionId"] = state.RegionId,
                ["toRegionId"] = "hollowmere_margin",
                ["weights"] = new object?[] { 1, "witness", true },
            },
            operation: "testWorldTurn"));
        ApplyRequired(session, WorldConsequence.ScheduleEvent(
            "persistence_test",
            "wild_echo",
            turns: 3,
            eventPayload: new Dictionary<string, object?> { ["text"] = "A saved echo comes due." },
            operation: "testScheduleEvent"));
        ApplyRequired(session, WorldConsequence.CreateTrigger(
            "persistence_test",
            "saved bell",
            "delay",
            delay: 2,
            interval: 1,
            uses: 2,
            duration: 8,
            effectType: "message",
            effectFields: new Dictionary<string, object?> { ["text"] = "A saved bell rings." },
            description: "A test trigger for persistence.",
            anchorX: 4,
            anchorY: 5,
            playerVisible: true,
            operation: "testCreateTrigger"));
        ApplyRequired(session, WorldConsequence.RecordSuspicion(
            "persistence_test",
            "wild_echo",
            9,
            4,
            actorEntityId: playerId,
            operation: "testRecordSuspicion"));
        ApplyRequired(session, WorldConsequence.AddCanon(
            "persistence_test",
            "test_canon",
            "notice_1",
            "The notice remembers the bridge collapse.",
            "Notice remembers bridge.",
            new[] { "notice", "bridge" },
            operation: "testAddCanon"));
        ApplyRequired(session, WorldConsequence.UpdateBond(
            "persistence_test",
            lio.Id.Value,
            playerSoulId,
            loyaltyDelta: 2,
            fearDelta: 1,
            admirationDelta: 2,
            resentmentDelta: 0,
            posture: "uneasy_ally",
            operation: "testUpdateBond"));
        ApplyRequired(session, WorldConsequence.UpdateWant(
            "persistence_test",
            lio.Id.Value,
            status: "pressing",
            addTags: new[] { "save_surface" },
            operation: "testUpdateWant"));
        ApplyRequired(session, WorldConsequence.OfferTrade(
            "persistence_test",
            lio.Id.Value,
            itemName: "reed knife",
            quantity: 1,
            gold: 17,
            operation: "testOfferTrade"));
        ApplyRequired(session, WorldConsequence.OfferService(
            "persistence_test",
            lio.Id.Value,
            "quiet_path",
            "Quiet Path",
            "Open a route without saying folk magic aloud.",
            "create_route",
            goldCost: 2,
            targetHint: "nearest door",
            tags: new[] { "hush_hush", "folk_magic" },
            operation: "testOfferService"));
        ApplyRequired(session, WorldConsequence.CreatePersistentEffect(
            "persistence_test",
            playerId,
            "on_hit",
            "message",
            new Dictionary<string, object?> { ["text"] = "The saved thorn answers." },
            uses: 2,
            playerVisible: true,
            operation: "testPersistentEffect"));
        ApplyRequired(session, WorldConsequence.CreateFlow(
            "persistence_test",
            4,
            5,
            radius: 0,
            dx: 1,
            dy: 0,
            duration: 7,
            operation: "testCreateFlow"));
        ApplyRequired(session, WorldConsequence.SetWorldFlag(
            "persistence_test",
            "bridge_collapsed_in_save_test",
            new Dictionary<string, object?>
            {
                ["source"] = "persistence_test",
                ["turn"] = state.Turn,
                ["nested"] = new Dictionary<string, object?> { ["trueEnough"] = true },
            },
            "A nested world flag used by the save-surface test.",
            operation: "testSetWorldFlag"));
        var backgroundJob = ApplyRequired(session, WorldConsequence.QueueBackgroundJob(
            "persistence_test",
            "brazier_1",
            "entity_detail",
            priority: 5,
            operation: "testQueueBackgroundJob"));
        var jobId = Assert.IsType<string>(backgroundJob.Details["jobId"]);
        ApplyRequired(session, WorldConsequence.UpdateBackgroundJob(
            "persistence_test",
            jobId,
            BackgroundJobState.Applied.ToString(),
            operation: "testUpdateBackgroundJob",
            startedTurn: state.Turn,
            completedTurn: state.Turn,
            appliedTurn: state.Turn,
            resultText: "The brazier keeps a saved marginal note."));

        Assert.NotEmpty(state.Suspicions.Records);
        Assert.NotEmpty(state.TileFlows);
        Assert.Contains(state.Zones.Values, zone => zone.TileFlows.Count > 0);

        var savedAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var before = GameSaveService.Serialize(state, savedAt: savedAt);
        var loaded = GameSaveService.Deserialize(before);
        var after = GameSaveService.Serialize(loaded.State, loaded.PendingCast, loaded.PendingCastSerial, savedAt);

        Assert.Equal(before, after);
        Assert.True(StateValidator.Validate(loaded.State).IsValid);
        Assert.Equal(state.Claims.Records.Count, loaded.State.Claims.Records.Count);
        Assert.Equal(state.Rumors.Records.Count, loaded.State.Rumors.Records.Count);
        Assert.Equal(state.PromiseLedger.Promises.Count, loaded.State.PromiseLedger.Promises.Count);
        Assert.Equal(state.WorldTurns.Records.Count, loaded.State.WorldTurns.Records.Count);
        Assert.Equal(state.ScheduledEvents.Events.Count, loaded.State.ScheduledEvents.Events.Count);
        Assert.Equal(state.Triggers.Records.Count, loaded.State.Triggers.Records.Count);
        Assert.Equal(state.Suspicions.Records.Count, loaded.State.Suspicions.Records.Count);
        Assert.Equal(state.PersistentEffects.Records.Count, loaded.State.PersistentEffects.Records.Count);
        Assert.Equal(state.BackgroundJobs.Jobs.Count, loaded.State.BackgroundJobs.Jobs.Count);
        Assert.Equal(state.TileFlows.Count, loaded.State.TileFlows.Count);
        Assert.Equal(state.WorldFlags.Count, loaded.State.WorldFlags.Count);
        Assert.Contains(new GridPoint(33, 21), loaded.State.ExploredBySoulId[playerSoulId]);
        Assert.Equal(state.LastControlledMoveDelta, loaded.State.LastControlledMoveDelta);
        Assert.Contains(loaded.State.Zones.Values, zone => zone.TileFlows.Count > 0);
        Assert.True(loaded.State.Entities[lio.Id].TryGet<WantComponent>(out var loadedWant));
        Assert.Contains("save_surface", loadedWant.Tags);
        Assert.True(loaded.State.Entities[lio.Id].TryGet<ServiceComponent>(out var loadedServices));
        Assert.Contains(loadedServices.Offers, offer => offer.Id == "quiet_path");
        Assert.True(loaded.State.Entities[lio.Id].TryGet<MerchantComponent>(out var loadedMerchant));
        Assert.Equal(17, loadedMerchant.Gold);
        Assert.Contains(loaded.State.Claims.Records, record => record.BoundPromiseId == promiseId);
        Assert.Contains(loaded.State.PromiseLedger.Promises, record =>
            record.Id == promiseId
            && record.LastEligibilityFailure == "direction_mismatch"
            && record.LastEligibilityContext == "trigger=travel;region=hollowmere_margin;zone=1,0;direction=east"
            && record.LastEligibilityTurn == promiseEligibilityTurn
            && record.SourceClaimId == "claim_seed_persistence"
            && record.SourceSpeakerId == lio.Id.Value
            && record.SourceListenerSoulId == playerSoulId
            && record.SourceConfidence == 82);
        Assert.Contains(loaded.State.Rumors.Records, record =>
            record.Id == rumorId
            && record.DistortionHistory.Contains("distortion: persistence test"));
    }

    [Fact]
    public async Task SaveLoadPreservesPendingCast()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sorcerer_pending_{Guid.NewGuid():N}.json");
        try
        {
            var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
            var begin = await session.ExecuteAsync(new BeginCastCommand("summon a brass moth that bites enemies"));

            Assert.True(begin.Success);
            Assert.NotNull(session.Observation().PendingCast);

            var save = await session.ExecuteAsync(new SaveCommand(path));
            Assert.True(save.Success);

            Assert.NotNull(save.Magic);
            Assert.False(string.IsNullOrWhiteSpace(save.Magic.ResolvedMagicJson));
            var saved = GameSaveService.Load(path);
            Assert.NotNull(saved.PendingCast?.Resolution);

            var loaded = GameSession.CreateImperialEncounter(new WildMagicController(new ThrowingSpellProvider()));
            var load = await loaded.ExecuteAsync(new LoadCommand(path));

            Assert.True(load.Success);
            Assert.NotNull(loaded.Observation().PendingCast);
            Assert.Equal("ready", loaded.Observation().PendingCast!.State);

            var resolved = await loaded.ExecuteAsync(new AwaitCastCommand());
            Assert.True(resolved.Success);
            Assert.Equal("cast", resolved.Action);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task SaveWaitsForPendingDialogueClaimExtraction()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sorcerer_claim_flush_{Guid.NewGuid():N}.json");
        var extraction = new TaskCompletionSource<DialogueClaimExtractionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var session = GameSession.CreateImperialEncounter(
                new WildMagicController(new MockSpellProvider()),
                claimExtractor: new DelayedDialogueClaimExtractor(extraction.Task));
            session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));

            var talk = await session.ExecuteAsync(new TalkCommand("Lio, what waits on the road?"));
            Assert.True(talk.Success);

            var saveTask = session.ExecuteAsync(new SaveCommand(path));
            await Task.Yield();
            Assert.False(saveTask.IsCompleted);

            extraction.SetResult(new DialogueClaimExtractionResult(
                "delayed-test",
                "{}",
                TechnicalFailure: false,
                Error: null,
                new[]
                {
                    new DialogueClaimProposal(
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
                        Tags: new[] { "item", "blade" }),
                }));

            var save = await saveTask;
            Assert.True(save.Success);
            Assert.Contains(save.Deltas, delta => delta.Operation == "claimPromise");

            var loaded = GameSaveService.Load(path);
            Assert.Contains(loaded.State.Claims.Records, claim =>
                claim.Subject == "fine blade"
                && claim.Status == "promised");
            Assert.Contains(loaded.State.PromiseLedger.Promises, promise =>
                promise.Subject == "fine blade"
                && promise.RealizationKind == "item");
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task MaterializedSpellJsonReplaysWithoutOriginalProvider()
    {
        var command = new CastCommand("set the nearest soldier's boots burning with a blue coal");
        var original = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 17);

        var originalResult = await original.ExecuteAsync(command);

        Assert.True(originalResult.Success);
        var materialized = originalResult.Magic?.ResolvedMagicJson;
        Assert.False(string.IsNullOrWhiteSpace(materialized));

        var replay = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { materialized! })),
            seed: 17);
        var replayResult = await replay.ExecuteAsync(command);

        Assert.True(replayResult.Success);
        Assert.Equal(originalResult.Magic!.EffectTypes, replayResult.Magic!.EffectTypes);
        Assert.Equal(original.Engine.State.Turn, replay.Engine.State.Turn);
    }

    [Fact]
    public async Task MaterializedDialogueReplaysWithoutOriginalProviders()
    {
        var response = new DialogueResponse(
            "Old Maren keeps a fine blade under the burned oak north of here.",
            Intent: "confide_lead",
            Proposals: new DialogueProposalSet(
                Claims: new[]
                {
                    new DialogueClaimProposal(
                        "Old Maren keeps a fine blade under the burned oak north of here.",
                        "item",
                        "fine blade",
                        Salience: 4,
                        Confidence: 84,
                        PlayerVisible: true,
                        BindAsPromise: true,
                        PromiseKind: "rumor",
                        RealizationKind: "item",
                        TriggerHint: "travel",
                        ClaimedPlace: "burned oak north of here",
                        ItemName: "fine blade"),
                }));
        var extraction = new DialogueClaimProposal(
            "Old Maren has a niece named Nannerl.",
            "person",
            "Nannerl",
            Salience: 3,
            Confidence: 80,
            PlayerVisible: true,
            BindAsPromise: false);
        var command = new TalkCommand("Lio, trust me with one useful thing.");
        var original = GameSession.CreateImperialEncounter(
            dialogueProvider: new FixtureDialogueProvider(response),
            claimExtractor: new FixtureDialogueClaimExtractor(requiresSpokenTextSupport: false, extraction),
            seed: 31);
        PrepareDialogueAccess(original);

        var originalTalk = await original.ExecuteAsync(command);
        var originalWait = await original.ExecuteAsync(new WaitCommand());

        Assert.True(originalTalk.Success, string.Join("\n", originalTalk.Messages));
        Assert.NotNull(originalTalk.Dialogue);
        Assert.NotNull(originalTalk.Dialogue.Response?.Proposals?.Claims);
        Assert.Single(originalWait.DialogueClaimExtractions);
        Assert.NotEmpty(originalWait.DialogueClaimExtractions.Single().Claims);

        var replay = GameSession.CreateImperialEncounter(
            dialogueProvider: new ReplayDialogueProvider(new[] { originalTalk.Dialogue! }),
            claimExtractor: new ReplayDialogueClaimExtractor(originalWait.DialogueClaimExtractions),
            seed: 31);
        PrepareDialogueAccess(replay);
        var replayTalk = await replay.ExecuteAsync(command);
        var replayWait = await replay.ExecuteAsync(new WaitCommand());

        Assert.True(replayTalk.Success);
        Assert.True(replayWait.Success);
        Assert.Contains(replayTalk.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Contains(replayWait.Deltas, delta => delta.Operation == "claimPromise");
        Assert.Equal(
            original.Engine.State.PromiseLedger.Promises.Count,
            replay.Engine.State.PromiseLedger.Promises.Count);
        Assert.Equal(
            original.Engine.State.Claims.Records.Count,
            replay.Engine.State.Claims.Records.Count);

        var savedAt = new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            GameSaveService.Serialize(original.Engine.State, savedAt: savedAt),
            GameSaveService.Serialize(replay.Engine.State, savedAt: savedAt));
    }

    private sealed class DelayedDialogueClaimExtractor : IDialogueClaimExtractor
    {
        private readonly Task<DialogueClaimExtractionResult> _result;

        public DelayedDialogueClaimExtractor(Task<DialogueClaimExtractionResult> result)
        {
            _result = result;
        }

        public string Name => "delayed-test";

        public bool RequiresSpokenTextSupport => false;

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken) =>
            _result;
    }

    private sealed class FixtureDialogueProvider : IDialogueProvider
    {
        private readonly DialogueResponse _response;

        public FixtureDialogueProvider(DialogueResponse response)
        {
            _response = response;
        }

        public string Name => "fixture-dialogue";

        public Task<DialogueProviderResult> ResolveAsync(
            DialogueRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DialogueProviderResult(
                Name,
                RawText: "",
                TechnicalFailure: false,
                Error: null,
                Response: _response));
    }

    private sealed class ThrowingSpellProvider : ISpellProvider
    {
        public string Name => "throwing";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Loaded materialized pending casts should not call the provider.");
    }

    private sealed class FixtureDialogueClaimExtractor : IDialogueClaimExtractor
    {
        private readonly IReadOnlyList<DialogueClaimProposal> _claims;

        public FixtureDialogueClaimExtractor(
            bool requiresSpokenTextSupport,
            params DialogueClaimProposal[] claims)
        {
            RequiresSpokenTextSupport = requiresSpokenTextSupport;
            _claims = claims;
        }

        public string Name => "fixture-claims";

        public bool RequiresSpokenTextSupport { get; }

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

    private static void PrepareDialogueAccess(GameSession session)
    {
        foreach (var id in new[] { "soldier_1", "soldier_2" })
        {
            session.Engine.EntityById(id)!.Set(new ControllerComponent(ControllerKind.None));
        }

        var door = session.Engine.EntityById("cell_door_1")!;
        door.Set(door.Get<DoorComponent>() with { IsOpen = true });
        door.Set(door.Get<PhysicalComponent>() with { BlocksMovement = false, BlocksSight = false });
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(13, 5)));
    }

    private static WorldConsequenceApplyResult ApplyRequired(GameSession session, WorldConsequence consequence)
    {
        var applied = session.Engine.ApplyConsequence(consequence);
        Assert.True(applied.Applied, applied.Error ?? $"Consequence {consequence.Type} was not applied.");
        return applied;
    }
}
