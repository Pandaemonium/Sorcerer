using System.Text.Json;
using System.Text.Json.Serialization;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Persistence;

public static class GameSaveService
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(
        GameState state,
        PendingCastSave? pendingCast = null,
        int pendingCastSerial = 0,
        DateTimeOffset? savedAt = null)
    {
        var envelope = new GameSaveEnvelope(
            CurrentSchemaVersion,
            savedAt ?? DateTimeOffset.UtcNow,
            GameStateSave.FromState(state),
            pendingCast,
            Math.Max(0, pendingCastSerial));
        return JsonSerializer.Serialize(envelope, JsonOptions);
    }

    public static LoadedGameSave Deserialize(string json)
    {
        var envelope = JsonSerializer.Deserialize<GameSaveEnvelope>(json, JsonOptions)
            ?? throw new InvalidDataException("Save file is empty or malformed.");
        if (envelope.SchemaVersion != CurrentSchemaVersion)
        {
            throw new NotSupportedException($"Unsupported save schema version {envelope.SchemaVersion}.");
        }

        return new LoadedGameSave(
            envelope.State.ToState(),
            envelope.PendingCast,
            Math.Max(0, envelope.PendingCastSerial));
    }

    public static void Save(
        string path,
        GameState state,
        PendingCastSave? pendingCast = null,
        int pendingCastSerial = 0)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, Serialize(state, pendingCast, pendingCastSerial));
    }

    public static LoadedGameSave Load(string path) =>
        Deserialize(File.ReadAllText(path));

    internal static Dictionary<string, object?> NormalizeMap(IReadOnlyDictionary<string, object?>? source) =>
        source is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : source
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    pair => pair.Key,
                    pair => NormalizeValue(pair.Value),
                    StringComparer.OrdinalIgnoreCase);

    internal static object? NormalizeValue(object? value)
    {
        if (value is JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        property => property.Name,
                        property => NormalizeValue(property.Value),
                        StringComparer.OrdinalIgnoreCase),
                JsonValueKind.Array => element.EnumerateArray().Select(item => NormalizeValue(item)).ToArray(),
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number when element.TryGetInt32(out var integer) => integer,
                JsonValueKind.Number when element.TryGetInt64(out var longInteger) => longInteger,
                JsonValueKind.Number => element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null,
            };
        }

        if (value is IReadOnlyDictionary<string, object?> readOnlyMap)
        {
            return NormalizeMap(readOnlyMap);
        }

        if (value is IDictionary<string, object?> map)
        {
            return NormalizeMap(map.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase));
        }

        if (value is IEnumerable<object?> values && value is not string)
        {
            return values.Select(NormalizeValue).ToArray();
        }

        return value;
    }
}

public sealed record GameSaveEnvelope(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    GameStateSave State,
    PendingCastSave? PendingCast,
    int PendingCastSerial);

public sealed record LoadedGameSave(
    GameState State,
    PendingCastSave? PendingCast,
    int PendingCastSerial);

public sealed record PendingCastSave(
    string Id,
    string Text,
    CastPerformance? Performance,
    MaterializedMagicResolution? Resolution = null);

