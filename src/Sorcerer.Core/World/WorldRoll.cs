namespace Sorcerer.Core.World;

public sealed record RealmProfile(
    string RealmId,
    string Name,
    string Status,
    string Ruler,
    string TraditionId,
    int ImperialGripDelta,
    IReadOnlyList<string> Tags);

public sealed class WorldRoll
{
    private readonly Dictionary<string, RealmProfile> _realms;

    private WorldRoll(IEnumerable<RealmProfile> realms)
    {
        _realms = realms.ToDictionary(realm => realm.RealmId, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<RealmProfile> Realms => _realms.Values;

    public RealmProfile RealmFor(string realmId) =>
        _realms.TryGetValue(realmId, out var realm)
            ? realm
            : new RealmProfile(realmId, realmId, "unmapped", "unknown", "wild_color", 0, Array.Empty<string>());

    public static WorldRoll Create(int seed)
    {
        var realms = new List<RealmProfile>
        {
            new("empire", "Grand Empire of Vigovia", "ruling", "Emperor Odran of Vigovia", "charter_geometry", 0, new[] { "empire", "marble", "law" }),
            new("unruled", "Unruled Wilds", "unruled", "no single ruler", "wild_color", -25, new[] { "wild", "border", "unruled" }),
        };

        realms.Add(RollRealm(
            seed,
            "hollowmere",
            "Hollowmere",
            "reed_memory",
            new[] { "Matriarch Vey of the Reed Houses", "the Bent Council", "Reed-Captain Sola" },
            new[] { "water", "memory", "resistance" }));
        var rivalId = new[] { "stalnaz", "brall", "ryolan", "vint" }
            [StableSeed(seed, "old_kingdoms", "rival") % 4];
        realms.Add(RollOldKingdom(
            seed,
            "stalnaz",
            "Stalnaz",
            "crystal_song",
            new[] { "Queen Ilyra of Stalnaz", "Queen Maresca", "Queen Selen of the Singing Court" },
            new[] { "crystal", "song", "mountain", "queendom" },
            rivalId));
        realms.Add(RollOldKingdom(
            seed,
            "brall",
            "Brall",
            "bone_song",
            new[] { "the Bone Jarls", "Jarl Aven of the Whale Gate", "the Seven-Hold Council" },
            new[] { "bone", "ale", "whalebone", "holds" },
            rivalId));
        realms.Add(RollOldKingdom(
            seed,
            "ryolan",
            "Ryolan",
            "blood_oath",
            new[] { "King Tovan of Ryolan", "King Sorek the Oath-Keeper", "King Cal of the Red Team" },
            new[] { "blood", "oath", "chariot", "honor" },
            rivalId));
        realms.Add(RollOldKingdom(
            seed,
            "vint",
            "Vint",
            "woven_thread",
            new[] { "First Speaker Sest", "the Ribbon Coalition", "Speaker Halen" },
            new[] { "woven", "gossip", "republic", "intrigue" },
            rivalId));
        realms.Add(new RealmProfile(
            "threen",
            "Independent Kingdom of Threen",
            "client",
            "the Canal Court",
            "artisan_letters",
            8,
            new[] { "canals", "literature", "artisans", "courtesy" }));
        realms.Add(new RealmProfile(
            "monteary",
            "Monteary",
            "deferential",
            "the Breeders' Council",
            "horse_song",
            4,
            new[] { "horses", "grassland", "trade" }));
        realms.Add(new RealmProfile(
            "ontria",
            "Ontria",
            "chartered",
            "the Clan Mothers",
            "ancestor_culture",
            2,
            new[] { "ancestors", "clans", "living_culture" }));
        realms.Add(new RealmProfile(
            "gontark",
            "Gontark",
            "watched",
            "the Horn Council",
            "curse_craft",
            5,
            new[] { "goatfolk", "curses", "crags" }));
        realms.Add(new RealmProfile(
            "parn",
            "The Parn Sunroads",
            "nomadic",
            "no single ruler",
            "sound_ink",
            -5,
            new[] { "desert", "caravans", "music", "tattoos" }));
        realms.Add(new RealmProfile(
            "rentacosta",
            "Rentacosta",
            "free_city",
            "the Harbor Compact",
            "tide_trade",
            -8,
            new[] { "coast", "ships", "languages", "merfolk_trade" }));

        return new WorldRoll(realms);
    }

    public static int StableSeed(int seed, params string[] parts)
    {
        unchecked
        {
            var hash = 1469598103934665603UL ^ (uint)Math.Max(1, seed);
            foreach (var part in parts)
            {
                foreach (var ch in part.ToLowerInvariant())
                {
                    hash ^= ch;
                    hash *= 1099511628211UL;
                }

                hash ^= 0xff;
                hash *= 1099511628211UL;
            }

            return (int)(hash & 0x7fffffff);
        }
    }

    private static RealmProfile RollRealm(
        int seed,
        string id,
        string name,
        string traditionId,
        IReadOnlyList<string> rulers,
        IReadOnlyList<string> tags)
    {
        var status = Pick(
            seed,
            id,
            "status",
            new[]
            {
                ("defiant", -22, "defiant"),
                ("restive", -10, "restive"),
                ("client", -4, "client"),
                ("occupied", 12, "occupied"),
                ("crushed", 22, "crushed"),
            });
        var ruler = rulers[StableSeed(seed, id, "ruler") % rulers.Count];
        return new RealmProfile(
            id,
            name,
            status.Item1,
            ruler,
            traditionId,
            status.Item2,
            tags.Concat(new[] { status.Item3 }).ToArray());
    }

    private static RealmProfile RollOldKingdom(
        int seed,
        string id,
        string name,
        string traditionId,
        IReadOnlyList<string> rulers,
        IReadOnlyList<string> tags,
        string rivalId)
    {
        var isRival = id.Equals(rivalId, StringComparison.OrdinalIgnoreCase);
        var status = isRival
            ? (Name: "rival", Grip: -30)
            : Pick(
                seed,
                id,
                "conquered_status",
                new[]
                {
                    (Name: "occupied", Grip: 12),
                    (Name: "restive", Grip: 4),
                    (Name: "pacified", Grip: 8),
                    (Name: "crushed", Grip: 22),
                });
        var ruler = rulers[StableSeed(seed, id, "ruler") % rulers.Count];
        return new RealmProfile(
            id,
            name,
            status.Name,
            ruler,
            traditionId,
            status.Grip,
            tags.Concat(new[] { isRival ? "rival" : "conquered", status.Name }).ToArray());
    }

    private static T Pick<T>(int seed, string realmId, string lane, IReadOnlyList<T> options) =>
        options[StableSeed(seed, realmId, lane) % options.Count];
}
