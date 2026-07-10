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
    [InlineData("raise the fallen soldier to fight for me", "animation")]
    [InlineData("make the statue come alive and guard the door", "animation")]
    [InlineData("unravel the ward waiting behind me", "dispelling")]
    [InlineData("break the enchantment on the captain's blade", "dispelling")]
    [InlineData("show me where the emperor stands", "divination")]
    [InlineData("divine what the prisoner wants most", "divination")]
    [InlineData("spread a rumor that my shadow arrives before me", "rumor_legend")]
    [InlineData("let the whole district believe the captain is a traitor", "rumor_legend")]
    [InlineData("soften the captain's heart until he trusts me", "heart_bond")]
    [InlineData("make the cell door's lock forget its shape", "ways_and_seals")]
    [InlineData("let the guard's sword leap to my hand", "ways_and_seals")]
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

    [Fact]
    public async Task NeedsCapabilityAnswerLoadsCardAndReResolvesOnce()
    {
        // First answer asks for a capability by index name; second answer is a real resolution.
        var provider = new NeedsCapabilityThenResolveProvider("summoning");
        var session = GameSession.CreateImperialEncounter(new WildMagicController(provider));

        // Spell text that keyword-routes to nothing, so the first request genuinely lacks the card.
        var result = await session.ExecuteAsync(new CastCommand("heal my wounds with warm green light"));

        Assert.True(result.Success, result.Magic?.Error);
        Assert.Equal(2, provider.Requests.Count);
        // The retry request carried the requested capability that the first request lacked.
        Assert.DoesNotContain(
            provider.Requests[0].SelectedCapabilities ?? new List<Sorcerer.Magic.Capabilities.CapabilityCard>(),
            card => card.Id == "summoning");
        Assert.Contains(
            provider.Requests[1].SelectedCapabilities ?? new List<Sorcerer.Magic.Capabilities.CapabilityCard>(),
            card => card.Id == "summoning");
    }

    [Fact]
    public async Task ObviousSpellSkipsSlowRouterWhileOpaqueSpellUsesIt()
    {
        var obviousRouter = new CountingSpellRouter("summoning");
        var obviousProvider = new CapturingSpellProvider();
        var obviousSession = GameSession.CreateImperialEncounter(new WildMagicController(
            obviousProvider,
            router: obviousRouter));

        await obviousSession.ExecuteAsync(new CastCommand("raise a wall of ice between us"));

        Assert.Equal(0, obviousRouter.Calls);
        Assert.Contains(
            Assert.Single(obviousProvider.Requests).SelectedCapabilities ?? Array.Empty<Sorcerer.Magic.Capabilities.CapabilityCard>(),
            card => card.Id == "terrain_shape" || card.Id == "barrier_shaping");

        var opaqueRouter = new CountingSpellRouter("summoning");
        var opaqueProvider = new CapturingSpellProvider();
        var opaqueSession = GameSession.CreateImperialEncounter(new WildMagicController(
            opaqueProvider,
            router: opaqueRouter));

        await opaqueSession.ExecuteAsync(new CastCommand("make the fresco weep real tears"));

        Assert.Equal(1, opaqueRouter.Calls);
        Assert.Contains("summoning -", opaqueRouter.LastCapabilityIndex);
        var request = Assert.Single(opaqueProvider.Requests);
        Assert.Contains(
            request.SelectedCapabilities ?? Array.Empty<Sorcerer.Magic.Capabilities.CapabilityCard>(),
            card => card.Id == "summoning");
        Assert.DoesNotContain(" - ", request.CapabilityIndex ?? "");
    }

    [Fact]
    public async Task HiddenEntitySliceIncludesNamedRemoteTargetButNotUnrelatedActors()
    {
        var provider = new CapturingSpellProvider();
        var controller = new WildMagicController(provider);
        var session = GameSession.CreateImperialEncounter(controller);

        await controller.ResolveAsync(
            session.Engine,
            new CastCommand("make the prisoner forget me from far away"),
            System.Threading.CancellationToken.None);

        var context = Assert.IsType<Sorcerer.Core.Views.MagicContextView>(Assert.Single(provider.Requests).Context);
        Assert.Contains(context.Visible, entity => entity.Id == "prisoner_1");
        Assert.DoesNotContain(context.Visible, entity => entity.Visibility == "hidden_from_player" && entity.Id != "prisoner_1");
    }

    private sealed class NeedsCapabilityThenResolveProvider : Sorcerer.Magic.Resolution.ISpellProvider
    {
        private readonly string _capability;
        private int _calls;

        public NeedsCapabilityThenResolveProvider(string capability) => _capability = capability;

        public List<Sorcerer.Magic.Resolution.SpellRequest> Requests { get; } = new();

        public string Name => "needs-capability-stub";

        public Task<Sorcerer.Magic.Resolution.SpellProviderResult> ResolveAsync(
            Sorcerer.Magic.Resolution.SpellRequest request,
            System.Threading.CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (_calls++ == 0)
            {
                return Task.FromResult(new Sorcerer.Magic.Resolution.SpellProviderResult(
                    Name, $"{{\"needsCapability\":\"{_capability}\"}}", Resolution: null,
                    TechnicalFailure: false, Error: null, Stats: null, RequestedCapability: _capability));
            }

            var resolution = new Sorcerer.Magic.Resolution.SpellResolution(
                Accepted: true,
                Severity: "minor",
                OutcomeText: "A small blue helper unfolds from the air.",
                Effects: new[]
                {
                    new Sorcerer.Magic.Resolution.SpellEffect("heal", new Dictionary<string, object?>
                    {
                        ["target"] = "player",
                        ["amount"] = 3,
                    }),
                },
                Costs: new[]
                {
                    new Sorcerer.Magic.Resolution.SpellCost("mana", new Dictionary<string, object?> { ["amount"] = 2 }),
                },
                RejectedReason: null);
            return Task.FromResult(new Sorcerer.Magic.Resolution.SpellProviderResult(
                Name, "{}", resolution, TechnicalFailure: false, Error: null));
        }
    }

    private sealed class CountingSpellRouter : Sorcerer.Magic.Capabilities.ISpellRouter
    {
        private readonly string _capability;

        public CountingSpellRouter(string capability) => _capability = capability;

        public int Calls { get; private set; }

        public string LastCapabilityIndex { get; private set; } = "";

        public string Name => "counting-router";

        public Task<Sorcerer.Magic.Capabilities.SpellRouteResult> RouteAsync(
            string spellText,
            string capabilityIndex,
            System.Threading.CancellationToken cancellationToken)
        {
            Calls++;
            LastCapabilityIndex = capabilityIndex;
            return Task.FromResult(new Sorcerer.Magic.Capabilities.SpellRouteResult(
                new[] { _capability },
                "{}",
                TechnicalFailure: false,
                Error: null));
        }
    }

    private sealed class CapturingSpellProvider : Sorcerer.Magic.Resolution.ISpellProvider
    {
        private readonly MockSpellProvider _inner = new();

        public List<Sorcerer.Magic.Resolution.SpellRequest> Requests { get; } = new();

        public string Name => _inner.Name;

        public Task<Sorcerer.Magic.Resolution.SpellProviderResult> ResolveAsync(
            Sorcerer.Magic.Resolution.SpellRequest request,
            System.Threading.CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _inner.ResolveAsync(request, cancellationToken);
        }
    }

    private sealed class CapturingSpellAuditSink : ISpellAuditSink
    {
        public List<SpellAuditEntry> Entries { get; } = new();

        public void Record(SpellAuditEntry entry) => Entries.Add(entry);
    }
}