public sealed record GameStateSave(
    int Width,
    int Height,
    int Turn,
    int Seed,
    ulong RngState,
    string RegionId,
    string CurrentZoneId,
    string RunStatus,
    string? RunConclusion,
    int NextEntitySerial,
    string ControlledEntityId,
    PointSave? SelectedTarget,
    List<EntitySave> Entities,
    List<ZoneSave> Zones,
    List<PointSave> BlockingTerrain,
    List<TerrainSave> Terrain,
    List<TerrainExpirationSave> TerrainExpirations,
    List<ExploredSave> ExploredBySoulId,
    List<string> Messages,
    List<SoulRecord> Souls,
    List<DeedRecord> Deeds,
    List<string> AppliedDeedIds,
    List<FactionRecord> Factions,
    List<LegendTag> LegendTags,
    List<WorldMemoryRecord> Memories,
    List<ClaimRecord> Claims,
    List<RumorRecord> Rumors,
    List<WorldTurnRecord> WorldTurns,
    List<WorldPromise> Promises,
    List<ScheduledEventRecord> ScheduledEvents,
    List<TriggerRecord> Triggers,
    List<SuspicionRecord> Suspicions,
    List<CanonRecord> CanonRecords,
    List<BondRecord> Bonds,
    BackgroundJobSettings BackgroundSettings,
    List<BackgroundJob> BackgroundJobs,
    Dictionary<string, object?>? WorldFlags = null,
    List<PersistentEffectRecord>? PersistentEffects = null,
    PointSave? LastControlledMoveDelta = null,
    List<TileFlowSave>? TileFlows = null,
    List<EchoRecord>? Echoes = null)
{
    public static GameStateSave FromState(GameState state) =>
        new(
            state.Width,
            state.Height,
            state.Turn,
            state.Seed,
            state.Rng.State,
            state.RegionId,
            state.CurrentZoneId,
            state.RunStatus,
            state.RunConclusion,
            state.NextEntitySerial,
            state.ControlledEntityId.Value,
            state.SelectedTarget is null ? null : PointSave.From(state.SelectedTarget.Value),
            state.Entities.Values
                .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
                .Select(EntitySave.FromEntity)
                .ToList(),
            state.Zones
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => ZoneSave.FromZone(pair.Value))
                .ToList(),
            state.BlockingTerrain
                .OrderBy(point => point.Y)
                .ThenBy(point => point.X)
                .Select(PointSave.From)
                .ToList(),
            state.Terrain
                .OrderBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.X)
                .Select(pair => new TerrainSave(PointSave.From(pair.Key), pair.Value))
                .ToList(),
            state.TerrainExpirations
                .OrderBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.X)
                .Select(pair => new TerrainExpirationSave(PointSave.From(pair.Key), pair.Value))
                .ToList(),
            state.ExploredBySoulId
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ExploredSave(
                    pair.Key,
                    pair.Value
                        .OrderBy(point => point.Y)
                        .ThenBy(point => point.X)
                        .Select(PointSave.From)
                        .ToList()))
                .ToList(),
            state.Messages.ToList(),
            state.Souls.Snapshot().OrderBy(record => record.SoulId, StringComparer.OrdinalIgnoreCase).ToList(),
            state.Deeds.Records.ToList(),
            state.Deeds.AppliedSnapshot().OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
            state.Factions.Snapshot().OrderBy(faction => faction.Id, StringComparer.OrdinalIgnoreCase).ToList(),
            state.Legend.Snapshot().ToList(),
            state.Memories.Snapshot().ToList(),
            state.Claims.Snapshot().ToList(),
            state.Rumors.Snapshot().ToList(),
            state.WorldTurns.Snapshot()
                .Select(record => record with { Details = GameSaveService.NormalizeMap(record.Details) })
                .ToList(),
            state.PromiseLedger.Snapshot().ToList(),
            state.ScheduledEvents.Snapshot()
                .OrderBy(record => record.DueTurn)
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Select(record => record with { Payload = GameSaveService.NormalizeMap(record.Payload) })
                .ToList(),
            state.Triggers.Snapshot()
                .OrderBy(record => record.NextTurn)
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .Select(record => record with { EffectFields = GameSaveService.NormalizeMap(record.EffectFields) })
                .ToList(),
            state.Suspicions.Snapshot().ToList(),
            state.Canon.Snapshot().ToList(),
            state.Bonds.Snapshot()
                .OrderBy(record => record.SubjectSoulId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(record => record.TargetSoulId, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            state.BackgroundSettings,
            state.BackgroundJobs.Snapshot()
                .OrderBy(record => record.CreatedTurn)
                .ThenBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            GameSaveService.NormalizeMap(state.WorldFlags),
            state.PersistentEffects.Snapshot()
                .Select(record => record with { EffectFields = GameSaveService.NormalizeMap(record.EffectFields) })
                .OrderBy(record => record.Id, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            state.LastControlledMoveDelta is null ? null : PointSave.From(state.LastControlledMoveDelta.Value),
            state.TileFlows
                .OrderBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.X)
                .Select(pair => new TileFlowSave(PointSave.From(pair.Key), pair.Value.Dx, pair.Value.Dy, pair.Value.ExpiresTurn))
                .ToList(),
            state.Echoes.Snapshot().ToList());

    public GameState ToState()
    {
        var state = new GameState(Width, Height)
        {
            Turn = Turn,
            Seed = Math.Max(1, Seed),
            Rng = new DeterministicRng(RngState),
            RegionId = RegionId,
            CurrentZoneId = CurrentZoneId,
            RunStatus = string.IsNullOrWhiteSpace(RunStatus) ? "running" : RunStatus,
            RunConclusion = RunConclusion,
            NextEntitySerial = Math.Max(1, NextEntitySerial),
            ControlledEntityId = EntityId.Create(ControlledEntityId),
            SelectedTarget = SelectedTarget?.ToGridPoint(),
            LastControlledMoveDelta = LastControlledMoveDelta?.ToGridPoint(),
            BackgroundSettings = BackgroundSettings ?? new BackgroundJobSettings(),
        };

        foreach (var entity in Entities ?? new List<EntitySave>())
        {
            var restored = entity.ToEntity();
            state.Entities[restored.Id] = restored;
        }

        foreach (var zone in Zones ?? new List<ZoneSave>())
        {
            var restored = zone.ToZone();
            state.Zones[restored.ZoneId] = restored;
        }

        foreach (var point in BlockingTerrain ?? new List<PointSave>())
        {
            state.BlockingTerrain.Add(point.ToGridPoint());
        }

        foreach (var entry in Terrain ?? new List<TerrainSave>())
        {
            state.Terrain[entry.Point.ToGridPoint()] = entry.Value;
        }

        foreach (var entry in TerrainExpirations ?? new List<TerrainExpirationSave>())
        {
            state.TerrainExpirations[entry.Point.ToGridPoint()] = entry.ExpiresTurn;
        }

        foreach (var entry in ExploredBySoulId ?? new List<ExploredSave>())
        {
            state.ExploredBySoulId[entry.SoulId] = new HashSet<GridPoint>(
                (entry.Points ?? new List<PointSave>()).Select(point => point.ToGridPoint()));
        }

        state.Messages.AddRange(Messages ?? new List<string>());
        state.Souls.ReplaceAll(Souls ?? new List<SoulRecord>());
        state.Deeds.ReplaceAll(Deeds ?? new List<DeedRecord>(), AppliedDeedIds ?? new List<string>());
        state.Factions.ReplaceAll(Factions ?? new List<FactionRecord>());
        state.Legend.ReplaceAll(LegendTags ?? new List<LegendTag>());
        state.Memories.ReplaceAll(Memories ?? new List<WorldMemoryRecord>());
        state.Claims.ReplaceAll(Claims ?? new List<ClaimRecord>());
        state.Rumors.ReplaceAll(Rumors ?? new List<RumorRecord>());
        state.WorldTurns.ReplaceAll((WorldTurns ?? new List<WorldTurnRecord>())
            .Select(record => record with { Details = GameSaveService.NormalizeMap(record.Details) }));
        state.PromiseLedger.ReplaceAll(Promises ?? new List<WorldPromise>());
        state.ScheduledEvents.ReplaceAll((ScheduledEvents ?? new List<ScheduledEventRecord>())
            .Select(record => record with { Payload = GameSaveService.NormalizeMap(record.Payload) }));
        state.Triggers.ReplaceAll((Triggers ?? new List<TriggerRecord>())
            .Select(record => record with { EffectFields = GameSaveService.NormalizeMap(record.EffectFields) }));
        state.Suspicions.ReplaceAll(Suspicions ?? new List<SuspicionRecord>());
        state.Canon.ReplaceAll(CanonRecords ?? new List<CanonRecord>());
        state.Bonds.ReplaceAll(Bonds ?? new List<BondRecord>());
        state.BackgroundJobs.ReplaceAll(BackgroundJobs ?? new List<BackgroundJob>());
        foreach (var pair in GameSaveService.NormalizeMap(WorldFlags))
        {
            state.WorldFlags[pair.Key] = pair.Value;
        }

        state.PersistentEffects.ReplaceAll((PersistentEffects ?? new List<PersistentEffectRecord>())
            .Select(record => record with { EffectFields = GameSaveService.NormalizeMap(record.EffectFields) }));
        foreach (var flow in TileFlows ?? new List<TileFlowSave>())
        {
            state.TileFlows[flow.Point.ToGridPoint()] = new TileFlow(flow.Dx, flow.Dy, flow.ExpiresTurn);
        }

        state.Echoes.ReplaceAll(Echoes);
        return state;
    }
}

