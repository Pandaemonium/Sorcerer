using System.Linq;
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
        return new SpellRequest(spellText, context, index.Names, selected, capabilities.CapabilityIndex());
    }

    [Fact]
    public void SystemPromptLeadsWithStaticSectionsAndAdvertisesOperationsAsText()
    {
        var request = BuildRequest("raise a wall of ice between us");
        var system = SpellPromptBuilder.System(request);

        Assert.StartsWith("You are the wild magic resolver for Sorcerer.", system);
        Assert.Contains("consequenceType:", system);
        Assert.Contains("Capability index", system);
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
        // Null-omitting: the caster is present, selectedTarget (null here) is dropped.
        Assert.True(root.TryGetProperty("caster", out _));
        Assert.False(root.TryGetProperty("selectedTarget", out _));
        // The engine-facing view still owns a non-null Operations index (only the wire copy nulls it).
        Assert.True(((MagicContextView)request.Context).Operations.Names.Count > 0);
    }
}
