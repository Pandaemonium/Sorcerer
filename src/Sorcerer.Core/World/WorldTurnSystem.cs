using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;

namespace Sorcerer.Core.World;

public sealed class WorldTurnSystem
{
    private const int PromiseStirCooldown = 8;
    private const int WantStirCooldown = 12;

    public IReadOnlyList<StateDelta> Apply(
        GameState state,
        string reason,
        int budget = 2,
        bool announce = true,
        bool allowFactionRecovery = false,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        var apply = applyConsequence ?? (consequence => DefaultApply(state, consequence));
        var remaining = Math.Max(0, budget);
        var deltas = new List<StateDelta>();
        if (remaining <= 0)
        {
            return deltas;
        }

        if (allowFactionRecovery)
        {
            RecoverFactionPressure(state, reason, deltas, apply);
        }

        if (TryApplyFactionPressure(state, reason, announce, deltas, apply))
        {
            remaining--;
        }

        if (remaining <= 0)
        {
            return deltas;
        }

        if (remaining > 0
            && TrySpreadRumor(state, reason, announce, deltas, apply))
        {
            remaining--;
        }

        if (remaining > 0
            && TryStirPromise(state, reason, announce, deltas, apply))
        {
            remaining--;
        }

        if (remaining > 0
            && TryStirWant(state, reason, deltas, apply))
        {
            remaining--;
        }

        return deltas;
    }

    private static bool TryStirWant(
        GameState state,
        string reason,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        var candidate = state.Entities.Values
            .Where(entity => entity.Id != state.ControlledEntityId)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Where(entity => !entity.TryGet<ActorComponent>(out var actor) || actor.Alive)
            .Select(entity => new
            {
                Entity = entity,
                Want = entity.TryGet<WantComponent>(out var want) ? want : null,
            })
            .Where(item => item.Want is not null)
            .Where(item => item.Want!.Salience >= 4)
            .Where(item => item.Want!.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
            .Where(item => !HasWantMemory(state, item.Entity.Id.Value, item.Want!))
            .Where(item => !state.WorldTurns.HasRecent("want_stir", item.Want!.Id, state.Turn, WantStirCooldown))
            .OrderByDescending(item => item.Want!.Salience)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        var entity = candidate.Entity;
        var want = candidate.Want!;
        return TryApplyWorldTurnTransaction(
            state,
            deltas,
            applyConsequence,
            "want_stir",
            want.Id,
            localDeltas =>
            {
                var memoryText = $"{entity.Name} keeps returning to a want: {want.Text}";
                var memory = ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.RecordMemory(
                    "world_turn",
                    entity.Id.Value,
                    memoryText,
                    $"want:{want.Id}",
                    want.Salience,
                    shareable: false,
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: entity.Id.Value,
                    evidence: want.Text,
                    reason: "A high-salience active want stirred at a bounded world-turn apply point.",
                    operation: "wantStirMemory",
                    details: new Dictionary<string, object?>
                    {
                        ["entityId"] = entity.Id.Value,
                        ["wantId"] = want.Id,
                        ["wantStatus"] = want.Status,
                        ["playerVisible"] = false,
                        ["summary"] = $"{entity.Name}'s active want stirs in memory.",
                    }));
                if (!memory.Applied)
                {
                    return false;
                }

                return RecordMove(
                    state,
                    reason,
                    "want_stir",
                    want.Id,
                    $"{entity.Name}'s active want stirs.",
                    new Dictionary<string, object?>
                    {
                        ["consequenceType"] = WorldConsequenceTypes.RecordMemory,
                        ["entityId"] = entity.Id.Value,
                        ["wantId"] = want.Id,
                        ["salience"] = want.Salience,
                        ["status"] = want.Status,
                        ["tags"] = want.Tags,
                        ["memoryOperation"] = "wantStirMemory",
                    },
                    announce: false,
                    localDeltas,
                    applyConsequence);
            });
    }

