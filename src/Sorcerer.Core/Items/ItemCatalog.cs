namespace Sorcerer.Core.Items;

public sealed record ItemDefinition(
    string Id,
    string Name,
    char Glyph,
    string Kind,
    string Material,
    IReadOnlyList<string> Tags,
    int Value,
    string StackPolicy = "commodity",
    string UseProfile = "inert",
    string? EquipmentSlot = null);

public sealed class ItemCatalog
{
    private readonly Dictionary<string, ItemDefinition> _items = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<ItemDefinition> Items => _items.Values;

    public void Add(ItemDefinition item) => _items[item.Id] = item;

    public ItemDefinition? Find(string idOrName) =>
        _items.TryGetValue(idOrName, out var item)
            ? item
            : _items.Values.FirstOrDefault(candidate =>
                candidate.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));

    public static ItemCatalog CreateMinimal()
    {
        var catalog = new ItemCatalog();
        catalog.Add(new ItemDefinition("gold", "gold", '$', "currency", "gold", new[] { "coin", "value" }, 1));
        catalog.Add(new ItemDefinition("grave_salt", "grave salt", '*', "reagent", "salt", new[] { "death", "ward", "bone" }, 8));
        catalog.Add(new ItemDefinition("moon_pearl", "moon pearl", 'o', "reagent", "pearl", new[] { "moon", "water", "beauty" }, 40, "unique"));
        catalog.Add(new ItemDefinition("red_tincture", "red tincture", '!', "consumable", "glass", new[] { "blood", "healing", "medicine" }, 12, UseProfile: "heal:6"));
        catalog.Add(new ItemDefinition("charcoal_wand", "charcoal wand", '/', "focus", "charcoal", new[] { "wand", "focus", "burnt" }, 18, "unique", EquipmentSlot: "hand"));
        catalog.Add(new ItemDefinition("imperial_cell_key", "imperial cell key", 'k', "key", "iron", new[] { "key", "imperial", "cell" }, 5, "unique", "key"));
        return catalog;
    }
}
