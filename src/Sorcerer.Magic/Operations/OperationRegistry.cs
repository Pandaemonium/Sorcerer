using Sorcerer.Core.Views;

namespace Sorcerer.Magic.Operations;

public sealed class OperationRegistry
{
    private readonly Dictionary<string, IOperation> _operations;
    private readonly Dictionary<string, string> _aliases;

    private OperationRegistry(IEnumerable<IOperation> operations)
    {
        _operations = operations.ToDictionary(op => op.Name, StringComparer.OrdinalIgnoreCase);
        _aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var operation in _operations.Values)
        {
            _aliases[operation.Name] = operation.Name;
        }

        foreach (var operation in _operations.Values)
        {
            _aliases[operation.Name] = operation.Name;
            foreach (var alias in operation.Aliases)
            {
                _aliases.TryAdd(alias, operation.Name);
            }

            foreach (var alias in operation.Card.Aliases)
            {
                _aliases.TryAdd(alias, operation.Name);
            }
        }
    }

    public IReadOnlyCollection<IOperation> Operations => _operations.Values;

    public string Canonicalize(string name) =>
        _aliases.TryGetValue(name.Trim(), out var canonical) ? canonical : name.Trim();

    public IOperation? Resolve(string name) =>
        _operations.TryGetValue(Canonicalize(name), out var operation) ? operation : null;

    public bool Supports(string name) => Resolve(name) is not null;

    public OperationIndex ToIndex() =>
        new(
            _operations.Keys.OrderBy(name => name).ToArray(),
            _operations.Values
                .OrderBy(op => op.Name)
                .Select(op => op.Card.ToView())
                .ToArray());

    /// <summary>
    /// Advertises core operations plus any non-core operation named in
    /// <paramref name="selectedEffectTypes"/> (the effect types unlocked by routed capability
    /// cards). Validation and application still resolve against the full registry regardless of
    /// what this index advertises; narrowing only shapes what the resolver prompt/context show.
    /// </summary>
    public OperationIndex ToNarrowedIndex(IEnumerable<string> selectedEffectTypes)
    {
        var allowed = new HashSet<string>(selectedEffectTypes, StringComparer.OrdinalIgnoreCase);
        var operations = _operations.Values
            .Where(op => op.IsCore || allowed.Contains(op.Name))
            .OrderBy(op => op.Name)
            .ToArray();
        return new(
            operations.Select(op => op.Name).ToArray(),
            operations.Select(op => op.Card.ToView()).ToArray());
    }

    public static OperationRegistry Build(IEnumerable<IOperation> operations, IEnumerable<OperationCard>? cards = null)
    {
        var ops = operations.ToArray();
        if (cards is not null)
        {
            var byName = ops.ToDictionary(op => op.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var card in cards)
            {
                if (byName.TryGetValue(card.Name, out var operation) && operation is OperationBase operationBase)
                {
                    operationBase.AttachCard(card);
                }
            }
        }

        return new OperationRegistry(ops);
    }

    public static OperationRegistry CreateDefault()
    {
        var operations = new IOperation[]
            {
            new DamageOperation(),
            new AreaDamageOperation(),
            new HealOperation(),
            new RestoreManaOperation(),
            new PushOperation(),
            new PullOperation(),
            new TeleportOperation(),
            new CreateTileOperation(),
            new CreateTilesOperation(),
            new AddStatusOperation(),
            new RemoveStatusOperation(),
            new SummonOperation(),
            new TransformEntityOperation(),
            new TransformItemOperation(),
            new PossessOperation(),
            new ChangeFactionOperation(),
            new AddTraitOperation(),
            new CreateTriggerOperation(),
            new ScheduleEventOperation(),
            new AddCurseOperation(),
            new CreatePromiseOperation(),
            new MessageOperation(),
            new AreaStatusOperation(),
            new ModifyInventoryOperation(),
            new AddTagOperation(),
            new RemoveTagOperation(),
            new AccelerateStatusOperation(),
            new ConjureItemOperation(),
            new ConjureCreatureOperation(),
            new AddResistanceOperation(),
            new AddWeaknessOperation(),
            new SetFlagOperation(),
            new DelayIncomingOperation(),
            new EditMemoryOperation(),
            new CreatePersistentEffectOperation(),
            new SetBehaviorOperation(),
            new CreateFlowOperation(),
            };
        return Build(operations, OperationCardLoader.LoadDefaultContentCards());
    }
}
