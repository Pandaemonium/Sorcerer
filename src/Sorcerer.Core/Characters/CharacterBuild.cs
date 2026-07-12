namespace Sorcerer.Core.Characters;

/// <summary>
/// The output of character creation: an origin plus the player's adjustments on top of it.
/// Stats are post-spend absolutes (not deltas). Null/blank text fields mean "keep the
/// origin's default", so an untouched build reproduces the origin exactly
/// (docs/CHARACTER_AND_STATS.md — empty name simply means others improvise one for you).
/// </summary>
public sealed record CharacterBuild(
    string OriginId,
    int Vigor,
    int Attunement,
    int Composure,
    string? Name = null,
    string? Appearance = null,
    string? Backstory = null,
    string? MagicalSignature = null,
    string? BonusCharterSpellId = null,
    string? PortraitPath = null);