public sealed record ZoneSave(
    string ZoneId,
    string RegionId,
    bool Generated,
    List<EntitySave> Entities,
    List<PointSave> BlockingTerrain,
    List<TerrainSave> Terrain,
    List<TerrainExpirationSave> TerrainExpirations,
    List<ExploredSave> ExploredBySoulId,
    List<string> RoomProfiles,
    List<string> PromiseHooks,
    List<TileFlowSave>? TileFlows = null)
{
    public static ZoneSave FromZone(ZoneSnapshot zone) =>
        new(
            zone.ZoneId,
            zone.RegionId,
            zone.Generated,
            zone.Entities.Values
                .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
                .Select(EntitySave.FromEntity)
                .ToList(),
            zone.BlockingTerrain
                .OrderBy(point => point.Y)
                .ThenBy(point => point.X)
                .Select(PointSave.From)
                .ToList(),
            zone.Terrain
                .OrderBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.X)
                .Select(pair => new TerrainSave(PointSave.From(pair.Key), pair.Value))
                .ToList(),
            zone.TerrainExpirations
                .OrderBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.X)
                .Select(pair => new TerrainExpirationSave(PointSave.From(pair.Key), pair.Value))
                .ToList(),
            zone.ExploredBySoulId
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => new ExploredSave(
                    pair.Key,
                    pair.Value
                        .OrderBy(point => point.Y)
                        .ThenBy(point => point.X)
                        .Select(PointSave.From)
                        .ToList()))
                .ToList(),
            zone.RoomProfiles.ToList(),
            zone.PromiseHooks.ToList(),
            zone.TileFlows
                .OrderBy(pair => pair.Key.Y)
                .ThenBy(pair => pair.Key.X)
                .Select(pair => new TileFlowSave(PointSave.From(pair.Key), pair.Value.Dx, pair.Value.Dy, pair.Value.ExpiresTurn))
                .ToList());

    public ZoneSnapshot ToZone() =>
        new(
            ZoneId,
            RegionId,
            Generated,
            (Entities ?? new List<EntitySave>())
                .Select(entity => entity.ToEntity())
                .ToDictionary(entity => entity.Id, entity => entity),
            new HashSet<GridPoint>((BlockingTerrain ?? new List<PointSave>()).Select(point => point.ToGridPoint())),
            (Terrain ?? new List<TerrainSave>()).ToDictionary(entry => entry.Point.ToGridPoint(), entry => entry.Value),
            (TerrainExpirations ?? new List<TerrainExpirationSave>()).ToDictionary(entry => entry.Point.ToGridPoint(), entry => entry.ExpiresTurn),
            (TileFlows ?? new List<TileFlowSave>()).ToDictionary(entry => entry.Point.ToGridPoint(), entry => new TileFlow(entry.Dx, entry.Dy, entry.ExpiresTurn)),
            (ExploredBySoulId ?? new List<ExploredSave>()).ToDictionary(
                entry => entry.SoulId,
                entry => (IReadOnlySet<GridPoint>)new HashSet<GridPoint>((entry.Points ?? new List<PointSave>()).Select(point => point.ToGridPoint())),
                StringComparer.OrdinalIgnoreCase),
            RoomProfiles ?? new List<string>(),
            PromiseHooks ?? new List<string>());
}

