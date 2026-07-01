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
            new("empire", "Grand Empire of Vigovia", "ruling", "Emperor Odran of Vigovia", "wild_color", 0, new[] { "empire", "marble", "law" }),
            new("unruled", "Unruled Wilds", "unruled", "no single ruler", "wild_color", -25, new[] { "wild", "border", "unruled" }),
        };

        realms.Add(RollRealm(
            seed,
            "hollowmere",
            "Hollowmere",
            "wild_color",
            new[] { "Matriarch Vey of the Reed Houses", "the Bent Council", "Reed-Captain Sola" },
            new[] { "water", "memory", "resistance" }));
        realms.Add(RollRealm(
            seed,
            "stalnaz",
            "Stalnaz",
            "bone_oath",
            new[] { "Bone-King Tovan", "the White Aunties", "Marshal Sorek" },
            new[] { "bone", "oath", "mountain" }));
        realms.Add(RollRealm(
            seed,
            "brall",
            "Brall",
            "brass_song",
            new[] { "Harbor-Duke Aven", "the Bell Parliament", "Saint-Merchant Nio" },
            new[] { "brass", "song", "harbor" }));
        realms.Add(RollRealm(
            seed,
            "ryolan",
            "Ryolan",
            "crystal_witness",
            new[] { "Glass Regent Iri", "the Witness Choir", "Prince Cal of the Cut Mirrors" },
            new[] { "crystal", "witness", "court" }));
        realms.Add(RollRealm(
            seed,
            "vint",
            "Vint",
            "salt_orchard",
            new[] { "Orchard Queen Sest", "the Salt Prior", "Speaker Halen" },
            new[] { "salt", "orchard", "ancestor" }));

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

    private static T Pick<T>(int seed, string realmId, string lane, IReadOnlyList<T> options) =>
        options[StableSeed(seed, realmId, lane) % options.Count];
}
