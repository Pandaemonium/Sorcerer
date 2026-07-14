using System.Linq;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Replay;
using Xunit;

namespace Sorcerer.Tests;

public sealed class WinConditionTests
{
    [Fact]
    public async Task EmperorExistsAsNormalActorInReachableCapital()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var emperor = session.Engine.EntityById("emperor_odran");
        Assert.NotNull(emperor);
        Assert.Equal("vigovian_capital", session.View().World!.RegionId);
        Assert.Equal("Vigovian Capital", session.View().World!.RegionName);
        Assert.True(emperor!.TryGet<ActorComponent>(out var actor));
        Assert.True(actor.Alive);
        Assert.True(emperor.TryGet<TagsComponent>(out var tags));
        Assert.Contains("emperor", tags.Tags);
        Assert.True(emperor.TryGet<WantComponent>(out var want));
        Assert.Equal("want_emperor_odran_order", want.Id);
        Assert.Equal(5, want.Salience);
        Assert.Contains("containment", want.Tags);
        Assert.Contains("promise_source", want.Tags);
        Assert.Equal("running", session.Engine.State.RunStatus);
    }

    [Fact]
    public async Task RunArcMovementDerivesFromRegionAndDefenses()
    {
        static string Movement(GameSession session) => session.View().World!.RunArc!.Movement;

        // Escape: still in the containment yard.
        var escape = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        Assert.Equal("escape", Movement(escape));

        // Foothold: out of the yard, the empire's defenses still intact.
        var foothold = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        await foothold.ExecuteAsync(new TravelCommand(Direction.East));
        Assert.Equal("foothold", Movement(foothold));

        // War: out of the yard and imperial defenses have been spent.
        var war = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        await war.ExecuteAsync(new TravelCommand(Direction.East));
        foreach (var faction in war.Engine.State.Factions.FactionsByRole("empire_bloc"))
        {
            war.Engine.State.Factions.AdjustResource(faction.Id, "defenses", -99);
        }
        Assert.Equal("war", Movement(war));

        // Reach: at the marble heart of the empire.
        var reach = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()), seed: 7);
        await reach.ExecuteAsync(new TravelCommand(Direction.East));
        await reach.ExecuteAsync(new TravelCommand(Direction.East));
        await reach.ExecuteAsync(new TravelCommand(Direction.East));
        Assert.Equal("reach", Movement(reach));
    }

    [Fact]
    public async Task CapitalApproachViewDerivesFromDistrictGeographyNotAMeter()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));

        // Outside the capital there is no approach view at all.
        Assert.Null(session.View().World!.CapitalApproach);

        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var approach = session.View().World!.CapitalApproach;
        Assert.NotNull(approach);
        Assert.True(approach!.InCapital);
        Assert.True(approach.EmperorPresent);
        Assert.True(approach.EmperorAlive);

        // The thresholds are the capital's own districts, in Censor Gate -> Archive Quarter ->
        // Inner Court approach order -- not a plot-access number.
        var thresholdIds = approach.Thresholds.Select(threshold => threshold.DistrictId).ToArray();
        var knownThresholds = new[] { "censor_gate", "archive_quarter", "inner_court" };
        Assert.Equal(knownThresholds, thresholdIds.Where(id => knownThresholds.Contains(id)).ToArray());
        Assert.Contains(approach.Thresholds, threshold =>
            threshold.DistrictId == "censor_gate" && threshold.Tags.Contains("gate"));
        Assert.Contains("Vigovian Capital", approach.Summary);

        // The organic gate is legible: the imperial defense stands as guards around the throne.
        Assert.True(approach.ThroneGuards > 0);
        Assert.Contains("guard", approach.Summary, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CapitalGuardDensityScalesWithImperialDefensesOrganically()
    {
        // While the empire's defenses stand, guards materialize around the throne.
        var full = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        await TravelEastToCapital(full);
        var guardedCount = CountImperialGuards(full);
        Assert.True(guardedCount > 0, "Guards should stand around Odran while imperial defenses hold.");

        // Spend the empire's defenses (force, or allies waging war off-screen) and the throne stands
        // far less guarded -- reaching Odran is emergent, not a binary gate.
        var depleted = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        foreach (var faction in depleted.Engine.State.Factions.FactionsByRole("empire_bloc"))
        {
            depleted.Engine.State.Factions.AdjustResource(faction.Id, "defenses", -99);
        }

        await TravelEastToCapital(depleted);
        Assert.True(
            CountImperialGuards(depleted) < guardedCount,
            "Depleting imperial defenses should organically thin the guard around the throne.");
    }

    private static async Task TravelEastToCapital(GameSession session)
    {
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));
    }

    private static int CountImperialGuards(GameSession session) =>
        session.Engine.State.Entities.Values.Count(entity =>
            entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Contains("guard", System.StringComparer.OrdinalIgnoreCase));

    [Fact]
    public async Task KillingEmperorThroughOrdinaryDamageWinsRun()
    {
        var damageEmperor = """
            {
              "accepted": true,
              "severity": "major",
              "outcomeText": "The spell finds the man inside the office.",
              "effects": [
                {
                  "type": "damage",
                  "target": "emperor_odran",
                  "amount": 50,
                  "damageType": "wild"
                }
              ],
              "costs": []
            }
            """;
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { damageEmperor })));
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));
        await session.ExecuteAsync(new TravelCommand(Direction.East));

        var result = await session.ExecuteAsync(new CastCommand("strike the emperor with a mortal blue verdict"));

        Assert.True(result.Success);
        Assert.True(result.ShouldQuit);
        Assert.Equal("victory", session.Engine.State.RunStatus);
        Assert.Contains(result.Messages, message => message.Contains("Emperor Odran falls", StringComparison.OrdinalIgnoreCase));
        Assert.False(session.Engine.EntityById("emperor_odran")!.Get<ActorComponent>().Alive);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "runComplete"
            && delta.Target == "emperor_odran"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRunStatus)
            && Equals(delta.Details["status"], "victory"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "runCompleteMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["status"], "victory"));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "runCompleteSkipped");
        Assert.Equal("victory", session.Observation(debug: true).Debug!.RunStatus);
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "chronicle"
            && record.Text.Contains("Emperor Odran", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task KillingControlledBodyCompletesRunAsDefeat()
    {
        var damagePlayer = """
            {
              "accepted": true,
              "severity": "major",
              "outcomeText": "The room takes your borrowed breath back.",
              "effects": [
                {
                  "type": "damage",
                  "target": "player",
                  "amount": 50,
                  "damageType": "wild"
                }
              ],
              "costs": []
            }
            """;
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new ReplaySpellProvider(new[] { damagePlayer })));

        var result = await session.ExecuteAsync(new CastCommand("let the room swallow my current body whole"));

        Assert.True(result.Success);
        Assert.True(result.ShouldQuit);
        Assert.Equal("defeat", session.Engine.State.RunStatus);
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "runComplete"
            && delta.Target == "player"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.UpdateRunStatus)
            && Equals(delta.Details["status"], "defeat"));
        Assert.Contains(result.Deltas, delta =>
            delta.Operation == "runCompleteMessage"
            && Equals(delta.Details["consequenceType"], WorldConsequenceTypes.Message)
            && Equals(delta.Details["status"], "defeat"));
        Assert.DoesNotContain(result.Deltas, delta => delta.Operation == "runCompleteSkipped");
        Assert.Contains(result.Messages, message => message.Contains("Your body falls", StringComparison.OrdinalIgnoreCase));
        Assert.False(session.Engine.State.ControlledEntity.Get<ActorComponent>().Alive);
        Assert.Contains(session.Engine.State.Canon.Records, record =>
            record.Kind == "chronicle"
            && record.Tags.Contains("defeat"));
    }

    [Fact]
    public void RunChronicleFallbackTextIsGrammaticalWithNoLegendOrDeeds()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new MockSpellProvider()));
        session.Engine.State.RunStatus = "defeat";
        session.Engine.State.RunConclusion = "The sorcerer's current body is dead.";

        var chronicle = RunChronicle.Build(session.Engine.State);

        Assert.Contains("the ledgers remember few official marks", chronicle.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("remember left", chronicle.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunChronicleCanPersistAsInertMemorialInLaterWorld()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sorcerer_memorials_{Guid.NewGuid():N}.jsonl");
        try
        {
            var damageEmperor = """
                {
                  "accepted": true,
                  "severity": "major",
                  "outcomeText": "The spell finds the man inside the office.",
                  "effects": [
                    {
                      "type": "damage",
                      "target": "emperor_odran",
                      "amount": 50,
                      "damageType": "wild"
                    }
                  ],
                  "costs": []
                }
                """;
            var session = GameSession.CreateImperialEncounter(
                new WildMagicController(new ReplaySpellProvider(new[] { damageEmperor })));
            await session.ExecuteAsync(new TravelCommand(Direction.East));
            await session.ExecuteAsync(new TravelCommand(Direction.East));
            await session.ExecuteAsync(new TravelCommand(Direction.East));
            await session.ExecuteAsync(new CastCommand("strike the emperor with a mortal blue verdict"));

            CrossRunMemorialStore.AppendLatestChronicle(session.Engine.State, path);
            var memorials = CrossRunMemorialStore.Load(path);
            var nextRun = GameSession.CreateImperialEncounter(
                new WildMagicController(new MockSpellProvider()),
                seed: 31,
                memorials: memorials);

            var memorial = nextRun.Engine.EntityById("memorial_1");
            Assert.NotNull(memorial);
            Assert.True(memorial!.TryGet<TagsComponent>(out var tags));
            Assert.Contains("memorial", tags.Tags);
            Assert.False(memorial.Has<ActorComponent>());
            Assert.Contains("Emperor Odran", memorial.Get<DescriptionComponent>().Text);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
