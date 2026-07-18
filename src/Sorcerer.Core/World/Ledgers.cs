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
    IReadOnlyDictionary<string, int> FactionFirstReactions,
    // The soul's learned charter repertoire (docs/CHARTER_MAGIC.md): spell ids, gained
    // diegetically. Soul-bound so body swap carries it; null-tolerant so pre-charter saves
    // load cleanly.
    IReadOnlyList<string>? KnownCharterSpells = null);

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
            KnownCharterSpells = record.KnownCharterSpells?.ToArray() ?? Array.Empty<string>(),
        };

    public IReadOnlyList<string> KnownCharterSpellsFor(string soulId) =>
        _records.TryGetValue(soulId, out var record)
            ? record.KnownCharterSpells ?? Array.Empty<string>()
            : Array.Empty<string>();

    public bool KnowsCharterSpell(string soulId, string spellId) =>
        KnownCharterSpellsFor(soulId).Contains(spellId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Adds a charter spell to the soul's repertoire; false if already known or no such soul.</summary>
    public bool LearnCharterSpell(string soulId, string spellId)
    {
        if (string.IsNullOrWhiteSpace(spellId)
            || !_records.TryGetValue(soulId, out var record)
            || KnowsCharterSpell(soulId, spellId))
        {
            return false;
        }

        _records[soulId] = record with
        {
            KnownCharterSpells = (record.KnownCharterSpells ?? Array.Empty<string>())
                .Append(spellId.Trim())
                .ToArray(),
        };
        return true;
    }

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
    string AttributionStatus = "attributed",
    string? Summary = null);

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
        string attributionStatus = "attributed",
        string? summary = null)
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
            attributionStatus,
            summary);
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

    public IReadOnlyList<FactionRecord> FactionsByRole(string role) =>
        _factions.Values
            .Where(faction => faction.Role.Equals(role, StringComparison.OrdinalIgnoreCase))
            .OrderBy(faction => faction.Id)
            .ToArray();

    public string RoleOf(string factionId) =>
        _factions.TryGetValue(factionId, out var faction) ? faction.Role : "unknown";

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

public sealed record ClaimRecord(
    string Id,
    int Turn,
    string Source,
    string SpeakerId,
    string ListenerSoulId,
    string Text,
    string Category,
    string Subject,
    int Salience,
    int Confidence,
    string Status,
    bool PlayerVisible,
    IReadOnlyList<string> Tags,
    string? BoundPromiseId = null,
    string? AppliedTo = null);

public sealed class ClaimLedger
{
    private readonly List<ClaimRecord> _records = new();

    public IReadOnlyList<ClaimRecord> Records => _records;

    public ClaimRecord Append(
        int turn,
        string source,
        string speakerId,
        string listenerSoulId,
        string text,
        string category,
        string subject,
        int salience,
        int confidence,
        bool playerVisible,
        IEnumerable<string>? tags = null,
        string status = "reported",
        string? boundPromiseId = null,
        string? appliedTo = null)
    {
        var record = new ClaimRecord(
            $"claim_{_records.Count + 1}",
            turn,
            string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim(),
            string.IsNullOrWhiteSpace(speakerId) ? "unknown" : speakerId.Trim(),
            string.IsNullOrWhiteSpace(listenerSoulId) ? "unknown" : listenerSoulId.Trim(),
            text.Trim(),
            string.IsNullOrWhiteSpace(category) ? "memory" : category.Trim(),
            string.IsNullOrWhiteSpace(subject) ? text.Trim() : subject.Trim(),
            Math.Clamp(salience, 1, 5),
            Math.Clamp(confidence, 0, 100),
            string.IsNullOrWhiteSpace(status) ? "reported" : status.Trim(),
            playerVisible,
            (tags ?? Array.Empty<string>())
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(tag => tag, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            string.IsNullOrWhiteSpace(boundPromiseId) ? null : boundPromiseId.Trim(),
            string.IsNullOrWhiteSpace(appliedTo) ? null : appliedTo.Trim());
        _records.Add(record);
        return record;
    }

    public ClaimRecord? Update(string id, string? status = null, string? boundPromiseId = null, string? appliedTo = null)
    {
        var index = _records.FindIndex(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var existing = _records[index];
        var updated = existing with
        {
            Status = string.IsNullOrWhiteSpace(status) ? existing.Status : status.Trim(),
            BoundPromiseId = string.IsNullOrWhiteSpace(boundPromiseId) ? existing.BoundPromiseId : boundPromiseId.Trim(),
            AppliedTo = string.IsNullOrWhiteSpace(appliedTo) ? existing.AppliedTo : appliedTo.Trim(),
        };
        _records[index] = updated;
        return updated;
    }

    public IReadOnlyList<ClaimRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<ClaimRecord> records)
    {
        _records.Clear();
        _records.AddRange(records);
    }
}

public sealed record RumorRecord(
    string Id,
    int CreatedTurn,
    int LastTurn,
    string SourceKind,
    string SourceId,
    string OriginRegionId,
    string CurrentRegionId,
    string Text,
    string OriginalText,
    int Salience,
    string Status,
    IReadOnlyList<string> CarrierIds,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> DistortionHistory,
    int Hops);

public sealed class RumorLedger
{
    private readonly List<RumorRecord> _records = new();

