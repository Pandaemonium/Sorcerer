using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Magic;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// The resolver may charge an item the caster does not carry (a visible-but-unowned object or a
/// hallucinated component). Rather than reject the player's otherwise-valid spell, the engine
/// substitutes an unpayable item cost with raw effort (mana). A cost the caster can actually pay is
/// still consumed. Keeps the richer-cost behavior from frustrating players on weaker models.
/// </summary>
public sealed class SpellCostRepairTests
{
    [Fact]
    public async Task UnpayableItemCostIsSubstitutedWithManaInsteadOfFailing()
    {
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "minor",
            OutcomeText: "Warm light wards you.",
            Effects: new[]
            {
                new SpellEffect("addStatus", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["status"] = "warded",
                    ["duration"] = 3,
                }),
            },
            Costs: new[]
            {
                // "red tincture" is a healing item lying on the floor in this scene, not carried.
                new SpellCost("item", new Dictionary<string, object?>
                {
                    ["item"] = "red tincture",
                    ["quantity"] = 1,
                }),
            },
            RejectedReason: null);

        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new FixtureSpellProvider(resolution)));

        var result = await session.ExecuteAsync(new CastCommand("ward myself with warm light"));

        Assert.True(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.DoesNotContain(result.Messages, message => message.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Deltas, delta => delta.Operation.Equals("cost:mana", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation.Equals("cost:item", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PayableItemCostIsStillConsumed()
    {
        // The default origin carries grave salt, so a grave-salt item cost bites the item, not mana.
        var resolution = new SpellResolution(
            Accepted: true,
            Severity: "moderate",
            OutcomeText: "You crush grave salt into the working.",
            Effects: new[]
            {
                new SpellEffect("damage", new Dictionary<string, object?>
                {
                    ["target"] = "nearest_enemy",
                    ["amount"] = 4,
                    ["damageType"] = "arcane",
                }),
            },
            Costs: new[]
            {
                new SpellCost("item", new Dictionary<string, object?>
                {
                    ["item"] = "grave salt",
                    ["quantity"] = 1,
                }),
            },
            RejectedReason: null);

        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new FixtureSpellProvider(resolution)));

        var result = await session.ExecuteAsync(new CastCommand("wither the soldier with grave salt"));

        Assert.True(result.Success);
        Assert.Contains(result.Deltas, delta => delta.Operation.Equals("cost:item", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        private readonly SpellResolution _resolution;

        public FixtureSpellProvider(SpellResolution resolution) => _resolution = resolution;

        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(SpellRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SpellProviderResult("fixture", "{}", _resolution, false, null));
    }
}
