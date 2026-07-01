using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record SoulRecord(
    string SoulId,
    SoulStatsComponent Stats,
    int Mana,
    int MaxMana,
    string OriginId,
    string OriginName,
    string Tradition,
    string MagicalSignature,
    string Backstory,
    IReadOnlyDictionary<string, int> FactionFirstReactions);

public sealed class SoulLedger
{
    private readonly Dictionary<string, SoulRecord> _records = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<SoulRecord> Records => _records.Values;

    public bool TryGet(string soulId, out SoulRecord record) =>
        _records.TryGetValue(soulId, out record!);

    public SoulRecord Get(string soulId) => _records[soulId];

    public void Set(SoulRecord record) =>
        _records[record.SoulId] = record with
        {
            FactionFirstReactions = new Dictionary<string, int>(
                record.FactionFirstReactions,
                StringComparer.OrdinalIgnoreCase),
        };

    public void AdjustMana(string soulId, int mana, int maxMana)
    {
        if (!_records.TryGetValue(soulId, out var record))
        {
            return;
        }

        _records[soulId] = record with
        {
            Mana = Math.Clamp(mana, 0, Math.Max(0, maxMana)),
            MaxMana = Math.Max(0, maxMana),
        };
    }

    public IReadOnlyList<SoulRecord> Snapshot() => _records.Values.ToArray();

    public void ReplaceAll(IEnumerable<SoulRecord> records)
    {
        _records.Clear();
        foreach (var record in records)
        {
            Set(record);
        }
    }
}

public sealed record DeedRecord(
    string Id,
    int Turn,
    string ActorSoulId,
    string Kind,
    int Magnitude,
    string PlaceKey,
    string Visibility,
    IReadOnlyList<string> Witnesses,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string>? EffectWitnesses = null,
    string? AttributedSoulId = null,
    string AttributionStatus = "attributed");

public sealed class DeedLedger
{
    private readonly List<DeedRecord> _records = new();
    private readonly HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<DeedRecord> Records => _records;

    public IReadOnlySet<string> AppliedIds => _applied;

    public DeedRecord Append(
        int turn,
        string actorSoulId,
        string kind,
        int magnitude,
        string placeKey,
        string visibility,
        IEnumerable<string>? witnesses = null,
        IEnumerable<string>? tags = null,
        IEnumerable<string>? effectWitnesses = null,
        string? attributedSoulId = null,
        string attributionStatus = "attributed")
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
            (tags ?? Array.Empty<string>()).ToArray(),
            (effectWitnesses ?? Array.Empty<string>()).ToArray(),
            attributedSoulId ?? actorSoulId,
            attributionStatus);
        _records.Add(record);
        return record;
    }

    public bool IsApplied(string deedId) => _applied.Contains(deedId);

    public void MarkApplied(string deedId) => _applied.Add(deedId);

    public IReadOnlyList<string> AppliedSnapshot() => _applied.ToArray();

    public void ReplaceAll(IEnumerable<DeedRecord> records, IEnumerable<string>? appliedIds = null)
    {
        _records.Clear();
        _records.AddRange(records);
        _applied.Clear();
        foreach (var id in appliedIds ?? Array.Empty<string>())
        {
            _applied.Add(id);
        }
    }
}

public sealed record FactionRecord(
    string Id,
    string Name,
    string Role,
    Dictionary<string, int> Standing,
    Dictionary<string, int> Resources,
    IReadOnlyList<string> HostileRoles);

public sealed class FactionLedger
{
    private readonly Dictionary<string, FactionRecord> _factions = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<FactionRecord> Factions => _factions.Values;

