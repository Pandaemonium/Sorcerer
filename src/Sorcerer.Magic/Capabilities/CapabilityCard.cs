namespace Sorcerer.Magic.Capabilities;

public sealed record CapabilityCard(
    string Id,
    IReadOnlyList<string> Triggers,
    string IndexLine,
    IReadOnlyList<string> EffectTypes,
    IReadOnlyList<string> RequiredContext,
    string PromptBlock,
    IReadOnlyList<string> Examples,
    int Version = 1);

public sealed class CapabilityRegistry
{
    private readonly Dictionary<string, CapabilityCard> _cards = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<CapabilityCard> Cards => _cards.Values;

    public void Add(CapabilityCard card) => _cards[card.Id] = card;

    public IReadOnlyList<CapabilityCard> Select(string spellText)
    {
        var lower = spellText.ToLowerInvariant();
        return _cards.Values
            .Where(card => card.Triggers.Any(trigger => lower.Contains(trigger, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(card => card.Id)
            .Take(7)
            .ToArray();
    }

    public static CapabilityRegistry CreateDefault()
    {
        var registry = new CapabilityRegistry();
        registry.Add(new CapabilityCard(
            "terrain_shape",
            new[] { "wall", "floor", "ice", "fire", "water", "mist", "bridge", "terrain" },
            "terrain_shape - create or alter local tiles and hazards",
            new[] { "createTile", "createTiles" },
            new[] { "nearby_tiles", "selected_target" },
            "Use terrain operations for local tile changes, never global map rewrites.",
            Array.Empty<string>()));
        registry.Add(new CapabilityCard(
            "summoning",
            new[] { "summon", "call", "conjure", "create creature", "familiar" },
            "summoning - create bounded creatures or helpers",
            new[] { "summon", "createEntity" },
            new[] { "visible_tiles", "factions" },
            "Summons must have bounded stats, a faction, and a valid placement.",
            Array.Empty<string>()));
        registry.Add(new CapabilityCard(
            "transformation",
            new[] { "turn", "transform", "change into", "make into", "teeth", "glass" },
            "transformation - alter entities, items, props, bodies, or materials",
            new[] { "transformEntity", "transformItem", "addTrait" },
            new[] { "visible_entities", "spell_anchors" },
            "Transformations should preserve engine authority and avoid free win-button changes.",
            Array.Empty<string>()));
        registry.Add(new CapabilityCard(
            "prophecy",
            new[] { "promise", "prophecy", "omen", "debt", "future", "tomorrow" },
            "prophecy - write a future commitment into the Promise Ledger",
            new[] { "createPromise", "scheduleEvent" },
            new[] { "promises", "region" },
            "Promises are powerful because the world may later honor them. Use real costs.",
            Array.Empty<string>()));
        return registry;
    }
}
