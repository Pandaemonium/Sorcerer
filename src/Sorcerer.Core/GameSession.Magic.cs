using System.Diagnostics;
using System.Text.Json;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

/// <summary>
/// <see cref="GameSession"/> casting: pending wild casts, charter magic, and spell echoes (repertoire).
/// Split from the GameSession orchestrator (Phase 0.3); ExecuteAsync, the command
/// switch, View/Observation, and the post-action pipeline stay in GameSession.cs.
/// </summary>
public sealed partial class GameSession
{
    private ActionResult BeginCast(BeginCastCommand command)
    {
        var turn = Engine.State.Turn;
        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return ActionResult.Simple(
                "begin_cast",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "No spell was spoken.");
        }

        // Same instant charter detour as CastSpellAsync below: a *known* form's name casts it
        // now, with no provider call and no pending cast, so the GUI spell box (begin/await)
        // and CLI `cast` agree on charter names. Agent contract: begin_cast with a charter name
        // settles immediately — a following await_cast gets "No spell is waiting to resolve.";
        // Observation().PendingCast == null remains the settled signal.
        if (CharterSpellbook.Default.Find(command.Text) is { } charterSpell
            && Engine.State.Souls.KnowsCharterSpell(ControlledSoulId(), charterSpell.Id))
        {
            return CastCharterSpell(charterSpell);
        }

