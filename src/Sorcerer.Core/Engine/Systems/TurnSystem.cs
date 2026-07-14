using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Status;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

public sealed class TurnSystem
{
    private sealed record BackgroundTextMaterialization(
        string Text,
        IReadOnlyList<StateDelta> Deltas);

    private readonly GameEngine _engine;
    private readonly TriggerSystem _triggers;
    private readonly TerrainReactionSystem _terrainReactions;
    private readonly LoreCatalog _loreCatalog;
    private readonly GameState _state;
    private readonly StatusRegistry _statusRegistry;
    private readonly WorldReactionSystem _worldReactions = new();
    private readonly WorldTurnSystem _worldTurns = new();
    private readonly IBackgroundTextGenerator? _backgroundTextGenerator;

    // Diegetic pursuit pacing (docs/FREE_FOLK_MOVEMENT.md, "The marble answers slowly"):
    // word of the hunt travels ahead of the hunter, and the hunter takes real road time.
    private const int WitchhunterTraceTurns = 8;
    private const int WitchhunterArrivalTurns = 16;

    // Names are a stopgap authored list until named hunters become data archetypes (plan
    // §3.1); selection is deterministic per dispatch turn so replays agree.
    private static readonly string[] WitchhunterNames =
    {
        "Censor-Pursuivant Adlen Vessik",
        "Censor-Pursuivant Marta Krace",
        "Pursuivant-Captain Odo Brandt",
        "Censor-Pursuivant Ilse Havener",
        "Pursuivant Gero Maltz",
        "Censor-Pursuivant Wilhelmina Stroh",
    };

    // Set when an empire_report scheduled event lands heat during this turn's event
    // resolution, so the same turn's world pump treats it as a reaction turn (no quiet-pump
    // heat decay racing the pressure ladder).
    private bool _empireReportArrivedThisTurn;

    public TurnSystem(
        GameEngine engine,
        GameState state,
        StatusRegistry statusRegistry,
        LoreCatalog loreCatalog,
        IBackgroundTextGenerator? backgroundTextGenerator = null)
    {
        _engine = engine;
        _triggers = new TriggerSystem(engine);
        _terrainReactions = new TerrainReactionSystem(engine);
        _loreCatalog = loreCatalog;
        _state = state;
        _statusRegistry = statusRegistry;
        _backgroundTextGenerator = backgroundTextGenerator;
    }

