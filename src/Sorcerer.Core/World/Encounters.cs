namespace Sorcerer.Core.World;

/// <summary>
/// One member of an encounter cast: a guard, keeper, or rival slot the assembler fills with
/// a named NPC. WantPattern/WantStakes are the social-route surface — dialogue context quotes
/// wants, so the stakes text is what teaches the dialogue model when this person would yield.
/// </summary>
public sealed record EncounterCastSlotDefinition(
    string Id,
    string Role,
    string TitlePattern,
    char Glyph,
    string AiPolicyId,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> Roles,
    string WantPattern,
    string WantStakes,
    int MinHitPoints = 7,
    int MaxHitPoints = 10,
    int MinAttack = 0,
    int MaxAttack = 2,
    IReadOnlyList<string>? InteractableVerbs = null,
    IReadOnlyList<int>? CountByTier = null);

public sealed record EncounterFactionCastDefinition(
    string FactionId,
    int Weight,
    int MinImperialPresence,
    IReadOnlyList<EncounterCastSlotDefinition> Slots);

/// <summary>
/// A reusable encounter ingredient per IMPLEMENTATION_PLAN §3.4: an arrangement of existing
/// content (cast, objective placement, canon line) — never a scripted room graph or a ledger.
/// Kind: guarded_cache | keeper | restricted_site | rival_claimant.
/// Formation: ring | adjacent | inside.
/// </summary>
public sealed record EncounterArchetypeDefinition(
    string Id,
    string Kind,
    int MinTier,
    int MaxTier,
    bool RequiresInterior,
    bool AmbientEligible,
    string Formation,
    string CanonPattern,
    int Weight,
    IReadOnlyList<string> Tags,
    IReadOnlyList<EncounterFactionCastDefinition> Casts);
