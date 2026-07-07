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

    /// <summary>
    /// The handful of operations that make up the palette of most casts. Their full cards are always
    /// advertised so a routing miss still degrades to a recognizable spell, never a silent capability
    /// loss. Everything else is trimmed to a lean card (name + summary) unless a routed capability
    /// unlocks it. Deliberately small (docs/OPTIMIZATION_PLAN.md WS2.2): every operation demoted out
    /// of this set has a home on a capability card, so a spell that needs one routes its full card
    /// back in, and the recall floor in <see cref="ToRoutedIndex"/> keeps unrouted spells able to
    /// see every operation by name.
    /// </summary>
    private static readonly HashSet<string> CoreCommon = new(StringComparer.OrdinalIgnoreCase)
    {
        "damage", "heal", "addStatus", "removeStatus", "message", "createTiles",
    };

    private static readonly IReadOnlyDictionary<string, string> EmptyFields =
        new Dictionary<string, string>();

    /// <summary>
    /// Like <see cref="ToNarrowedIndex"/>, but additionally trims the operation *cards* the resolver
    /// sees. A card keeps its full prompt guidance, fields, and examples only when it is core-common,
    /// unlocked by a selected capability, or not reachable by any capability at all (so routing could
    /// never have brought it in). Every other advertised operation is reduced to a lean name+summary
    /// card. This is where the bulk of per-cast context (and latency) is saved; validation and apply
    /// still run against the full registry regardless of what the card advertised.
    /// </summary>
    public OperationIndex ToRoutedIndex(
        IEnumerable<string> selectedEffectTypes,
        IEnumerable<string> routableEffectTypes)
    {
        var selected = new HashSet<string>(selectedEffectTypes.Select(Canonicalize), StringComparer.OrdinalIgnoreCase);
        var routable = new HashSet<string>(routableEffectTypes.Select(Canonicalize), StringComparer.OrdinalIgnoreCase);

        // Recall floor (docs/OPTIMIZATION_PLAN.md WS1.1): when routing selected nothing, the spell
        // is one the trigger/router vocabulary did not anticipate — exactly the cast that must not
        // be pigeonholed into the core palette. Advertise every operation by name (lean cards
        // except the common core) instead of hiding the gated ones; the model can then reach for
        // any mechanic and the engine still validates as usual.
        var recallFloor = selected.Count == 0;
        var operations = _operations.Values
            .Where(op => recallFloor || op.IsCore || selected.Contains(op.Name))
            .OrderBy(op => op.Name)
            .ToArray();

        return new(
            operations.Select(op => op.Name).ToArray(),
            operations
                .Select(op => KeepFullCard(op, selected, routable, recallFloor) ? op.Card.ToView() : LeanCard(op.Card))
                .ToArray());
    }

    private static bool KeepFullCard(IOperation op, HashSet<string> selected, HashSet<string> routable, bool recallFloor) =>
        recallFloor
            ? CoreCommon.Contains(op.Name)
            : KeepFullCard(op, selected, routable);

    private static bool KeepFullCard(IOperation op, HashSet<string> selected, HashSet<string> routable) =>
        CoreCommon.Contains(op.Name)
        || selected.Contains(op.Name)
        || !routable.Contains(op.Name);

    private static OperationCardView LeanCard(OperationCard card) =>
        new(card.Name, Array.Empty<string>(), card.Summary, string.Empty, EmptyFields, Array.Empty<object>());

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
            new ConsequenceOperation(),
            new AreaStatusOperation(),
            new ModifyInventoryOperation(),
            new AddTagOperation(),
            new RemoveTagOperation(),
            new AccelerateStatusOperation(),
            new ConjureItemOperation(),
            new ConjureFixtureOperation(),
            new ConjureCreatureOperation(),
            new AddResistanceOperation(),
            new AddWeaknessOperation(),
            new SetFlagOperation(),
            new DelayIncomingOperation(),
            new EditMemoryOperation(),
            new CreatePersistentEffectOperation(),
            new SetBehaviorOperation(),
            new CreateFlowOperation(),
            new AnimateEntityOperation(),
            new DispelMagicOperation(),
            new RevealTruthOperation(),
            };
        return Build(operations, OperationCardLoader.LoadDefaultContentCards());
    }
}
