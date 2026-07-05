using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Llm;
using Sorcerer.Magic;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Capabilities;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Phase 2 of the WildMagic import: deterministic capability routing is exercised as a table
/// (spell text -> expected card family) so it can be tuned and measured before a live router is
/// trusted, and the routing metrics that make prompt-trimming measurable are shown to reach the
/// magic audit record.
/// </summary>
public sealed class CapabilityRoutingTests
{
    [Theory]
    [InlineData("raise a wall of ice between us", "terrain_shape")]
    [InlineData("summon a friendly brass moth that bites enemies", "summoning")]
    [InlineData("charm the nearest soldier into helping me", "faction_charm")]
    [InlineData("conjure a shard of glass from nothing", "conjure_item")]
    [InlineData("in three turns a debt collector arrives because I stole tomorrow", "delayed_effects")]
    [InlineData("make the soldier dance helplessly", "behavior_control")]
    [InlineData("reveal the nearest creature by making its shadow glow", "divination")]
    public void DeterministicSelectionRoutesSpellTextToExpectedCardFamily(string spellText, string expectedCard)
    {
        var registry = CapabilityRegistry.CreateDefault();

        var selected = registry.Select(spellText).Select(card => card.Id).ToArray();

        Assert.Contains(expectedCard, selected);
    }

    [Fact]
    public void MundaneSpellWithNoTriggerSelectsNoCapabilityCards()
    {
        var registry = CapabilityRegistry.CreateDefault();

        Assert.Empty(registry.Select("heal my wounds with warm green light"));
    }

    [Fact]
    public async Task CastRecordsRoutingMetricsInAudit()
    {
        var sink = new CapturingSpellAuditSink();
        var session = GameSession.CreateImperialEncounter(
            new WildMagicController(new MockSpellProvider(), audit: sink));

        await session.ExecuteAsync(new CastCommand("raise a wall of ice between us"));

        var entry = Assert.Single(sink.Entries);
        Assert.NotNull(entry.Routing);
        Assert.True(entry.Routing!.AdvertisedOperationCount > 0);
        Assert.True(entry.Routing.ContextPayloadBytes > 0);
    }

    private sealed class CapturingSpellAuditSink : ISpellAuditSink
    {
        public List<SpellAuditEntry> Entries { get; } = new();

        public void Record(SpellAuditEntry entry) => Entries.Add(entry);
    }
}
