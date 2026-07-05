using System;
using System.Linq;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Validation;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 8 of the WildMagic import: control follows a soul/body split. These lock that possession
/// moves the controlled pointer and the soul into the inhabited body while the vacated body remains
/// a real entity, and that a swapped state survives save/load intact.
/// </summary>
public sealed class BodySoulIdentityTests
{
    // Possession requires the caster adjacent to a target that cannot resist; mirror the setup the
    // characterization suite uses (relocate the caster next to soldier_1 and web it).
    private static async Task<ActionResult> PossessSoldierOne(GameSession session)
    {
        var soldier = session.Engine.EntityById("soldier_1")!;
        var soldierPosition = soldier.Get<PositionComponent>().Position;
        session.Engine.State.ControlledEntity.Set(
            new PositionComponent(new GridPoint(soldierPosition.X - 1, soldierPosition.Y)));
        session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test", "soldier_1", "webbed", duration: 3, sourceEntityId: "player"));
        return await session.ExecuteAsync(new PossessCommand("soldier_1"));
    }

    [Fact]
    public async Task PossessionMovesControlAndSoulButLeavesVacatedBodyReal()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var originalBodyId = session.Engine.State.ControlledEntityId.Value;

        var possess = await PossessSoldierOne(session);

        Assert.True(possess.Success);
        Assert.Equal("soldier_1", session.Engine.State.ControlledEntityId.Value);
        Assert.Equal("player_soul", session.Engine.EntityById("soldier_1")!.Get<SoulComponent>().SoulId);
        Assert.NotNull(session.Engine.EntityById(originalBodyId));
    }

    [Fact]
    public async Task SaveLoadPreservesPossessedState()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        await PossessSoldierOne(session);

        var saved = GameSaveService.Serialize(
            session.Engine.State,
            savedAt: new DateTimeOffset(2026, 7, 5, 0, 0, 0, TimeSpan.Zero));
        var loaded = GameSaveService.Deserialize(saved);

        Assert.True(StateValidator.Validate(loaded.State).IsValid);
        Assert.Equal("soldier_1", loaded.State.ControlledEntityId.Value);
        var soldier = loaded.State.Entities.Values.Single(entity => entity.Id.Value == "soldier_1");
        Assert.Equal("player_soul", soldier.Get<SoulComponent>().SoulId);
    }
}