    public IReadOnlyList<RumorRecord> Records => _records;

    public RumorRecord Append(
        int turn,
        string sourceKind,
        string sourceId,
        string originRegionId,
        string currentRegionId,
        string text,
        int salience,
        IEnumerable<string>? carrierIds = null,
        IEnumerable<string>? tags = null,
        string status = "active",
        IEnumerable<string>? distortionHistory = null,
        int hops = 0,
        string? originalText = null)
    {
        var record = new RumorRecord(
            $"rumor_{_records.Count + 1}",
            turn,
            turn,
            NormalizeToken(sourceKind, "unknown"),
            NormalizeToken(sourceId, "unknown"),
            NormalizeToken(originRegionId, "unknown"),
            NormalizeToken(currentRegionId, "unknown"),
            text.Trim(),
            string.IsNullOrWhiteSpace(originalText) ? text.Trim() : originalText.Trim(),
            Math.Clamp(salience, 1, 5),
            NormalizeToken(status, "active"),
            NormalizeList(carrierIds),
            NormalizeList(tags),
            NormalizeHistoryList(distortionHistory),
            Math.Max(0, hops));
        _records.Add(record);
        return record;
    }

    public bool HasSource(string sourceKind, string sourceId) =>
        _records.Any(record =>
            record.SourceKind.Equals(sourceKind, StringComparison.OrdinalIgnoreCase)
            && record.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase));

    public RumorRecord? Replace(RumorRecord record)
    {
        var index = _records.FindIndex(existing => existing.Id.Equals(record.Id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        _records[index] = Normalize(record);
        return _records[index];
    }

    public IReadOnlyList<RumorRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<RumorRecord> records)
    {
        _records.Clear();
        foreach (var record in records)
        {
            _records.Add(Normalize(record));
        }
    }

    private static RumorRecord Normalize(RumorRecord record) =>
        record with
        {
            SourceKind = NormalizeToken(record.SourceKind, "unknown"),
            SourceId = NormalizeToken(record.SourceId, "unknown"),
            OriginRegionId = NormalizeToken(record.OriginRegionId, "unknown"),
            CurrentRegionId = NormalizeToken(record.CurrentRegionId, record.OriginRegionId),
            Text = record.Text.Trim(),
            OriginalText = string.IsNullOrWhiteSpace(record.OriginalText) ? record.Text.Trim() : record.OriginalText.Trim(),
            Salience = Math.Clamp(record.Salience, 1, 5),
            Status = NormalizeToken(record.Status, "active"),
            CarrierIds = NormalizeList(record.CarrierIds),
            Tags = NormalizeList(record.Tags),
            DistortionHistory = NormalizeHistoryList(record.DistortionHistory),
            Hops = Math.Max(0, record.Hops),
        };

    private static string NormalizeToken(string text, string fallback)
    {
        var chars = (text ?? "")
            .Trim()
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join(
            "_",
            new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string>? values) =>
        (values ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static IReadOnlyList<string> NormalizeHistoryList(IEnumerable<string>? values)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();
        foreach (var value in values ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var trimmed = value.Trim();
            if (seen.Add(trimmed))
            {
                normalized.Add(trimmed);
            }
        }

        return normalized.ToArray();
    }
}

public sealed record WorldTurnRecord(
    string Id,
    int Turn,
    string Reason,
    string Kind,
    string SourceId,
    string Summary,
    IReadOnlyDictionary<string, object?> Details);

public sealed class WorldTurnLedger
{
    private const string IdPrefix = "world_turn_";
    private const int MaxRecords = 160;

    private readonly List<WorldTurnRecord> _records = new();
    private int _nextSerial;

    public IReadOnlyList<WorldTurnRecord> Records => _records;

    public WorldTurnRecord Add(
        int turn,
        string reason,
        string kind,
        string sourceId,
        string summary,
        IReadOnlyDictionary<string, object?>? details = null)
    {
        var record = new WorldTurnRecord(
            $"{IdPrefix}{++_nextSerial}",
            turn,
            Clean(reason, "turn"),
            Clean(kind, "move"),
            Clean(sourceId, "unknown"),
            summary.Trim(),
            NormalizeDetails(details));
        _records.Add(record);
        if (_records.Count > MaxRecords)
        {
            _records.RemoveRange(0, _records.Count - MaxRecords);
        }

        return record;
    }

    public bool HasRecent(string kind, string sourceId, int currentTurn, int cooldownTurns) =>
        _records.Any(record =>
            record.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
            && record.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase)
            && currentTurn - record.Turn < cooldownTurns);

    public IReadOnlyList<WorldTurnRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<WorldTurnRecord> records)
    {
        _records.Clear();
        _records.AddRange(records.Select(record => record with
        {
            Reason = Clean(record.Reason, "turn"),
            Kind = Clean(record.Kind, "move"),
            SourceId = Clean(record.SourceId, "unknown"),
            Summary = record.Summary.Trim(),
            Details = NormalizeDetails(record.Details),
        }));
        _nextSerial = Math.Max(_nextSerial, LedgerIds.HighestSerial(_records.Select(record => record.Id), IdPrefix));
    }