public sealed record EntitySave(
    string Id,
    string Name,
    List<ComponentSave> Components)
{
    public static EntitySave FromEntity(Entity entity) =>
        new(
            entity.Id.Value,
            entity.Name,
            entity.Components
                .Select(ComponentSave.FromComponent)
                .OrderBy(component => component.Type, StringComparer.OrdinalIgnoreCase)
                .ToList());

    public Entity ToEntity()
    {
        var entity = new Entity(EntityId.Create(Id), Name);
        foreach (var component in Components ?? new List<ComponentSave>())
        {
            SetComponent(entity, component.ToComponent());
        }

        return entity;
    }

    private static void SetComponent(Entity entity, IEntityComponent component)
    {
        switch (component)
        {
            case PositionComponent typed:
                entity.Set(typed);
                break;
            case RenderableComponent typed:
                entity.Set(typed);
                break;
            case TagsComponent typed:
                entity.Set(typed);
                break;
            case DescriptionComponent typed:
                entity.Set(typed);
                break;
            case PhysicalComponent typed:
                entity.Set(typed);
                break;
            case ActorComponent typed:
                entity.Set(typed);
                break;
            case ControllerComponent typed:
                entity.Set(typed);
                break;
            case BodyStatsComponent typed:
                entity.Set(typed);
                break;
            case SoulStatsComponent typed:
                entity.Set(typed);
                break;
            case InventoryComponent typed:
                entity.Set(typed);
                break;
            case EquipmentComponent typed:
                entity.Set(typed);
                break;
            case ItemComponent typed:
                entity.Set(typed);
                break;
            case StackComponent typed:
                entity.Set(typed);
                break;
            case MerchantComponent typed:
                entity.Set(typed);
                break;
            case ServiceComponent typed:
                entity.Set(typed);
                break;
            case FixtureComponent typed:
                entity.Set(typed);
                break;
            case ReadableComponent typed:
                entity.Set(typed);
                break;
            case ClaimSourceComponent typed:
                entity.Set(typed);
                break;
            case InteractableComponent typed:
                entity.Set(typed);
                break;
            case InteriorEntranceComponent typed:
                entity.Set(typed);
                break;
            case InteriorExitComponent typed:
                entity.Set(typed);
                break;
            case SoulComponent typed:
                entity.Set(typed);
                break;
            case ProfileComponent typed:
                entity.Set(typed);
                break;
            case WantComponent typed:
                entity.Set(typed);
                break;
            case KnowledgeComponent typed:
                entity.Set(typed);
                break;
            case StatusContainerComponent typed:
                entity.Set(typed);
                break;
            case MemoryComponent typed:
                entity.Set(typed);
                break;
            case FactionComponent typed:
                entity.Set(typed);
                break;
            case DoorComponent typed:
                entity.Set(typed);
                break;
            case AiComponent typed:
                entity.Set(typed);
                break;
            case SummonedComponent typed:
                entity.Set(typed);
                break;
            case PromiseAnchorComponent typed:
                entity.Set(typed);
                break;
            case ResistanceComponent typed:
                entity.Set(typed);
                break;
            case DelayedDamageComponent typed:
                entity.Set(typed);
                break;
            case BehaviorTagsComponent typed:
                entity.Set(typed);
                break;
            default:
                throw new InvalidDataException($"Unsupported component {component.GetType().Name}.");
        }
    }
}

