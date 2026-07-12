using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Magic;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Charter magic (docs/CHARTER_MAGIC.md) and spell echoes (docs/SPELL_ECHOES.md): the two
/// instant, zero-model-call casting lanes. Both must resolve deterministically through the
/// shared apply pipeline with the provider stage removed, so most tests here use a provider
/// that throws if it is ever consulted.
/// </summary>
public class CharterAndEchoTests
{
    private const string DeserterOrigin = "deserter_charter_mage";

    [Fact]
    public void EveryCharterSpellBundleValidatesAgainstTheOperationRegistry()
    {
        var registry = OperationRegistry.CreateDefault();
        var spells = CharterSpellbook.Default.Spells;

        Assert.True(spells.Count >= 6, $"Expected a first roster of 6-10 charter spells, found {spells.Count}.");
        Assert.All(spells, spell =>
        {
            Assert.False(string.IsNullOrWhiteSpace(spell.Line));
            Assert.NotEmpty(spell.EffectTypes);
            Assert.All(spell.EffectTypes, type =>
                Assert.True(registry.Supports(type), $"{spell.Id} uses unsupported effect type '{type}'."));
            Assert.NotEmpty(spell.Cost);
        });
    }

    [Fact]
    public async Task CharterCastIsInstantFixedCostAndNeverTouchesTheProvider()
    {
        var session = ThrowingProviderSession(DeserterOrigin);
        var player = session.Engine.State.ControlledEntity;
        var manaBefore = player.Get<ActorComponent>().Mana;
        var soldier = session.Engine.EntityById("soldier_1")!;
        var soldierHpBefore = soldier.Get<ActorComponent>().HitPoints;
        var turnBefore = session.Engine.State.Turn;

        var result = await session.ExecuteAsync(TextCommandParser.Parse("charter bolt_directive_1"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.True(result.ConsumedTurn);
        Assert.Equal(turnBefore + 1, session.Engine.State.Turn);
        Assert.Equal("charter", result.Magic?.Provider);
        Assert.Equal(manaBefore - 2, player.Get<ActorComponent>().Mana);
        Assert.True(soldier.Get<ActorComponent>().HitPoints < soldierHpBefore, "The bolt should have landed.");
        Assert.Contains(result.Messages, message => message.Contains("thin white bolt"));
        Assert.Contains(result.Messages, message => message.Contains("Cost: 2 mana"));
    }

    [Fact]
    public async Task CastTextExactlyMatchingAKnownCharterFormRoutesThroughTheCharterLane()
    {
        // GUI parity: the spell-entry box submits CastCommand text; a known form's exact name
        // must reach the instant charter lane without a new verb or a provider call.
        var session = ThrowingProviderSession(DeserterOrigin);

        var result = await session.ExecuteAsync(new CastCommand("Bolt Directive I"));

        Assert.True(result.Success, string.Join(" / ", result.Messages));
        Assert.Equal("charter", result.Magic?.Provider);
    }

    [Fact]
    public async Task UnlearnedCharterFormIsRefusedWithoutConsumingATurn()
    {
        var session = ThrowingProviderSession(originId: null); // fugitive: knows no forms
        var turnBefore = session.Engine.State.Turn;

        var result = await session.ExecuteAsync(TextCommandParser.Parse("charter bolt_directive_1"));

        Assert.False(result.Success);
        Assert.False(result.ConsumedTurn);
        Assert.Equal(turnBefore, session.Engine.State.Turn);
        Assert.Contains(result.Messages, message => message.Contains("not learned"));
    }

    [Fact]
    public async Task ReadingTaggedParaphernaliaTeachesItsCharterForm()
    {
        // The opening containment notice carries "teaches_charter:binding_writ_1"
        // (docs/CHARTER_MAGIC.md - acquisition through ordinary verbs).
        var session = ThrowingProviderSession(originId: null);
        session.Engine.State.ControlledEntity.Set(new PositionComponent(new GridPoint(5, 6)));

        var read = await session.ExecuteAsync(new ReadCommand("notice"));

        Assert.True(read.Success, string.Join(" / ", read.Messages));
        Assert.Contains(read.Messages, message => message.Contains("Binding Writ I"));
        Assert.True(session.Engine.State.Souls.KnowsCharterSpell("player_soul", "binding_writ_1"));

        var cast = await session.ExecuteAsync(TextCommandParser.Parse("charter binding_writ_1"));
        Assert.True(cast.Success, string.Join(" / ", cast.Messages));
    }

    [Fact]
    public async Task CharterRepertoireSurvivesSaveAndLoad()
    {
        var session = ThrowingProviderSession(DeserterOrigin);
        var path = Path.Combine(Path.GetTempPath(), $"sorcerer_charter_{Guid.NewGuid():N}.json");
        try
        {
            var saved = await session.ExecuteAsync(new SaveCommand(path));
            Assert.True(saved.Success, string.Join(" / ", saved.Messages));
            var loaded = await session.ExecuteAsync(new LoadCommand(path));
            Assert.True(loaded.Success, string.Join(" / ", loaded.Messages));

            Assert.True(session.Engine.State.Souls.KnowsCharterSpell("player_soul", "bolt_directive_1"));
            var cast = await session.ExecuteAsync(TextCommandParser.Parse("charter bolt_directive_1"));
            Assert.True(cast.Success, string.Join(" / ", cast.Messages));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CharterCastWitnessesQuietlyWhileWildCastReadsAsUncanny()
    {
        // The witness split (docs/CHARTER_MAGIC.md): a witnessed wild cast writes uncanny
        // legend and raises heat; a witnessed charter cast reads as plausibly licensed work.
        var wildSession = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureProvider(
            AcceptedSpell("Blue fire snaps out.", new SpellEffect("damage", new Dictionary<string, object?>
            {
                ["target"] = "soldier_1",
                ["amount"] = 2,
                ["damageType"] = "fire",
            })))));
        var wild = await wildSession.ExecuteAsync(new CastCommand("strike the soldier with blue fire"));
        Assert.True(wild.Success, string.Join(" / ", wild.Messages));
        Assert.Contains(wildSession.Engine.State.Legend.Snapshot(), tag =>
            tag.ActorSoulId == "player_soul" && tag.Tag == "uncanny");

        var charterSession = ThrowingProviderSession(DeserterOrigin);
        var charter = await charterSession.ExecuteAsync(TextCommandParser.Parse("charter bolt_directive_1"));
        Assert.True(charter.Success, string.Join(" / ", charter.Messages));
        Assert.DoesNotContain(charterSession.Engine.State.Legend.Snapshot(), tag =>
            tag.ActorSoulId == "player_soul" && tag.Tag == "uncanny");
        Assert.Contains(charter.Messages, message => message.Contains("licensed charter work"));
    }

    [Fact]
    public async Task EchoCommandsRequireTheExperimentFlag()
    {
        var session = ThrowingProviderSession(originId: null);

        var list = await session.ExecuteAsync(TextCommandParser.Parse("echoes"));
        var cast = await session.ExecuteAsync(TextCommandParser.Parse("echo 1"));

        Assert.False(list.Success);
        Assert.False(cast.Success);
        Assert.False(cast.ConsumedTurn);
        Assert.Contains(list.Messages, message => message.Contains("disabled"));
    }

    [Fact]
    public async Task AcceptedWildCastIsRecordedAndRecastableAsAnInstantEcho()
    {
        var session = EchoSession(out var soldierDamage);
        var soldier = session.Engine.EntityById("soldier_1")!;
        var hpStart = soldier.Get<ActorComponent>().HitPoints;

        var wild = await session.ExecuteAsync(new CastCommand("strike the soldier with blue fire"));
        Assert.True(wild.Success, string.Join(" / ", wild.Messages));
        Assert.Equal(hpStart - soldierDamage, soldier.Get<ActorComponent>().HitPoints);

        var grimoire = await session.ExecuteAsync(TextCommandParser.Parse("echoes"));
        Assert.True(grimoire.Success);
        Assert.Contains(grimoire.Messages, message => message.Contains("Grimoire (1"));

        var echo = await session.ExecuteAsync(TextCommandParser.Parse("echo 1"));
        Assert.True(echo.Success, string.Join(" / ", echo.Messages));
        Assert.Equal("echo", echo.Magic?.Provider);
        Assert.Equal(hpStart - (2 * soldierDamage), soldier.Get<ActorComponent>().HitPoints);
        Assert.Equal(1, session.Engine.State.Echoes.Records.Single().TimesCast);
    }

    [Fact]
    public async Task EchoRepetitionClimbsTheManaCostLadder()
    {
        var session = EchoSession(out _);
        await session.ExecuteAsync(new CastCommand("strike the soldier with blue fire"));
        var player = session.Engine.State.ControlledEntity;

        var manaBeforeFirst = player.Get<ActorComponent>().Mana;
        var first = await session.ExecuteAsync(TextCommandParser.Parse("echo 1"));
        Assert.True(first.Success, string.Join(" / ", first.Messages));
        Assert.Equal(manaBeforeFirst, player.Get<ActorComponent>().Mana); // recorded cast was free

        var manaBeforeSecond = player.Get<ActorComponent>().Mana;
        var second = await session.ExecuteAsync(TextCommandParser.Parse("echo 1"));
        Assert.True(second.Success, string.Join(" / ", second.Messages));
        // Repetition fatigue: the second repeat carries a +1 mana surcharge.
        Assert.Equal(manaBeforeSecond - 1, player.Get<ActorComponent>().Mana);
        Assert.Contains(second.Messages, message => message.Contains("Cost: 1 mana"));
    }

    [Fact]
    public async Task EchoGrimoireSurvivesSaveAndLoad()
    {
        var session = EchoSession(out _);
        await session.ExecuteAsync(new CastCommand("strike the soldier with blue fire"));
        var path = Path.Combine(Path.GetTempPath(), $"sorcerer_echo_{Guid.NewGuid():N}.json");
        try
        {
            Assert.True((await session.ExecuteAsync(new SaveCommand(path))).Success);
            Assert.True((await session.ExecuteAsync(new LoadCommand(path))).Success);

            var record = Assert.Single(session.Engine.State.Echoes.Records);
            Assert.Equal("strike the soldier with blue fire", record.SpellText);
            var echo = await session.ExecuteAsync(TextCommandParser.Parse("echo 1"));
            Assert.True(echo.Success, string.Join(" / ", echo.Messages));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BeginCastWithKnownCharterNameResolvesInstantlyWithoutPendingCast()
    {
        // GUI parity: the spell box submits begin_cast/await_cast, so the instant charter lane
        // must intercept there too, or a known form typed into the box gambles as wild magic.
        var session = ThrowingProviderSession(DeserterOrigin);
        var player = session.Engine.State.ControlledEntity;
        var manaBefore = player.Get<ActorComponent>().Mana;

        var begin = await session.ExecuteAsync(new BeginCastCommand("Bolt Directive I"));

        Assert.True(begin.Success, string.Join(" / ", begin.Messages));
        Assert.Equal("cast", begin.Action);
        Assert.Equal("charter", begin.Magic?.Provider);
        Assert.True(begin.ConsumedTurn);
        Assert.Equal(manaBefore - 2, player.Get<ActorComponent>().Mana);
        Assert.Null(session.Observation().PendingCast);

        var stale = await session.ExecuteAsync(new AwaitCastCommand());
        Assert.False(stale.Success);
        Assert.Contains(stale.Messages, message => message.Contains("No spell is waiting"));
    }

    [Fact]
    public async Task BeginCastWithWildTextStillCreatesAPendingCast()
    {
        var session = EchoSession(out _);

        var begin = await session.ExecuteAsync(new BeginCastCommand("strike the soldier with blue fire"));
        Assert.True(begin.Success, string.Join(" / ", begin.Messages));
        Assert.Equal("begin_cast", begin.Action);
        Assert.NotNull(session.Observation().PendingCast);

        var applied = await session.ExecuteAsync(new AwaitCastCommand());
        Assert.True(applied.Success, string.Join(" / ", applied.Messages));
        Assert.Null(session.Observation().PendingCast);
    }

    [Fact]
    public async Task BeginCastCharterDetourIsBlockedWhileAWildCastIsPending()
    {
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureProvider(
            AcceptedSpell("Blue fire snaps out."))), DeserterOrigin);
        var player = session.Engine.State.ControlledEntity;

        Assert.True((await session.ExecuteAsync(new BeginCastCommand("strike with blue fire"))).Success);
        var manaBefore = player.Get<ActorComponent>().Mana;

        var blocked = await session.ExecuteAsync(new BeginCastCommand("Bolt Directive I"));

        Assert.False(blocked.Success);
        Assert.Contains(blocked.Messages, message => message.Contains("A spell is waiting to resolve"));
        Assert.Equal(manaBefore, player.Get<ActorComponent>().Mana);
    }

    [Fact]
    public void ViewRepertoireListsKnownCharterForms()
    {
        var deserter = ThrowingProviderSession(DeserterOrigin).View().Repertoire;
        Assert.NotNull(deserter);
        Assert.Contains(deserter!.CharterSpells, spell => spell.Id == "bolt_directive_1");
        Assert.All(deserter.CharterSpells, spell =>
        {
            Assert.False(string.IsNullOrWhiteSpace(spell.Name));
            Assert.False(string.IsNullOrWhiteSpace(spell.CostText));
            Assert.False(string.IsNullOrWhiteSpace(spell.Targeting));
        });

        var fugitive = ThrowingProviderSession(originId: null).View().Repertoire;
        Assert.NotNull(fugitive);
        Assert.Empty(fugitive!.CharterSpells);
        Assert.False(fugitive.EchoesEnabled);
        Assert.Empty(fugitive.Echoes);
    }

    [Fact]
    public async Task ViewRepertoireListsEchoesWithFatigue()
    {
        var session = EchoSession(out _);
        await session.ExecuteAsync(new CastCommand("strike the soldier with blue fire"));
        await session.ExecuteAsync(TextCommandParser.Parse("echo 1"));

        var repertoire = session.View().Repertoire;

        Assert.NotNull(repertoire);
        Assert.True(repertoire!.EchoesEnabled);
        var echo = Assert.Single(repertoire.Echoes);
        Assert.Equal(1, echo.Index);
        Assert.Equal(1, echo.TimesCast);
        Assert.Equal(1, echo.NextCastFatigue);
    }

    [Fact]
    public async Task CharacterSheetListsCharterForms()
    {
        var deserter = await ThrowingProviderSession(DeserterOrigin)
            .ExecuteAsync(TextCommandParser.Parse("character"));
        Assert.Contains(deserter.Messages, message =>
            message.StartsWith("Charter forms:") && message.Contains("Bolt Directive"));

        var fugitive = await ThrowingProviderSession(originId: null)
            .ExecuteAsync(TextCommandParser.Parse("character"));
        Assert.Contains(fugitive.Messages, message => message.Contains("Charter forms: none learned"));
    }

    private static GameSession ThrowingProviderSession(string? originId) =>
        GameSession.CreateImperialEncounter(
            new WildMagicController(new ThrowIfConsultedProvider()),
            originId);

    /// <summary>A session with the echo flag on and a fixed 3-damage wild spell fixture.</summary>
    private static GameSession EchoSession(out int soldierDamage)
    {
        soldierDamage = 3;
        var session = GameSession.CreateImperialEncounter(new WildMagicController(new FixtureProvider(
            AcceptedSpell("Blue fire snaps out.", new SpellEffect("damage", new Dictionary<string, object?>
            {
                ["target"] = "soldier_1",
                ["amount"] = 3,
                ["damageType"] = "fire",
            })))));
        session.Engine.State.WorldFlags[GameSession.EchoesEnabledFlag] = true;
        return session;
    }

    private static SpellResolution AcceptedSpell(string outcome, params SpellEffect[] effects) =>
        new(
            Accepted: true,
            Severity: "minor",
            OutcomeText: outcome,
            Effects: effects,
            Costs: Array.Empty<SpellCost>(),
            RejectedReason: null);

    private sealed class FixtureProvider : ISpellProvider
    {
        private readonly SpellResolution _resolution;

        public FixtureProvider(SpellResolution resolution) => _resolution = resolution;

        public string Name => "fixture";

        public Task<SpellProviderResult> ResolveAsync(SpellRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new SpellProviderResult(Name, "", _resolution, TechnicalFailure: false, Error: null));
    }

    private sealed class ThrowIfConsultedProvider : ISpellProvider
    {
        public string Name => "throw-if-consulted";

        public Task<SpellProviderResult> ResolveAsync(SpellRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "The charter/echo lane must never consult the spell provider.");
    }
}
