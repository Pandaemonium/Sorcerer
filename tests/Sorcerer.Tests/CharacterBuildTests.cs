using Sorcerer.Core;
using Sorcerer.Core.Characters;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Scenarios;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Character creation rules: builds are additive on the origin baseline, capped by pool and
/// per-stat ceiling, and Sanitize coerces any input into a legal build without throwing.
/// The build flows into the world through CreationRules.EffectiveOrigin at scenario setup.
/// </summary>
public sealed class CharacterBuildTests
{
    private static readonly OriginCatalog Catalog = OriginCatalog.LoadDefault();
    private static readonly CharterSpellbook Spellbook = CharterSpellbook.Default;

    [Fact]
    public void FromOriginIsZeroSpendAndLegal()
    {
        foreach (var origin in Catalog.Origins)
        {
            var build = CreationRules.FromOrigin(origin);
            Assert.Equal(0, CreationRules.PointsSpent(origin, build));
            Assert.True(CreationRules.IsLegal(origin, build));
        }
    }

    [Fact]
    public void SanitizeClampsOverspendAndCap()
    {
        var origin = Catalog.Default;
        var wild = new CharacterBuild(origin.Id, Vigor: 99, Attunement: 99, Composure: 99);

        var sane = CreationRules.Sanitize(wild, Catalog, Spellbook);

        Assert.True(CreationRules.IsLegal(origin, sane));
        Assert.Equal(CreationRules.PointPool, CreationRules.PointsSpent(origin, sane));
        Assert.True(sane.Vigor <= CreationRules.StatCap);
        Assert.True(sane.Attunement <= CreationRules.StatCap);
        Assert.True(sane.Composure <= CreationRules.StatCap);
    }

    [Fact]
    public void SanitizeRaisesStatsBelowBaseline()
    {
        var origin = Catalog.Default;
        var under = new CharacterBuild(origin.Id, Vigor: 0, Attunement: 0, Composure: 0);

        var sane = CreationRules.Sanitize(under, Catalog, Spellbook);

        Assert.Equal(origin.BodyVigor, sane.Vigor);
        Assert.Equal(origin.SoulAttunement, sane.Attunement);
        Assert.Equal(origin.SoulComposure, sane.Composure);
    }

    [Fact]
    public void SanitizeToleratesUnknownOriginAndSpellAndBlankText()
    {
        var junk = new CharacterBuild(
            "no_such_origin", 4, 4, 3,
            Name: "   ",
            Appearance: "",
            BonusCharterSpellId: "no_such_spell");

        var sane = CreationRules.Sanitize(junk, Catalog, Spellbook);

        Assert.Equal(Catalog.Default.Id, sane.OriginId);
        Assert.Null(sane.Name);
        Assert.Null(sane.Appearance);
        Assert.Null(sane.BonusCharterSpellId);
    }

    [Fact]
    public void EffectiveOriginKeepsOriginTextWhenBuildTextIsBlank()
    {
        var origin = Catalog.Default;
        var build = CreationRules.FromOrigin(origin) with { Name = " ", Backstory = null };

        var effective = CreationRules.EffectiveOrigin(origin, build);

        Assert.Equal(origin.PublicName, effective.PublicName);
        Assert.Equal(origin.Backstory, effective.Backstory);
    }

    [Fact]
    public void EffectiveOriginOverridesTextAndUnionsCharterSpellsWithoutDuplicates()
    {
        var origin = Catalog.Resolve("deserter_charter_mage");
        Assert.NotNull(origin.StartingCharterSpells);
        var alreadySeeded = origin.StartingCharterSpells![0];
        var build = CreationRules.FromOrigin(origin) with
        {
            Name = "Vess",
            BonusCharterSpellId = alreadySeeded,
        };

        var effective = CreationRules.EffectiveOrigin(origin, build);

        Assert.Equal("Vess", effective.PublicName);
        Assert.Equal(origin.StartingCharterSpells!.Count, effective.StartingCharterSpells!.Count);

        var withNew = CreationRules.EffectiveOrigin(
            origin,
            build with { BonusCharterSpellId = "census_glass_1" });
        Assert.Contains("census_glass_1", withNew.StartingCharterSpells!);
        Assert.Equal(origin.StartingCharterSpells!.Count + 1, withNew.StartingCharterSpells!.Count);
    }

