using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Magic;
using Sorcerer.Magic.Costs;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Coverage for the Group 4 wild-magic operations (animateEntity, dispelMagic, revealTruth)
/// and the wild-magic reach rule for open_or_unlock consequences. These close "can do
/// anything" gaps found during the 2026-07 playtest arc: raising corpses/props, cancelling
/// active magic in-fiction (BUG_LOG [16] had no cancel path), and divination that answers
/// with engine truth instead of narration.
/// </summary>
public class WildMagicGroup4OperationTests
{
    [Fact]
    public async Task AnimateCorpseRaisesItAsABoundedAllyAndClearsCorpseState()
    {
        var session = Session(AcceptedSpell(
            "The soldier's body stands back up, re-strung by wild magic.",
            new SpellEffect("animateEntity", new Dictionary<string, object?>
            {
                ["target"] = "soldier_1",
                ["faction"] = "player",
            })));
        var soldier = session.Engine.EntityById("soldier_1")!;
        soldier.Set(soldier.Get<ActorComponent>() with { HitPoints = 0 });

        var result = await session.ExecuteAsync(new CastCommand("raise the fallen soldier to fight for me"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        var actor = soldier.Get<ActorComponent>();
        Assert.True(actor.Alive);
        Assert.Equal("player", actor.Faction);
        Assert.InRange(actor.HitPoints, 1, 12);
        Assert.InRange(actor.Attack, 0, 4);
        Assert.Equal("player", soldier.Get<FactionComponent>().FactionId);
        Assert.Equal(ControllerKind.Ai, soldier.Get<ControllerComponent>().Kind);
        Assert.True(soldier.Get<PhysicalComponent>().BlocksMovement);
        var tags = soldier.Get<TagsComponent>().Tags;
        Assert.Contains("animated", tags, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("defeated", tags, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith("risen ", soldier.Name);
        Assert.True(soldier.Has<SummonedComponent>());
    }

    [Fact]
    public async Task AnimateFixtureGivesItABoundedBodyUnderThePlayersBanner()
    {
        var session = Session(AcceptedSpell(
            "The brazier unfolds brass legs and stands.",
            new SpellEffect("animateEntity", new Dictionary<string, object?>
            {
                ["target"] = "brazier_1",
            })));

        var result = await session.ExecuteAsync(new CastCommand("wake the brazier to fight at my side"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        var brazier = session.Engine.EntityById("brazier_1")!;
        var actor = brazier.Get<ActorComponent>();
        Assert.True(actor.Alive);
        Assert.Equal("player", actor.Faction);
        Assert.InRange(actor.HitPoints, 1, 12);
        Assert.StartsWith("animated ", brazier.Name);
        Assert.True(brazier.Has<SoulComponent>());
    }

    [Fact]
    public async Task AnimateRejectsALivingActorInsteadOfSeizingIt()
    {
        var session = Session(AcceptedSpell(
            "The living soldier lurches like a puppet.",
            new SpellEffect("animateEntity", new Dictionary<string, object?>
            {
                ["target"] = "soldier_1",
            })));
        var turnBefore = session.Engine.State.Turn;

        var result = await session.ExecuteAsync(new CastCommand("puppet the soldier's body with strings of light"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(turnBefore + 1, session.Engine.State.Turn);
        Assert.Contains(result.Messages, message => message.Contains("corpse or an inert object"));
        Assert.Equal("empire", session.Engine.EntityById("soldier_1")!.Get<ActorComponent>().Faction);
    }

    [Fact]
    public async Task DispelMagicUnravelsStatusesTriggersAndFlowsInOneCast()
    {
        // The BUG_LOG [16] ward had "no in-fiction way to cancel it"; dispelMagic is that way.
        var session = Session(AcceptedSpell(
            "You pull a loose thread and the weave answers.",
            new SpellEffect("dispelMagic", new Dictionary<string, object?>
            {
                ["target"] = "player",
                ["scope"] = "all",
                ["radius"] = 1,
            })));
        var player = session.Engine.State.ControlledEntity;
        var position = player.Get<PositionComponent>().Position;
        Applied(session.Engine.ApplyConsequence(WorldConsequence.ApplyStatus(
            "test_setup", player.Id.Value, "burning", duration: 5, emitMessage: false)));
        Applied(session.Engine.ApplyConsequence(WorldConsequence.CreateTrigger(
            "test_setup",
            "waiting ward",
            "ward",
            delay: 1,
            interval: 1,
            uses: 99,
            duration: null,
            effectType: "addStatus",
            effectFields: new Dictionary<string, object?> { ["status"] = "webbed_thorns", ["duration"] = 3 },
            description: "a ward that keeps firing",
            anchorEntityId: player.Id.Value,
            radius: 2,
            targetFilter: "enemies")));
        Applied(session.Engine.ApplyConsequence(WorldConsequence.CreateFlow(
            "test_setup", position.X, position.Y, radius: 0, dx: 1, dy: 0, duration: 9)));
        Assert.NotEmpty(session.Engine.State.Triggers.Records);
        Assert.NotEmpty(session.Engine.State.TileFlows);

        var result = await session.ExecuteAsync(new CastCommand("unravel every enchantment on me"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.Empty(session.Engine.State.Triggers.Records);
        Assert.Empty(session.Engine.State.TileFlows);
        Assert.DoesNotContain(
            player.Get<StatusContainerComponent>().Statuses,
            status => status.Id.Equals("burning", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DispelMagicWithNothingActiveRejectsHonestly()
    {
        var session = Session(AcceptedSpell(
            "You pull at the weave and find nothing loose.",
            new SpellEffect("dispelMagic", new Dictionary<string, object?>
            {
                ["target"] = "player",
            })));
        var turnBefore = session.Engine.State.Turn;

        var result = await session.ExecuteAsync(new CastCommand("unravel every enchantment on me"));

        Assert.False(result.Success);
        Assert.True(result.ConsumedTurn);
        Assert.Equal(turnBefore + 1, session.Engine.State.Turn);
        Assert.Contains(result.Messages, message => message.Contains("no active magic"));
    }

    [Fact]
    public async Task WildMagicCanResolveBorrowedTideInsteadOfOnlyHidingItsStatus()
    {
        var session = Session(new SpellResolution(
            Accepted: true,
            Severity: "major",
            OutcomeText: "The borrowed water remembers its own shore and leaves your blood.",
            Effects: new[]
            {
                new SpellEffect("resolveCurse", new Dictionary<string, object?>
                {
                    ["target"] = "player",
                    ["profileId"] = "curse_tide_debt_body",
                }),
            },
            Costs: new[]
            {
                new SpellCost("mana", new Dictionary<string, object?> { ["amount"] = 4 }),
            },
            RejectedReason: null));
        var player = session.Engine.State.ControlledEntity;
        SpellCostApplier.Apply(session.Engine, new[]
        {
            new SpellCost("curse", new Dictionary<string, object?>
            {
                ["profileId"] = "curse_tide_debt_body",
            }),
        });
        Assert.Contains(player.Get<StatusContainerComponent>().Statuses, status => status.Id == "borrowed_tide");
        var curse = Assert.Single(session.Engine.State.PromiseLedger.Promises, promise =>
            promise.CostProfileId == "curse_tide_debt_body"
            && promise.Status != "cleared");

        var result = await session.ExecuteAsync(new CastCommand("cure Borrowed Tide by returning its water to the river"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.Equal("cleared", session.Engine.State.PromiseLedger.Promises.Single(promise => promise.Id == curse.Id).Status);
        Assert.DoesNotContain(player.Get<StatusContainerComponent>().Statuses, status => status.Id == "borrowed_tide");
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "resolveCurse"
            && Equals(delta.Details["profileId"], "curse_tide_debt_body"));

        var laterTurns = session.Engine.AdvanceTurn().Concat(session.Engine.AdvanceTurn()).ToArray();
        Assert.DoesNotContain(laterTurns, delta =>
            delta.Operation is "borrowedTideWet" or "borrowedTideDry");
    }

    [Fact]
    public async Task RevealTruthReportsATargetsWantFromTheEngineLedger()
    {
        var session = Session(AcceptedSpell(
            "The question folds itself into the world and comes back written.",
            new SpellEffect("revealTruth", new Dictionary<string, object?>
            {
                ["aspect"] = "wants",
                ["target"] = "prisoner_1",
            })));

        var result = await session.ExecuteAsync(new CastCommand("divine what the prisoner wants most"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.Contains(result.Messages, message =>
            message.Contains("Lio of Hollowmere wants this:")
            && message.Contains("Escape the containment yard"));
    }

    [Fact]
    public async Task RevealTruthNamesDirectionAndDistanceOfANamedBeing()
    {
        var session = Session(AcceptedSpell(
            "A direction becomes briefly undeniable.",
            new SpellEffect("revealTruth", new Dictionary<string, object?>
            {
                ["aspect"] = "whereabouts",
                ["subject"] = "ward-captain",
            })));

        var result = await session.ExecuteAsync(new CastCommand("show me where the captain stands"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.Contains(result.Messages, message =>
            message.Contains("imperial ward-captain is") && message.Contains("tiles"));
    }

    [Fact]
    public async Task RevealTruthNatureReportsAllegianceHostilityAndMarksTheTargetRevealed()
    {
        var session = Session(AcceptedSpell(
            "The soldier's nature is read out like a ledger line.",
            new SpellEffect("revealTruth", new Dictionary<string, object?>
            {
                ["aspect"] = "nature",
                ["target"] = "soldier_1",
            })));

        var result = await session.ExecuteAsync(new CastCommand("read the true nature of the nearest soldier"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.Contains(result.Messages, message =>
            message.Contains("sworn to empire") && message.Contains("hostile to you"));
        var soldier = session.Engine.EntityById("soldier_1")!;
        Assert.Contains(
            soldier.Get<StatusContainerComponent>().Statuses,
            status => status.Id.Equals("revealed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WildMagicCanUnlockAVisibleDoorAcrossTheRoom()
    {
        // The dialogue/engine lanes stay adjacency-bound (range 2), but a cast may work a lock
        // it can see: "the lock forgets its shape" from across the yard is signature wild magic.
        var session = Session(AcceptedSpell(
            "The lock exhales and forgets what it was for.",
            new SpellEffect("consequence", new Dictionary<string, object?>
            {
                ["consequenceType"] = "open_or_unlock",
                ["target"] = "cell_door_1",
                ["unlock"] = true,
                ["open"] = true,
            })));
        var door = session.Engine.EntityById("cell_door_1")!;
        Assert.False(door.Get<DoorComponent>().IsOpen);

        var result = await session.ExecuteAsync(new CastCommand("make the cell door's lock forget its shape"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        var doorState = door.Get<DoorComponent>();
        Assert.True(doorState.IsOpen);
        Assert.Null(doorState.KeyId);
    }

    private static GameSession Session(SpellResolution resolution) =>
        GameSession.CreateImperialEncounter(new WildMagicController(new FixtureSpellProvider(resolution)));

    private static void Applied(WorldConsequenceApplyResult result) =>
        Assert.True(result.Applied, result.Error);

    private static SpellResolution AcceptedSpell(string outcome, params SpellEffect[] effects) =>
        new(
            Accepted: true,
            Severity: "minor",
            OutcomeText: outcome,
            Effects: effects,
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);

    private sealed class FixtureSpellProvider : ISpellProvider
    {
        private readonly SpellResolution _resolution;

        public FixtureSpellProvider(SpellResolution resolution) => _resolution = resolution;

        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(
            SpellRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new SpellProviderResult(
                Name,
                "",
                _resolution,
                TechnicalFailure: false,
                Error: null));
    }
}
