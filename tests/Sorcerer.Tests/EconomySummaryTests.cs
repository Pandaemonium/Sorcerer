using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 2.4: the working buy/sell/service economy is made legible by a read-only
/// <see cref="Sorcerer.Core.Views.EconomySummaryView"/> -- gold on hand and sellable stacks (source
/// measures) and the paid services offered by providers present in the zone (the recurring sinks in
/// reach). It composes ordinary inventory and service state; these pin that it mirrors both.
/// </summary>
public sealed class EconomySummaryTests
{
    [Fact]
    public void EconomySummaryMeasuresPurseAndSinksInReach()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;

        var before = session.Observation(debug: true).Debug!.Economy;
        Assert.NotNull(before);

        // GoldOnHand mirrors the sorcerer's actual inventory gold.
        var walletGold = player.TryGet<InventoryComponent>(out var inv) && inv.Items.TryGetValue("gold", out var g)
            ? g
            : 0;
        Assert.Equal(walletGold, before!.GoldOnHand);

        // A newly offered paid service becomes a measured sink in reach, with its cost counted.
        var provider = session.Engine.State.Entities.Values.First(e => e.Id != player.Id && e.Has<ActorComponent>());
        session.Engine.ApplyConsequence(WorldConsequence.OfferService(
            "test",
            provider.Id.Value,
            "quiet_passage_probe",
            "quiet passage",
            "A quiet way past the checkpoint.",
            "record_memory",
            goldCost: 7));

        var after = session.Observation(debug: true).Debug!.Economy!;
        Assert.Equal(before.PaidServicesInReach + 1, after.PaidServicesInReach);
        Assert.Equal(before.ServiceGoldInReach + 7, after.ServiceGoldInReach);
    }

    [Fact]
    public void EconomyGoldOnHandTracksASourceLanding()
    {
        var session = GameSession.CreateImperialEncounter();
        var player = session.Engine.State.ControlledEntity;
        var before = session.Observation(debug: true).Debug!.Economy!.GoldOnHand;

        // A source landing (loot, a sale) shows up as more gold on hand.
        var inventory = player.Get<InventoryComponent>();
        inventory.Items["gold"] = (inventory.Items.TryGetValue("gold", out var cur) ? cur : 0) + 20;

        var after = session.Observation(debug: true).Debug!.Economy!.GoldOnHand;
        Assert.Equal(before + 20, after);
    }
}