    [Fact]
    public void RandomBuildIsLegalAcrossManySeeds()
    {
        for (var seed = 0; seed < 500; seed++)
        {
            var build = CreationRules.RandomBuild(Catalog, new Random(seed));
            var origin = Catalog.Resolve(build.OriginId);
            Assert.True(CreationRules.IsLegal(origin, build), $"seed {seed} produced an illegal build");
        }
    }

    [Fact]
    public void ImperialEncounterAppliesBuildToPlayerSoulProfileAndActor()
    {
        var origin = Catalog.Resolve("merfolk_exile");
        var build = CreationRules.Sanitize(
            CreationRules.FromOrigin(origin) with
            {
                Vigor = origin.BodyVigor + 1,
                Attunement = origin.SoulAttunement + 2,
                Name = "Brine-Called Ess",
                Appearance = "gill-scarred and dripping starlight",
                MagicalSignature = "salt crystallizing midair",
                BonusCharterSpellId = "mending_writ_1",
                PortraitPath = @"C:\portraits\ess.png",
            },
            Catalog,
            Spellbook);

        var state = TestScenarios.ImperialEncounter(build: build);
        var player = state.ControlledEntity;

        var actor = player.Get<ActorComponent>();
        Assert.Equal(CharacterMath.MaxHitPointsFromVigor(build.Vigor), actor.MaxHitPoints);
        Assert.Equal(CharacterMath.MaxManaFromAttunement(build.Attunement), actor.MaxMana);
        Assert.Equal(build.Vigor, player.Get<BodyStatsComponent>().Vigor);

        Assert.True(state.Souls.TryGet("player_soul", out var soul));
        Assert.Equal(build.Attunement, soul.Stats.Attunement);
        Assert.Equal(origin.Id, soul.OriginId);
        Assert.True(state.Souls.KnowsCharterSpell("player_soul", "mending_writ_1"));

        var profile = player.Get<ProfileComponent>();
        Assert.Equal("Brine-Called Ess", profile.PublicName);
        Assert.Equal("gill-scarred and dripping starlight", profile.Appearance);
        Assert.Equal("salt crystallizing midair", profile.MagicalSignature);
        Assert.Equal(origin.Backstory, profile.Backstory);
        Assert.Equal(@"C:\portraits\ess.png", profile.PortraitPath);
    }

    [Fact]
    public void ImperialEncounterWithoutBuildMatchesOriginDefaults()
    {
        var state = TestScenarios.ImperialEncounter();
        var profile = state.ControlledEntity.Get<ProfileComponent>();

        Assert.Equal(Catalog.Default.PublicName, profile.PublicName);
        Assert.Equal("", profile.PortraitPath);
    }

    [Fact]
    public void PortraitPathSurvivesSaveLoad()
    {
        var build = CreationRules.FromOrigin(Catalog.Default) with
        {
            PortraitPath = @"C:\portraits\keeper.png",
        };
        var state = TestScenarios.ImperialEncounter(build: build);

        var saved = GameSaveService.Serialize(
            state,
            savedAt: new DateTimeOffset(2026, 7, 11, 0, 0, 0, TimeSpan.Zero));
        var loaded = GameSaveService.Deserialize(saved);

        Assert.Equal(
            @"C:\portraits\keeper.png",
            loaded.State.ControlledEntity.Get<ProfileComponent>().PortraitPath);
    }

    [Fact]
    public void ProfilePayloadWithoutPortraitPathStillLoads()
    {
        var save = new ComponentSave("profile", new Dictionary<string, object?>
        {
            ["publicName"] = "the sorcerer",
            ["appearance"] = "a fugitive",
            ["origin"] = "fugitive_wild_sorcerer",
            ["magicalSignature"] = "color",
            ["backstory"] = "story",
        });

        var component = Assert.IsType<ProfileComponent>(save.ToComponent());
        Assert.Equal("", component.PortraitPath);
    }
}