    public FactionRecord AddOrGet(
        string id,
        string name,
        string role,
        IReadOnlyDictionary<string, int>? resources = null,
        IEnumerable<string>? hostileRoles = null)
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
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase),
            NormalizeRoles(hostileRoles));
        foreach (var pair in resources ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase))
        {
            faction.Resources[pair.Key] = pair.Value;
        }

        _factions[id] = faction;
        return faction;
    }

    public void Set(FactionRecord faction) =>
        _factions[faction.Id] = Clone(faction);

    public void AdjustStanding(string factionId, string axis, int delta)
    {
        var faction = AddOrGet(factionId, factionId, "unknown");
        faction.Standing.TryGetValue(axis, out var current);
        faction.Standing[axis] = current + delta;
    }

    public void AdjustStandingByRole(string role, string axis, int delta)
    {
        foreach (var faction in FactionsByRole(role))
        {
            AdjustStanding(faction.Id, axis, delta);
        }
    }

    public int StandingValue(string factionId, string axis) =>
        _factions.TryGetValue(factionId, out var faction)
        && faction.Standing.TryGetValue(axis, out var value)
            ? value
            : 0;

    public int ResourceValue(string factionId, string resource) =>
        _factions.TryGetValue(factionId, out var faction)
        && faction.Resources.TryGetValue(resource, out var value)
            ? value
            : 0;

    public void AdjustResource(string factionId, string resource, int delta, int min = 0, int? max = null)
    {
        var faction = AddOrGet(factionId, factionId, "unknown");
        faction.Resources.TryGetValue(resource, out var current);
        var adjusted = current + delta;
        if (max is not null)
        {
            adjusted = Math.Min(adjusted, max.Value);
        }

        faction.Resources[resource] = Math.Max(min, adjusted);
    }

    public bool TrySpendResource(string factionId, string resource, int amount)
    {
        var faction = AddOrGet(factionId, factionId, "unknown");
        faction.Resources.TryGetValue(resource, out var current);
        if (current < amount)
        {
            return false;
        }

        faction.Resources[resource] = current - amount;
        return true;
    }

    public IReadOnlyList<FactionRecord> FactionsByRole(string role) =>
        _factions.Values
            .Where(faction => faction.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
            .OrderBy(faction => faction.Id)
            .ToArray();

    public bool IsHostile(string actorFactionId, string targetFactionId)
    {
        if (actorFactionId.Equals(targetFactionId, StringComparison.OrdinalIgnoreCase)
            || targetFactionId.Equals("neutral", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var actor = AddOrGet(actorFactionId, actorFactionId, "unknown");
        var target = AddOrGet(targetFactionId, targetFactionId, "unknown");
        if (actor.Standing.TryGetValue($"allied:{target.Id}", out var directAlliance) && directAlliance > 0)
        {
            return false;
        }

        if (actor.Standing.TryGetValue($"hostile:{target.Id}", out var directHostility) && directHostility > 0)
        {
            return true;
        }

        if (target.Role.Equals("player", StringComparison.OrdinalIgnoreCase))
        {
            if (actor.Standing.TryGetValue("player-alliance", out var alliance) && alliance > 0)
            {
                return false;
            }

            if (actor.Standing.TryGetValue("player-hostility", out var hostility) && hostility > 0)
            {
                return true;
            }
        }

        return actor.HostileRoles.Contains(target.Role, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<FactionRecord> Snapshot() =>
        _factions.Values.Select(Clone).ToArray();

    public void ReplaceAll(IEnumerable<FactionRecord> factions)
    {
        _factions.Clear();
        foreach (var faction in factions)
        {
            Set(faction);
        }
    }

    private static FactionRecord Clone(FactionRecord faction) =>
        faction with
        {
            Standing = new Dictionary<string, int>(faction.Standing, StringComparer.OrdinalIgnoreCase),
            Resources = new Dictionary<string, int>(faction.Resources, StringComparer.OrdinalIgnoreCase),
            HostileRoles = NormalizeRoles(faction.HostileRoles),
        };

    private static IReadOnlyList<string> NormalizeRoles(IEnumerable<string>? roles) =>
        (roles ?? Array.Empty<string>())
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(role => role)
            .ToArray();
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

    public IReadOnlyList<LegendTag> Snapshot() => _tags.ToArray();

    public void ReplaceAll(IEnumerable<LegendTag> tags)
    {
        _tags.Clear();
        _tags.AddRange(tags);
    }
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

    public IReadOnlyList<WorldMemoryRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<WorldMemoryRecord> records)
    {
        _records.Clear();
        _records.AddRange(records);
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

    public bool TryGet(string subjectSoulId, string targetSoulId, out BondRecord bond) =>
        _bonds.TryGetValue(Key(subjectSoulId, targetSoulId), out bond!);

    public BondRecord GetOrCreate(string subjectSoulId, string targetSoulId)
    {
        if (TryGet(subjectSoulId, targetSoulId, out var existing))
        {
            return existing;
        }

        var created = new BondRecord(
            subjectSoulId,
            targetSoulId,
            Loyalty: 0,
            Fear: 0,
            Admiration: 0,
            Resentment: 0,
            Posture: "neutral");
        Set(created);
        return created;
    }

    public BondRecord Adjust(
        string subjectSoulId,
        string targetSoulId,
        int loyalty = 0,
        int fear = 0,
        int admiration = 0,
        int resentment = 0,
        string? posture = null)
    {
        var current = GetOrCreate(subjectSoulId, targetSoulId);
        var updated = current with
        {
            Loyalty = ClampBond(current.Loyalty + loyalty),
            Fear = ClampBond(current.Fear + fear),
            Admiration = ClampBond(current.Admiration + admiration),
            Resentment = ClampBond(current.Resentment + resentment),
            Posture = string.IsNullOrWhiteSpace(posture) ? current.Posture : posture!,
        };
        Set(updated);
        return updated;
    }

    public IReadOnlyList<BondRecord> Snapshot() => _bonds.Values.ToArray();

    public void ReplaceAll(IEnumerable<BondRecord> bonds)
    {
        _bonds.Clear();
        foreach (var bond in bonds)
        {
            Set(bond);
        }
    }

    private static string Key(string subjectSoulId, string targetSoulId) =>
        $"{subjectSoulId}->{targetSoulId}";

    private static int ClampBond(int value) => Math.Clamp(value, -10, 10);
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

public sealed record TriggerRecord(
    string Id,
    string Name,
    string Kind,
    int CreatedTurn,
    int NextTurn,
    int Interval,
    int RemainingUses,
    int? ExpiresTurn,
    EntityId? SourceEntityId,
    string? AnchorEntityId,
    GridPoint? AnchorPoint,
    int Radius,
    string TargetFilter,
    string EffectType,
    IReadOnlyDictionary<string, object?> EffectFields,
    string Description,
    bool PlayerVisible);

public sealed class TriggerLedger
{
    private readonly List<TriggerRecord> _records = new();

    public IReadOnlyList<TriggerRecord> Records => _records;

    public TriggerRecord Add(
        string name,
        string kind,
        int createdTurn,
        int nextTurn,
        int interval,
        int remainingUses,
        int? expiresTurn,
        EntityId? sourceEntityId,
        string? anchorEntityId,
        GridPoint? anchorPoint,
        int radius,
        string targetFilter,
        string effectType,
        IReadOnlyDictionary<string, object?> effectFields,
        string description,
        bool playerVisible)
    {
        var record = new TriggerRecord(
            $"trigger_{_records.Count + 1}",
            string.IsNullOrWhiteSpace(name) ? kind : name.Trim(),
            string.IsNullOrWhiteSpace(kind) ? "delay" : kind.Trim(),
            createdTurn,
            nextTurn,
            Math.Max(1, interval),
            Math.Max(1, remainingUses),
            expiresTurn,
            sourceEntityId,
            string.IsNullOrWhiteSpace(anchorEntityId) ? null : anchorEntityId.Trim(),
            anchorPoint,
            Math.Max(0, radius),
            string.IsNullOrWhiteSpace(targetFilter) ? "all" : targetFilter.Trim(),
            string.IsNullOrWhiteSpace(effectType) ? "message" : effectType.Trim(),
            new Dictionary<string, object?>(effectFields, StringComparer.OrdinalIgnoreCase),
            description.Trim(),
            playerVisible);
        _records.Add(record);
        return record;
    }

    public IReadOnlyList<TriggerRecord> Due(int turn) =>
        _records
            .Where(record => record.NextTurn <= turn
                && record.RemainingUses > 0
                && (record.ExpiresTurn is null || record.ExpiresTurn >= turn))
            .OrderBy(record => record.NextTurn)
            .ThenBy(record => record.Id)
            .ToArray();

    public void Replace(TriggerRecord record)
    {
        var index = _records.FindIndex(existing => existing.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _records[index] = record with
            {
                EffectFields = new Dictionary<string, object?>(record.EffectFields, StringComparer.OrdinalIgnoreCase),
            };
        }
    }

    public void Remove(string id) =>
        _records.RemoveAll(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public void RemoveExpired(int turn)
    {
        _records.RemoveAll(record =>
            record.RemainingUses <= 0
            || (record.ExpiresTurn is not null && record.ExpiresTurn < turn));
    }

    public IReadOnlyList<TriggerRecord> Snapshot() =>
        _records
            .Select(record => record with
            {
                EffectFields = new Dictionary<string, object?>(record.EffectFields, StringComparer.OrdinalIgnoreCase),
            })
            .ToArray();

    public void ReplaceAll(IEnumerable<TriggerRecord> records)
    {
        _records.Clear();
        foreach (var record in records)
        {
            _records.Add(record with
            {
                EffectFields = new Dictionary<string, object?>(record.EffectFields, StringComparer.OrdinalIgnoreCase),
            });
        }
    }
}

public sealed record SuspicionRecord(
    string Id,
    int Turn,
    string WitnessSoulId,
    string Kind,
    GridPoint EffectPoint,
    string Status,
    string? SuspectedSoulId,
    int? AttributedTurn,
    int ExpiresTurn);

public sealed class SuspicionLedger
{
    private readonly List<SuspicionRecord> _records = new();

    public IReadOnlyList<SuspicionRecord> Records => _records;

    public SuspicionRecord Append(
        int turn,
        string witnessSoulId,
        string kind,
        GridPoint effectPoint,
        string status,
        string? suspectedSoulId,
        int? attributedTurn,
        int expiresTurn)
    {
        var record = new SuspicionRecord(
            $"suspicion_{_records.Count + 1}",
            turn,
            witnessSoulId,
            kind,
            effectPoint,
            status,
            suspectedSoulId,
            attributedTurn,
            expiresTurn);
        _records.Add(record);
        return record;
    }

    public void Replace(SuspicionRecord record)
    {
        var index = _records.FindIndex(existing =>
            existing.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _records[index] = record;
        }
    }

    public IReadOnlyList<SuspicionRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<SuspicionRecord> records)
    {
        _records.Clear();
        _records.AddRange(records);
    }
}
