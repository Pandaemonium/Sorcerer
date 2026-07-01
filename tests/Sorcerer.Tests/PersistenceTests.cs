using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Validation;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Replay;
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

            var loaded = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
            var load = await loaded.ExecuteAsync(new LoadCommand(path));

            Assert.True(load.Success);
            Assert.NotNull(loaded.Observation().PendingCast);

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

    private sealed class DelayedDialogueClaimExtractor : IDialogueClaimExtractor
    {
        private readonly Task<DialogueClaimExtractionResult> _result;

        public DelayedDialogueClaimExtractor(Task<DialogueClaimExtractionResult> result)
        {
            _result = result;
        }

        public string Name => "delayed-test";

        public Task<DialogueClaimExtractionResult> ExtractAsync(
            DialogueClaimRequest request,
            CancellationToken cancellationToken) =>
            _result;
    }
}
