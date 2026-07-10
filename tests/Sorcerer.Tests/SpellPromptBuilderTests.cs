using System.Linq;
using System.Text;
using System.Text.Json;
using Sorcerer.Core;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Locks the resolver prompt layout introduced in docs/OPTIMIZATION_PLAN.md WS2.3: static content
/// first (for KV-cache reuse), the operation catalog rendered as prompt text rather than serialized
/// into the user-message context JSON, and the null-omitting wire copy.
/// </summary>
public sealed class SpellPromptBuilderTests
{
    private static SpellRequest BuildRequest(string spellText)
    {
        var session = GameSession.CreateImperialEncounter();
        var capabilities = CapabilityRegistry.CreateDefault();
        var registry = OperationRegistry.CreateDefault();
        var selected = capabilities.Select(spellText);
        var index = registry.ToRoutedIndex(
            selected.SelectMany(card => card.EffectTypes),
            capabilities.AllEffectTypes());
        var required = selected.SelectMany(card => card.RequiredContext).ToHashSet(System.StringComparer.OrdinalIgnoreCase);
        var context = session.Engine.MagicContext(index, required);
        return new SpellRequest(spellText, context, index.Names, selected, capabilities.CapabilityNames());
    }

    [Fact]
    public void SystemPromptLeadsWithStaticSectionsAndAdvertisesOperationsAsText()
    {
        var request = BuildRequest("raise a wall of ice between us");
        var system = SpellPromptBuilder.System(request);

        Assert.StartsWith("You are Sorcerer's wild-magic resolver.", system);
        Assert.Contains("consequenceType:", system);
        Assert.Contains("Capability names for one bounded retry", system);
        Assert.Contains("Operation guidance", system);
        // Operation guidance is rendered as "- name: ..." lines, one per advertised op.
        Assert.Contains("- createTiles:", system);
    }

    [Fact]
    public void WireContextOmitsTheOperationCatalogAndNullFields()
    {
        var request = BuildRequest("heal my wounds with warm green light");
        var wire = SpellPromptBuilder.WireContextJson(request);

        using var document = JsonDocument.Parse(wire);
        var root = document.RootElement;
        // The operation catalog is prompt text now, never re-serialized into the context JSON.
        Assert.False(root.TryGetProperty("operations", out _));
        // The provider gets the compact projection, not the engine/audit record shape.
        Assert.True(root.TryGetProperty("caster", out _));
        Assert.False(root.TryGetProperty("selected", out _));
        Assert.False(root.TryGetProperty("recentEvents", out _));
        Assert.False(root.TryGetProperty("knownPromises", out _));
        // The engine-facing view still owns the rich, non-null Operations index.
        Assert.True(((MagicContextView)request.Context).Operations.Names.Count > 0);
    }

    [Fact]
    public void ContextSlicesFollowRoutedCapabilityRequirements()
    {
        var healing = JsonDocument.Parse(SpellPromptBuilder.WireContextJson(BuildRequest("heal my wounds with warm green light")));
        var terrain = JsonDocument.Parse(SpellPromptBuilder.WireContextJson(BuildRequest("raise a wall of ice between us")));
        var prophecy = JsonDocument.Parse(SpellPromptBuilder.WireContextJson(BuildRequest("promise that the door remembers my name")));

        Assert.False(healing.RootElement.TryGetProperty("terrain", out _));
        Assert.False(healing.RootElement.TryGetProperty("promises", out _));
        Assert.False(healing.RootElement.TryGetProperty("lore", out _));
        Assert.True(terrain.RootElement.TryGetProperty("terrain", out _));
        Assert.True(prophecy.RootElement.TryGetProperty("promises", out _));
        Assert.True(prophecy.RootElement.TryGetProperty("lore", out _));
    }

    [Theory]
    [InlineData("heal my wounds with warm green light")]
    [InlineData("raise a wall of ice between us")]
    [InlineData("make the captain forget my face")]
    [InlineData("spread a rumor that my shadow arrives before me")]
    public void ResolverPayloadStaysInsideCompactBudget(string spell)
    {
        var request = BuildRequest(spell);
        var contextBytes = Encoding.UTF8.GetByteCount(SpellPromptBuilder.WireContextJson(request));
        var totalBytes = Encoding.UTF8.GetByteCount(SpellPromptBuilder.System(request))
            + Encoding.UTF8.GetByteCount(SpellPromptBuilder.User(request));

        Assert.True(contextBytes <= 5_000, $"Resolver context for '{spell}' was {contextBytes} bytes.");
        Assert.True(totalBytes <= 12_000, $"Resolver prompt for '{spell}' was {totalBytes} bytes.");
    }
}