    private static string Clean(string text, string fallback) =>
        string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

    private static IReadOnlyDictionary<string, object?> NormalizeDetails(IReadOnlyDictionary<string, object?>? details) =>
        details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : details
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
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

/// <summary>
/// A ledger id is minted as "{prefix}_{serial}". Counting live records to pick the next serial
/// collides once removals keep the count below the historical high-water mark (e.g. a fixed-size
/// ledger that prunes old entries, or any ledger that removes fired/consumed records) -- a new
/// record then reuses an existing id. Ledgers that mint ids this way should track a monotonic
/// serial instead of deriving it from Count, and use <see cref="HighestSerial"/> to resume that
/// counter correctly after ReplaceAll (rollback restore or save/load).
/// </summary>
internal static class LedgerIds
{
    public static int HighestSerial(IEnumerable<string> ids, string prefix)
    {
        var max = 0;
        foreach (var id in ids)
        {
            if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && int.TryParse(id.AsSpan(prefix.Length), out var serial)
                && serial > max)
            {
                max = serial;
            }
        }

        return max;
    }
}

public sealed record ScheduledEventRecord(
    string Id,
    int DueTurn,
    string Kind,
    EntityId? SourceEntityId,
    IReadOnlyDictionary<string, object?> Payload);

public sealed class ScheduledEventLedger
{
    private const string IdPrefix = "event_";

    private readonly List<ScheduledEventRecord> _events = new();
    private int _nextSerial;

    public IReadOnlyList<ScheduledEventRecord> Events => _events;

    public ScheduledEventRecord Schedule(
        int dueTurn,
        string kind,
        EntityId? sourceEntityId,
        IReadOnlyDictionary<string, object?> payload)
    {
        var record = new ScheduledEventRecord(
            $"{IdPrefix}{++_nextSerial}",
            dueTurn,
            kind,
            sourceEntityId,
            payload);
        _events.Add(record);
        return record;
    }

    public IReadOnlyList<ScheduledEventRecord> Due(int turn) =>
        _events
            .Where(record => record.DueTurn <= turn)
            .OrderBy(record => record.DueTurn)
            .ThenBy(record => record.Id)
            .ToArray();

    public void Remove(string id) =>
        _events.RemoveAll(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<ScheduledEventRecord> Snapshot() => _events.ToArray();

    public void ReplaceAll(IEnumerable<ScheduledEventRecord> records)
    {
        _events.Clear();
        _events.AddRange(records);
        _nextSerial = Math.Max(_nextSerial, LedgerIds.HighestSerial(_events.Select(record => record.Id), IdPrefix));
    }
}

/// <summary>
/// One remembered wild cast (docs/SPELL_ECHOES.md): the recorded resolution can be re-fed
/// through the ordinary validate/apply pipeline instantly, with no model call. Soul-bound:
/// the repertoire is a within-run personal tradition and dies with the run.
/// </summary>
public sealed record EchoRecord(
    string Id,
    string Name,
    string SpellText,
    string ResolvedMagicJson,
    IReadOnlyList<string> EffectTypes,
    string SoulId,
    int CreatedTurn,
    int TimesCast);

public sealed class EchoLedger
{
    private readonly List<EchoRecord> _records = new();

    public IReadOnlyList<EchoRecord> Records => _records;