    private static bool HasWantMemory(GameState state, string entityId, WantComponent want)
    {
        var provenance = $"want:{want.Id}";
        return state.Memories.Records.Any(memory =>
            memory.SubjectId.Equals(entityId, StringComparison.OrdinalIgnoreCase)
            && memory.Provenance.Equals(provenance, StringComparison.OrdinalIgnoreCase)
            && memory.Text.Contains(want.Text, StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryStirPromise(
        GameState state,
        string reason,
        bool announce,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        var promise = state.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible)
            .Where(promise => promise.Salience >= 3)
            .Where(promise => !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase))
            .Where(promise => !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase))
            .Where(promise => !state.WorldTurns.HasRecent("promise_stir", promise.Id, state.Turn, PromiseStirCooldown))
            .OrderByDescending(promise => promise.Salience)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (promise is null)
        {
            return false;
        }

        var summary = $"A lead tugs at the world: {promise.Text}";
        return TryApplyWorldTurnTransaction(
            state,
            deltas,
            applyConsequence,
            "promise_stir",
            promise.Id,
            localDeltas => RecordMove(
                state,
                reason,
                "promise_stir",
                promise.Id,
                summary,
                new Dictionary<string, object?>
                {
                    ["promiseId"] = promise.Id,
                    ["status"] = promise.Status,
                    ["realizationKind"] = promise.RealizationKind,
                    ["triggerHint"] = promise.TriggerHint,
                },
                announce,
                localDeltas,
                applyConsequence));
    }

    private static bool TrySpreadRumor(
        GameState state,
        string reason,
        bool announce,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        return TryApplyWorldTurnTransaction(
            state,
            deltas,
            applyConsequence,
            "rumor_spread",
            "rumor",
            localDeltas =>
            {
                var rumorDeltas = RumorSystem.Propagate(
                    state,
                    $"world_turn:{reason}",
                    maxRumors: 1,
                    maxCarriersPerRumor: 1,
                    announce: false,
                    applyConsequence: applyConsequence);
                if (rumorDeltas.Count == 0)
                {
                    return false;
                }

                localDeltas.AddRange(rumorDeltas);
                if (rumorDeltas.Any(IsRejectedDelta))
                {
                    return false;
                }

                var spread = rumorDeltas.FirstOrDefault(delta =>
                    delta.Operation.Equals("rumorSpread", StringComparison.OrdinalIgnoreCase))
                    ?? rumorDeltas[0];
                return RecordMove(
                    state,
                    reason,
                    "rumor_spread",
                    spread.Target,
                    spread.Summary,
                    spread.Details,
                    announce,
                    localDeltas,
                    applyConsequence);
            });
    }

    private static bool TryApplyFactionPressure(
        GameState state,
        string reason,
        bool announce,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        foreach (var faction in state.Factions.FactionsByRole("empire_bloc"))
        {
            var heat = state.Factions.ResourceValue(faction.Id, "heat");
            if (heat < 3)
            {
                continue;
            }

            if (state.Turn < state.Factions.ResourceValue(faction.Id, "response_cooldown_until"))
            {
                continue;
            }

            if (heat >= 5 && HasFactionResource(state, faction.Id, "warrants", 1))
            {
                return TryApplyWorldTurnTransaction(
                    state,
                    deltas,
                    applyConsequence,
                    "faction_pressure",
                    faction.Id,
                    localDeltas =>
                    {
                        if (!TrySpendFactionResource(state, faction.Id, "warrants", 1, localDeltas, applyConsequence))
                        {
                            return false;
                        }

                        TrySpendFactionResource(state, faction.Id, "defenses", 1, localDeltas, applyConsequence);
                        if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.ScheduleEvent(
                            "world_turn",
                            "empire_warrant",
                            3,
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["text"] = "A wanted poster learns your outline before the ink dries.",
                            })).Applied)
                        {
                            return false;
                        }

                        if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.AddCanon(
                            "world_turn",
                            "censorate_memo",
                            faction.Id,
                            "Censorate memorandum: the fugitive's legend is to be named, copied, and pinned where color gathers.",
                            "Censorate prepares a wanted poster.",
                            new[] { "empire", "warrant", "legend" },
                            operation: "censorateMemo")).Applied)
                        {
                            return false;
                        }

