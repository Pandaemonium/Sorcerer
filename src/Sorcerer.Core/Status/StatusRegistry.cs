namespace Sorcerer.Core.Status;

public sealed record StatusDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    bool BlocksMovement = false,
    bool BlocksAction = false,
    bool ConcealsBearer = false,
    int DamagePerTurn = 0,
    int HealPerTurn = 0,
    int? DefaultDuration = null);

public sealed class StatusRegistry
{
    private readonly Dictionary<string, StatusDefinition> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<StatusDefinition> Statuses => _statuses.Values;

    public void Add(StatusDefinition status)
    {
        _statuses[status.Id] = status;
        _aliases[status.Id] = status.Id;
        foreach (var alias in status.Aliases)
        {
            _aliases[alias] = status.Id;
        }
    }

    public string Canonicalize(string idOrAlias)
    {
        var trimmed = idOrAlias.Trim();
        if (_aliases.TryGetValue(trimmed, out var id))
        {
            return id;
        }

        var normalized = trimmed.Replace('-', '_');
        foreach (var alias in _aliases.Keys
            .Where(alias => alias.Length >= 4)
            .OrderByDescending(alias => alias.Length))
        {
            if (normalized.Contains(alias, StringComparison.OrdinalIgnoreCase))
            {
                return _aliases[alias];
            }
        }

        return trimmed;
    }

    public StatusDefinition? Find(string idOrAlias)
    {
        var id = Canonicalize(idOrAlias);
        return _statuses.TryGetValue(id, out var status) ? status : null;
    }

    public bool BlocksAction(string idOrAlias) => Find(idOrAlias)?.BlocksAction == true;

    public bool BlocksMovement(string idOrAlias) => Find(idOrAlias)?.BlocksMovement == true;

    public bool ConcealsBearer(string idOrAlias) => Find(idOrAlias)?.ConcealsBearer == true;

    public int DamagePerTurn(string idOrAlias) => Find(idOrAlias)?.DamagePerTurn ?? 0;

    public int HealPerTurn(string idOrAlias) => Find(idOrAlias)?.HealPerTurn ?? 0;

    public static StatusRegistry CreateDefault()
    {
        var registry = new StatusRegistry();
        registry.Add(new StatusDefinition("burning", "burning", Array.Empty<string>(), DamagePerTurn: 2, DefaultDuration: 3));
        registry.Add(new StatusDefinition("poisoned", "poisoned", Array.Empty<string>(), DamagePerTurn: 1, DefaultDuration: 5));
        registry.Add(new StatusDefinition("frozen", "frozen", new[] { "petrified", "crystallized", "ice_locked" }, BlocksMovement: true, BlocksAction: true, DefaultDuration: 2));
        registry.Add(new StatusDefinition(
            "rooted",
            "rooted",
            new[]
            {
                "webbed",
                "bound",
                "sticky_webbed",
                "immobilized",
                "immobile",
                "restrained",
                "pinned",
                "anchored",
                "tethered",
                "snared",
                "held",
                "kneeling",
                "buckled",
                "joint_locked",
                "boneless",
                "crumpled",
                "collapsed",
                // Physical binding verbs a resolver reaches for when a spell locks feet/limbs in
                // place. Chosen so none is a substring of another status word (e.g. "fused" is
                // deliberately omitted because it is a substring of "confused").
                "welded",
                "cemented",
                "mortared",
                "shackled",
                "manacled",
                "cuffed",
                "clamped",
                "encased",
                "entombed",
                "glued",
                "stuck",
                "trapped",
                // Safe to include even though "fused" is a substring of "confused": "confused" is
                // a direct alias of stunned below, so it resolves before the substring pass, and
                // in any compound ("confused_...") the longer "confused" wins the substring race.
                "fused",
            },
            BlocksMovement: true,
            BlocksAction: true,
            DefaultDuration: 3));
        registry.Add(new StatusDefinition("stunned", "stunned", new[] { "asleep", "dazed", "bewildered", "confused", "disoriented", "staggered" }, BlocksAction: true, DefaultDuration: 1));
        registry.Add(new StatusDefinition(
            "concealed",
            "concealed",
            new[] { "hidden", "camouflaged", "river_concealed", "river_color_cloak", "shadowed", "veiled", "mist_cloaked" },
            ConcealsBearer: true,
            DefaultDuration: 4));
        registry.Add(new StatusDefinition(
            "regenerating",
            "regenerating",
            new[] { "mending", "green_mending", "healing_over_time", "renewing", "verdant" },
            HealPerTurn: 2,
            DefaultDuration: 4));
        registry.Add(new StatusDefinition("borrowed_body", "borrowed body", new[] { "soul_swapped", "possessed" }, DefaultDuration: 8));
        registry.Add(new StatusDefinition("revealed", "revealed", Array.Empty<string>(), DefaultDuration: 5));
        return registry;
    }
}