    public IReadOnlyList<EchoRecord> ForSoul(string soulId) =>
        _records.Where(record => record.SoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// Records an accepted cast. Re-casting the same phrase updates the existing echo's
    /// resolution (the freshest version of the tradition) instead of minting a duplicate.
    /// </summary>
    public EchoRecord Record(
        string spellText,
        string resolvedMagicJson,
        IReadOnlyList<string> effectTypes,
        string soulId,
        int turn)
    {
        var existing = _records.FindIndex(record =>
            record.SoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase)
            && record.SpellText.Equals(spellText, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            var updated = _records[existing] with
            {
                ResolvedMagicJson = resolvedMagicJson,
                EffectTypes = effectTypes.ToArray(),
            };
            _records[existing] = updated;
            return updated;
        }

        var record = new EchoRecord(
            $"echo_{_records.Count + 1}",
            EchoName(spellText),
            spellText,
            resolvedMagicJson,
            effectTypes.ToArray(),
            soulId,
            turn,
            TimesCast: 0);
        _records.Add(record);
        return record;
    }

    /// <summary>Finds a soul's echo by 1-based grimoire number, id, or name/text fragment.</summary>
    public EchoRecord? Find(string reference, string soulId)
    {
        var mine = ForSoul(soulId);
        var trimmed = (reference ?? "").Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (int.TryParse(trimmed, out var index) && index >= 1 && index <= mine.Count)
        {
            return mine[index - 1];
        }

        return mine.FirstOrDefault(record => record.Id.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? mine.FirstOrDefault(record => record.Name.Equals(trimmed, StringComparison.OrdinalIgnoreCase))
            ?? mine.FirstOrDefault(record => record.SpellText.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
    }

    public void IncrementCast(string id)
    {
        var index = _records.FindIndex(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _records[index] = _records[index] with { TimesCast = _records[index].TimesCast + 1 };
        }
    }

    public IReadOnlyList<EchoRecord> Snapshot() => _records.ToArray();

    public void ReplaceAll(IEnumerable<EchoRecord>? records)
    {
        _records.Clear();
        if (records is null)
        {
            return;
        }

        foreach (var record in records)
        {
            _records.Add(record with { EffectTypes = record.EffectTypes?.ToArray() ?? Array.Empty<string>() });
        }
    }

    private static string EchoName(string spellText)
    {
        var words = spellText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length <= 5 ? spellText.Trim() : string.Join(' ', words.Take(5)) + "...";
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

/// <summary>
/// An effect anchored to an entity that fires on a combat event (<c>on_hit</c> when the anchor is
/// struck, <c>on_strike</c> when the anchor lands a hit) rather than on turn cadence, unlike
/// <see cref="TriggerRecord"/>. A non-null <see cref="LinkPartnerId"/> marks a sympathetic link:
/// a fraction of damage the anchor takes is mirrored onto the partner.
/// </summary>
public sealed record PersistentEffectRecord(
    string Id,
    string AnchorEntityId,
    string Hook,
    string EffectType,
    IReadOnlyDictionary<string, object?> EffectFields,
    int RemainingUses,
    string? LinkPartnerId,
    bool PlayerVisible);

public sealed class PersistentEffectLedger
{
    private readonly List<PersistentEffectRecord> _records = new();

    public IReadOnlyList<PersistentEffectRecord> Records => _records;

    public PersistentEffectRecord Add(
        string anchorEntityId,
        string hook,
        string effectType,
        IReadOnlyDictionary<string, object?> effectFields,
        int uses,
        string? linkPartnerId,
        bool playerVisible)
    {
        var record = new PersistentEffectRecord(
            $"persistent_{_records.Count + 1}",
            anchorEntityId,
            string.IsNullOrWhiteSpace(hook) ? "on_hit" : hook.Trim(),
            string.IsNullOrWhiteSpace(effectType) ? "message" : effectType.Trim(),
            new Dictionary<string, object?>(effectFields, StringComparer.OrdinalIgnoreCase),
            Math.Max(1, uses),
            string.IsNullOrWhiteSpace(linkPartnerId) ? null : linkPartnerId.Trim(),
            playerVisible);
        _records.Add(record);
        return record;
    }

    public IReadOnlyList<PersistentEffectRecord> ForAnchorAndHook(string anchorEntityId, string hook) =>
        _records
            .Where(record => record.RemainingUses > 0
                && record.Hook.Equals(hook, StringComparison.OrdinalIgnoreCase)
                && record.AnchorEntityId.Equals(anchorEntityId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public void Consume(string id)
    {
        var index = _records.FindIndex(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return;
        }

        var remaining = _records[index].RemainingUses - 1;
        if (remaining <= 0)
        {
            _records.RemoveAt(index);
        }
        else
        {
            _records[index] = _records[index] with { RemainingUses = remaining };
        }
    }

    public void Replace(PersistentEffectRecord record)
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

    public IReadOnlyList<PersistentEffectRecord> Snapshot() =>
        _records
            .Select(record => record with
            {
                EffectFields = new Dictionary<string, object?>(record.EffectFields, StringComparer.OrdinalIgnoreCase),
            })
            .ToArray();

    public void ReplaceAll(IEnumerable<PersistentEffectRecord> records)
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
