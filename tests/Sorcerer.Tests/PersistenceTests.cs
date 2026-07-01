using Sorcerer.Core;
using Sorcerer.Core.Commands;
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
}
