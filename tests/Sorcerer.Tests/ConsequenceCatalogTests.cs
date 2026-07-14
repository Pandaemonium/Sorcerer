using System.Reflection;
using Sorcerer.Core;
using Sorcerer.Core.Consequences;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Characterization safety net for the consequence dispatch table (Phase 0.1).
///
/// <para>
/// <see cref="WorldConsequenceApplier.Apply"/> currently routes every consequence through one
/// ~72-case <c>normalizedType switch</c>, backed by three parallel lists that must stay in sync:
/// the <see cref="WorldConsequenceTypes"/> string constants, its <see cref="WorldConsequenceTypes.Normalize"/>
/// alias table, and its <see cref="WorldConsequenceTypes.IsKnown"/> membership chain.
/// </para>
///
/// <para>
/// Phase 0.2 replaces that switch with a <c>ConsequenceFamilyRegistry</c>. These tests pin the
/// observable dispatch contract -- catalog membership, per-type routing to a real handler,
/// alias normalization, and rejection of unknown types -- so the restructuring cannot silently
/// drop a type, break an alias, or let a handler fall through to the unknown-type reject path.
/// They assert behavior (every known type dispatches), not the switch's shape, so they survive
/// the refactor unchanged.
/// </para>
/// </summary>
public sealed class ConsequenceCatalogTests
{
    private const string UnknownTypeError = "Unknown world consequence type";

