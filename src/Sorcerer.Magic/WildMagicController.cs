using System.Text.Json;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.References;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Magic.Auditing;
using Sorcerer.Magic.Capabilities;
using Sorcerer.Magic.Costs;
using Sorcerer.Magic.Operations;
using Sorcerer.Magic.Resolution;

namespace Sorcerer.Magic;

public sealed class WildMagicController : IWildMagicController
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly ISpellAuditSink _audit;
    private readonly ISpellProvider _provider;
    private readonly ISpellRouter _router;
    private readonly OperationRegistry _registry;
    private readonly CapabilityRegistry _capabilities;

    public WildMagicController(
        ISpellProvider provider,
        OperationRegistry? registry = null,
        ISpellAuditSink? audit = null,
        CapabilityRegistry? capabilities = null,
        ISpellRouter? router = null)
    {
        _provider = provider;
        _registry = registry ?? OperationRegistry.CreateDefault();
        _audit = audit ?? NullSpellAuditSink.Instance;
        _capabilities = capabilities ?? CapabilityRegistry.CreateDefault();
        _router = router ?? NullSpellRouter.Instance;
    }

    public async Task<MaterializedMagicResolution> ResolveAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken)
    {
        var selectedCapabilities = await RouteCapabilitiesAsync(command.Text, cancellationToken);
        var request = BuildSpellRequest(engine, command.Text, selectedCapabilities);
        SpellProviderResult providerResult;
        try
        {
            providerResult = await _provider.ResolveAsync(request, cancellationToken);

            // Escape hatch (docs/OPTIMIZATION_PLAN.md WS1.2): the model may answer
            // {"needsCapability":"name"} when the loaded mechanics don't fit. Load that card and
            // re-resolve exactly once; a card we don't know, one already loaded, or a second such
            // answer just falls through to the null-resolution technical-failure path below.
            if (providerResult.RequestedCapability is { Length: > 0 } requested
                && _capabilities.Find(requested) is { } extraCard
                && !selectedCapabilities.Any(card => card.Id.Equals(extraCard.Id, StringComparison.OrdinalIgnoreCase)))
            {
                selectedCapabilities = selectedCapabilities.Append(extraCard).ToArray();
                request = BuildSpellRequest(engine, command.Text, selectedCapabilities);
                providerResult = await _provider.ResolveAsync(request, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MaterializedMagicResolution(
                _provider.Name,
                command.Text,
                command.Performance ?? CastPerformance.Neutral,
                RawText: "",
                Accepted: false,
                TechnicalFailure: true,
                Error: ex.Message,
                EffectTypes: Array.Empty<string>(),
                ResolvedMagicJson: null);
        }

        if (providerResult.TechnicalFailure || providerResult.Resolution is null)
        {
            var error = providerResult.Error
                ?? (providerResult.RequestedCapability is { Length: > 0 }
                    ? "The resolver asked for a capability it could not use, and produced no spell."
                    : "Spell provider failed.");
            return new MaterializedMagicResolution(
                providerResult.Provider,
                command.Text,
                command.Performance ?? CastPerformance.Neutral,
                providerResult.RawText,
                Accepted: false,
                TechnicalFailure: true,
                Error: error,
                EffectTypes: Array.Empty<string>(),
                ResolvedMagicJson: null,
                ProviderStats: providerResult.Stats);
        }

        var resolution = RepairNarration(command.Text, Normalize(providerResult.Resolution));
        return new MaterializedMagicResolution(
            providerResult.Provider,
            command.Text,
            command.Performance ?? CastPerformance.Neutral,
            providerResult.RawText,
            resolution.Accepted,
            TechnicalFailure: false,
            Error: resolution.Accepted ? null : resolution.RejectedReason ?? "The spell refuses to become real.",
            resolution.Effects.Select(effect => effect.Type).ToArray(),
            SerializeResolution(resolution),
            ProviderStats: providerResult.Stats);
    }

    public ActionResult ApplyResolved(GameEngine engine, MaterializedMagicResolution materialized)
    {
        var turnBefore = engine.State.Turn;
        var command = new CastCommand(materialized.SpellText, materialized.Performance);
        // Apply-time context is only used for the audit record, so it uses keyword-only selection:
        // re-running the LLM router here would spend a second call and be non-deterministic.
        var request = BuildSpellRequest(engine, materialized.SpellText, _capabilities.Select(materialized.SpellText));

        if (materialized.TechnicalFailure || string.IsNullOrWhiteSpace(materialized.ResolvedMagicJson))
        {
            var error = materialized.Error ?? "Spell provider failed.";
            var technicalProviderResult = new SpellProviderResult(
                materialized.Provider,
                materialized.RawText,
                Resolution: null,
                TechnicalFailure: true,
                Error: error,
                Stats: materialized.ProviderStats);
            var result = TechnicalFailure(engine, materialized.Provider, turnBefore, error);
            Audit(technicalProviderResult, command, request, result, Array.Empty<string>());
            return result;
        }

        SpellResolution resolution;
        SpellProviderResult providerResult;
        try
        {
            resolution = RepairNarration(
                materialized.SpellText,
                Normalize(SpellResolutionJson.Parse(materialized.ResolvedMagicJson, _registry)));
            providerResult = new SpellProviderResult(
                materialized.Provider,
                materialized.RawText,
                resolution,
                TechnicalFailure: false,
                Error: null,
                Stats: materialized.ProviderStats);
        }
        catch (Exception ex)
        {
            providerResult = new SpellProviderResult(
                materialized.Provider,
                materialized.RawText,
                Resolution: null,
                TechnicalFailure: true,
                Error: ex.Message);
            var result = TechnicalFailure(engine, materialized.Provider, turnBefore, ex.Message);
            Audit(providerResult, command, request, result, new[] { "materialized_parse_failed" });
            return result;
        }

        if (!resolution.Accepted)
        {
            var result = WithResolutionJson(Rejected(
                engine,
                providerResult.Provider,
                turnBefore,
                resolution.RejectedReason ?? "The spell refuses to become real.",
                Array.Empty<string>()),
                resolution);
            Audit(providerResult, command, request, result, Array.Empty<string>());
            return result;
        }

        resolution = WithSummonReferenceRepair(resolution);
        resolution = RepairUnpayableItemCosts(engine, resolution);
        var projectedEntities = ProjectedSummonedEntities(engine, resolution);
        var effectContext = new EffectContext(
            engine,
            engine.State.ControlledEntity,
            new EngineReferenceResolver(engine, engine.State.ControlledEntity, projectedEntities: projectedEntities));
        var validationIssues = ValidateResolution(engine, command.Text, resolution, effectContext);
        if (validationIssues.Count > 0)
        {
            var reason = string.Join("; ", validationIssues.Select(issue => issue.Message).Distinct());
            var result = validationIssues.Any(issue => issue.TechnicalFailure)
                ? TechnicalFailure(engine, providerResult.Provider, turnBefore, reason)
                : Rejected(
                    engine,
                    providerResult.Provider,
                    turnBefore,
                    reason,
                    resolution.Effects.Select(effect => effect.Type).ToArray());
            result = WithResolutionJson(result, resolution);
            Audit(providerResult, command, request, result, validationIssues.Select(issue => issue.Code).ToArray());
            return result;
        }

        var transaction = GameTransaction.Begin(engine.State);
        var deltas = new List<StateDelta>();
        try
        {
            // The transaction above already owns a whole-cast snapshot and this method rolls
            // it back wholesale if any nested consequence rejects or the post-cast state is
            // invalid (below), so nested consequence applications don't need their own.
            using var scope = WorldConsequenceGuard.EnterScope();
            if (!string.IsNullOrWhiteSpace(resolution.OutcomeText))
            {
                deltas.AddRange(ApplyMagicMessage(
                    engine,
                    resolution.OutcomeText,
                    "wildMagicOutcome",
                    "Accepted wild magic narration.").Deltas);
            }

            var effectStart = deltas.Count;
            foreach (var effect in resolution.Effects)
            {
                var operation = _registry.Resolve(effect.Type)
                    ?? throw new InvalidOperationException($"Unsupported effect type {effect.Type}.");
                deltas.AddRange(operation.Apply(effectContext, effect));
            }

            var effectEnd = deltas.Count;
            deltas.AddRange(SpellCostApplier.Apply(engine, resolution.Costs));
            if (deltas.Count == effectEnd)
            {
                // Distinguishes a genuinely free cast from "the cost line was silently dropped":
                // no cost line at all previously left this ambiguous (FEEL_LOG [03]).
                deltas.AddRange(ApplyMagicMessage(engine, "Cost: nothing.", "costNothing", "Accepted wild magic cost was free.").Deltas);
            }

            var costEnd = deltas.Count;
            var actor = engine.State.ControlledEntity;
            var actorPosition = actor.Get<PositionComponent>().Position;
            var effectPoint = FirstEffectPoint(engine, deltas);
            var deed = engine.ApplyConsequence(WorldConsequence.RecordDeed(
                "engine",
                actor.Id.Value,
                materialized.DeedKind,
                MagnitudeFor(resolution.Severity),
                actorPosition.X,
                actorPosition.Y,
                effectPoint?.X,
                effectPoint?.Y,
                resolution.Effects.Select(effect => effect.Type).Concat(new[] { resolution.Severity }).ToArray(),
                sourceEntityId: actor.Id.Value));
            deltas.AddRange(deed.Deltas);

            var rejectedDeltas = HiddenDiagnostics(RejectedDeltas(deltas));
            if (rejectedDeltas.Count > 0)
            {
                transaction.Rollback();

                // Engine-authored consequences (narration before effectStart, deed after costEnd)
                // should never reject; if one does, that is our bug, not the world refusing.
                var engineSegmentRejected =
                    AnyRejectedIn(deltas, 0, effectStart) || AnyRejectedIn(deltas, costEnd, deltas.Count);
                if (engineSegmentRejected)
                {
                    var error = RejectedApplySummary(rejectedDeltas);
                    var failure = WithResolutionJson(
                        TechnicalFailure(engine, providerResult.Provider, turnBefore, error),
                        resolution);
                    failure = failure with
                    {
                        Deltas = rejectedDeltas.Concat(failure.Deltas).ToArray(),
                    };
                    Audit(providerResult, command, request, failure, new[] { "apply_consequence_rejected" });
                    return failure;
                }

                // A world-refused effect or cost is an in-world rejection, never a technical
                // failure: the whole working collapses, nothing mutates, and the turn is spent.
                var errors = RejectedApplyErrors(rejectedDeltas);
                var reason = AnyRejectedIn(deltas, effectEnd, costEnd)
                    ? $"The spell's price cannot be paid, and the working collapses. ({errors})"
                    : $"The magic reaches for what is not there, and the working collapses. ({errors})";
                var rejection = WithResolutionJson(
                    Rejected(
                        engine,
                        providerResult.Provider,
                        turnBefore,
                        reason,
                        resolution.Effects.Select(effect => effect.Type).ToArray()),
                    resolution);
                rejection = rejection with
                {
                    Deltas = rejectedDeltas.Concat(rejection.Deltas).ToArray(),
                };
                Audit(providerResult, command, request, rejection, new[] { "apply_consequence_rejected" });
                return rejection;
            }

            if (deltas.Count == 0)
            {
                var message = "The spell answers with a small blue spark.";
                deltas.AddRange(ApplyMagicMessage(
                    engine,
                    message,
                    "wildMagicFallback",
                    "Accepted wild magic produced no other visible effect.").Deltas);
            }

            var turnDeltas = engine.AdvanceTurn();
            var stateReport = engine.ValidateState();
            if (!stateReport.IsValid)
            {
                var error = string.Join("; ", stateReport.Issues.Select(issue => issue.Message));
                transaction.Rollback();
                var failure = TechnicalFailure(engine, providerResult.Provider, turnBefore, error);
                Audit(providerResult, command, request, failure, stateReport.Issues.Select(issue => issue.Code).ToArray());
                return failure;
            }

            transaction.Commit();
            var result = new ActionResult
            {
                Action = "cast",
                Success = true,
                ConsumedTurn = true,
                TurnBefore = turnBefore,
                TurnAfter = engine.State.Turn,
                Messages = deltas.PlayerMessages()
                    .Concat(turnDeltas.PlayerMessages())
                    .ToArray(),
                Deltas = deltas.Concat(turnDeltas).ToArray(),
                Magic = new MagicResolutionRecord(
                    providerResult.Provider,
                    Accepted: true,
                    TechnicalFailure: false,
                    EffectTypes: resolution.Effects.Select(effect => effect.Type).ToArray(),
                    Error: null)
                {
                    ResolvedMagicJson = SerializeResolution(resolution),
                },
            };
            Audit(providerResult, command, request, result, Array.Empty<string>());
            return result;
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            var result = TechnicalFailure(engine, providerResult.Provider, turnBefore, ex.Message);
            Audit(providerResult, command, request, result, new[] { "application_exception" });
            return result;
        }
    }

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<StateDelta> RejectedDeltas(IEnumerable<StateDelta> deltas) =>
        deltas.Where(IsRejectedDelta).ToArray();

    private static bool AnyRejectedIn(IReadOnlyList<StateDelta> deltas, int start, int end)
    {
        for (var i = start; i < end && i < deltas.Count; i++)
        {
            if (IsRejectedDelta(deltas[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static StateDelta AsHiddenDiagnostic(StateDelta delta)
    {
        var details = new Dictionary<string, object?>(delta.Details, StringComparer.OrdinalIgnoreCase)
        {
            ["auditOnly"] = true,
            ["playerVisible"] = false,
        };
        return delta with { Details = details };
    }

    private static IReadOnlyList<StateDelta> HiddenDiagnostics(IReadOnlyList<StateDelta> deltas) =>
        deltas.Select(AsHiddenDiagnostic).ToArray();

    private static string RejectedApplyErrors(IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = rejectedDeltas
            .Select(delta =>
            {
                var error = delta.Details.TryGetValue("error", out var rawError) && rawError is not null
                    ? rawError.ToString()
                    : delta.Summary;
                return string.IsNullOrWhiteSpace(delta.Target)
                    ? error
                    : $"{delta.Target}: {error}";
            })
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return string.Join("; ", errors);
    }

    private static string RejectedApplySummary(IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = RejectedApplyErrors(rejectedDeltas);
        return string.IsNullOrWhiteSpace(errors)
            ? "Accepted wild magic produced a rejected world consequence."
            : $"Accepted wild magic produced a rejected world consequence: {errors}";
    }

    public async Task<ActionResult> CastAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(engine, command, cancellationToken);
        return ApplyResolved(engine, resolved);
    }

    /// <summary>
    /// Uses deterministic routing first and consults the slow semantic router only for an opaque
    /// or under-routed multi-clause spell. A router failure, timeout, or empty answer degrades
    /// cleanly to keyword-only selection; only outer cancellation propagates.
    /// </summary>
    private async Task<IReadOnlyList<CapabilityCard>> RouteCapabilitiesAsync(
        string spellText,
        CancellationToken cancellationToken)
    {
        if (!_capabilities.ShouldConsultRouter(spellText))
        {
            return _capabilities.Select(spellText);
        }

        IReadOnlyList<string> routerNames = Array.Empty<string>();
        try
        {
            var route = await _router.RouteAsync(spellText, _capabilities.CapabilityIndex(), cancellationToken);
            if (!route.TechnicalFailure)
            {
                routerNames = route.CapabilityNames;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Router is best-effort: any failure falls back to keyword-only selection below.
        }

        return _capabilities.Select(spellText, routerNames);
    }

    private SpellRequest BuildSpellRequest(
        GameEngine engine,
        string spellText,
        IReadOnlyList<CapabilityCard> selectedCapabilities)
    {
        var operationIndex = _registry.ToRoutedIndex(
            selectedCapabilities.SelectMany(card => card.EffectTypes),
            _capabilities.AllEffectTypes());
        var requiredContext = selectedCapabilities
            .SelectMany(card => card.RequiredContext)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var contextView = engine.MagicContext(operationIndex, requiredContext, spellText);
        return new SpellRequest(
            spellText,
            contextView,
            operationIndex.Names,
            selectedCapabilities,
            _capabilities.CapabilityNames());
    }

    private void Audit(
        SpellProviderResult providerResult,
        CastCommand command,
        SpellRequest request,
        ActionResult result,
        IReadOnlyList<string> validationErrors) =>
        _audit.Record(new SpellAuditEntry(
            DateTimeOffset.UtcNow,
            providerResult.Provider,
            command.Text,
            request.Context,
            providerResult.RawText,
            providerResult.Resolution,
            result,
            validationErrors,
            command.Performance,
            BuildRoutingRecord(request, providerResult.Stats)));

    private static SpellRoutingRecord BuildRoutingRecord(SpellRequest request, Sorcerer.Core.Telemetry.ProviderCallStats? stats) =>
        new(
            request.SelectedCapabilities?.Select(card => card.Id).ToArray() ?? Array.Empty<string>(),
            request.SupportedOperations.Count,
            System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Context, JsonOptions)),
            stats);

    private SpellResolution Normalize(SpellResolution resolution) =>
        resolution with
        {
            Effects = resolution.Effects
                .Select(effect => effect with { Type = _registry.Canonicalize(effect.Type) })
                .ToArray(),
        };

    private static SpellResolution RepairNarration(string spellText, SpellResolution resolution)
    {
        var effectTypes = resolution.Effects
            .Select(effect => effect.Type)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mechanicKinds = MechanicalKinds(resolution.Effects);
        var canClipSupplementalNarration = HasMechanicalBacking(effectTypes);
        if (!canClipSupplementalNarration)
        {
            return resolution;
        }

        var effects = resolution.Effects
            .Where(effect => !IsUnsafeSupplementalMessage(effect, effectTypes))
            .ToArray();
        var outcomeText = resolution.OutcomeText;
        if (NarrationClaimsPlayerCommand(outcomeText))
        {
            outcomeText = RepairedOutcomeText(spellText, mechanicKinds);
        }

        return resolution with
        {
            OutcomeText = outcomeText,
            Effects = effects,
        };
    }

    private static bool IsUnsafeSupplementalMessage(SpellEffect effect, IReadOnlySet<string> effectTypes)
    {
        if (!effect.Type.Equals("message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var text = ReadString(effect.Fields, "text", ReadString(effect.Fields, "message", ""));
        return NarrationClaimsPlayerCommand(text);
    }

    private static bool HasMechanicalBacking(IReadOnlySet<string> effectTypes) =>
        effectTypes.Any(effect => !effect.Equals("message", StringComparison.OrdinalIgnoreCase));

    private static string RepairedOutcomeText(string spellText, IReadOnlySet<string> effectTypes)
    {
        if (HasAnyEffect(effectTypes, "createPromise"))
        {
            return PromiseIntentNeedsLedger(spellText)
                ? "The spell binds itself to a future event."
                : "The spell leaves a promise the world can remember.";
        }

        if (HasAnyEffect(effectTypes, "scheduleEvent", "createTrigger"))
        {
            return "The spell sets a future working in motion.";
        }

        return "The spell takes a concrete shape.";
    }

    private static ActionResult WithResolutionJson(ActionResult result, SpellResolution resolution) =>
        result.Magic is null
            ? result
            : result with
            {
                Magic = result.Magic with
                {
                    ResolvedMagicJson = SerializeResolution(resolution),
                },
            };

    private static string SerializeResolution(SpellResolution resolution) =>
        JsonSerializer.Serialize(resolution, JsonOptions);

    private static SpellResolution WithSummonReferenceRepair(SpellResolution resolution)
    {
        var effects = resolution.Effects
            .Select(effect => effect.Type.Equals("summon", StringComparison.OrdinalIgnoreCase)
                ? effect with { Fields = RepairSummonFields(effect, resolution.Effects) }
                : effect)
            .ToArray();
        return resolution with { Effects = effects };
    }

    private static IReadOnlyDictionary<string, object?> RepairSummonFields(
        SpellEffect summon,
        IReadOnlyList<SpellEffect> allEffects)
    {
        if (HasFieldText(summon.Fields, "entityId", "entity_id", "id"))
        {
            return summon.Fields;
        }

        var name = FirstNonBlank(
            ReadString(summon.Fields, "name", ""),
            ReadString(summon.Fields, "entityName", ""),
            ReadString(summon.Fields, "entity_name", ""),
            "summon")!;
        var prefix = OperationHelpers.NormalizeToken(name, "summon");
        var referencedId = allEffects
            .Where(effect => !ReferenceEquals(effect, summon))
            .SelectMany(TargetFieldTexts)
            .Select(target => OperationHelpers.NormalizeToken(target, ""))
            .FirstOrDefault(target => target.Equals(prefix, StringComparison.OrdinalIgnoreCase)
                || target.StartsWith($"{prefix}_", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(referencedId))
        {
            return summon.Fields;
        }

        return new Dictionary<string, object?>(summon.Fields, StringComparer.OrdinalIgnoreCase)
        {
            ["entityId"] = referencedId,
        };
    }

    private static IReadOnlyDictionary<string, Entity> ProjectedSummonedEntities(
        GameEngine engine,
        SpellResolution resolution)
    {
        var projected = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var serial = engine.State.NextEntitySerial;
        var origin = engine.State.ControlledEntity.Get<PositionComponent>().Position;
        foreach (var effect in resolution.Effects)
        {
            if (!effect.Type.Equals("summon", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = FirstNonBlank(
                ReadString(effect.Fields, "name", ""),
                ReadString(effect.Fields, "entityName", ""),
                ReadString(effect.Fields, "entity_name", ""),
                "summoned wonder")!;
            var prefix = OperationHelpers.NormalizeToken(name, "summon");
            var explicitId = FirstNonBlank(
                ReadString(effect.Fields, "entityId", ""),
                ReadString(effect.Fields, "entity_id", ""),
                ReadString(effect.Fields, "id", ""));
            var id = string.IsNullOrWhiteSpace(explicitId)
                ? $"{prefix}_{serial++}"
                : OperationHelpers.NormalizeToken(explicitId, prefix);
            if (engine.EntityById(id) is not null || projected.ContainsKey(id))
            {
                continue;
            }

            var faction = OperationHelpers.NormalizeToken(ReadString(effect.Fields, "faction", "player"), "player");
            var hp = Math.Clamp(ReadInt(effect.Fields, "hp", 5), 1, 20);
            var attack = Math.Clamp(ReadInt(effect.Fields, "attack", 2), 0, 10);
            projected[id] = new Entity(EntityId.Create(id), name)
                .Set(new PositionComponent(origin))
                .Set(new RenderableComponent('*', faction))
                .Set(new TagsComponent(new[] { "summoned", "wild_magic", "projected" }))
                .Set(new PhysicalComponent(BlocksMovement: true, Material: "summoned"))
                .Set(new ActorComponent(hp, hp, 0, 0, attack, 0, faction));
        }

        return projected;
    }

    private static bool HasFieldText(IReadOnlyDictionary<string, object?> fields, params string[] keys) =>
        keys.Any(key => !string.IsNullOrWhiteSpace(ReadString(fields, key, "")));

    private static IEnumerable<string> TargetFieldTexts(SpellEffect effect)
    {
        foreach (var key in new[] { "target", "targetEntityId", "target_entity_id", "targetId", "target_id", "entityId", "entity_id" })
        {
            var value = ReadString(effect.Fields, key, "");
            if (!string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    private IReadOnlyList<SpellValidationIssue> ValidateResolution(
        GameEngine engine,
        string spellText,
        SpellResolution resolution,
        EffectContext effectContext)
    {
        var issues = new List<SpellValidationIssue>();
        if (resolution.Accepted && resolution.Effects.Count == 0)
        {
            issues.Add(new SpellValidationIssue(
                "no_effects",
                "Accepted spell resolutions need at least one effect."));
        }

        foreach (var effect in resolution.Effects)
        {
            var operation = _registry.Resolve(effect.Type);
            if (operation is null)
            {
                issues.Add(new SpellValidationIssue(
                    "unsupported_effect",
                    $"Unsupported effect type {effect.Type}."));
                continue;
            }

            var outcome = operation.Validate(effectContext, effect);
            if (!outcome.Ok)
            {
                issues.Add(new SpellValidationIssue(
                    outcome.Fatal ? "operation_shape" : "operation_rejected",
                    outcome.RejectReason ?? $"{effect.Type} could not be applied.",
                    outcome.Fatal));
            }
        }

        issues.AddRange(MechanicalCurseValidator.Validate(engine, resolution));
        ValidateNarrativeMechanics(spellText, resolution, issues);
        issues.AddRange(ValidateCosts(engine, resolution.Costs));
        return issues;
    }

    private static void ValidateNarrativeMechanics(
        string spellText,
        SpellResolution resolution,
        List<SpellValidationIssue> issues)
    {
        var mechanicKinds = MechanicalKinds(resolution.Effects);
        if (PromiseIntentNeedsLedger(spellText) && !HasAnyEffect(mechanicKinds, "createPromise", "scheduleEvent", "createTrigger"))
        {
            issues.Add(new SpellValidationIssue(
                "promise_effect_missing",
                "The resolver treated a promised future as flavor. Promise, prophecy, oath, and omen spells need createPromise, scheduleEvent, or createTrigger.",
                TechnicalFailure: true));
        }

        if (NarrationClaimsUnsupportedMechanics(resolution.OutcomeText, mechanicKinds, out var outcomeReason))
        {
            issues.Add(new SpellValidationIssue(
                "outcome_claims_unsupported_mechanics",
                outcomeReason,
                TechnicalFailure: true));
        }

        foreach (var effect in resolution.Effects.Where(effect => effect.Type.Equals("message", StringComparison.OrdinalIgnoreCase)))
        {
            var text = ReadString(effect.Fields, "text", ReadString(effect.Fields, "message", ""));
            if (NarrationClaimsUnsupportedMechanics(text, mechanicKinds, out var reason))
            {
                issues.Add(new SpellValidationIssue(
                    "message_claims_unsupported_mechanics",
                    reason,
                    TechnicalFailure: true));
            }
        }
    }

    private static bool PromiseIntentNeedsLedger(string text)
    {
        var tokens = Tokens(text);
        return tokens.Contains("promise")
            || tokens.Contains("promises")
            || tokens.Contains("promised")
            || tokens.Contains("prophecy")
            || tokens.Contains("prophecies")
            || tokens.Contains("omen")
            || tokens.Contains("omens")
            || tokens.Contains("oath")
            || tokens.Contains("oaths");
    }

    private static bool NarrationClaimsUnsupportedMechanics(
        string text,
        IReadOnlySet<string> effectTypes,
        out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        if (NarrationClaimsPlayerCommand(lower))
        {
            reason = "Message and outcome text cannot perform player commands; use the matching engine command or operation instead.";
            return true;
        }

        if (ClaimsRouteReveal(lower)
            && !HasAnyEffect(effectTypes, "createPromise", "scheduleEvent", "createTrigger", "createTiles", "summon", "conjureCreature", "createRoute", "spawnFixture", "spawnEntity", "spawnItem"))
        {
            reason = "A route, passage, or landmark reveal needs a backing promise, trigger, terrain change, or spawned site operation.";
            return true;
        }

        if (ClaimsDoorState(lower)
            && !HasAnyEffect(effectTypes, "transformEntity", "createTiles", "createPromise", "scheduleEvent", "createTrigger", "openOrUnlock"))
        {
            reason = "Opening or unlocking a door must be represented by an engine operation, not only by narration.";
            return true;
        }

        if (ClaimsInventoryChange(lower)
            && !HasAnyEffect(effectTypes, "modifyInventory", "conjureItem", "transformItem", "transferItem", "spawnItem", "addMerchantStock", "offerTrade", "executeTrade"))
        {
            reason = "Inventory gains, losses, purchases, or sales must be represented by inventory/item operations, not only by narration.";
            return true;
        }

        return false;
    }

    private static bool ClaimsRouteReveal(string lower) =>
        ContainsAny(lower, "hidden route", "secret route", "route lies", "reveals the route", "reveals a route", "reveals the hidden route", "reveals a hidden route", "revealing the route", "revealing a route", "revealing the hidden route")
        || (ContainsAny(lower, "passage", "path", "landmark") && ContainsAny(lower, "reveals", "revealed", "appears", "opens", "lies beneath", "lies beyond"));

    private static bool ClaimsDoorState(string lower) =>
        ContainsAny(lower, "door opens", "door unlocks", "gate opens", "gate unlocks", "cell opens", "cell unlocks", "lock opens", "lock unlocks")
        || (ContainsAny(lower, "door", "gate", "cell door", "lock") && ContainsAny(lower, "is open", "is unlocked", "swings open"));

    private static bool ClaimsInventoryChange(string lower) =>
        ContainsAny(lower, "added to your inventory", "appears in your inventory", "you gain ", "you receive ", "you buy ", "you sell ");

    private static bool NarrationClaimsPlayerCommand(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        return ContainsAny(lower, "you read ", "you read:", "you read the", "you travel ", "you move ", "you buy ", "you sell ", "you pick up ", "you take ");
    }

    private static HashSet<string> MechanicalKinds(IEnumerable<SpellEffect> effects)
    {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var effect in effects)
        {
            AddMechanicalKind(kinds, effect.Type);
            if (!effect.Type.Equals("consequence", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var consequenceType = FirstNonBlank(
                ReadString(effect.Fields, "consequenceType", ""),
                ReadString(effect.Fields, "consequence_type", ""),
                ReadString(effect.Fields, "worldConsequenceType", ""),
                ReadString(effect.Fields, "world_consequence_type", ""));
            AddMechanicalKind(kinds, consequenceType);
        }

        return kinds;
    }

    private static void AddMechanicalKind(HashSet<string> kinds, string? kind)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return;
        }

        kinds.Add(kind);
        var normalized = WorldConsequenceTypes.Normalize(kind);
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            kinds.Add(normalized);
        }
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static bool HasAnyEffect(IReadOnlySet<string> effectTypes, params string[] names) =>
        names.Any(name => effectTypes.Contains(name) || effectTypes.Contains(WorldConsequenceTypes.Normalize(name)));

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static HashSet<string> Tokens(string text) =>
        text.Split(
                new[] { ' ', '\t', '\r', '\n', '.', ',', ';', ':', '!', '?', '\'', '"', '-', '_', '/', '\\', '(', ')', '[', ']' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<SpellValidationIssue> ValidateCosts(
        GameEngine engine,
        IReadOnlyList<SpellCost> costs)
    {
        var issues = new List<SpellValidationIssue>();
        foreach (var cost in costs)
        {
            switch (cost.Type)
            {
                case "mana":
                case "health":
                case "hp":
                case "maxHealth":
                case "max_health":
                case "maxMana":
                case "max_mana":
                case "status":
                case "curse":
                    break;
                case "item":
                    ValidateItemCost(engine, cost, issues);
                    break;
                default:
                    issues.Add(new SpellValidationIssue(
                        "unsupported_cost",
                        $"Unsupported cost type {cost.Type}."));
                    break;
            }
        }

        return issues;
    }

    /// <summary>
    /// The resolver sometimes charges an item the caster does not carry (a visible-but-unowned
    /// object, or a hallucinated component). Rather than reject the player's otherwise-valid spell,
    /// substitute an unpayable item cost with a small mana cost - the caster improvises with raw
    /// effort instead of the missing focus. Item costs the caster can actually pay are untouched,
    /// and treasured items are never spent on the resolver's whim.
    /// </summary>
    private static SpellResolution RepairUnpayableItemCosts(GameEngine engine, SpellResolution resolution)
    {
        if (resolution.Costs.Count == 0)
        {
            return resolution;
        }

        engine.State.ControlledEntity.TryGet<InventoryComponent>(out var inventory);
        var repaired = new List<SpellCost>(resolution.Costs.Count);
        var changed = false;
        foreach (var cost in resolution.Costs)
        {
            if (!cost.Type.Equals("item", StringComparison.OrdinalIgnoreCase))
            {
                repaired.Add(cost);
                continue;
            }

            var name = ReadString(cost.Fields, "item", ReadString(cost.Fields, "name", ""));
            var quantity = Math.Max(1, ReadInt(cost.Fields, "quantity", ReadInt(cost.Fields, "amount", 1)));
            var available = inventory is not null && inventory.Items.TryGetValue(name, out var have) ? have : 0;
            if (!string.IsNullOrWhiteSpace(name) && available >= quantity)
            {
                // The caster carries it: keep the item cost. If it is a treasured item, downstream
                // validation still fizzles the spell (protecting the player's guarded possessions);
                // only genuinely unpayable costs are substituted below.
                repaired.Add(cost);
                continue;
            }

            repaired.Add(new SpellCost("mana", new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["amount"] = 3,
                ["substitutedForItem"] = string.IsNullOrWhiteSpace(name) ? "item" : name,
            }));
            changed = true;
        }

        return changed ? resolution with { Costs = repaired } : resolution;
    }

    private static void ValidateItemCost(
        GameEngine engine,
        SpellCost cost,
        List<SpellValidationIssue> issues)
    {
        var name = ReadString(cost.Fields, "item", ReadString(cost.Fields, "name", ""));
        var quantity = Math.Max(1, ReadInt(cost.Fields, "quantity", ReadInt(cost.Fields, "amount", 1)));
        var allowProtected = ReadBool(cost.Fields, "allowProtected", ReadBool(cost.Fields, "allow_protected", false));
        if (string.IsNullOrWhiteSpace(name))
        {
            issues.Add(new SpellValidationIssue("item_cost_missing_name", "Item cost does not name an item."));
            return;
        }

        if (!engine.State.ControlledEntity.TryGet<InventoryComponent>(out var inventory)
            || !inventory.Items.TryGetValue(name, out var available)
            || available < quantity)
        {
            issues.Add(new SpellValidationIssue("item_cost_unavailable", $"Item cost is unavailable: {name}."));
            return;
        }

        if (!allowProtected && inventory.TreasuredItems.Contains(name))
        {
            issues.Add(new SpellValidationIssue(
                "item_cost_protected",
                $"The spell reaches for protected item {name}; the casting fizzles before consuming it."));
        }
    }

    private static ActionResult TechnicalFailure(
        GameEngine engine,
        string provider,
        int turnBefore,
        string error)
    {
        var message = ApplyMagicMessage(
            engine,
            error,
            "wildMagicTechnicalFailure",
            "Wild magic failed for a technical provider or validation reason.");
        return new ActionResult
        {
            Action = "cast",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = turnBefore,
            TurnAfter = engine.State.Turn,
            Messages = message.Messages.Count == 0 ? new[] { error } : message.Messages,
            Deltas = message.Deltas,
            TechnicalFailure = true,
            Magic = new MagicResolutionRecord(
                provider,
                Accepted: false,
                TechnicalFailure: true,
                EffectTypes: Array.Empty<string>(),
                Error: error),
        };
    }

    private static ActionResult Rejected(
        GameEngine engine,
        string provider,
        int turnBefore,
        string reason,
        IReadOnlyList<string> effectTypes)
    {
        var rejection = ApplyMagicMessage(
            engine,
            reason,
            "wildMagicRejected",
            "Wild magic produced an intentional in-world rejection.");
        var turnDeltas = engine.AdvanceTurn();
        return new ActionResult
        {
            Action = "cast",
            Success = false,
            ConsumedTurn = true,
            TurnBefore = turnBefore,
            TurnAfter = engine.State.Turn,
            Messages = rejection.Messages.Concat(turnDeltas.PlayerMessages()).ToArray(),
            Magic = new MagicResolutionRecord(
                provider,
                Accepted: false,
                TechnicalFailure: false,
                EffectTypes: effectTypes,
                Error: reason),
            Deltas = rejection.Deltas.Concat(turnDeltas).ToArray(),
        };
    }

    private static WorldConsequenceApplyResult ApplyMagicMessage(
        GameEngine engine,
        string message,
        string operation,
        string reason) =>
        engine.ApplyConsequence(WorldConsequence.Message(
            "wild_magic",
            message,
            targetEntityId: engine.State.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: engine.State.ControlledEntityId.Value,
            evidence: message,
            reason: reason,
            operation: operation,
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = true,
            }));

    private static string ReadString(IReadOnlyDictionary<string, object?> fields, string key, string fallback) =>
        fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value) ?? fallback
            : fallback;

    private static int ReadInt(IReadOnlyDictionary<string, object?> fields, string key, int fallback) =>
        fields.TryGetValue(key, out var value) && int.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : fallback;

    private static bool ReadBool(IReadOnlyDictionary<string, object?> fields, string key, bool fallback) =>
        fields.TryGetValue(key, out var value) && bool.TryParse(Convert.ToString(value), out var parsed)
            ? parsed
            : fallback;

    private static int MagnitudeFor(string severity) =>
        severity.Trim().ToLowerInvariant() switch
        {
            "minor" => 1,
            "moderate" => 3,
            "major" => 5,
            "catastrophic" => 8,
            _ => 2,
        };

    private static GridPoint? FirstEffectPoint(GameEngine engine, IReadOnlyList<StateDelta> deltas)
    {
        foreach (var delta in deltas)
        {
            if (engine.EntityById(delta.Target) is { } entity
                && entity.TryGet<PositionComponent>(out var position))
            {
                return position.Position;
            }

            if (delta.Target.StartsWith("tile:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = delta.Target["tile:".Length..]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var x)
                    && int.TryParse(parts[1], out var y))
                {
                    return new GridPoint(x, y);
                }
            }
        }

        return null;
    }
}