        var id = $"cast_{++_pendingCastSerial}";
        var castCommand = new CastCommand(command.Text, command.Performance ?? CastPerformance.Neutral);
        var cancellation = new CancellationTokenSource();
        _pendingCast = new PendingCast(
            id,
            castCommand,
            ResolutionTask: _magic.ResolveAsync(Engine, castCommand, cancellation.Token),
            Cancellation: cancellation);
        // Show the player the spell they reached for while it resolves, not an internal cast id.
        var message = $"Wild spell: {command.Text.Trim()}";
        var applied = ApplySessionMessage(
            "pending_cast",
            message,
            operation: "pendingCastMessage",
            details: new Dictionary<string, object?>
            {
                ["pendingCastId"] = id,
                ["status"] = "resolving",
                ["performance"] = command.Performance?.ToString() ?? CastPerformance.Neutral.ToString(),
            });
        return new ActionResult
        {
            Action = "begin_cast",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = applied.Deltas,
        };
    }

    private async Task<ActionResult> AwaitCast(AwaitCastCommand command, CancellationToken cancellationToken)
    {
        var pending = _pendingCast;
        if (pending is null)
        {
            return ActionResult.Simple(
                "await_cast",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "No spell is waiting to resolve.");
        }

        var materialized = await MaterializePendingCastAsync(pending, cancellationToken);
        if (command.Performance is { } performance)
        {
            // The minigame score finalizes after the provider call started, so it arrives with
            // await_cast and is stamped here, at the apply boundary the engine owns.
            materialized = materialized with { Performance = performance };
        }

        if (_pendingCast?.Id == pending.Id)
        {
            _pendingCast = null;
        }

        DisposePendingCastResolutionOwnership(pending);
        var result = _magic.ApplyResolved(Engine, materialized);
        RecordEchoIfEnabled(pending.Command.Text, result);
        return result;
    }

    private ActionResult CancelCast()
    {
        var turn = Engine.State.Turn;
        if (_pendingCast is null)
        {
            return ActionResult.Simple(
                "cancel_cast",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "No spell is waiting to cancel.");
        }

        var pending = _pendingCast;
        var id = pending.Id;
        _pendingCast = null;
        CancelPendingCastResolution(pending);
        var message = $"Pending cast {id} dissipates.";
        var applied = ApplySessionMessage(
            "pending_cast",
            message,
            operation: "pendingCastMessage",
            details: new Dictionary<string, object?>
            {
                ["pendingCastId"] = id,
                ["status"] = "cancelled",
            });
        return new ActionResult
        {
            Action = "cancel_cast",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = applied.Deltas,
        };
    }

    private ActionResult ProtectItem(string item, bool protectedState)
    {
        var turn = Engine.State.Turn;
        var actor = Engine.State.ControlledEntity;
        var action = protectedState ? "protect" : "unprotect";
        var message = protectedState
            ? $"{item} is protected from wild magic costs."
            : $"{item} is available as ordinary spell fuel.";
        var applied = Engine.ApplyConsequence(WorldConsequence.ModifyInventory(
            "inventory",
            actor.Id.Value,
            item,
            op: action,
            amount: 0,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: actor.Id.Value,
            evidence: item,
            operation: action,
            message: message));
        if (!applied.Applied)
        {
            var failure = applied.Error ?? $"You are not carrying {item}.";
            return new ActionResult
            {
                Action = action,
                Success = false,
                ConsumedTurn = false,
                TurnBefore = turn,
                TurnAfter = turn,
                Messages = new[] { failure },
                Deltas = applied.Deltas,
            };
        }

        return new ActionResult
        {
            Action = action,
            Success = true,
            ConsumedTurn = false,
            TurnBefore = turn,
            TurnAfter = turn,
            Messages = applied.Messages.ToArray(),
            Deltas = applied.Deltas,
        };
    }

    /// <summary>Flag key gating the spell-echo experiment (docs/SPELL_ECHOES.md).</summary>
    public const string EchoesEnabledFlag = "echoes_enabled";

    /// <summary>
    /// Wild casting, with one instant detour: text that exactly matches a *known* charter
    /// form's id or name casts that form with zero model calls (docs/CHARTER_MAGIC.md). The
    /// GUI spell box and CLI `cast` both reach charter magic this way without a new verb, and
    /// free-text wild casting is untouched because only learned forms intercept.
    /// </summary>
    private async Task<ActionResult> CastSpellAsync(CastCommand cast, CancellationToken cancellationToken)
    {
        if (CharterSpellbook.Default.Find(cast.Text) is { } charterSpell
            && Engine.State.Souls.KnowsCharterSpell(ControlledSoulId(), charterSpell.Id))
        {
            return CastCharterSpell(charterSpell);
        }

        var result = await _magic.CastAsync(Engine, cast, cancellationToken);
        RecordEchoIfEnabled(cast.Text, result);
        return result;
    }

    private ActionResult CharterMagic(string? reference)
    {
        var turn = Engine.State.Turn;
        var soulId = ControlledSoulId();
        if (string.IsNullOrWhiteSpace(reference))
        {
            var known = Engine.State.Souls.KnownCharterSpellsFor(soulId)
                .Select(id => CharterSpellbook.Default.Find(id))
                .OfType<CharterSpell>()
                .ToArray();
            if (known.Length == 0)
            {
                return ActionResult.Simple(
                    "charter",
                    success: true,
                    consumedTurn: false,
                    turn,
                    turn,
                    "You know no charter forms. They are learned from manuals, warrants, and licensed paraphernalia.");
            }

            var lines = new List<string> { $"Known charter forms ({known.Length}):" };
            lines.AddRange(known.Select(spell =>
                $"- {spell.Id} ({spell.Name}): {spell.Summary} Cost: {spell.CostText}. Targeting: {spell.Targeting}."));
            lines.Add("Cast one with 'charter <id>'. Charter magic is instant, weak, and precise.");
            return ActionResult.Simple("charter", success: true, consumedTurn: false, turn, turn, lines.ToArray());
        }

        var spell = CharterSpellbook.Default.Find(reference);
        if (spell is null)
        {
            return ActionResult.Simple(
                "charter",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"No charter form answers to '{reference.Trim()}'.");
        }

        if (!Engine.State.Souls.KnowsCharterSpell(soulId, spell.Id))
        {
            return ActionResult.Simple(
                "charter",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"You have not learned {spell.Name}. Charter forms are learned, not improvised.");
        }

        return CastCharterSpell(spell);
    }

    private ActionResult CastCharterSpell(CharterSpell spell) =>
        _magic.ApplyResolved(Engine, new MaterializedMagicResolution(
            Provider: "charter",
            SpellText: spell.Name,
            Performance: CastPerformance.Neutral,
            RawText: "",
            Accepted: true,
            TechnicalFailure: false,
            Error: null,
            EffectTypes: spell.EffectTypes,
            ResolvedMagicJson: spell.BuildResolvedMagicJson(),
            DeedKind: "charter_magic"));

    private bool EchoesEnabled => EchoesEnabledFor(Engine.State);

    /// <summary>Tolerant read of the echoes world flag (bool, "true", or "1"), shared with the
    /// view builder so the GUI's repertoire panel gates the same way the commands do.</summary>
    public static bool EchoesEnabledFor(GameState state)
    {
        if (!state.WorldFlags.TryGetValue(EchoesEnabledFlag, out var raw) || raw is null)
        {
            return false;
        }

        if (raw is bool flag)
        {
            return flag;
        }

        var text = Convert.ToString(raw);
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase) || text == "1";
    }

    private void RecordEchoIfEnabled(string spellText, ActionResult result)
    {
        if (!EchoesEnabled
            || !result.Success
            || string.IsNullOrWhiteSpace(spellText)
            || result.Magic is not { Accepted: true } magic
            || string.IsNullOrWhiteSpace(magic.ResolvedMagicJson))
        {
            return;
        }

        Engine.State.Echoes.Record(
            spellText,
            magic.ResolvedMagicJson!,
            magic.EffectTypes,
            ControlledSoulId(),
            Engine.State.Turn);
    }

    private ActionResult ListEchoes()
    {
        var turn = Engine.State.Turn;
        if (!EchoesEnabled)
        {
            return ActionResult.Simple(
                "echoes",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "Spell echoes are disabled for this run.");
        }

        var mine = Engine.State.Echoes.ForSoul(ControlledSoulId());
        if (mine.Count == 0)
        {
            return ActionResult.Simple(
                "echoes",
                success: true,
                consumedTurn: false,
                turn,
                turn,
                "Your grimoire is empty. Accepted wild casts are remembered here as echoes.");
        }

        var lines = new List<string> { $"Grimoire ({mine.Count} echoes):" };
        lines.AddRange(mine.Select((record, index) =>
        {
            var fatigue = record.TimesCast > 0 ? $", +{record.TimesCast} mana fatigue" : "";
            return $"{index + 1}. {record.Name} (cast {record.TimesCast}x{fatigue})";
        }));
        lines.Add("Re-cast one instantly with 'echo <number>'. Repetition climbs the cost ladder.");
        return ActionResult.Simple("echoes", success: true, consumedTurn: false, turn, turn, lines.ToArray());
    }

    private ActionResult CastEcho(string reference)
    {
        var turn = Engine.State.Turn;
        if (!EchoesEnabled)
        {
            return ActionResult.Simple(
                "echo",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "Spell echoes are disabled for this run.");
        }

        var record = Engine.State.Echoes.Find(reference, ControlledSoulId());
        if (record is null)
        {
            return ActionResult.Simple(
                "echo",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"No echo in your grimoire answers to '{reference.Trim()}'. Use 'echoes' to list them.");
        }

        // Repetition fatigue (docs/SPELL_ECHOES.md): each repeat of the same echo adds a mana
        // surcharge on top of the recorded costs, so improvisation stays the best deal. The
        // surcharge rides the ordinary cost pipeline and shows up in the cost line.
        var json = record.TimesCast > 0
            ? WithEchoFatigue(record.ResolvedMagicJson, record.TimesCast)
            : record.ResolvedMagicJson;
        var result = _magic.ApplyResolved(Engine, new MaterializedMagicResolution(
            Provider: "echo",
            SpellText: record.SpellText,
            Performance: CastPerformance.Neutral,
            RawText: "",
            Accepted: true,
            TechnicalFailure: false,
            Error: null,
            EffectTypes: record.EffectTypes,
            ResolvedMagicJson: json));
        if (result.Success)
        {
            Engine.State.Echoes.IncrementCast(record.Id);
        }

        return result;
    }

    private static string WithEchoFatigue(string resolvedMagicJson, int surcharge)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(resolvedMagicJson) is not System.Text.Json.Nodes.JsonObject root)
            {
                return resolvedMagicJson;
            }

            if (root["costs"] is not System.Text.Json.Nodes.JsonArray costs)
            {
                costs = new System.Text.Json.Nodes.JsonArray();
                root["costs"] = costs;
            }

            costs.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "mana",
                ["fields"] = new System.Text.Json.Nodes.JsonObject { ["amount"] = surcharge },
            });
            return root.ToJsonString();
        }
        catch (JsonException)
        {
            return resolvedMagicJson;
        }
    }
}
