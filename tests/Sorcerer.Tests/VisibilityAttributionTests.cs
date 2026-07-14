using System.Linq;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Views;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 1.1 — one visibility and attribution policy. Deed capture and suspicion attribution both
/// project from <see cref="PerceptionSystem.ClassifyEffectWitnesses"/>, so there is exactly one
/// line-of-sight/range/concealment rule and one actor-vs-effect distinction. These pin that the
/// same escape, seen publicly / behind cover (effect only) / under concealment / not at all,
/// classifies four explainably different ways.
/// </summary>
public sealed class VisibilityAttributionTests
{
    [Fact]
    public void ClassifyEffectWitnessesDistinguishesActorEffectBothAndNeither()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var engine = session.Engine;
        var actor = engine.State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        var witness = engine.EntityById("soldier_1")!;

        // Put the witness three tiles east of the actor on a cleared sightline. Distance 3 is beyond
        // the concealed-notice radius (2), so concealment can hide the actor while the effect at the
        // actor's own tile stays visible.
        var witnessPoint = new GridPoint(origin.X + 3, origin.Y);
        witness.Set(new PositionComponent(witnessPoint));
        for (var dx = 0; dx <= 3; dx++)
        {
            engine.State.BlockingTerrain.Remove(new GridPoint(origin.X + dx, origin.Y));
        }