                        if (AdjustFactionResource(state, faction.Id, "heat", -2, deltas: localDeltas, applyConsequence: applyConsequence) != -2)
                        {
                            return false;
                        }

                        if (!SetFactionResource(state, faction.Id, "response_cooldown_until", state.Turn + 8, localDeltas, applyConsequence))
                        {
                            return false;
                        }

                        return RecordMove(
                            state,
                            reason,
                            "faction_pressure",
                            faction.Id,
                            "A Censorate clerk starts drafting a wanted poster in your shape.",
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["response"] = "empire_warrant",
                                ["heatBefore"] = heat,
                            },
                            announce,
                            localDeltas,
                            applyConsequence);
                    });
            }

            heat = state.Factions.ResourceValue(faction.Id, "heat");
            if (heat >= 3
                && !HasActiveOrPendingPatrol(state, faction.Id)
                && HasFactionResource(state, faction.Id, "patrols", 1))
            {
                return TryApplyWorldTurnTransaction(
                    state,
                    deltas,
                    applyConsequence,
                    "faction_pressure",
                    faction.Id,
                    localDeltas =>
                    {
                        if (!TrySpendFactionResource(state, faction.Id, "patrols", 1, localDeltas, applyConsequence))
                        {
                            return false;
                        }

                        if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.ScheduleEvent(
                            "world_turn",
                            "empire_patrol",
                            2,
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["text"] = "An imperial patrol follows the color of your last working.",
                            })).Applied)
                        {
                            return false;
                        }

                        if (AdjustFactionResource(state, faction.Id, "heat", -2, deltas: localDeltas, applyConsequence: applyConsequence) != -2)
                        {
                            return false;
                        }

                        if (!SetFactionResource(state, faction.Id, "response_cooldown_until", state.Turn + 8, localDeltas, applyConsequence))
                        {
                            return false;
                        }

                        return RecordMove(
                            state,
                            reason,
                            "faction_pressure",
                            faction.Id,
                            "The Empire spends a patrol to answer your legend.",
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["response"] = "empire_patrol",
                                ["heatBefore"] = heat,
                            },
                            announce,
                            localDeltas,
                            applyConsequence);
                    });
            }

            if (!state.WorldTurns.HasRecent("faction_pressure_blocked", faction.Id, state.Turn, PromiseStirCooldown))
            {
                return TryApplyWorldTurnTransaction(
                    state,
                    deltas,
                    applyConsequence,
                    "faction_pressure_blocked",
                    faction.Id,
                    localDeltas =>
                    {
                        if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.AddCanon(
                            "world_turn",
                            "censorate_memo",
                            faction.Id,
                            "Censorate memorandum: no patrol is currently free; keep the file warm.",
                            "Censorate lacks a free patrol.",
                            new[] { "empire", "resource_shortage" },
                            operation: "censorateMemo")).Applied)
                        {
                            return false;
                        }

                        return RecordMove(
                            state,
                            reason,
                            "faction_pressure_blocked",
                            faction.Id,
                            "The Censorate keeps your file warm, but no patrol is free.",
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["heatBefore"] = heat,
                                ["response"] = "resource_shortage",
                            },
                            announce,
                            localDeltas,
                            applyConsequence);
                    });
            }
        }

        return false;
    }

    private static void RecoverFactionPressure(
        GameState state,
        string reason,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        foreach (var faction in state.Factions.FactionsByRole("empire_bloc"))
        {
            _ = TryApplyWorldTurnTransaction(
                state,
                deltas,
                applyConsequence,
                "faction_recovery",
                faction.Id,
                localDeltas =>
                {
                    var adjustments = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    TrackAdjustment(adjustments, "patrols", RegenerateOne(state, faction.Id, "patrols", localDeltas, applyConsequence));
                    TrackAdjustment(adjustments, "informants", RegenerateOne(state, faction.Id, "informants", localDeltas, applyConsequence));
                    TrackAdjustment(adjustments, "warrants", RegenerateOne(state, faction.Id, "warrants", localDeltas, applyConsequence));
                    TrackAdjustment(adjustments, "heat", AdjustFactionResource(state, faction.Id, "heat", -1, deltas: localDeltas, applyConsequence: applyConsequence));
                    if (adjustments.Count == 0)
                    {
                        return false;
                    }

                    return RecordMove(
                        state,
                        reason,
                        "faction_recovery",
                        faction.Id,
                        $"{faction.Name} pressure quietly recovers.",
                        new Dictionary<string, object?>
                        {
                            ["consequenceType"] = WorldConsequenceTypes.AdjustFactionResource,
                            ["factionId"] = faction.Id,
                            ["adjustments"] = adjustments
                                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                                .Select(pair => $"{pair.Key}:{pair.Value}")
                                .ToArray(),
                        },
                        announce: false,
                        localDeltas,
                        applyConsequence);
                });
        }
    }

    private static void TrackAdjustment(Dictionary<string, int> adjustments, string resource, int delta)
    {
        if (delta != 0)
        {
            adjustments[resource] = delta;
        }
    }

    private static int RegenerateOne(
        GameState state,
        string factionId,
        string resource,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        var max = state.Factions.ResourceValue(factionId, $"max_{resource}");
        if (max <= 0)
        {
            return 0;
        }

        var current = state.Factions.ResourceValue(factionId, resource);
        if (current < max)
        {
            return AdjustFactionResource(state, factionId, resource, 1, max: max, deltas: deltas, applyConsequence: applyConsequence);
        }

        return 0;
    }

    private static bool RecordMove(
        GameState state,
        string reason,
        string kind,
        string sourceId,
        string summary,
        IReadOnlyDictionary<string, object?> details,
        bool announce,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (announce)
        {
            var message = ApplyConsequence(deltas, applyConsequence, WorldConsequence.Message(
                "world_turn",
                summary,
                visibility: WorldConsequenceVisibility.Message,
                operation: "worldTurnMessage"));
            if (!message.Applied)
            {
                return false;
            }
        }

        return ApplyConsequence(deltas, applyConsequence, WorldConsequence.RecordWorldTurn(
            "world_turn",
            reason,
            kind,
            sourceId,
            summary,
            details,
            operation: "worldTurn",
            details: new Dictionary<string, object?>
            {
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            })).Applied;
    }

    private static bool HasActiveOrPendingPatrol(GameState state, string factionId) =>
        state.ScheduledEvents.Events.Any(item =>
            item.Kind.Equals("empire_patrol", StringComparison.OrdinalIgnoreCase)
            && (!item.Payload.TryGetValue("factionId", out var rawFactionId)
                || string.Equals(Convert.ToString(rawFactionId), factionId, StringComparison.OrdinalIgnoreCase)))
        || state.Entities.Values.Any(entity =>
            entity.TryGet<ActorComponent>(out var actor)
            && actor.Alive
            && actor.Faction.Equals(factionId, StringComparison.OrdinalIgnoreCase)
            && entity.TryGet<AiComponent>(out var ai)
            && ai.PolicyId.Equals("imperial_patrol", StringComparison.OrdinalIgnoreCase));

    private static bool TryApplyWorldTurnTransaction(
        GameState state,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string attemptedKind,
        string attemptedSourceId,
        Func<List<StateDelta>, bool> applyMove)
    {
        var snapshot = GameStateSnapshot.Capture(state);
        var transactionDeltas = new List<StateDelta>();
        if (applyMove(transactionDeltas)
            && !transactionDeltas.Any(IsRejectedDelta))
        {
            deltas.AddRange(transactionDeltas);
            return true;
        }

        if (transactionDeltas.Count > 0)
        {
            snapshot.Restore(state);
            var rejectedDeltas = transactionDeltas.Where(IsRejectedDelta).ToArray();
            deltas.AddRange(rejectedDeltas);
            deltas.Add(WorldTurnSkippedDelta(
                attemptedKind,
                attemptedSourceId,
                transactionDeltas.Count,
                rejectedDeltas));
            return false;
        }

        return false;
    }

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta WorldTurnSkippedDelta(
        string attemptedKind,
        string attemptedSourceId,
        int rolledBackDeltaCount,
        IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = rejectedDeltas
            .Select(delta => delta.Details.TryGetValue("error", out var error) && error is not null
                ? error.ToString()
                : delta.Summary)
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StateDelta(
            "worldTurnSkipped",
            attemptedSourceId,
            $"World-turn move {attemptedKind} was rolled back after a rejected consequence.",
            new Dictionary<string, object?>
            {
                ["kind"] = attemptedKind,
                ["sourceId"] = attemptedSourceId,
                ["rolledBackDeltaCount"] = rolledBackDeltaCount,
                ["rejectedCount"] = errors.Length,
                ["errors"] = errors,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private static bool HasFactionResource(GameState state, string factionId, string resource, int amount) =>
        state.Factions.ResourceValue(factionId, resource) >= Math.Max(1, amount);

    private static bool TrySpendFactionResource(
        GameState state,
        string factionId,
        string resource,
        int amount,
        List<StateDelta>? deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        var safeAmount = Math.Max(1, amount);
        if (state.Factions.ResourceValue(factionId, resource) < safeAmount)
        {
            return false;
        }

        return AdjustFactionResource(state, factionId, resource, -safeAmount, deltas: deltas, applyConsequence: applyConsequence) == -safeAmount;
    }

    private static int AdjustFactionResource(
        GameState state,
        string factionId,
        string resource,
        int delta,
        int min = 0,
        int? max = null,
        List<StateDelta>? deltas = null,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        var apply = applyConsequence ?? (consequence => DefaultApply(state, consequence));
        if (delta == 0)
        {
            return 0;
        }

        var current = state.Factions.ResourceValue(factionId, resource);
        var next = current + delta;
        if (next < min)
        {
            next = min;
        }

        if (max is { } ceiling && next > ceiling)
        {
            next = ceiling;
        }

        var actualDelta = next - current;
        if (actualDelta == 0)
        {
            return 0;
        }

        var applied = ApplyConsequence(apply, WorldConsequence.AdjustFactionResource(
            "world_turn",
            factionId,
            resource,
            actualDelta,
            min,
            max,
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = false,
            }));
        if (deltas is not null)
        {
            deltas.AddRange(applied.Deltas);
        }

        return applied.Applied ? actualDelta : 0;
    }

    private static bool SetFactionResource(
        GameState state,
        string factionId,
        string resource,
        int value,
        List<StateDelta>? deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        var current = state.Factions.ResourceValue(factionId, resource);
        return AdjustFactionResource(
            state,
            factionId,
            resource,
            value - current,
            min: int.MinValue,
            deltas: deltas,
            applyConsequence: applyConsequence) == value - current;
    }

    private static WorldConsequenceApplyResult ApplyConsequence(
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        WorldConsequence consequence)
    {
        var applied = ApplyConsequence(applyConsequence, consequence);
        deltas.AddRange(applied.Deltas);
        return applied;
    }

    private static WorldConsequenceApplyResult ApplyConsequence(
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        WorldConsequence consequence) =>
        applyConsequence(consequence);

    private static WorldConsequenceApplyResult DefaultApply(GameState state, WorldConsequence consequence) =>
        WorldConsequenceGuard.ApplyWithNewApplier(state, consequence);
}
