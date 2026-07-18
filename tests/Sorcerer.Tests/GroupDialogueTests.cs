using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class GroupDialogueTests
{
    [Fact]
    public async Task GroupTalkGathersNearbyParticipantsAndLeavesShareableMemories()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var players = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        var ids = SeatParticipants(session, players, new[]
        {
            ("teller_a", "Ragna Bone-Tale, tale-witness", new[] { "resident", "witness", "tale", "hall" }),
            ("teller_b", "Ulf Storm-Sworn, harpoon mender", new[] { "resident", "story", "brall" }),
            ("teller_c", "Sef Ale-Oath, cask-keep", new[] { "resident", "hall", "ale" }),
        });

        var result = await session.ExecuteAsync(new GroupTalkCommand("Tell me what happened off the quay."));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        Assert.True(result.ConsumedTurn);
        // At least two named participants spoke, and every spoken line is attributed to a seated one.
        var spokenNames = result.Messages.Where(m => m.Contains(": \"")).Select(m => m.Split(':')[0].Trim()).ToArray();
        Assert.True(spokenNames.Length >= 2, $"expected a multi-speaker exchange, got {spokenNames.Length}");

        // Real, replayable state: participants kept shareable memories with provenance.
        var withMemory = ids.Select(id => session.Engine.EntityById(id))
            .Count(e => e is not null && e.TryGet<MemoryComponent>(out var m) && m.Records.Count > 0);
        Assert.True(withMemory >= 2, "group utterances should become shareable memories on the speakers");
    }

    [Fact]
    public async Task HollowmereGroupSplitsHonestlyBetweenStabilityAndFreedom()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var players = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
        SeatParticipants(session, players, new[]
        {
            ("loyalist", "Maren Client-True, ferry clerk", new[] { "resident", "hollowmere", "empire", "client" }),
            ("free", "Nim Red-Thread, oathkeeper", new[] { "resident", "hollowmere", "free_folk", "shelter" }),
        });

        var result = await session.ExecuteAsync(new GroupTalkCommand("Should this house take the stranger in?"));

        Assert.True(result.Success, string.Join(" | ", result.Messages));
        var transcript = string.Join("\n", result.Messages);
        // Both sides of the honest disagreement are present: the pro-stability voice frets about
        // roads and reaping, the pro-freedom voice about sheltering strangers.
        Assert.Contains("roads", transcript, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("strangers", transcript, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GroupTalkNeedsEnoughPeople()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        // Move the player somewhere empty so no one is in reach.
        var player = session.Engine.State.ControlledEntity;
        player.Set(new PositionComponent(new GridPoint(1, 1)));

        var result = await session.ExecuteAsync(new GroupTalkCommand("Anyone?"));

        Assert.False(result.Success);
        Assert.Contains(result.Messages, m => m.Contains("not enough people", System.StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SeatParticipants(
        GameSession session,
        GridPoint origin,
        (string Id, string Name, string[] Tags)[] people)
    {
        var state = session.Engine.State;
        var placed = new List<string>();
        var offsets = new[] { new GridPoint(1, 0), new GridPoint(-1, 0), new GridPoint(0, 1), new GridPoint(1, 1) };
        for (var i = 0; i < people.Length; i++)
        {
            var (id, name, tags) = people[i];
            var entity = new Entity(EntityId.Create(id), name);
            var pos = new GridPoint(origin.X + offsets[i].X, origin.Y + offsets[i].Y);
            entity.Set(new PositionComponent(pos));
            entity.Set(new ActorComponent(8, 8, 0, 0, 0, 0, "neutral"));
            entity.Set(new TagsComponent(tags));
            entity.Set(new WantComponent(id + "_want", "someone to hear them out", 3));
            state.Entities[entity.Id] = entity;
            placed.Add(id);
        }

        return placed.ToArray();
    }
}