        // Move any third party off the sightline so it neither blocks nor joins the classification.
        foreach (var other in engine.State.Entities.Values
            .Where(entity => entity.Id != actor.Id && entity.Id != witness.Id)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && position.Position.Y == origin.Y
                && position.Position.X > origin.X
                && position.Position.X <= witnessPoint.X)
            .ToArray())
        {
            other.Set(new PositionComponent(new GridPoint(origin.X, origin.Y + 6)));
        }

        WitnessObservation Classify(GridPoint actorOrigin, GridPoint? effect) =>
            engine.ClassifyEffectWitnesses(actorOrigin, effect, actor)
                .Single(observation => observation.WitnessEntityId == witness.Id.Value);

        // Public: the witness sees the unconcealed actor and the effect at the actor's tile.
        var both = Classify(origin, origin);
        Assert.True(both.SawActor);
        Assert.True(both.SawEffect);
        Assert.Equal("both", both.Classification);

        // Actor only: the effect lands far outside the witness's sight; the actor does not.
        var farEffect = new GridPoint(witnessPoint.X + PerceptionSystem.DefaultSightRadius + 2, origin.Y);
        var actorOnly = Classify(origin, farEffect);
        Assert.True(actorOnly.SawActor);
        Assert.False(actorOnly.SawEffect);
        Assert.Equal("actor", actorOnly.Classification);

        // Concealed (behind cover / cloaked): the witness beyond the notice radius sees the effect
        // but cannot pin the concealed actor.
        engine.ApplyConsequence(WorldConsequence.ApplyStatus("test", actor.Id.Value, "concealed", duration: 5));
        var effectOnly = Classify(origin, origin);
        Assert.False(effectOnly.SawActor);
        Assert.True(effectOnly.SawEffect);
        Assert.Equal("effect", effectOnly.Classification);

        // Neither: pushed outside sight of both, the witness drops out of the classification entirely.
        witness.Set(new PositionComponent(new GridPoint(origin.X + PerceptionSystem.DefaultSightRadius + 6, origin.Y)));
        Assert.DoesNotContain(
            engine.ClassifyEffectWitnesses(origin, origin, actor),
            observation => observation.WitnessEntityId == witness.Id.Value);
    }

    [Fact]
    public void ConcealedActorMakesAWitnessedEffectSuspiciousButUnattributed()
    {
        // The same policy drives suspicion: an unconcealed actor is attributed; a concealed one is
        // noticed (pending) but not pinned. This is the suspicion projection of the classification.
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var engine = session.Engine;
        var actor = engine.State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        var actorSoulId = actor.TryGet<SoulComponent>(out var soul) ? soul.SoulId : actor.Id.Value;

        var witness = engine.EntityById("soldier_1")!;
        witness.Set(new PositionComponent(new GridPoint(origin.X + 3, origin.Y)));
        for (var dx = 0; dx <= 3; dx++)
        {
            engine.State.BlockingTerrain.Remove(new GridPoint(origin.X + dx, origin.Y));
        }
        foreach (var other in engine.State.Entities.Values
            .Where(entity => entity.Id != actor.Id && entity.Id != witness.Id)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && position.Position.Y == origin.Y
                && position.Position.X > origin.X
                && position.Position.X <= origin.X + 3)
            .ToArray())
        {
            other.Set(new PositionComponent(new GridPoint(origin.X, origin.Y + 6)));
        }

        var attributed = engine.PlanEffectSuspicion(origin, "wild_magic", actor)
            .Single(plan => plan.WitnessSoulId == (witness.TryGet<SoulComponent>(out var s) ? s.SoulId : witness.Id.Value));
        Assert.Equal("attributed", attributed.Status);
        Assert.Equal(actorSoulId, attributed.SuspectedSoulId);

        engine.ApplyConsequence(WorldConsequence.ApplyStatus("test", actor.Id.Value, "concealed", duration: 5));
        var pending = engine.PlanEffectSuspicion(origin, "wild_magic", actor)
            .Single(plan => plan.WitnessSoulId == (witness.TryGet<SoulComponent>(out var s) ? s.SoulId : witness.Id.Value));
        Assert.Equal("pending", pending.Status);
        Assert.Null(pending.SuspectedSoulId);
    }

    [Fact]
    public void DebugObservationSurfacesWitnessClassificationButPlayerViewDoesNot()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var engine = session.Engine;
        var actor = engine.State.ControlledEntity;
        var origin = actor.Get<PositionComponent>().Position;
        var witness = engine.EntityById("soldier_1")!;

        // Adjacent -> line of sight is trivially clear (no intervening tile).
        var witnessPoint = new GridPoint(origin.X + 1, origin.Y);
        witness.Set(new PositionComponent(witnessPoint));
        engine.State.BlockingTerrain.Remove(witnessPoint);

        var debug = session.Observation(debug: true).Debug!;
        Assert.NotNull(debug.Witnesses);
        var card = debug.Witnesses!.Single(entry => entry.WitnessEntityId == witness.Id.Value);
        Assert.True(card.SawActor);
        Assert.True(card.SawEffect);
        Assert.Equal("both", card.Classification);

        // The player-facing observation carries no debug state, so the classification never leaks
        // into non-omniscient views.
        Assert.Null(session.Observation(debug: false).Debug);
    }

    [Fact]
    public async Task WitnessedWildMagicNamesTheCarrierRatherThanSomeone()
    {
        var session = GameSession.CreateImperialEncounter(seed: 7);
        var engine = session.Engine;
        var player = engine.State.ControlledEntity;
        var origin = player.Get<PositionComponent>().Position;
        var witness = engine.EntityById("soldier_1")!;
        var witnessName = witness.TryGet<ProfileComponent>(out var profile)
            && !string.IsNullOrWhiteSpace(profile.PublicName)
            ? profile.PublicName
            : witness.Name;

        // Adjacent, clear line of sight: the witness sees the caster, so the deed is public.
        var witnessPoint = new GridPoint(origin.X + 1, origin.Y);
        witness.Set(new PositionComponent(witnessPoint));
        engine.State.BlockingTerrain.Remove(witnessPoint);

        var deed = engine.ApplyConsequence(WorldConsequence.RecordDeed(
            "test", player.Id.Value, "wild_magic", magnitude: 3,
            originX: origin.X, originY: origin.Y, effectX: origin.X, effectY: origin.Y,
            tags: new[] { "addStatus", "minor" }, sourceEntityId: player.Id.Value));
        Assert.True(deed.Applied, deed.Error);
        Assert.Equal("public", Assert.Single(deed.Deltas, delta => delta.Operation == "recordDeed").Details["visibility"]);

        var wait = await session.ExecuteAsync(new WaitCommand());

        // In-fiction evidence names the carrier who noticed (docs/AESTHETICS_AND_TONE.md), instead
        // of the old faceless "The people who saw...".
        Assert.Contains(wait.Messages, message =>
            message.Contains(witnessName, System.StringComparison.OrdinalIgnoreCase)
            && message.Contains("wild magic", System.StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(wait.Messages, message =>
            message.Contains("The people who saw", System.StringComparison.OrdinalIgnoreCase));
    }
}