    /// <summary>
    /// The canonical consequence catalog, reflected from the <see cref="WorldConsequenceTypes"/>
    /// public <c>const string</c> fields so it tracks the source of truth automatically.
    /// </summary>
    public static IReadOnlyList<string> CanonicalTypes { get; } =
        typeof(WorldConsequenceTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

    public static IEnumerable<object[]> CanonicalTypeCases =>
        CanonicalTypes.Select(type => new object[] { type });

    /// <summary>
    /// Drift guard: the exact set of canonical consequence types is pinned here. Adding or
    /// removing a consequence type is a deliberate design act (per the complexity budget) and
    /// must update this list, which forces the change through review rather than letting the
    /// catalog grow or shrink silently.
    /// </summary>
    [Fact]
    public void CanonicalCatalogMatchesPinnedSet()
    {
        var expected = new[]
        {
            "damage",
            "heal",
            "restore_mana",
            "adjust_actor_resource",
            "move_entity",
            "set_terrain",
            "update_terrain",
            "apply_status",
            "remove_status",
            "accelerate_status",
            "spawn_entity",
            "spawn_item",
            "spawn_fixture",
            "create_promise",
            "update_promise",
            "message",
            "modify_inventory",
            "transfer_item",
            "update_equipment",
            "add_tags",
            "remove_tags",
            "change_faction",
            "update_control",
            "set_controlled_entity",
            "swap_souls",
            "set_world_flag",
            "update_run_status",
            "set_selected_target",
            "queue_background_job",
            "update_background_job",
            "schedule_event",
            "update_scheduled_event",
            "create_trigger",
            "update_trigger",
            "adjust_faction_standing",
            "adjust_faction_resource",
            "record_suspicion",
            "update_suspicion",
            "record_deed",
            "update_deed",
            "add_legend",
            "add_canon",
            "record_world_turn",
            "record_exploration",
            "transform_entity",
            "set_resistance",
            "set_weakness",
            "delay_incoming_damage",
            "release_delayed_damage",
            "edit_memory",
            "create_persistent_effect",
            "update_persistent_effect",
            "set_behavior",
            "update_behavior",
            "create_flow",
            "update_flow",
            "record_claim",
            "update_claim",
            "record_rumor",
            "update_rumor",
            "record_memory",
            "update_bond",
            "update_want",
            "add_merchant_stock",
            "offer_trade",
            "execute_trade",
            "offer_service",
            "request_service",
            "open_or_unlock",
            "create_route",
            "free_captive",
            "animate_entity",
        }.OrderBy(type => type, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, CanonicalTypes);
    }

    /// <summary>
    /// Registry parity (Phase 0.2 architecture enforcement): the reflected constant catalog, the
    /// <see cref="WorldConsequenceTypes.Canonical"/> membership set that backs <c>IsKnown</c>, and
    /// the applier's <see cref="WorldConsequenceApplier.DispatchableConsequenceTypes"/> dispatch
    /// registry are all the same set. This guarantees every known type has exactly one dispatch
    /// owner, no handler is orphaned, and the membership set and dispatch table cannot drift apart
    /// when families are split into partial files.
    /// </summary>
    [Fact]
    public void CatalogMembershipAndDispatchRegistryAgree()
    {
        var canonicalMembership = WorldConsequenceTypes.Canonical
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();
        var dispatchable = WorldConsequenceApplier.DispatchableConsequenceTypes
            .OrderBy(type => type, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(CanonicalTypes, canonicalMembership);
        Assert.Equal(CanonicalTypes, dispatchable);
    }

    /// <summary>
    /// Every canonical type reports as known and is a fixed point of normalization. If a constant
    /// is added to the catalog but omitted from <see cref="WorldConsequenceTypes.IsKnown"/> or its
    /// normalize table, this fails -- catching the parallel-list drift the plan warns about.
    /// </summary>
    [Theory]
    [MemberData(nameof(CanonicalTypeCases))]
    public void CanonicalTypeIsKnownAndNormalizesToItself(string type)
    {
        Assert.True(WorldConsequenceTypes.IsKnown(type), $"'{type}' should be a known consequence type.");
        Assert.Equal(type, WorldConsequenceTypes.Normalize(type));
    }

    /// <summary>
    /// Dispatch pin: applying a minimal consequence of each canonical type through the real engine
    /// apply point routes it to a handler -- it never falls through to the unknown-type reject.
    /// A handler may legitimately reject the bare consequence for its own reasons (missing target,
    /// no payload); what matters here is that the type was recognized and dispatched. This is the
    /// invariant Phase 0.2's registry must preserve for all 72 types.
    /// </summary>
    [Theory]
    [MemberData(nameof(CanonicalTypeCases))]
    public void CanonicalTypeDispatchesToAHandler(string type)
    {
        var session = GameSession.CreateImperialEncounter();

        var result = session.Engine.ApplyConsequence(
            new WorldConsequence(type, "consequence_catalog_test"));

        Assert.DoesNotContain(UnknownTypeError, result.Error ?? string.Empty);
    }

    /// <summary>
    /// Reject pin: a type outside the catalog is not known, normalizes to itself unchanged, and is
    /// rejected at the apply point with the unknown-type error. This is the negative half of the
    /// dispatch contract -- it proves the "not unknown-type" assertion above is meaningful.
    /// </summary>
    [Fact]
    public void UnknownConsequenceTypeIsRejectedAndNeverDispatches()
    {
        const string unknown = "definitely_not_a_real_consequence_type";
        Assert.False(WorldConsequenceTypes.IsKnown(unknown));
        Assert.Equal(unknown, WorldConsequenceTypes.Normalize(unknown));

        var session = GameSession.CreateImperialEncounter();

        var result = session.Engine.ApplyConsequence(
            new WorldConsequence(unknown, "consequence_catalog_test"));

        Assert.False(result.Applied);
        Assert.Contains(UnknownTypeError, result.Error ?? string.Empty);
    }

    /// <summary>
    /// Alias pin: the semantic aliases (distinct fictional phrasings, not mere casing) and the
    /// format-normalization behavior (camelCase, PascalCase, kebab-case, spacing, padding) both
    /// collapse to the intended canonical type. These are the aliases most likely to be broken by
    /// a mechanical refactor of the normalize table, and each stays reachable for content/model
    /// authors that spell a consequence differently.
    /// </summary>
    [Theory]
    // free_captive semantic aliases
    [InlineData("release_captive", WorldConsequenceTypes.FreeCaptive)]
    [InlineData("releaseCaptive", WorldConsequenceTypes.FreeCaptive)]
    [InlineData("free_prisoner", WorldConsequenceTypes.FreeCaptive)]
    [InlineData("freePrisoner", WorldConsequenceTypes.FreeCaptive)]
    // animate_entity semantic aliases
    [InlineData("raise_dead", WorldConsequenceTypes.AnimateEntity)]
    [InlineData("raiseDead", WorldConsequenceTypes.AnimateEntity)]
    [InlineData("animate_corpse", WorldConsequenceTypes.AnimateEntity)]
    [InlineData("animate_object", WorldConsequenceTypes.AnimateEntity)]
    // format normalization: casing, separators, spacing, padding all reach the canonical token
    [InlineData("recordMemory", WorldConsequenceTypes.RecordMemory)]
    [InlineData("RecordMemory", WorldConsequenceTypes.RecordMemory)]
    [InlineData("record-memory", WorldConsequenceTypes.RecordMemory)]
    [InlineData("record memory", WorldConsequenceTypes.RecordMemory)]
    [InlineData("  record__memory  ", WorldConsequenceTypes.RecordMemory)]
    [InlineData("adjustFactionResource", WorldConsequenceTypes.AdjustFactionResource)]
    [InlineData("delay-incoming-damage", WorldConsequenceTypes.DelayIncomingDamage)]
    public void AliasNormalizesToCanonicalTypeAndIsKnown(string alias, string expected)
    {
        Assert.Equal(expected, WorldConsequenceTypes.Normalize(alias));
        Assert.True(WorldConsequenceTypes.IsKnown(alias), $"alias '{alias}' should be known.");
    }
}