public sealed record ComponentSave(
    string Type,
    Dictionary<string, object?> Fields)
{
    public static ComponentSave FromComponent(IEntityComponent component) =>
        component switch
        {
            PositionComponent value => New("position", ("x", value.Position.X), ("y", value.Position.Y)),
            RenderableComponent value => New("renderable", ("glyph", value.Glyph.ToString()), ("palette", value.Palette)),
            TagsComponent value => New("tags", ("tags", value.Tags.ToArray())),
            DescriptionComponent value => New("description", ("text", value.Text)),
            PhysicalComponent value => New(
                "physical",
                ("blocksMovement", value.BlocksMovement),
                ("blocksSight", value.BlocksSight),
                ("material", value.Material),
                ("size", value.Size),
                ("durability", value.Durability)),
            ActorComponent value => New(
                "actor",
                ("hitPoints", value.HitPoints),
                ("maxHitPoints", value.MaxHitPoints),
                ("mana", value.Mana),
                ("maxMana", value.MaxMana),
                ("attack", value.Attack),
                ("defense", value.Defense),
                ("faction", value.Faction)),
            ControllerComponent value => New("controller", ("kind", value.Kind.ToString())),
            BodyStatsComponent value => New("bodyStats", ("vigor", value.Vigor)),
            SoulStatsComponent value => New("soulStats", ("attunement", value.Attunement), ("composure", value.Composure)),
            InventoryComponent value => New(
                "inventory",
                ("items", SortIntMap(value.Items)),
                ("treasuredItems", value.TreasuredItems.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray())),
            EquipmentComponent value => New(
                "equipment",
                ("slots", SortStringMap(value.Slots)),
                ("focusSlots", value.FocusSlots.OrderBy(item => item, StringComparer.OrdinalIgnoreCase).ToArray())),
            ItemComponent value => New(
                "item",
                ("itemType", value.ItemType),
                ("value", value.Value),
                ("material", value.Material),
                ("tags", value.Tags.ToArray()),
                ("stackPolicy", value.StackPolicy),
                ("useProfile", value.UseProfile),
                ("equipmentSlot", value.EquipmentSlot)),
            StackComponent value => New("stack", ("quantity", value.Quantity)),
            MerchantComponent value => New("merchant", ("wares", SortIntMap(value.Wares)), ("gold", value.Gold)),
            ServiceComponent value => New("services", ("offers", value.Offers.OrderBy(offer => offer.Id, StringComparer.OrdinalIgnoreCase).ToArray())),
            FixtureComponent value => New(
                "fixture",
                ("fixtureType", value.FixtureType),
                ("tags", value.Tags.ToArray()),
                ("canAnchorMagic", value.CanAnchorMagic)),
            ReadableComponent value => New("readable", ("title", value.Title), ("textKey", value.TextKey)),
            ClaimSourceComponent value => New("claimSource", ("claims", value.Claims.ToArray())),
            InteractableComponent value => New("interactable", ("verbs", value.Verbs.ToArray())),
            InteriorEntranceComponent value => New(
                "interiorEntrance",
                ("interiorZoneId", value.InteriorZoneId),
                ("interiorId", value.InteriorId),
                ("name", value.Name),
                ("kind", value.Kind),
                ("summary", value.Summary),
                ("accessPolicy", value.AccessPolicy),
                ("requiredItem", value.RequiredItem),
                ("exteriorZoneId", value.ExteriorZoneId),
                ("exteriorX", value.ExteriorX),
                ("exteriorY", value.ExteriorY)),
            InteriorExitComponent value => New(
                "interiorExit",
                ("exteriorZoneId", value.ExteriorZoneId),
                ("interiorId", value.InteriorId),
                ("interiorName", value.InteriorName),
                ("exteriorX", value.ExteriorX),
                ("exteriorY", value.ExteriorY)),
            SoulComponent value => New("soul", ("soulId", value.SoulId)),
            ProfileComponent value => New(
                "profile",
                ("publicName", value.PublicName),
                ("appearance", value.Appearance),
                ("origin", value.Origin),
                ("magicalSignature", value.MagicalSignature),
                ("backstory", value.Backstory),
                ("portraitPath", value.PortraitPath)),
            WantComponent value => New(
                "want",
                ("id", value.Id),
                ("text", value.Text),
                ("salience", value.Salience),
                ("status", value.Status),
                ("stakes", value.Stakes),
                ("tags", value.Tags.ToArray())),
            KnowledgeComponent value => New("knowledge", ("topicTiers", SortIntMap(value.TopicTiers))),
            StatusContainerComponent value => New("statuses", ("statuses", value.Statuses.ToArray())),
            MemoryComponent value => New("memory", ("records", value.Records.ToArray())),
            FactionComponent value => New("faction", ("factionId", value.FactionId), ("roles", value.Roles.ToArray())),
            DoorComponent value => New("door", ("isOpen", value.IsOpen), ("keyId", value.KeyId)),
            AiComponent value => New("ai", ("policyId", value.PolicyId), ("parameters", value.Parameters is null ? null : GameSaveService.NormalizeMap(value.Parameters))),
            SummonedComponent value => New("summoned", ("source", value.Source), ("expiresTurn", value.ExpiresTurn)),
            PromiseAnchorComponent value => New("promiseAnchor", ("promiseIds", value.PromiseIds.ToArray())),
            ResistanceComponent value => New(
                "resistance",
                ("resistances", SortIntMap(value.Resistances)),
                ("weaknesses", SortIntMap(value.Weaknesses))),
            DelayedDamageComponent value => New("delayedDamage", ("buffered", value.Buffered), ("releaseTurn", value.ReleaseTurn)),
            BehaviorTagsComponent value => New("behaviorTags", ("tags", SortNullableIntMap(value.Tags))),
            _ => throw new InvalidDataException($"Unsupported component {component.GetType().Name}."),
        };

    public IEntityComponent ToComponent()
    {
        var fields = GameSaveService.NormalizeMap(Fields);
        return Type.Trim().ToLowerInvariant() switch
        {
            "position" => new PositionComponent(new GridPoint(ReadInt(fields, "x"), ReadInt(fields, "y"))),
            "renderable" => new RenderableComponent(ReadGlyph(fields), ReadString(fields, "palette", "default")),
            "tags" => new TagsComponent(ReadStringList(fields, "tags")),
            "description" => new DescriptionComponent(ReadString(fields, "text")),
            "physical" => new PhysicalComponent(
                ReadBool(fields, "blocksMovement", true),
                ReadBool(fields, "blocksSight"),
                ReadString(fields, "material", "flesh"),
                ReadInt(fields, "size", 1),
                ReadInt(fields, "durability")),
            "actor" => new ActorComponent(
                ReadInt(fields, "hitPoints"),
                ReadInt(fields, "maxHitPoints"),
                ReadInt(fields, "mana"),
                ReadInt(fields, "maxMana"),
                ReadInt(fields, "attack"),
                ReadInt(fields, "defense"),
                ReadString(fields, "faction", "neutral")),
            "controller" => new ControllerComponent(ReadControllerKind(fields)),
            "bodystats" => new BodyStatsComponent(ReadInt(fields, "vigor")),
            "soulstats" => new SoulStatsComponent(ReadInt(fields, "attunement"), ReadInt(fields, "composure")),
            "inventory" => new InventoryComponent(
                ReadIntMap(fields, "items"),
                new HashSet<string>(ReadStringList(fields, "treasuredItems"), StringComparer.OrdinalIgnoreCase)),
            "equipment" => new EquipmentComponent(
                ReadStringMap(fields, "slots"),
                new HashSet<string>(ReadStringList(fields, "focusSlots"), StringComparer.OrdinalIgnoreCase)),
            "item" => new ItemComponent(
                ReadString(fields, "itemType"),
                ReadInt(fields, "value"),
                ReadString(fields, "material"),
                ReadStringList(fields, "tags"),
                ReadString(fields, "stackPolicy", "commodity"),
                ReadString(fields, "useProfile", "inert"),
                ReadNullableString(fields, "equipmentSlot")),
            "stack" => new StackComponent(ReadInt(fields, "quantity", 1)),
            "merchant" => new MerchantComponent(ReadIntMap(fields, "wares"), ReadInt(fields, "gold", 30)),
            "services" => new ServiceComponent(ReadServiceOfferList(fields, "offers")),
            "fixture" => new FixtureComponent(
                ReadString(fields, "fixtureType"),
                ReadStringList(fields, "tags"),
                ReadBool(fields, "canAnchorMagic", true)),
            "readable" => new ReadableComponent(ReadString(fields, "title"), ReadString(fields, "textKey")),
            "claimsource" => new ClaimSourceComponent(ReadClaimSeedList(fields, "claims")),
            "interactable" => new InteractableComponent(ReadStringList(fields, "verbs")),
            "interiorentrance" => new InteriorEntranceComponent(
                ReadString(fields, "interiorZoneId"),
                ReadString(fields, "interiorId"),
                ReadString(fields, "name"),
                ReadString(fields, "kind"),
                ReadString(fields, "summary"),
                ReadString(fields, "accessPolicy", "public"),
                ReadNullableString(fields, "requiredItem"),
                ReadString(fields, "exteriorZoneId"),
                ReadInt(fields, "exteriorX"),
                ReadInt(fields, "exteriorY")),
            "interiorexit" => new InteriorExitComponent(
                ReadString(fields, "exteriorZoneId"),
                ReadString(fields, "interiorId"),
                ReadString(fields, "interiorName"),
                ReadInt(fields, "exteriorX"),
                ReadInt(fields, "exteriorY")),
            "soul" => new SoulComponent(ReadString(fields, "soulId")),
            "profile" => new ProfileComponent(
                ReadString(fields, "publicName"),
                ReadString(fields, "appearance"),
                ReadString(fields, "origin"),
                ReadString(fields, "magicalSignature"),
                ReadString(fields, "backstory"),
                ReadString(fields, "portraitPath")),
            "want" => new WantComponent(
                ReadString(fields, "id", "want"),
                ReadString(fields, "text"),
                ReadInt(fields, "salience", 2),
                ReadString(fields, "status", "active"),
                ReadString(fields, "stakes"),
                ReadStringList(fields, "tags")),
            "knowledge" => new KnowledgeComponent(ReadIntMap(fields, "topicTiers")),
            "statuses" => new StatusContainerComponent(ReadStatusList(fields, "statuses")),
            "memory" => new MemoryComponent(ReadEntityMemoryList(fields, "records")),
            "faction" => new FactionComponent(ReadString(fields, "factionId"), ReadStringList(fields, "roles")),
            "door" => new DoorComponent(ReadBool(fields, "isOpen"), ReadNullableString(fields, "keyId")),
            "ai" => new AiComponent(ReadString(fields, "policyId"), ReadObjectMapOrNull(fields, "parameters")),
            "summoned" => new SummonedComponent(ReadString(fields, "source"), ReadNullableInt(fields, "expiresTurn")),
            "promiseanchor" => new PromiseAnchorComponent(ReadStringList(fields, "promiseIds")),
            "resistance" => new ResistanceComponent(
                ReadIntMap(fields, "resistances"),
                ReadIntMap(fields, "weaknesses")),
            "delayeddamage" => new DelayedDamageComponent(ReadInt(fields, "buffered"), ReadInt(fields, "releaseTurn")),
            "behaviortags" => new BehaviorTagsComponent(ReadNullableIntMap(fields, "tags")),
            _ => throw new InvalidDataException($"Unsupported saved component type {Type}."),
        };
    }

    private static ComponentSave New(string type, params (string Key, object? Value)[] fields)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in fields)
        {
            map[field.Key] = GameSaveService.NormalizeValue(field.Value);
        }

        return new ComponentSave(type, map);
    }

    private static Dictionary<string, int> SortIntMap(IReadOnlyDictionary<string, int> source) =>
        source
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, int?> SortNullableIntMap(IReadOnlyDictionary<string, int?> source) =>
        source
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> SortStringMap(IReadOnlyDictionary<string, string> source) =>
        source
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    private static char ReadGlyph(IReadOnlyDictionary<string, object?> fields)
    {
        var glyph = ReadString(fields, "glyph", "?");
        return glyph.Length == 0 ? '?' : glyph[0];
    }

    private static ControllerKind ReadControllerKind(IReadOnlyDictionary<string, object?> fields) =>
        Enum.TryParse<ControllerKind>(ReadString(fields, "kind", "None"), ignoreCase: true, out var kind)
            ? kind
            : ControllerKind.None;

    private static IReadOnlyList<StatusInstance> ReadStatusList(IReadOnlyDictionary<string, object?> fields, string key) =>
        ReadObjectList(fields, key)
            .Select(map =>
            {
                var id = ReadString(map, "id", "status");
                return new StatusInstance(
                    id,
                    ReadString(map, "displayName", id),
                    ReadNullableInt(map, "expiresTurn"),
                    ReadInt(map, "intensity", 1),
                    ReadObjectMapOrNull(map, "details"));
            })
            .ToArray();

    private static IReadOnlyList<EntityMemoryRecord> ReadEntityMemoryList(IReadOnlyDictionary<string, object?> fields, string key) =>
        ReadObjectList(fields, key)
            .Select(map => new EntityMemoryRecord(
                ReadString(map, "id", "memory"),
                ReadString(map, "text"),
                ReadString(map, "source", "unknown"),
                ReadString(map, "provenance", "unknown"),
                ReadInt(map, "salience", 1),
                ReadBool(map, "shareable")))
            .ToArray();

    private static IReadOnlyList<ClaimSeed> ReadClaimSeedList(IReadOnlyDictionary<string, object?> fields, string key) =>
        ReadObjectList(fields, key)
            .Select(map => new ClaimSeed(
                ReadString(map, "text"),
                ReadString(map, "category", "rumor"),
                ReadString(map, "subject"),
                ReadInt(map, "salience", 3),
                ReadInt(map, "confidence", 75),
                ReadBoolAny(map, true, "playerVisible", "player_visible"),
                ReadBoolAny(map, false, "bindAsPromise", "bind_as_promise"),
                ReadString(map, "promiseKind", ReadString(map, "promise_kind", "rumor")),
                ReadNullableString(map, "realizationKind") ?? ReadNullableString(map, "realization_kind"),
                ReadNullableString(map, "triggerHint") ?? ReadNullableString(map, "trigger_hint"),
                ReadNullableString(map, "claimedPlace") ?? ReadNullableString(map, "claimed_place"),
                ReadStringList(map, "tags"),
                ReadNullableString(map, "spokenText") ?? ReadNullableString(map, "spoken_text"),
                ReadNullableString(map, "objectiveText") ?? ReadNullableString(map, "objective_text")))
            .ToArray();

    private static IReadOnlyList<ServiceOffer> ReadServiceOfferList(IReadOnlyDictionary<string, object?> fields, string key) =>
        ReadObjectList(fields, key)
            .Select(map =>
            {
                var id = ReadString(map, "id", "service");
                return new ServiceOffer(
                    id,
                    ReadString(map, "name", id),
                    ReadString(map, "description"),
                    ReadString(map, "effectKind", "record_memory"),
                    ReadInt(map, "goldCost"),
                    ReadNullableString(map, "itemCost"),
                    ReadNullableString(map, "targetHint"),
                    ReadBool(map, "revealed", true),
                    ReadStringList(map, "tags"),
                    ReadNullableString(map, "wantStatusOnComplete") ?? ReadNullableString(map, "want_status_on_complete"),
                    ReadNullableString(map, "wantStakesOnComplete") ?? ReadNullableString(map, "want_stakes_on_complete"),
                    ReadStringList(map, "wantAddTagsOnComplete").Concat(ReadStringList(map, "want_add_tags_on_complete")).ToArray(),
                    ReadStringList(map, "wantRemoveTagsOnComplete").Concat(ReadStringList(map, "want_remove_tags_on_complete")).ToArray());
            })
            .ToArray();

    private static IReadOnlyList<Dictionary<string, object?>> ReadObjectList(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<Dictionary<string, object?>>();
        }

        var items = value switch
        {
            object?[] array => array,
            IEnumerable<object?> enumerable when value is not string => enumerable.ToArray(),
            _ => Array.Empty<object?>(),
        };
        return items
            .Select(item => item as IReadOnlyDictionary<string, object?>)
            .Where(item => item is not null)
            .Select(item => GameSaveService.NormalizeMap(item!))
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?>? ReadObjectMapOrNull(IReadOnlyDictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object?> map
            ? GameSaveService.NormalizeMap(map)
            : null;

    private static Dictionary<string, int> ReadIntMap(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is not IReadOnlyDictionary<string, object?> map)
        {
            return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }

        return map
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => ToInt(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, int?> ReadNullableIntMap(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is not IReadOnlyDictionary<string, object?> map)
        {
            return new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        }

        return map
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value is null ? (int?)null : ToInt(pair.Value), StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<string, string> ReadStringMap(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is not IReadOnlyDictionary<string, object?> map)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        return map
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(pair => pair.Key, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadStringList(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string text => new[] { text },
            object?[] array => array.Select(item => item?.ToString() ?? string.Empty).Where(item => item.Length > 0).ToArray(),
            IEnumerable<string> strings => strings.Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            IEnumerable<object?> enumerable => enumerable.Select(item => item?.ToString() ?? string.Empty).Where(item => item.Length > 0).ToArray(),
            _ => Array.Empty<string>(),
        };
    }

    private static string ReadString(IReadOnlyDictionary<string, object?> fields, string key, string fallback = "") =>
        fields.TryGetValue(key, out var value) && value is not null
            ? value.ToString() ?? fallback
            : fallback;

    private static string? ReadNullableString(IReadOnlyDictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var value) && value is not null
            ? value.ToString()
            : null;

    private static int ReadInt(IReadOnlyDictionary<string, object?> fields, string key, int fallback = 0) =>
        fields.TryGetValue(key, out var value) ? ToInt(value, fallback) : fallback;

    private static int? ReadNullableInt(IReadOnlyDictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var value) && value is not null ? ToInt(value) : null;

    private static bool ReadBool(IReadOnlyDictionary<string, object?> fields, string key, bool fallback = false) =>
        fields.TryGetValue(key, out var value)
            ? value switch
            {
                bool typed => typed,
                string text when bool.TryParse(text, out var parsed) => parsed,
                int integer => integer != 0,
                long integer => integer != 0,
                _ => fallback,
            }
            : fallback;

    private static bool ReadBoolAny(IReadOnlyDictionary<string, object?> fields, bool fallback, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (!fields.TryGetValue(key, out var value))
            {
                continue;
            }

            return value switch
            {
                bool typed => typed,
                string text when bool.TryParse(text, out var parsed) => parsed,
                int integer => integer != 0,
                long integer => integer != 0,
                _ => fallback,
            };
        }

        return fallback;
    }

    private static int ToInt(object? value, int fallback = 0) =>
        value switch
        {
            int integer => integer,
            long integer => (int)Math.Clamp(integer, int.MinValue, int.MaxValue),
            double number => (int)Math.Round(number),
            float number => (int)Math.Round(number),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => fallback,
        };
}

public sealed record PointSave(int X, int Y)
{
    public static PointSave From(GridPoint point) => new(point.X, point.Y);

    public GridPoint ToGridPoint() => new(X, Y);
}

public sealed record TileFlowSave(PointSave Point, int Dx, int Dy, int? ExpiresTurn);

public sealed record TerrainSave(PointSave Point, string Value);

public sealed record TerrainExpirationSave(PointSave Point, int ExpiresTurn);

public sealed record ExploredSave(string SoulId, List<PointSave> Points);
