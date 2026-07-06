using System;
using System.Linq;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Parser-repair fixtures mined from WildMagic's <c>resolution_parsing.py</c> (Phase 1 of the
/// WildMagic import). Each fixture is one malformation class local models are known to emit; the
/// test locks the syntactic/schema-level shape the Sorcerer normalizer must produce. Repairs stay
/// syntactic — target strings are left for engine binding (ReferenceBinder), and whether a
/// normalized effect is actually castable is decided later by validation, not here.
/// </summary>
public sealed class SpellResolutionParserRepairTests
{
    private static SpellResolution Parse(string raw) =>
        SpellResolutionJson.Parse(raw, OperationRegistry.CreateDefault());

    private static SpellEffect Single(SpellResolution resolution)
    {
        Assert.Single(resolution.Effects);
        return resolution.Effects[0];
    }

    private static object? Field(SpellEffect effect, string key) =>
        effect.Fields.TryGetValue(key, out var value) ? value : null;

    private static object? Field(SpellCost cost, string key) =>
        cost.Fields.TryGetValue(key, out var value) ? value : null;

    // --- Classes Sorcerer already normalizes (characterization locks) ---

    [Fact]
    public void SnakeCaseEffectTypeKeyIsCanonicalized()
    {
        var resolution = Parse(
            """{"accepted":true,"effects":[{"effect_type":"damage","target":"nearest_enemy","amount":5}]}""");

        Assert.True(resolution.Accepted);
        Assert.Equal("damage", Single(resolution).Type);
    }

    [Fact]
    public void ElementNameAsEffectTypeBecomesDamage()
    {
        var effect = Single(Parse(
            """{"effects":[{"type":"fire","target":"nearest_enemy","amount":5}]}"""));

        Assert.Equal("damage", effect.Type);
        Assert.Equal("fire", Convert.ToString(Field(effect, "damageType")));
    }

    [Fact]
    public void NestedDetailsAreMergedIntoEffect()
    {
        var effect = Single(Parse(
            """{"effects":[{"type":"damage","details":{"amount":7,"target":"nearest_enemy"}}]}"""));

        Assert.Equal("damage", effect.Type);
        Assert.Equal(7, Convert.ToInt32(Field(effect, "amount")));
    }

    [Fact]
    public void CommonWrapperKeyIsUnwrapped()
    {
        var resolution = Parse(
            """{"resolution":{"accepted":true,"effects":[{"type":"heal","target":"player","amount":4}]}}""");

        Assert.True(resolution.Accepted);
        Assert.Equal("heal", Single(resolution).Type);
    }

    [Fact]
    public void ProseAroundJsonObjectIsStripped()
    {
        var resolution = Parse(
            """Here is the resolution: {"accepted":true,"effects":[{"type":"heal","target":"player","amount":3}]} Hope that helps!""");

        Assert.True(resolution.Accepted);
        Assert.Equal("heal", Single(resolution).Type);
    }

    [Fact]
    public void JunkOutcomeTextIsDropped()
    {
        var resolution = Parse(
            """{"accepted":true,"outcome_text":"success","effects":[{"type":"heal","target":"player","amount":3}]}""");

        Assert.Equal(string.Empty, resolution.OutcomeText);
    }

    [Fact]
    public void CostStringElementIsParsed()
    {
        var resolution = Parse(
            """{"accepted":true,"effects":[{"type":"heal","target":"player","amount":3}],"costs":["mana 5"]}""");

        var cost = Assert.Single(resolution.Costs);
        Assert.Equal("mana", cost.Type);
        Assert.Equal(5, Convert.ToInt32(Field(cost, "amount")));
    }

    [Fact]
    public void EffectShapedCostIsRescuedIntoEffects()
    {
        var resolution = Parse(
            """{"accepted":true,"effects":[],"costs":[{"type":"addWeakness","target":"nearest_enemy","damageType":"fire"}]}""");

        Assert.Equal("addWeakness", Single(resolution).Type);
        Assert.Empty(resolution.Costs);
    }

    [Fact]
    public void StatusNameCamelKeyMapsToStatusField()
    {
        var effect = Single(Parse(
            """{"effects":[{"type":"addStatus","statusName":"poisoned","target":"nearest_enemy"}]}"""));

        Assert.Equal("addStatus", effect.Type);
        Assert.Equal("poisoned", Convert.ToString(Field(effect, "status")));
    }

    [Fact]
    public void NullEffectsWithRejectionStayEmpty()
    {
        var resolution = Parse(
            """{"accepted":false,"effects":null,"rejected_reason":"The wild magic refuses."}""");

        Assert.False(resolution.Accepted);
        Assert.Empty(resolution.Effects);
        Assert.Equal("The wild magic refuses.", resolution.RejectedReason);
    }

    // --- Classes suspected to be gaps (assert the desired repaired shape) ---

    [Fact]
    public void BareEffectObjectIsWrappedInEnvelope()
    {
        var resolution = Parse(
            """{"type":"damage","target":"nearest_enemy","amount":5}""");

        Assert.True(resolution.Accepted);
        Assert.Equal("damage", Single(resolution).Type);
    }

    [Fact]
    public void SingularEffectObjectBecomesEffectsList()
    {
        var resolution = Parse(
            """{"accepted":true,"effect":{"type":"heal","target":"player","amount":5}}""");

        Assert.True(resolution.Accepted);
        Assert.Equal("heal", Single(resolution).Type);
    }

    [Fact]
    public void SingularEffectArrayBecomesEffectsList()
    {
        var resolution = Parse(
            """{"accepted":true,"effect":[{"type":"damage","target":"nearest_enemy","amount":5}]}""");

        Assert.Equal("damage", Single(resolution).Type);
    }

    [Fact]
    public void OutcomeContainerExposesEffects()
    {
        var resolution = Parse(
            """{"outcome":{"effects":[{"type":"damage","target":"nearest_enemy","amount":6}]}}""");

        Assert.Equal("damage", Single(resolution).Type);
    }

    [Fact]
    public void StatusWordAsEffectTypeBecomesAddStatus()
    {
        var effect = Single(Parse(
            """{"effects":[{"type":"burning","target":"nearest_enemy"}]}"""));

        Assert.Equal("addStatus", effect.Type);
        Assert.Equal("burning", Convert.ToString(Field(effect, "status")));
    }

    [Fact]
    public void CostDictionaryIsCoercedIntoCostList()
    {
        var resolution = Parse(
            """{"accepted":true,"effects":[{"type":"damage","target":"nearest_enemy","amount":5}],"costs":{"mana":5}}""");

        var cost = Assert.Single(resolution.Costs);
        Assert.Equal("mana", cost.Type);
        Assert.Equal(5, Convert.ToInt32(Field(cost, "amount")));
    }
}
