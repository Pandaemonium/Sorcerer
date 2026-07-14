using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Validation;

namespace Sorcerer.Core.World;

public sealed class WorldTurnSystem
{
    private const int PromiseStirCooldown = 8;
    private const int WantStirCooldown = 12;
    private const int NpcApproachCooldown = 6;

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

        if (remaining > 0 && TryApplyAlliedWar(state, deltas, apply))
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
            && TryNpcApproach(state, reason, announce, deltas, apply))
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

    // How much "gratitude" a resistance faction must hold -- earned through real anti-imperial help
    // like freeing its people -- before it will spend that goodwill waging war on the empire.
    private const int AlliedWarGratitudeThreshold = 4;

    // Alliance route of the organic capital approach (memory capital-organic-approach-design): a
    // resistance faction the player has meaningfully befriended spends its goodwill to wage war on
    // the empire off-screen, thinning imperial defenses (and so the capital guard) with the player
    // nowhere near. Bounded by a cooldown and by the empire actually having defenses left to lose,
    // and rolled back as one move if any child rejects.
    private static bool TryApplyAlliedWar(
        GameState state,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        foreach (var ally in state.Factions.FactionsByRole("resistance"))
        {
            if (state.Factions.StandingValue(ally.Id, "gratitude") < AlliedWarGratitudeThreshold)
            {
                continue;
            }

            if (state.Turn < state.Factions.ResourceValue(ally.Id, "response_cooldown_until"))
            {
                continue;
            }

            var empire = state.Factions.FactionsByRole("empire_bloc")
                .FirstOrDefault(faction => state.Factions.ResourceValue(faction.Id, "defenses") > 0);
            if (empire is null)
            {
                continue;
            }

            return TryApplyWorldTurnTransaction(
                state,
                deltas,
                applyConsequence,
                "allied_war",
                ally.Id,
                localDeltas =>
                {
                    if (!TrySpendFactionResource(state, empire.Id, "defenses", 1, localDeltas, applyConsequence))
                    {
                        return false;
                    }

                    if (!SetFactionResource(state, ally.Id, "response_cooldown_until", state.Turn + 10, localDeltas, applyConsequence))
                    {
                        return false;
                    }

                    // The ally spends goodwill committing to the fight.
                    if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.AdjustFactionStanding(
                        "world_turn",
                        ally.Id,
                        "gratitude",
                        -2)).Applied)
                    {
                        return false;
                    }

                    return ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.AddCanon(
                        "world_turn",
                        "allied_war",
                        ally.Id,
                        "Word comes down the road: your allies have struck an imperial garrison, and the Censorate has pulled soldiers from the capital to answer.",
                        "Allies wage war on the empire, thinning its defenses.",
                        new[] { "resistance", "war", "empire" },
                        operation: "alliedWar")).Applied;
                });
        }

        return false;
    }

    private static bool TryNpcApproach(
        GameState state,
        string reason,
        bool announce,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (!state.ControlledEntity.TryGet<PositionComponent>(out var playerPosition))
        {
            return false;
        }

        var candidate = state.Entities.Values
            .Where(entity => entity.Id != state.ControlledEntityId)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Where(entity => !entity.TryGet<AiComponent>(out var ai)
                || !ai.PolicyId.Equals("hostile", StringComparison.OrdinalIgnoreCase))
            .Where(entity => !IsHeldCaptive(entity))
            .Select(entity => new
            {
                Entity = entity,
                Position = entity.Get<PositionComponent>().Position,
                Want = entity.TryGet<WantComponent>(out var want) ? want : null,
                SeeksPlayer = entity.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Any(tag => tag.Equals("objective_contact", StringComparison.OrdinalIgnoreCase)
                        || tag.Equals("seeks_player", StringComparison.OrdinalIgnoreCase)
                        || tag.Equals("approach_player", StringComparison.OrdinalIgnoreCase)),
                RumorMemory = state.Memories.Records
                    .Where(memory => memory.SubjectId.Equals(entity.Id.Value, StringComparison.OrdinalIgnoreCase))
                    .Where(memory => memory.Provenance.StartsWith("rumor:", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(memory => memory.Salience)
                    .FirstOrDefault(),
            })
            .Select(item => new
            {
                item.Entity,
                item.Position,
                item.Want,
                item.SeeksPlayer,
                item.RumorMemory,
                Distance = Math.Abs(item.Position.X - playerPosition.Position.X)
                    + Math.Abs(item.Position.Y - playerPosition.Position.Y),
                Score = (item.RumorMemory?.Salience ?? 0) * 10
                    + (item.Want?.Status.Equals("active", StringComparison.OrdinalIgnoreCase) == true
                        ? item.Want.Salience * 4
                        : 0),
            })
            .Where(item => item.Distance is >= 3 and <= 8)
            .Where(item => item.RumorMemory is not null
                || item.SeeksPlayer
                    && item.Want?.Status.Equals("active", StringComparison.OrdinalIgnoreCase) == true
                    && item.Want.Salience >= 4)
            .Where(item => !state.WorldTurns.HasRecent(
                "npc_approach",
                item.Entity.Id.Value,
                state.Turn,
                NpcApproachCooldown))
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (candidate is null)
        {
            return false;
        }

        var step = ApproachStep(state, candidate.Entity, candidate.Position, playerPosition.Position);
        if (step is null)
        {
            return false;
        }

        var cause = candidate.RumorMemory is not null
            ? $"the rumor they heard — {candidate.RumorMemory.Text}"
            : $"their active want — {candidate.Want!.Text}";
        // Rumor memory just echoes a rumor the player already holds in the journal, so the player
        // message must not re-dump that text (log-spam fix). A want is the NPC's own motivation and
        // is not otherwise surfaced, so it stays named. Full detail always rides evidence/details.
        var announceCause = candidate.RumorMemory is not null
            ? "word that has been spreading about you"
            : cause;
        return TryApplyWorldTurnTransaction(
            state,
            deltas,
            applyConsequence,
            "npc_approach",
            candidate.Entity.Id.Value,
            localDeltas =>
            {
                if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.MoveEntity(
                    "world_turn",
                    candidate.Entity.Id.Value,
                    step.Value.X,
                    step.Value.Y,
                    operation: "npcApproachMove",
                    visibility: WorldConsequenceVisibility.Hidden,
                    sourceEntityId: candidate.Entity.Id.Value,
                    evidence: cause,
                    reason: "A bounded world-turn initiative moved an interested NPC one step toward the player.",
                    emitMessage: false,
                    details: new Dictionary<string, object?>
                    {
                        ["cause"] = cause,
                        ["fromX"] = candidate.Position.X,
                        ["fromY"] = candidate.Position.Y,
                        ["playerVisible"] = false,
                    })).Applied)
                {
                    return false;
                }

                if (announce && !ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.Message(
                    "world_turn",
                    $"{candidate.Entity.Name} approaches, drawn by {announceCause}.",
                    targetEntityId: candidate.Entity.Id.Value,
                    visibility: WorldConsequenceVisibility.Message,
                    sourceEntityId: candidate.Entity.Id.Value,
                    evidence: cause,
                    reason: "NPC initiative named its carrier and cause.",
                    operation: "npcApproachMessage",
                    details: new Dictionary<string, object?>
                    {
                        ["entityId"] = candidate.Entity.Id.Value,
                        ["cause"] = cause,
                    })).Applied)
                {
                    return false;
                }

                return RecordMove(
                    state,
                    reason,
                    "npc_approach",
                    candidate.Entity.Id.Value,
                    $"{candidate.Entity.Name} approaches because of {cause}.",
                    new Dictionary<string, object?>
                    {
                        ["entityId"] = candidate.Entity.Id.Value,
                        ["cause"] = cause,
                        ["x"] = step.Value.X,
                        ["y"] = step.Value.Y,
                    },
                    announce: false,
                    localDeltas,
                    applyConsequence);
            });
    }

    private static GridPoint? ApproachStep(GameState state, Entity entity, GridPoint from, GridPoint target)
    {
        var dx = Math.Sign(target.X - from.X);
        var dy = Math.Sign(target.Y - from.Y);
        var candidates = new[]
        {
            new GridPoint(from.X + dx, from.Y + dy),
            new GridPoint(from.X + dx, from.Y),
            new GridPoint(from.X, from.Y + dy),
        };
        foreach (var point in candidates)
        {
            if (point.X >= 0 && point.Y >= 0 && point.X < state.Width && point.Y < state.Height
                && !state.BlockingTerrain.Contains(point)
                && !state.Entities.Values.Any(other =>
                    other.Id != entity.Id
                    && other.TryGet<PositionComponent>(out var position)
                    && position.Position == point
                    && (!other.TryGet<PhysicalComponent>(out var physical) || physical.BlocksMovement)))
            {
                return point;
            }
        }

        return null;
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

            // Top of the ladder (Phase 2.2): at the highest heat the Censorate throws a manhunt
            // cordon over the district and pulls the reserve to man it. Per the organic capital
            // approach, that very commitment strips the guard the sorcerer will later face --
            // provoking the empire hardest thins its own walls. Requires defenses to spend, so it
            // degrades gracefully to the warrant/patrol rungs when the reserve is already gone.
            if (heat >= 7 && HasFactionResource(state, faction.Id, "defenses", 2))
            {
                return TryApplyWorldTurnTransaction(
                    state,
                    deltas,
                    applyConsequence,
                    "faction_pressure",
                    faction.Id,
                    localDeltas =>
                    {
                        if (!TrySpendFactionResource(state, faction.Id, "defenses", 2, localDeltas, applyConsequence))
                        {
                            return false;
                        }

                        if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.ScheduleEvent(
                            "world_turn",
                            "empire_cordon",
                            2,
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["text"] = "A manhunt cordon closes over the district; the empire has stripped a wall to man the streets.",
                            })).Applied)
                        {
                            return false;
                        }

                        if (!ApplyConsequence(localDeltas, applyConsequence, WorldConsequence.AddCanon(
                            "world_turn",
                            "censorate_memo",
                            faction.Id,
                            "Censorate memorandum: cordon the district and pull the reserve to work it; the capital's watch will notice the gap.",
                            "Censorate commits defenses to a cordon.",
                            new[] { "empire", "cordon", "manhunt" },
                            operation: "censorateMemo")).Applied)
                        {
                            return false;
                        }

                        if (AdjustFactionResource(state, faction.Id, "heat", -3, deltas: localDeltas, applyConsequence: applyConsequence) != -3)
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
                            "The Empire throws a cordon over the district, stripping a wall to do it.",
                            new Dictionary<string, object?>
                            {
                                ["factionId"] = faction.Id,
                                ["response"] = "empire_cordon",
                                ["heatBefore"] = heat,
                            },
                            announce,
                            localDeltas,
                            applyConsequence);
                    });
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
        var beforeValidation = StateValidator.Validate(state);
        var snapshot = GameStateSnapshot.Capture(state);
        var transactionDeltas = new List<StateDelta>();
        bool moveSucceeded;
        using (WorldConsequenceGuard.EnterScope())
        {
            // This snapshot already covers the whole move; nested ApplyConsequence calls made
            // inside applyMove skip their own per-consequence snapshot (see EnterScope).
            moveSucceeded = applyMove(transactionDeltas) && !transactionDeltas.Any(IsRejectedDelta);
        }

        if (moveSucceeded)
        {
            var afterValidation = StateValidator.Validate(state);
            if (!beforeValidation.IsValid || afterValidation.IsValid)
            {
                deltas.AddRange(transactionDeltas);
                return true;
            }

            snapshot.Restore(state);
            deltas.Add(WorldTurnSkippedDelta(attemptedKind, attemptedSourceId, transactionDeltas.Count, Array.Empty<StateDelta>()));
            return false;
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

    // A held captive cannot wander toward the player on rumor -- it stays put until freed (its cell
    // opened, its bonds cut), so a "free them" promise stays coherent instead of the prisoner
    // strolling out of a locked cell because word spread.
    private static bool IsHeldCaptive(Entity entity) =>
        (entity.TryGet<AiComponent>(out var ai)
            && ai.PolicyId.Equals("captive", StringComparison.OrdinalIgnoreCase))
        || (entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => tag.Equals("prisoner", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("captive", StringComparison.OrdinalIgnoreCase)));

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
            },
            allowLargeDelta: true));
        if (deltas is not null)
        {
            deltas.AddRange(applied.Deltas);
        }

        if (!applied.Applied)
        {
            return 0;
        }

        // Trust the ledger's own report of what actually landed rather than the value we asked
        // for -- the applier is entitled to clamp or otherwise adjust it, and callers such as
        // SetFactionResource compare this return against their own expectation to detect success.
        return applied.Details.TryGetValue("value", out var rawValue) && rawValue is not null
            ? Convert.ToInt32(rawValue) - current
            : actualDelta;
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
