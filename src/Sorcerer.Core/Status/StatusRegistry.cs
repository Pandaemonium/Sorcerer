namespace Sorcerer.Core.Status;

public sealed record StatusDefinition(
    string Id,
    string DisplayName,
    IReadOnlyList<string> Aliases,
    bool BlocksMovement = false,
    bool BlocksAction = false,
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

    public static StatusRegistry CreateDefault()
    {
        var registry = new StatusRegistry();
        registry.Add(new StatusDefinition("burning", "burning", Array.Empty<string>(), DefaultDuration: 3));
        registry.Add(new StatusDefinition("poisoned", "poisoned", Array.Empty<string>(), DefaultDuration: 5));
        registry.Add(new StatusDefinition("frozen", "frozen", new[] { "petrified", "crystallized", "ice_locked" }, BlocksMovement: true, BlocksAction: true, DefaultDuration: 2));
        registry.Add(new StatusDefinition("rooted", "rooted", new[] { "webbed", "bound", "sticky_webbed", "immobilized", "restrained" }, BlocksMovement: true, BlocksAction: true, DefaultDuration: 3));
        registry.Add(new StatusDefinition("stunned", "stunned", new[] { "asleep", "dazed", "bewildered", "disoriented", "staggered" }, BlocksAction: true, DefaultDuration: 1));
        registry.Add(new StatusDefinition("borrowed_body", "borrowed body", new[] { "soul_swapped", "possessed" }, DefaultDuration: 8));
        registry.Add(new StatusDefinition("revealed", "revealed", Array.Empty<string>(), DefaultDuration: 5));
        return registry;
    }
}
