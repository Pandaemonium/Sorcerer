using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Xunit;

namespace Sorcerer.Tests;

public sealed class CharterFormsTests
{
    [Fact]
    public void SpellbookMeetsTheTwelveFormFloor()
    {
        Assert.True(CharterSpellbook.Default.Spells.Count >= 12,
            $"expected >=12 charter forms, found {CharterSpellbook.Default.Spells.Count}");
    }

    [Theory]
    [InlineData("cleansing_writ_1", false)]
    [InlineData("surveyor_edict_1", false)]
    [InlineData("bulwark_edict_1", false)]
    [InlineData("ward_step_1", true)]
    [InlineData("furrow_edict_1", true)]
    public async Task NewCharterFormCastsCleanly(string spellId, bool needsTile)
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        var soulId = session.Engine.State.ControlledEntity.Get<SoulComponent>().SoulId;
        Assert.True(session.Engine.State.Souls.LearnCharterSpell(soulId, spellId), $"could not learn {spellId}");

        if (needsTile)
        {
            var pos = session.Engine.State.ControlledEntity.Get<PositionComponent>().Position;
            await session.ExecuteAsync(new TargetCommand(new GridPoint(pos.X + 1, pos.Y)));
        }

        var result = await session.ExecuteAsync(new CharterCommand(spellId));
        Assert.True(result.Success, $"{spellId} failed: {string.Join(" / ", result.Messages)}");
    }
}
