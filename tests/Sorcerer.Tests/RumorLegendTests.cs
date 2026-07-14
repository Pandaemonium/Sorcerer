using System;
using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.World;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// The legend grows in the telling (Q25 / foundation for Brall's boasting): once a witnessed DEED's
/// rumor has travelled a few hops it takes on an escalating, admiring frame -- the tale outrunning
/// the truth -- while the original deed is preserved. Non-deed rumors (leads, gossip) are left plain.
/// </summary>
public sealed class RumorLegendTests
{
    private const string Deed = "the sorcerer tore a containment ward open in the marble yard";

    [Fact]
    public void WellTravelledDeedRumorsGrowIntoTallerTales()
    {
        var (session, fromRegion, toRegion) = CrossRegionSetup();
        var state = session.Engine.State;
        state.Rumors.Append(
            state.Turn, "deed", "deed_1", fromRegion, fromRegion, Deed,
            salience: 5, carrierIds: new[] { $"region:{fromRegion}" }, tags: new[] { "rumor", "deed" }, hops: 2);

        RumorSystem.Propagate(state, "test", maxRumors: 1, maxCarriersPerRumor: 2,
            announce: false, applyConsequence: session.Engine.ApplyConsequence);

        var rumor = state.Rumors.Records.First(r => r.SourceId == "deed_1");
        Assert.True(rumor.Hops >= 3, "the deed should have hopped past the embellishment threshold");
        Assert.Equal(Deed, rumor.OriginalText); // the truth is kept
        Assert.NotEqual(Deed, rumor.Text);      // the tale has grown in the telling
    }

    [Fact]
    public void TheGrownTaleStillContainsTheDeed()
    {
        var (session, fromRegion, toRegion) = CrossRegionSetup();
        var state = session.Engine.State;
        state.Rumors.Append(
            state.Turn, "deed", "deed_2", fromRegion, fromRegion, Deed,
            salience: 5, carrierIds: new[] { $"region:{fromRegion}" }, tags: new[] { "rumor", "deed" }, hops: 2);

        RumorSystem.Propagate(state, "test", maxRumors: 1, maxCarriersPerRumor: 2,
            announce: false, applyConsequence: session.Engine.ApplyConsequence);

        var rumor = state.Rumors.Records.First(r => r.SourceId == "deed_2");
        Assert.Contains("containment ward open in the marble yard", rumor.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NonDeedRumorsAreNotDressedUp()
    {
        var (session, fromRegion, toRegion) = CrossRegionSetup();
        var state = session.Engine.State;
        const string lead = "a ferry oathkeeper at the Red Thread Houses knows who broke the road's promise";
        state.Rumors.Append(
            state.Turn, "lead", "lead_1", fromRegion, fromRegion, lead,
            salience: 5, carrierIds: new[] { $"region:{fromRegion}" }, tags: new[] { "rumor", "lead" }, hops: 2);

        RumorSystem.Propagate(state, "test", maxRumors: 1, maxCarriersPerRumor: 2,
            announce: false, applyConsequence: session.Engine.ApplyConsequence);

        var rumor = state.Rumors.Records.First(r => r.SourceId == "lead_1");
        Assert.Equal(lead, rumor.Text); // a lead stays a plain lead, never a boast
    }

    private static (GameSession Session, string FromRegion, string ToRegion) CrossRegionSetup()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var state = session.Engine.State;
        var graph = WorldPlaceGraph.Create(state.Seed, RegionCatalog.LoadDefault());
        var settlements = graph.Settlements.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        var road = graph.Roads.First(candidate =>
            !settlements[candidate.FromSettlementId].RegionId.Equals(
                settlements[candidate.ToSettlementId].RegionId, StringComparison.OrdinalIgnoreCase));
        var fromRegion = settlements[road.FromSettlementId].RegionId;
        var toRegion = settlements[road.ToSettlementId].RegionId;
        state.RegionId = toRegion;
        return (session, fromRegion, toRegion);
    }
}