    public IReadOnlyList<StateDelta> AdvanceTurn()
    {
        var deltas = new List<StateDelta>();
        _state.Turn += 1;
        _empireReportArrivedThisTurn = false;
        deltas.AddRange(ExpireStatuses());
        deltas.AddRange(ExpireBehaviors());
        deltas.AddRange(ApplyTerrainReactions());
        deltas.AddRange(ApplyStatusTicks());
        deltas.AddRange(ReleaseDueDelayedDamage());
        deltas.AddRange(ApplyTileFlows());
        deltas.AddRange(ExpireTerrain());
        deltas.AddRange(ResolveScheduledEvents());
        deltas.AddRange(ResolveTriggers());
        var worldReaction = ApplyWorldReactions();
        deltas.AddRange(worldReaction.Deltas);
        // A report landing on an imperial desk is deferred deed reaction: the turn it arrives
        // counts as a reaction turn, so quiet-pump recovery cannot decay the fresh alarm before
        // the pressure ladder gets to answer it.
        deltas.AddRange(PropagateRumors(worldReaction.AppliedAny || _empireReportArrivedThisTurn));
        deltas.AddRange(EnqueueRumorDistortionJobs());
        deltas.AddRange(PumpBackgroundJobs());
        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyTileFlows()
    {
        var deltas = new List<StateDelta>();
        var expired = _state.TileFlows
            .Where(pair => pair.Value.ExpiresTurn is { } expiry && expiry <= _state.Turn)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var point in expired)
        {
            deltas.AddRange(UpdateTileFlow(point, "expire"));
        }

        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position) && _state.TileFlows.ContainsKey(position.Position))
            .OrderBy(entity => entity.Id.Value)
            .ToArray())
        {
            var position = entity.Get<PositionComponent>().Position;
            var flow = _state.TileFlows[position];
            var destination = position.Translate(flow.Dx, flow.Dy);
            if (!_engine.InBounds(destination)
                || _state.BlockingTerrain.Contains(destination)
                || _engine.BlockingEntityAt(destination) is not null)
            {
                continue;
            }

            var applied = _engine.ApplyConsequence(WorldConsequence.MoveEntity(
                "tile_flow",
                entity.Id.Value,
                destination.X,
                destination.Y,
                operation: "tileFlow",
                sourceEntityId: entity.Id.Value,
                reason: "A tile-flow field moved an entity at turn start.",
                message: $"{Subject(entity)} {Verb(entity, "slide", "slides")} across the flowing ground.",
                details: new Dictionary<string, object?>
                {
                    ["dx"] = flow.Dx,
                    ["dy"] = flow.Dy,
                }));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> UpdateTileFlow(GridPoint point, string action) =>
        _engine.ApplyConsequence(WorldConsequence.UpdateFlow(
            "tile_flow",
            point.X,
            point.Y,
            action,
            reason: $"Tile flow at {point.X},{point.Y} lifecycle changed.",
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = false,
            })).Deltas;

    private IReadOnlyList<StateDelta> ReleaseDueDelayedDamage()
    {
        var deltas = new List<StateDelta>();
        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<DelayedDamageComponent>(out var buffer) && buffer.ReleaseTurn <= _state.Turn)
            .OrderBy(entity => entity.Id.Value)
            .ToArray())
        {
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.ReleaseDelayedDamage(
                "turn",
                entity.Id.Value,
                sourceEntityId: entity.Id.Value,
                reason: "Delayed damage buffer reached its release turn.",
                operation: "releaseDelayedDamage")).Deltas);
        }

        return deltas;
    }

    public WorldConsequenceApplyResult EnqueueBackgroundJob(string purpose, Entity target, int priority) =>
        _engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
            "background",
            target.Id.Value,
            purpose,
            priority,
            visibility: WorldConsequenceVisibility.Hidden,
            sourceEntityId: _state.ControlledEntityId.Value,
            evidence: $"Queued {purpose} background generation for {target.Name}.",
            operation: "queueBackgroundJob",
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = false,
            }));

    private WorldConsequenceApplyResult UpdateBackgroundJob(BackgroundJob job, string operation) =>
        _engine.ApplyConsequence(WorldConsequence.UpdateBackgroundJob(
            "background",
            job.Id,
            job.State.ToString(),
            evidence: $"Background job {job.Id} moved to {job.State}.",
            reason: "Background job lifecycle advanced at the turn-pump apply point.",
            operation: operation,
            startedTurn: job.StartedTurn,
            completedTurn: job.CompletedTurn,
            appliedTurn: job.AppliedTurn,
            resultText: job.ResultText,
            error: job.Error,
            details: new Dictionary<string, object?>
            {
                ["purpose"] = job.Purpose,
                ["targetId"] = job.TargetId,
                ["priority"] = job.Priority,
                ["playerVisible"] = false,
            }));

    private IReadOnlyList<StateDelta> ExpireStatuses()
    {
        var deltas = new List<StateDelta>();
        foreach (var entity in _state.Entities.Values)
        {
            if (!entity.TryGet<StatusContainerComponent>(out var container))
            {
                continue;
            }

            var expired = container.Statuses
                .Where(status => !IsStatusActive(status))
                .Select(status => status.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var status in expired)
            {
                deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.RemoveStatus(
                    "status_expiry",
                    entity.Id.Value,
                    status,
                    sourceEntityId: entity.Id.Value,
                    reason: $"Status {status} expired at turn start.",
                    operation: "expireStatus",
                    details: new Dictionary<string, object?>
                    {
                        ["emitMessage"] = false,
                        ["playerVisible"] = false,
                    })).Deltas);
            }
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ExpireBehaviors()
    {
        var deltas = new List<StateDelta>();
        foreach (var entity in _state.Entities.Values
            .Where(entity => entity.TryGet<BehaviorTagsComponent>(out _))
            .OrderBy(entity => entity.Id.Value)
            .ToArray())
        {
            var behaviors = entity.Get<BehaviorTagsComponent>();
            var expired = behaviors.Tags
                .Where(pair => pair.Value is { } expiry && expiry <= _state.Turn)
                .Select(pair => pair.Key)
                .ToArray();
            foreach (var tag in expired)
            {
                deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.UpdateBehavior(
                    "behavior_expiry",
                    entity.Id.Value,
                    tag,
                    "expire",
                    sourceEntityId: entity.Id.Value,
                    reason: $"Behavior tag {tag} expired at turn start.",
                    operation: "expireBehavior",
                    details: new Dictionary<string, object?>
                    {
                        ["playerVisible"] = false,
                    })).Deltas);
            }
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyStatusTicks()
    {
        var deltas = new List<StateDelta>();
        foreach (var entity in _state.Entities.Values.OrderBy(entity => entity.Id.Value))
        {
            if (!entity.TryGet<ActorComponent>(out var actor)
                || !actor.Alive
                || !entity.TryGet<StatusContainerComponent>(out var container))
            {
                continue;
            }

            var active = container.Statuses.Where(IsStatusActive).ToArray();
            var damage = active.Sum(status => _statusRegistry.DamagePerTurn(status.Id) * Math.Max(1, status.Intensity));
            var healing = active.Sum(status => _statusRegistry.HealPerTurn(status.Id) * Math.Max(1, status.Intensity));
            if (damage > 0)
            {
                var damageMessage = actor.HitPoints - damage <= 0
                    ? $"{Subject(entity)} {Verb(entity, "fall", "falls")} to ongoing harm."
                    : $"{Subject(entity)} {Verb(entity, "take", "takes")} {damage} ongoing harm.";
                var applied = _engine.ApplyConsequence(WorldConsequence.AdjustActorResource(
                    "status_tick",
                    entity.Id.Value,
                    "health",
                    -damage,
                    min: 0,
                    sourceEntityId: entity.Id.Value,
                    reason: "Status damage tick.",
                    operation: "statusTickDamage",
                    emitMessage: true,
                    message: damageMessage,
                    details: new Dictionary<string, object?>
                    {
                        ["damageType"] = "ongoing",
                    }));
                deltas.AddRange(applied.Deltas);
                actor = entity.Get<ActorComponent>();
                if (!actor.Alive)
                {
                    continue;
                }
            }

            if (healing <= 0 || !actor.Alive)
            {
                continue;
            }

            var healed = Math.Min(healing, actor.MaxHitPoints - actor.HitPoints);
            if (healed <= 0)
            {
                continue;
            }

            var healingApplied = _engine.ApplyConsequence(WorldConsequence.AdjustActorResource(
                "status_tick",
                entity.Id.Value,
                "health",
                healed,
                max: actor.MaxHitPoints,
                sourceEntityId: entity.Id.Value,
                reason: "Status healing tick.",
                operation: "statusTickHeal",
                emitMessage: true,
                message: $"{Subject(entity)} {Verb(entity, "regenerate", "regenerates")} {healed} HP."));
            deltas.AddRange(healingApplied.Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ExpireTerrain()
    {
        var deltas = new List<StateDelta>();
        var expired = _state.TerrainExpirations
            .Where(pair => pair.Value <= _state.Turn)
            .Select(pair => pair.Key)
            .ToArray();
        foreach (var point in expired)
        {
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.UpdateTerrain(
                "terrain_expiry",
                point.X,
                point.Y,
                "expire",
                visibility: WorldConsequenceVisibility.Message,
                reason: $"Terrain at {point.X},{point.Y} expired at turn start.",
                operation: "expireTerrain",
                details: new Dictionary<string, object?>
                {
                    ["playerVisible"] = true,
                })).Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ResolveScheduledEvents()
    {
        var deltas = new List<StateDelta>();
        foreach (var scheduled in _state.ScheduledEvents.Due(_state.Turn).ToArray())
        {
            deltas.AddRange(ResolveScheduledEvent(scheduled));
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ResolveScheduledEvent(ScheduledEventRecord scheduled)
    {
        var snapshot = GameStateSnapshot.Capture(_state);
        var deltas = new List<StateDelta>();

        if (scheduled.Kind.Equals("empire_patrol", StringComparison.OrdinalIgnoreCase))
        {
            deltas.AddRange(ResolveEmpirePatrol(scheduled));
        }
        else if (scheduled.Kind.Equals(WorldReactionSystem.EmpireReportEventKind, StringComparison.OrdinalIgnoreCase))
        {
            deltas.AddRange(ResolveEmpireReport(scheduled));
        }
        else if (scheduled.Kind.Equals("empire_warrant", StringComparison.OrdinalIgnoreCase))
        {
            deltas.AddRange(ResolveEmpireWarrant(scheduled));
        }
        else if (scheduled.Kind.Equals("empire_hunter_trace", StringComparison.OrdinalIgnoreCase))
        {
            deltas.AddRange(ResolveWitchhunterTrace(scheduled));
        }
        else if (scheduled.Kind.Equals("empire_hunter", StringComparison.OrdinalIgnoreCase))
        {
            deltas.AddRange(ResolveWitchhunterArrival(scheduled));
        }
        else if (TryBuildScheduledConsequence(scheduled, out var consequence))
        {
            deltas.AddRange(_engine.ApplyConsequence(consequence).Deltas);
        }
        else if (LooksLikeMalformedScheduledConsequence(scheduled.Payload))
        {
            deltas.Add(ScheduledConsequenceRejectedDelta(
                scheduled,
                "Generic scheduled event consequence did not include consequenceType."));
        }
        else
        {
            deltas.AddRange(ApplyScheduledMessage(scheduled, ScheduledMessageText(scheduled), "scheduledEventMessage"));
        }

        if (!HasRejected(deltas))
        {
            deltas.AddRange(UpdateScheduledEvent(scheduled, "due"));
        }

        if (HasRejected(deltas))
        {
            var failedDeltas = deltas.ToArray();
            snapshot.Restore(_state);
            deltas.Clear();
            deltas.AddRange(failedDeltas.Where(IsRejectedDelta));
            deltas.Add(ScheduledEventSkippedDelta(scheduled, failedDeltas));
            deltas.AddRange(UpdateScheduledEvent(scheduled, "expire"));
        }

        return deltas;
    }

    private static string ScheduledMessageText(ScheduledEventRecord scheduled)
    {
        var text = ReadPayloadString(scheduled.Payload, "text");
        var description = ReadPayloadString(scheduled.Payload, "description");
        return !string.IsNullOrWhiteSpace(text)
            ? text!
            : !string.IsNullOrWhiteSpace(description)
                ? description!
                : $"Delayed magic comes due: {scheduled.Kind}.";
    }

    private static bool TryBuildScheduledConsequence(ScheduledEventRecord scheduled, out WorldConsequence consequence)
    {
        var consequenceType = ReadPayloadString(scheduled.Payload, "consequenceType")
            ?? ReadPayloadString(scheduled.Payload, "consequence_type");
        if (string.IsNullOrWhiteSpace(consequenceType))
        {
            consequence = default!;
            return false;
        }

        consequence = new WorldConsequence(
            consequenceType,
            ReadPayloadString(scheduled.Payload, "source") ?? "scheduled_event",
            SourceEntityId: ReadPayloadString(scheduled.Payload, "sourceEntityId")
                ?? ReadPayloadString(scheduled.Payload, "source_entity_id")
                ?? scheduled.SourceEntityId?.Value,
            TargetEntityId: ReadPayloadString(scheduled.Payload, "targetEntityId")
                ?? ReadPayloadString(scheduled.Payload, "target_entity_id")
                ?? ReadPayloadString(scheduled.Payload, "target"),
            Salience: ReadPayloadInt(scheduled.Payload, "salience") ?? 1,
            Confidence: ReadPayloadInt(scheduled.Payload, "confidence") ?? 100,
            Visibility: ReadPayloadString(scheduled.Payload, "visibility") ?? WorldConsequenceVisibility.Hidden,
            Evidence: ReadPayloadString(scheduled.Payload, "evidence"),
            Reason: ReadPayloadString(scheduled.Payload, "reason")
                ?? $"Scheduled event {scheduled.Id} delivered {consequenceType}.",
            Payload: ScheduledConsequencePayload(scheduled.Payload),
            Timing: ReadPayloadString(scheduled.Payload, "timing") ?? WorldConsequenceTiming.Immediate);
        return true;
    }

    private static IReadOnlyDictionary<string, object?> ScheduledConsequencePayload(IReadOnlyDictionary<string, object?> payload)
        => WorldConsequencePayloadBuilder.MergeNestedWithTopLevelFields(
            payload,
            ScheduledConsequenceMetadataKeys,
            "consequencePayload",
            "consequence_payload",
            "payload");

    private static bool LooksLikeMalformedScheduledConsequence(IReadOnlyDictionary<string, object?> payload)
    {
        var explicitEffect = ReadPayloadString(payload, "effectType")
            ?? ReadPayloadString(payload, "effect_type")
            ?? ReadPayloadString(payload, "type")
            ?? ReadPayloadString(payload, "operation")
            ?? ReadPayloadString(payload, "op");
        if (IsConsequenceAlias(explicitEffect))
        {
            return true;
        }

        return payload.ContainsKey("consequencePayload")
            || payload.ContainsKey("consequence_payload");
    }

    private static bool IsConsequenceAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim()
            .Replace("_", "", StringComparison.Ordinal)
            .Replace("-", "", StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "consequence" or "worldconsequence" or "typedconsequence" or "applyconsequence";
    }

    private static readonly HashSet<string> ScheduledConsequenceMetadataKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "consequenceType",
        "consequence_type",
        "source",
        "sourceEntityId",
        "source_entity_id",
        "targetEntityId",
        "target_entity_id",
        "target",
        "salience",
        "confidence",
        "visibility",
        "evidence",
        "reason",
        "timing",
        "consequencePayload",
        "consequence_payload",
        "payload",
    };

    private static string? ReadPayloadString(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        var text = Convert.ToString(raw);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ReadPayloadInt(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        // Match WorldConsequenceApplier.ReadInt's numeric coverage. A scheduled/delayed
        // consequence re-materializes this same payload later through this method, so a
        // fractional or long numeric field (plausible from LLM-authored JSON) that an immediate
        // consequence would round/clamp correctly must not silently drop here instead.
        return raw switch
        {
            int typed => typed,
            long typed => typed > int.MaxValue ? int.MaxValue : typed < int.MinValue ? int.MinValue : (int)typed,
            double typed => (int)Math.Round(typed),
            float typed => (int)Math.Round(typed),
            decimal typed => (int)Math.Round(typed),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? ReadPayloadBool(IReadOnlyDictionary<string, object?> payload, string key)
    {
        if (!payload.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            bool boolValue => boolValue,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static StateDelta ScheduledConsequenceRejectedDelta(ScheduledEventRecord scheduled, string error)
    {
        var effectType = ReadPayloadString(scheduled.Payload, "effectType")
            ?? ReadPayloadString(scheduled.Payload, "effect_type")
            ?? ReadPayloadString(scheduled.Payload, "type")
            ?? ReadPayloadString(scheduled.Payload, "operation")
            ?? ReadPayloadString(scheduled.Payload, "op");
        return new StateDelta(
            "worldConsequenceRejected",
            scheduled.Id,
            error,
            new Dictionary<string, object?>
            {
                ["consequenceType"] = "scheduled_event_consequence",
                ["eventId"] = scheduled.Id,
                ["eventType"] = scheduled.Kind,
                ["dueTurn"] = scheduled.DueTurn,
                ["effectType"] = effectType,
                ["error"] = error,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private static StateDelta ScheduledEventSkippedDelta(ScheduledEventRecord scheduled, IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var scheduledConsequenceType = ReadPayloadString(scheduled.Payload, "consequenceType")
            ?? ReadPayloadString(scheduled.Payload, "consequence_type");
        if (string.IsNullOrWhiteSpace(scheduledConsequenceType)
            && LooksLikeMalformedScheduledConsequence(scheduled.Payload))
        {
            scheduledConsequenceType = "scheduled_event_consequence";
        }

        if (!string.IsNullOrWhiteSpace(scheduledConsequenceType))
        {
            scheduledConsequenceType = WorldConsequenceTypes.Normalize(scheduledConsequenceType);
        }

        var errors = rejectedDeltas
            .Where(IsRejectedDelta)
            .Select(delta => delta.Summary)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StateDelta(
            "scheduledEventSkipped",
            scheduled.Id,
            $"Scheduled event {scheduled.Id} was rolled back after a rejected due consequence.",
            new Dictionary<string, object?>
            {
                ["eventId"] = scheduled.Id,
                ["eventType"] = scheduled.Kind,
                ["dueTurn"] = scheduled.DueTurn,
                ["scheduledConsequenceType"] = scheduledConsequenceType,
                ["rejectedCount"] = errors.Length,
                ["errors"] = errors,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private IReadOnlyList<StateDelta> UpdateScheduledEvent(ScheduledEventRecord scheduled, string action) =>
        _engine.ApplyConsequence(WorldConsequence.UpdateScheduledEvent(
            "scheduled_event",
            scheduled.Id,
            action,
            sourceEntityId: scheduled.SourceEntityId?.Value,
            reason: $"Scheduled event {scheduled.Id} lifecycle changed.",
            details: new Dictionary<string, object?>
            {
                ["eventType"] = scheduled.Kind,
                ["dueTurn"] = scheduled.DueTurn,
            })).Deltas;

    private IReadOnlyList<StateDelta> ResolveEmpirePatrol(ScheduledEventRecord scheduled)
    {
        var deltas = new List<StateDelta>();
        var text = scheduled.Payload.TryGetValue("text", out var rawText)
            ? Convert.ToString(rawText)
            : null;
        if (!string.IsNullOrWhiteSpace(text))
        {
            deltas.AddRange(ApplyScheduledMessage(scheduled, text!, "scheduledEventMessage"));
        }

        var position = FindRoadArrivalPoint();
        if (position is null)
        {
            deltas.AddRange(ApplyScheduledMessage(scheduled, "An imperial patrol loses the trail before it can enter the district.", "scheduledEventFailed"));
            return deltas;
        }

        var applied = _engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "scheduled_event",
            "imperial patrol-censor",
            position.Value.X,
            position.Value.Y,
            prefix: "imperial_patrol",
            glyph: 'i',
            faction: "empire",
            hp: 8,
            attack: 2,
            tags: new[] { "imperial", "patrol", "censorate" },
            material: "body",
            roles: new[] { "empire", "censorate", "patrol" },
            controllerKind: "ai",
            aiPolicyId: "imperial_patrol",
            summoned: false,
            operation: "resolveEmpirePatrol",
            message: "An imperial patrol-censor comes up the road with a folder full of careful fear."));
        if (!applied.Applied)
        {
            deltas.AddRange(ApplyScheduledMessage(
                scheduled,
                applied.Error ?? "An imperial patrol loses the trail before it can enter the district.",
                "scheduledEventFailed"));
        }
        else
        {
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    // A report of the fugitive's activity physically arrives at an imperial desk and raises
    // heat - unless everyone who was carrying the word has since died. Killing the witness
    // before the report lands is real counterplay (docs/FREE_FOLK_MOVEMENT.md, "the marble
    // answers slowly"). Overdue audits (cause=overdue) carry no witnesses and always land.
    private IReadOnlyList<StateDelta> ResolveEmpireReport(ScheduledEventRecord scheduled)
    {
        var deltas = new List<StateDelta>();
        var witnessIds = ReadPayloadString(scheduled.Payload, "witnessIds");
        if (!string.IsNullOrWhiteSpace(witnessIds))
        {
            var anyCarrierAlive = witnessIds
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(id => _state.Entities.Values.Any(entity =>
                    entity.Id.Value.Equals(id, StringComparison.OrdinalIgnoreCase)
                    && entity.TryGet<ActorComponent>(out var actor)
                    && actor.Alive));
            if (!anyCarrierAlive)
            {
                deltas.AddRange(ApplyScheduledMessage(
                    scheduled,
                    "Whoever might have carried word of you is past telling; no report arrives.",
                    "scheduledEventMessage"));
                return deltas;
            }
        }

        var text = ReadPayloadString(scheduled.Payload, "text")
            ?? "Word of your deeds reaches an imperial desk.";
        deltas.AddRange(ApplyScheduledMessage(scheduled, text, "scheduledEventMessage"));

        var heat = ReadPayloadInt(scheduled.Payload, "heat") ?? 1;
        foreach (var faction in _state.Factions.FactionsByRole("empire_bloc"))
        {
            deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.AdjustFactionResource(
                "scheduled_event",
                faction.Id,
                "heat",
                Math.Max(1, heat),
                evidence: scheduled.Id,
                reason: "A report of the fugitive's activity arrived at an imperial desk.")).Deltas);
        }

        _empireReportArrivedThisTurn = true;
        return deltas;
    }

    // The warrant's poster is paperwork; its teeth are a person. When a warrant matures the
    // Censorate puts a named witchhunter on the road: word of the pursuit travels ahead of
    // them (empire_hunter_trace), and they arrive later by the same road-edge rule as any
    // other responder (empire_hunter). Named people are finite: one hunter at a time.
    private IReadOnlyList<StateDelta> ResolveEmpireWarrant(ScheduledEventRecord scheduled)
    {
        var deltas = new List<StateDelta>();
        deltas.AddRange(ApplyScheduledMessage(scheduled, ScheduledMessageText(scheduled), "scheduledEventMessage"));
        if (HasActiveOrPendingWitchhunter())
        {
            return deltas;
        }

        var name = WitchhunterNames[Math.Abs(_state.Turn) % WitchhunterNames.Length];
        var factionId = ReadPayloadString(scheduled.Payload, "factionId") ?? "empire";
        deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "scheduled_event",
            "empire_hunter_trace",
            WitchhunterTraceTurns,
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["factionId"] = factionId,
                ["text"] = $"Road talk: {name} of the Censorate has been showing your description at posts along the way.",
            },
            evidence: scheduled.Id,
            reason: "The warrant put a witchhunter on the road; word travels ahead of the hunter.")).Deltas);
        deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.ScheduleEvent(
            "scheduled_event",
            "empire_hunter",
            WitchhunterArrivalTurns,
            new Dictionary<string, object?>
            {
                ["name"] = name,
                ["factionId"] = factionId,
            },
            evidence: scheduled.Id,
            reason: "The witchhunter travels; they arrive by road, not out of thin air.")).Deltas);
        return deltas;
    }

    private IReadOnlyList<StateDelta> ResolveWitchhunterTrace(ScheduledEventRecord scheduled)
    {
        var deltas = new List<StateDelta>();
        var name = ReadPayloadString(scheduled.Payload, "name") ?? "A Censorate witchhunter";
        var text = ReadPayloadString(scheduled.Payload, "text")
            ?? $"Road talk: {name} of the Censorate has been asking after your description.";
        deltas.AddRange(ApplyScheduledMessage(scheduled, text, "scheduledEventMessage"));
        deltas.AddRange(_engine.ApplyConsequence(WorldConsequence.RecordRumor(
            "scheduled_event",
            "hunter",
            scheduled.Id,
            _state.RegionId,
            _state.RegionId,
            $"{name} of the Censorate is asking the roads about a sorcerer.",
            salience: 3,
            tags: new[] { "empire", "witchhunter" },
            evidence: scheduled.Id,
            reason: "A witchhunter's questions leave a trail of road talk the player can hear about.")).Deltas);
        return deltas;
    }

    private IReadOnlyList<StateDelta> ResolveWitchhunterArrival(ScheduledEventRecord scheduled)
    {
        var deltas = new List<StateDelta>();
        var name = ReadPayloadString(scheduled.Payload, "name") ?? "A Censorate witchhunter";
        var position = FindRoadArrivalPoint();
        if (position is null)
        {
            deltas.AddRange(ApplyScheduledMessage(
                scheduled,
                $"{name} loses the trail before reaching the district; the roads will remember for them.",
                "scheduledEventFailed"));
            return deltas;
        }

        var applied = _engine.ApplyConsequence(WorldConsequence.SpawnEntity(
            "scheduled_event",
            name,
            position.Value.X,
            position.Value.Y,
            prefix: "witchhunter",
            glyph: 'H',
            faction: ReadPayloadString(scheduled.Payload, "factionId") ?? "empire",
            hp: 14,
            attack: 3,
            tags: new[] { "imperial", "censorate", "witchhunter" },
            material: "body",
            roles: new[] { "empire", "censorate", "hunter" },
            controllerKind: "ai",
            aiPolicyId: "imperial_patrol",
            summoned: false,
            operation: "resolveWitchhunter",
            message: $"{name} steps off the road, unfolding a warrant with your outline on it."));
        if (!applied.Applied)
        {
            deltas.AddRange(ApplyScheduledMessage(
                scheduled,
                applied.Error ?? $"{name} loses the trail before reaching the district.",
                "scheduledEventFailed"));
        }
        else
        {
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    private bool HasActiveOrPendingWitchhunter() =>
        _state.ScheduledEvents.Events.Any(item =>
            item.Kind.Equals("empire_hunter", StringComparison.OrdinalIgnoreCase)
            || item.Kind.Equals("empire_hunter_trace", StringComparison.OrdinalIgnoreCase))
        || _state.Entities.Values.Any(entity =>
            entity.Id.Value.StartsWith("witchhunter_", StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<ActorComponent>(out var actor)
            && actor.Alive);

    private IReadOnlyList<StateDelta> ApplyScheduledMessage(ScheduledEventRecord scheduled, string message, string operation) =>
        _engine.ApplyConsequence(WorldConsequence.Message(
            "scheduled_event",
            message,
            targetEntityId: scheduled.Id,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: scheduled.SourceEntityId?.Value,
            reason: $"Scheduled event {scheduled.Id} came due.",
            operation: operation,
            details: new Dictionary<string, object?>
            {
                ["scheduledEventId"] = scheduled.Id,
                ["eventType"] = scheduled.Kind,
                ["dueTurn"] = scheduled.DueTurn,
                ["playerVisible"] = ReadPayloadBool(scheduled.Payload, "playerVisible")
                    ?? ReadPayloadBool(scheduled.Payload, "player_visible")
                    ?? true,
                ["auditOnly"] = ReadPayloadBool(scheduled.Payload, "auditOnly")
                    ?? ReadPayloadBool(scheduled.Payload, "audit_only")
                    ?? false,
            })).Deltas;

    private IReadOnlyList<StateDelta> ApplyTerrainReactions()
    {
        return _terrainReactions.ApplyTurnReactions();
    }

    private IReadOnlyList<StateDelta> ResolveTriggers()
    {
        return _triggers.ApplyDue();
    }

    private IReadOnlyList<StateDelta> PumpBackgroundJobs()
    {
        var deltas = new List<StateDelta>();
        if (!_state.BackgroundSettings.Enabled)
        {
            return deltas;
        }

        deltas.AddRange(FailStaleRunningBackgroundJobs());
        for (var count = 0; count < _state.BackgroundSettings.JobsPerTurn; count++)
        {
            var completedPendingApply = _state.BackgroundJobs.Jobs
                .Where(job => job.State == BackgroundJobState.Completed)
                .OrderByDescending(job => job.Priority)
                .ThenBy(job => job.CompletedTurn ?? int.MaxValue)
                .ThenBy(job => job.Id)
                .FirstOrDefault();
            if (completedPendingApply is not null)
            {
                deltas.AddRange(ApplyBackgroundJob(completedPendingApply));
                continue;
            }

            var queued = _state.BackgroundJobs.NextQueued();
            if (queued is null)
            {
                return deltas;
            }

            var running = queued with
            {
                State = BackgroundJobState.Running,
                StartedTurn = _state.Turn,
            };
            deltas.AddRange(UpdateBackgroundJob(running, "backgroundJobStarted").Deltas);

            try
            {
                var text = GenerateBackgroundText(running);
                deltas.AddRange(text.Deltas);
                var completed = running with
                {
                    State = BackgroundJobState.Completed,
                    CompletedTurn = _state.Turn,
                    ResultText = text.Text,
                };
                deltas.AddRange(UpdateBackgroundJob(completed, "backgroundJobCompleted").Deltas);
                deltas.AddRange(ApplyBackgroundJob(completed));
            }
            catch (Exception ex)
            {
                deltas.AddRange(UpdateBackgroundJob(running with
                {
                    State = BackgroundJobState.Failed,
                    Error = ex.Message,
                }, "backgroundJobFailed").Deltas);
            }
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> FailStaleRunningBackgroundJobs()
    {
        var deltas = new List<StateDelta>();
        foreach (var running in _state.BackgroundJobs.Jobs
            .Where(job => job.State == BackgroundJobState.Running)
            .OrderBy(job => job.StartedTurn ?? int.MaxValue)
            .ThenBy(job => job.Id)
            .ToArray())
        {
            deltas.AddRange(UpdateBackgroundJob(running with
            {
                State = BackgroundJobState.Failed,
                Error = "stale_running_job",
            }, "backgroundJobFailed").Deltas);
        }

        return deltas;
    }

    private WorldReactionApplication ApplyWorldReactions()
    {
        return _worldReactions.ApplyPending(_state, _engine.ApplyConsequence);
    }

    private IReadOnlyList<StateDelta> PropagateRumors(bool appliedWorldReaction)
    {
        return _worldTurns.Apply(
            _state,
            "turn",
            budget: 2,
            allowFactionRecovery: !appliedWorldReaction,
            applyConsequence: _engine.ApplyConsequence);
    }

    private IReadOnlyList<StateDelta> EnqueueRumorDistortionJobs()
    {
        var deltas = new List<StateDelta>();
        if (!_state.BackgroundSettings.Enabled)
        {
            return deltas;
        }

        var activeCount = _state.BackgroundJobs.Jobs.Count(job =>
            job.State is BackgroundJobState.Queued or BackgroundJobState.Running or BackgroundJobState.Completed);
        if (activeCount >= _state.BackgroundSettings.MaxQueuedJobs)
        {
            return deltas;
        }

        foreach (var rumor in _state.Rumors.Records
            .Where(rumor => rumor.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(rumor => rumor.Salience >= 3)
            .Where(rumor => rumor.Hops >= 2)
            .Where(rumor => !rumor.DistortionHistory.Any(entry => entry.StartsWith("distortion:", StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(rumor => rumor.Salience)
            .ThenByDescending(rumor => rumor.Hops)
            .ThenBy(rumor => rumor.Id, StringComparer.OrdinalIgnoreCase))
        {
            if (activeCount >= _state.BackgroundSettings.MaxQueuedJobs)
            {
                return deltas;
            }

            if (_state.BackgroundJobs.HasActiveJob("rumor_distortion", rumor.Id))
            {
                continue;
            }

            var queued = _engine.ApplyConsequence(WorldConsequence.QueueBackgroundJob(
                "background",
                rumor.Id,
                "rumor_distortion",
                rumor.Salience,
                targetKind: "rumor",
                visibility: WorldConsequenceVisibility.Hidden,
                evidence: $"Queued rumor distortion for {rumor.Id}.",
                reason: "High-hop, high-salience rumor became eligible for background retelling.",
                operation: "queueBackgroundJob",
                details: new Dictionary<string, object?>
                {
                    ["rumorId"] = rumor.Id,
                    ["sourceKind"] = rumor.SourceKind,
                    ["sourceId"] = rumor.SourceId,
                    ["originRegionId"] = rumor.OriginRegionId,
                    ["currentRegionId"] = rumor.CurrentRegionId,
                    ["hops"] = rumor.Hops,
                    ["playerVisible"] = false,
                }));
            deltas.AddRange(queued.Deltas);
            if (queued.Applied)
            {
                activeCount++;
                deltas.AddRange(RecordBackgroundWorldTurn(
                    "background_job_queued",
                    rumor.Id,
                    $"Queued rumor distortion for {rumor.Id}.",
                    new Dictionary<string, object?>
                    {
                        ["consequenceType"] = WorldConsequenceTypes.QueueBackgroundJob,
                        ["jobId"] = queued.TargetId,
                        ["rumorId"] = rumor.Id,
                        ["purpose"] = "rumor_distortion",
                        ["sourceKind"] = rumor.SourceKind,
                        ["sourceId"] = rumor.SourceId,
                        ["hops"] = rumor.Hops,
                        ["queued"] = true,
                    }));
            }
        }

        return deltas;
    }

    private BackgroundTextMaterialization GenerateBackgroundText(BackgroundJob job)
    {
        if (_backgroundTextGenerator is null)
        {
            return new BackgroundTextMaterialization(GenerateDeterministicBackgroundText(job), Array.Empty<StateDelta>());
        }

        var request = BuildBackgroundTextRequest(job);
        BackgroundTextGenerationResult generated;
        try
        {
            generated = _backgroundTextGenerator.Generate(request);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TimeoutException or TaskCanceledException)
        {
            generated = new BackgroundTextGenerationResult(
                null,
                TechnicalFailure: true,
                Error: ex.Message,
                Provider: _backgroundTextGenerator.Name);
        }

        var useFallback = generated.TechnicalFailure || string.IsNullOrWhiteSpace(generated.Text);
        var text = useFallback
            ? GenerateDeterministicBackgroundText(job)
            : generated.AlreadyMaterialized
                ? generated.Text!
                : NormalizeGeneratedBackgroundText(generated.Text!);
        return new BackgroundTextMaterialization(
            text,
            new[]
            {
                BackgroundTextGeneratedDelta(job, generated, useFallback),
            });
    }

    private string GenerateDeterministicBackgroundText(BackgroundJob job)
    {
        if (job.Purpose.Equals("rumor_distortion", StringComparison.OrdinalIgnoreCase))
        {
            return GenerateRumorDistortionText(job);
        }

        var target = _state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return $"Background detail for missing target {job.TargetId}.";
        }

        var tags = TagsFor(target);
        var material = target.TryGet<PhysicalComponent>(out var physical) ? physical.Material : "unknown matter";
        var lore = LoreRouter.Select(
            _loreCatalog,
            new LoreQuery(
                tags.Append(material).Append(target.Name).Append(_state.RegionId).ToArray(),
                new[] { job.Purpose, "background", _state.RegionId },
                LoreAccessLevel(),
                Limit: 1))
            .FirstOrDefault();
        var loreLine = lore is null ? "" : $" {OneLine(lore.Body)}";
        return job.Purpose switch
        {
            "canon_detail" => $"{target.Name} gains a quiet margin note: {DescribeTradition(tags)}{loreLine}",
            "entity_detail" => $"{target.Name} is {material}.{loreLine} Tagged by {DescribeTags(tags)}, and waiting for a spell to make that matter.",
            _ => $"{target.Name} gains background detail for {job.Purpose}.",
        };
    }

    private BackgroundTextRequest BuildBackgroundTextRequest(BackgroundJob job)
    {
        if (job.Purpose.Equals("rumor_distortion", StringComparison.OrdinalIgnoreCase))
        {
            var rumor = _state.Rumors.Records.FirstOrDefault(record =>
                record.Id.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));
            return new BackgroundTextRequest(
                job.Id,
                job.Purpose,
                job.TargetId,
                job.Priority,
                _state.Turn,
                _state.RegionId,
                TargetKind: "rumor",
                TargetName: rumor?.SourceKind,
                TargetTags: rumor?.Tags ?? Array.Empty<string>(),
                OriginalText: rumor?.Text);
        }

        var target = _state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));
        if (target is null)
        {
            return new BackgroundTextRequest(
                job.Id,
                job.Purpose,
                job.TargetId,
                job.Priority,
                _state.Turn,
                _state.RegionId,
                TargetKind: "missing");
        }

        var tags = TagsFor(target);
        var material = target.TryGet<PhysicalComponent>(out var physical) ? physical.Material : "unknown matter";
        var lore = LoreRouter.Select(
            _loreCatalog,
            new LoreQuery(
                tags.Append(material).Append(target.Name).Append(_state.RegionId).ToArray(),
                new[] { job.Purpose, "background", _state.RegionId },
                LoreAccessLevel(),
                Limit: 1))
            .FirstOrDefault();
        return new BackgroundTextRequest(
            job.Id,
            job.Purpose,
            job.TargetId,
            job.Priority,
            _state.Turn,
            _state.RegionId,
            TargetKind: "entity",
            TargetName: target.Name,
            TargetMaterial: material,
            TargetTags: tags,
            RoutedLore: lore is null ? null : OneLine(lore.Body));
    }

    private static string NormalizeGeneratedBackgroundText(string text)
    {
        var normalized = OneLine(text);
        return normalized.Trim().Trim('"');
    }

    private static StateDelta BackgroundTextGeneratedDelta(
        BackgroundJob job,
        BackgroundTextGenerationResult generated,
        bool usedFallback) =>
        new(
            "backgroundTextGenerated",
            job.Id,
            usedFallback
                ? $"Background generator fell back to deterministic text for {job.Id}."
                : $"Background generator produced text for {job.Id}.",
            new Dictionary<string, object?>
            {
                ["jobId"] = job.Id,
                ["purpose"] = job.Purpose,
                ["targetId"] = job.TargetId,
                ["provider"] = generated.Provider,
                ["model"] = generated.Model,
                ["technicalFailure"] = generated.TechnicalFailure,
                ["usedFallback"] = usedFallback,
                ["error"] = generated.Error,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });

    private string GenerateRumorDistortionText(BackgroundJob job)
    {
        var rumor = _state.Rumors.Records.FirstOrDefault(record =>
            record.Id.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));
        if (rumor is null)
        {
            return $"Background rumor distortion for missing rumor {job.TargetId}.";
        }

        var original = rumor.Text.Trim().TrimEnd('.');
        var text = original
            .Replace("There is ", "There may be ", StringComparison.OrdinalIgnoreCase)
            .Replace("There are ", "There may be ", StringComparison.OrdinalIgnoreCase)
            .Replace("South of here", "Somewhere south of here", StringComparison.OrdinalIgnoreCase)
            .Replace("North of here", "Somewhere north of here", StringComparison.OrdinalIgnoreCase)
            .Replace("the wild sorcerer", "a bright-handed fugitive", StringComparison.OrdinalIgnoreCase)
            .Replace("someone bright and unnamed", "a nameless bright-handed figure", StringComparison.OrdinalIgnoreCase);
        text = text
            .Replace(", There may be ", ", there may be ", StringComparison.Ordinal)
            .Replace("; There may be ", "; there may be ", StringComparison.Ordinal);
        if (text.Equals(original, StringComparison.OrdinalIgnoreCase))
        {
            text = $"It is said that {LowerFirst(text)}";
        }

        return $"{UpperFirst(text)}.";
    }

    private IReadOnlyList<StateDelta> ApplyBackgroundJob(BackgroundJob job)
    {
        var deltas = new List<StateDelta>();
        if (string.IsNullOrWhiteSpace(job.ResultText))
        {
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Failed,
                Error = "Background job produced no text.",
            }, "backgroundJobFailed").Deltas);
            return deltas;
        }

        if (job.Purpose.Equals("rumor_distortion", StringComparison.OrdinalIgnoreCase))
        {
            return ApplyRumorDistortionJob(job);
        }

        if (CanonAlreadyExists(job))
        {
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Failed,
                Error = "canon_already_exists",
            }, "backgroundJobFailed").Deltas);
            return deltas;
        }

        var snapshot = GameStateSnapshot.Capture(_state);
        var applied = _engine.ApplyConsequence(WorldConsequence.AddCanon(
            "background",
            job.Purpose,
            job.TargetId,
            job.ResultText,
            job.ResultText.Length <= 80 ? job.ResultText : $"{job.ResultText[..77]}...",
            Array.Empty<string>(),
            evidence: job.ResultText,
            operation: "backgroundCanon"));
        deltas.AddRange(applied.Deltas);
        if (applied.Applied && !HasRejected(deltas))
        {
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Applied,
                AppliedTurn = _state.Turn,
            }, "backgroundJobApplied").Deltas);
            deltas.AddRange(RecordBackgroundWorldTurn(
                "background_detail_applied",
                job.TargetId,
                $"Applied background {job.Purpose} for {job.TargetId}.",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = WorldConsequenceTypes.AddCanon,
                    ["backgroundJobId"] = job.Id,
                    ["purpose"] = job.Purpose,
                    ["targetId"] = job.TargetId,
                    ["resultText"] = job.ResultText,
                    ["playerVisible"] = false,
                }));
        }

        if (!applied.Applied || HasRejected(deltas))
        {
            var failedDeltas = deltas.ToArray();
            snapshot.Restore(_state);
            deltas.Clear();
            deltas.AddRange(failedDeltas.Where(IsRejectedDelta));
            deltas.Add(BackgroundJobApplySkippedDelta(job, failedDeltas));
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Failed,
                Error = applied.Error ?? "background_apply_rejected",
            }, "backgroundJobFailed").Deltas);
        }

        return deltas;
    }

    private IReadOnlyList<StateDelta> ApplyRumorDistortionJob(BackgroundJob job)
    {
        var deltas = new List<StateDelta>();
        var rumor = _state.Rumors.Records.FirstOrDefault(record =>
            record.Id.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));
        if (rumor is null)
        {
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Failed,
                Error = $"Rumor not found: {job.TargetId}",
            }, "backgroundJobFailed").Deltas);
            return deltas;
        }

        var snapshot = GameStateSnapshot.Capture(_state);
        var applied = _engine.ApplyConsequence(WorldConsequence.UpdateRumor(
            "background",
            rumor.Id,
            text: job.ResultText,
            addTags: new[] { "distorted" },
            appendDistortionHistory: new[]
            {
                $"distortion: {job.Id} retold {rumor.Id} in {_state.RegionId} on turn {_state.Turn}.",
            },
            visibility: WorldConsequenceVisibility.Hidden,
            reason: "Background rumor distortion.",
            operation: "backgroundRumorDistortion",
            message: $"A rumor changes in retelling: {job.ResultText}",
            details: new Dictionary<string, object?>
            {
                ["backgroundJobId"] = job.Id,
                ["originalText"] = rumor.Text,
                ["playerVisible"] = false,
            }));
        deltas.AddRange(applied.Deltas);
        if (applied.Applied && !HasRejected(deltas))
        {
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Applied,
                AppliedTurn = _state.Turn,
                Error = null,
            }, "backgroundJobApplied").Deltas);
            deltas.AddRange(RecordBackgroundWorldTurn(
                "background_rumor_distortion",
                rumor.Id,
                $"Applied rumor distortion for {rumor.Id}.",
                new Dictionary<string, object?>
                {
                    ["consequenceType"] = WorldConsequenceTypes.UpdateRumor,
                    ["backgroundJobId"] = job.Id,
                    ["rumorId"] = rumor.Id,
                    ["purpose"] = job.Purpose,
                    ["originalText"] = rumor.Text,
                    ["resultText"] = job.ResultText,
                    ["playerVisible"] = false,
                }));
        }

        if (!applied.Applied || HasRejected(deltas))
        {
            var failedDeltas = deltas.ToArray();
            snapshot.Restore(_state);
            deltas.Clear();
            deltas.AddRange(failedDeltas.Where(IsRejectedDelta));
            deltas.Add(BackgroundJobApplySkippedDelta(job, failedDeltas));
            deltas.AddRange(UpdateBackgroundJob(job with
            {
                State = BackgroundJobState.Failed,
                AppliedTurn = null,
                Error = applied.Error ?? "background_apply_rejected",
            }, "backgroundJobFailed").Deltas);
        }

        return deltas;
    }

    private bool CanonAlreadyExists(BackgroundJob job) =>
        _state.Canon.Records.Any(record =>
            record.Kind.Equals(job.Purpose, StringComparison.OrdinalIgnoreCase)
            && record.AttachedTo.Equals(job.TargetId, StringComparison.OrdinalIgnoreCase));

    private static bool HasRejected(IEnumerable<StateDelta> deltas) =>
        deltas.Any(IsRejectedDelta);

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta BackgroundJobApplySkippedDelta(BackgroundJob job, IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = rejectedDeltas
            .Where(IsRejectedDelta)
            .Select(delta => delta.Summary)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StateDelta(
            "backgroundJobApplySkipped",
            job.Id,
            $"Background job {job.Id} was rolled back after a rejected apply consequence.",
            new Dictionary<string, object?>
            {
                ["jobId"] = job.Id,
                ["purpose"] = job.Purpose,
                ["targetId"] = job.TargetId,
                ["rejectedCount"] = errors.Length,
                ["errors"] = errors,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private IReadOnlyList<StateDelta> RecordBackgroundWorldTurn(
        string kind,
        string sourceId,
        string summary,
        IReadOnlyDictionary<string, object?> details) =>
        _engine.ApplyConsequence(WorldConsequence.RecordWorldTurn(
            "background",
            "turn",
            kind,
            sourceId,
            summary,
            details,
            visibility: WorldConsequenceVisibility.Hidden,
            consequenceReason: "Background job lifecycle advanced at the turn-pump apply point.",
            operation: "worldTurn",
            details: new Dictionary<string, object?>
            {
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            })).Deltas;

    private bool IsStatusActive(StatusInstance status) =>
        status.ExpiresTurn is null || status.ExpiresTurn > _state.Turn;

    private int LoreAccessLevel()
    {
        var canonDepth = _state.Canon.Records.Count(record =>
            record.Kind.Equals("readable", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("canon_detail", StringComparison.OrdinalIgnoreCase)
            || record.Kind.Equals("entity_detail", StringComparison.OrdinalIgnoreCase));
        return Math.Clamp(1 + (canonDepth / 2), 1, 3);
    }

    private static string OneLine(string text)
    {
        var normalized = text.Replace('\n', ' ').Trim();
        return normalized.Length <= 160 ? normalized : $"{normalized[..157]}...";
    }

    private static string LowerFirst(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? "the rumor changed"
            : $"{char.ToLowerInvariant(text[0])}{text[1..]}";

    private static string UpperFirst(string text) =>
        string.IsNullOrWhiteSpace(text)
            ? "The rumor changed"
            : $"{char.ToUpperInvariant(text[0])}{text[1..]}";

    private string Subject(Entity entity) =>
        entity.Id == _state.ControlledEntityId ? "You" : entity.Name;

    private string Verb(Entity entity, string secondPerson, string thirdPerson) =>
        entity.Id == _state.ControlledEntityId ? secondPerson : thirdPerson;

    // Imperial responders arrive like anyone else: at the edge of the map, off the road,
    // outside the player's sight (docs/FREE_FOLK_MOVEMENT.md, "arrival is travel, not
    // spawn"). Nothing imperial ever materializes where the player can see it; if no unseen
    // approach exists at all, the response fails rather than popping into view.
    private GridPoint? FindRoadArrivalPoint()
    {
        var playerPosition = _state.ControlledEntity.TryGet<PositionComponent>(out var player)
            ? player.Position
            : new GridPoint(_state.Width / 2, _state.Height / 2);
        var perception = new PerceptionSystem(_state, _statusRegistry);

        var edgeCandidates = new List<GridPoint>();
        for (var x = 1; x < _state.Width - 1; x++)
        {
            edgeCandidates.Add(new GridPoint(x, 1));
            edgeCandidates.Add(new GridPoint(x, _state.Height - 2));
        }

        for (var y = 2; y < _state.Height - 2; y++)
        {
            edgeCandidates.Add(new GridPoint(1, y));
            edgeCandidates.Add(new GridPoint(_state.Width - 2, y));
        }

        var unseenEdge = edgeCandidates
            .Where(CanSpawnAt)
            .Where(point => !PlayerCanSee(perception, playerPosition, point))
            .OrderByDescending(point => Chebyshev(playerPosition, point))
            .ThenBy(point => point.Y)
            .ThenBy(point => point.X)
            .Cast<GridPoint?>()
            .FirstOrDefault();
        if (unseenEdge is not null)
        {
            return unseenEdge;
        }

        // No usable edge tile: fall back to the farthest unseen interior tile. Never a tile
        // the player can currently see.
        GridPoint? best = null;
        var bestDistance = -1;
        for (var y = 1; y < _state.Height - 1; y++)
        {
            for (var x = 1; x < _state.Width - 1; x++)
            {
                var point = new GridPoint(x, y);
                if (!CanSpawnAt(point) || PlayerCanSee(perception, playerPosition, point))
                {
                    continue;
                }

                var distance = Chebyshev(playerPosition, point);
                if (distance > bestDistance)
                {
                    bestDistance = distance;
                    best = point;
                }
            }
        }

        return best;
    }

    // "Seen" uses the game's one sight rule: within the default sight radius with line of
    // sight - the same rule perception snapshots and witnessing use.
    private static bool PlayerCanSee(PerceptionSystem perception, GridPoint playerPosition, GridPoint point) =>
        Chebyshev(playerPosition, point) <= PerceptionSystem.DefaultSightRadius
        && perception.HasLineOfSight(playerPosition, point);

    private static int Chebyshev(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private bool CanSpawnAt(GridPoint point) =>
        point.X > 0
        && point.Y > 0
        && point.X < _state.Width - 1
        && point.Y < _state.Height - 1
        && !_state.BlockingTerrain.Contains(point)
        && !_state.Entities.Values.Any(entity =>
            entity.TryGet<PositionComponent>(out var position)
            && position.Position == point
            && entity.TryGet<PhysicalComponent>(out var physical)
            && physical.BlocksMovement
            && (!entity.TryGet<ActorComponent>(out var actor) || actor.Alive));

    private static string DescribeTradition(IReadOnlyList<string> tags)
    {
        if (tags.Contains("law", StringComparer.OrdinalIgnoreCase)
            || tags.Contains("imperial", StringComparer.OrdinalIgnoreCase))
        {
            return "marble law tries to make the world hold still.";
        }

        if (tags.Contains("hollowmere", StringComparer.OrdinalIgnoreCase)
            || tags.Contains("water", StringComparer.OrdinalIgnoreCase))
        {
            return "water remembers names the empire tried to flatten.";
        }

        if (tags.Contains("fire", StringComparer.OrdinalIgnoreCase))
        {
            return "fire is treated here as witness, appetite, and warning.";
        }

        return "the world keeps a little color in reserve.";
    }

    private static string DescribeTags(IReadOnlyList<string> tags) =>
        tags.Count == 0 ? "no obvious tradition" : string.Join(", ", tags.Take(6));

    private static IReadOnlyList<string> TagsFor(Entity entity)
    {
        var tags = new List<string>();
        if (entity.TryGet<TagsComponent>(out var tagComponent))
        {
            tags.AddRange(tagComponent.Tags);
        }

        if (entity.TryGet<ItemComponent>(out var item))
        {
            tags.AddRange(item.Tags);
            tags.Add(item.Material);
        }

        if (entity.TryGet<FixtureComponent>(out var fixture))
        {
            tags.AddRange(fixture.Tags);
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(tag => tag).ToArray();
    }
}
