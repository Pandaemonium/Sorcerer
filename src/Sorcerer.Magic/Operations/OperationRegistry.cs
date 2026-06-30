namespace Sorcerer.Magic.Operations;

public sealed class OperationRegistry
{
    private readonly Dictionary<string, OperationSpec> _operations;
    private readonly Dictionary<string, string> _aliases;

    private OperationRegistry(IEnumerable<OperationSpec> operations)
    {
        _operations = operations.ToDictionary(op => op.Name, StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in _operations.Values)
        {
            _aliases[operation.Name] = operation.Name;
            foreach (var alias in operation.Aliases)
            {
                _aliases[alias] = operation.Name;
            }
        }
    }

    public IReadOnlyCollection<OperationSpec> Operations => _operations.Values;

    public string Canonicalize(string name) =>
        _aliases.TryGetValue(name.Trim(), out var canonical) ? canonical : name.Trim();

    public bool Supports(string name) => _operations.ContainsKey(Canonicalize(name));

    public static OperationRegistry CreateDefault() =>
        new(new[]
        {
            new OperationSpec("damage", new[] { "harm", "attack" }, "Damage one target.", new[] { "target", "amount" }),
            new OperationSpec("heal", new[] { "restoreHealth" }, "Restore HP.", new[] { "target", "amount" }),
            new OperationSpec("push", Array.Empty<string>(), "Move a target away.", new[] { "target", "distance" }),
            new OperationSpec("pull", Array.Empty<string>(), "Move a target closer.", new[] { "target", "distance" }),
            new OperationSpec("teleport", Array.Empty<string>(), "Move an entity to a tile.", new[] { "target", "x", "y" }),
            new OperationSpec("addStatus", new[] { "status", "applyStatus" }, "Apply a status.", new[] { "target", "status" }),
            new OperationSpec("createTiles", new[] { "createTile", "terrain" }, "Create or alter terrain.", new[] { "tile" }),
            new OperationSpec("summon", new[] { "createEntity" }, "Create a bounded entity.", new[] { "name" }),
            new OperationSpec("transformEntity", Array.Empty<string>(), "Transform an entity.", new[] { "target" }),
            new OperationSpec("transformItem", new[] { "transformFixture" }, "Transform an item or fixture.", new[] { "target" }),
            new OperationSpec("createPromise", new[] { "promise", "prophecy" }, "Add a promise to the world.", new[] { "text" }),
            new OperationSpec("message", Array.Empty<string>(), "Add a message.", new[] { "text" }),
        });
}

