using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record DeedRecord(
    string Id,
    int Turn,
    string ActorSoulId,
    string Kind,
    int Magnitude,
    string PlaceKey,
    string Visibility,
    IReadOnlyList<string> Witnesses,
    IReadOnlyList<string> Tags);

public sealed class DeedLedger
{
    private readonly List<DeedRecord> _records = new();

    public IReadOnlyList<DeedRecord> Records => _records;

    public DeedRecord Append(
        int turn,
        string actorSoulId,
        string kind,
        int magnitude,
        string placeKey,
        string visibility,
        IEnumerable<string>? witnesses = null,
        IEnumerable<string>? tags = null)
    {
        var record = new DeedRecord(
            $"deed_{_records.Count + 1}",
            turn,
            actorSoulId,
            kind,
            magnitude,
            placeKey,
            visibility,
            (witnesses ?? Array.Empty<string>()).ToArray(),
            (tags ?? Array.Empty<string>()).ToArray());
        _records.Add(record);
        return record;
    }
}

public sealed record FactionRecord(
    string Id,
    string Name,
    string Role,
    Dictionary<string, int> Standing,
    Dictionary<string, int> Resources);

public sealed class FactionLedger
{
    private readonly Dictionary<string, FactionRecord> _factions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FactionRecord> Factions => _factions.Values;

    public FactionRecord AddOrGet(string id, string name, string role)
    {
        if (_factions.TryGetValue(id, out var existing))
        {
            return existing;
        }

        var faction = new FactionRecord(
            id,
            name,
            role,
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        _factions[id] = faction;
        return faction;
    }

    public void AdjustStanding(string factionId, string axis, int delta)
    {
        var faction = AddOrGet(factionId, factionId, "unknown");
        faction.Standing.TryGetValue(axis, out var current);
        faction.Standing[axis] = current + delta;
    }
}

public sealed record LegendTag(
    string ActorSoulId,
    string Tag,
    int Weight,
    string Source);

public sealed class LegendLedger
{
    private readonly List<LegendTag> _tags = new();

    public IReadOnlyList<LegendTag> Tags => _tags;

    public void Add(string actorSoulId, string tag, int weight, string source) =>
        _tags.Add(new LegendTag(actorSoulId, tag, weight, source));
}

public sealed record WorldMemoryRecord(
    string Id,
    string SubjectId,
    string Text,
    string Provenance,
    int Salience,
    bool Shareable);

public sealed class MemoryLedger
{
    private readonly List<WorldMemoryRecord> _records = new();

    public IReadOnlyList<WorldMemoryRecord> Records => _records;

    public WorldMemoryRecord Append(
        string subjectId,
        string text,
        string provenance,
        int salience,
        bool shareable)
    {
        var record = new WorldMemoryRecord(
            $"memory_{_records.Count + 1}",
            subjectId,
            text,
            provenance,
            salience,
            shareable);
        _records.Add(record);
        return record;
    }
}

public sealed record CanonRecord(
    string Id,
    string Kind,
    string AttachedTo,
    string Text,
    string Summary,
    IReadOnlyList<string> Tags,
    string Source,
    int TurnCreated);

public sealed class CanonLedger
{
    private readonly List<CanonRecord> _records = new();

    public IReadOnlyList<CanonRecord> Records => _records;

    public CanonRecord Add(
        string kind,
        string attachedTo,
        string text,
        string summary,
        IEnumerable<string>? tags,
        string source,
        int turnCreated)
    {
        var record = new CanonRecord(
            $"canon_{_records.Count + 1}",
            kind,
            attachedTo,
            text,
            summary,
            (tags ?? Array.Empty<string>()).ToArray(),
            source,
            turnCreated);
        _records.Add(record);
        return record;
    }

    public IReadOnlyList<CanonRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<CanonRecord> records)
    {
        _records.Clear();
        _records.AddRange(records);
    }
}

public sealed record BondRecord(
    string SubjectSoulId,
    string TargetSoulId,
    int Loyalty,
    int Fear,
    int Admiration,
    int Resentment,
    string Posture);

public sealed class BondLedger
{
    private readonly Dictionary<string, BondRecord> _bonds = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<BondRecord> Bonds => _bonds.Values;

    public void Set(BondRecord bond) =>
        _bonds[$"{bond.SubjectSoulId}->{bond.TargetSoulId}"] = bond;
}

public sealed record ScheduledEventRecord(
    string Id,
    int DueTurn,
    string Kind,
    EntityId? SourceEntityId,
    IReadOnlyDictionary<string, object?> Payload);

public sealed class ScheduledEventLedger
{
    private readonly List<ScheduledEventRecord> _events = new();

    public IReadOnlyList<ScheduledEventRecord> Events => _events;

    public ScheduledEventRecord Schedule(
        int dueTurn,
        string kind,
        EntityId? sourceEntityId,
        IReadOnlyDictionary<string, object?> payload)
    {
        var record = new ScheduledEventRecord(
            $"event_{_events.Count + 1}",
            dueTurn,
            kind,
            sourceEntityId,
            payload);
        _events.Add(record);
        return record;
    }

    public IReadOnlyList<ScheduledEventRecord> PopDue(int turn)
    {
        var due = _events
            .Where(record => record.DueTurn <= turn)
            .OrderBy(record => record.DueTurn)
            .ThenBy(record => record.Id)
            .ToArray();
        if (due.Length == 0)
        {
            return due;
        }

        foreach (var record in due)
        {
            _events.Remove(record);
        }

        return due;
    }

    public IReadOnlyList<ScheduledEventRecord> Snapshot() => _events.ToArray();

    public void ReplaceAll(IEnumerable<ScheduledEventRecord> records)
    {
        _events.Clear();
        _events.AddRange(records);
    }
}
