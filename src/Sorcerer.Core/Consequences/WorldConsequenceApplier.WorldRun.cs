using Sorcerer.Core.Characters;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Engine.Systems;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Runtime;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

/// <summary>
/// <see cref="WorldConsequenceApplier"/> handlers for world/run state: flags, run status, selection, exploration, faction standing/resources, world-turn audit, and messages.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyMessage(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Message consequence did not include text.");
        }

        var operation = ReadString(payload, "operation") ?? "message";
        var delta = new StateDelta(operation, consequence.TargetEntityId ?? "", text, Details(consequence));
        if (IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence))
        {
            _state.AddMessage(text);
        }

        return AppliedFromDelta(consequence, delta);
    }

    private WorldConsequenceApplyResult ApplySetWorldFlag(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var flag = NormalizeToken(
            FirstNonBlank(ReadString(payload, "flag"), ReadString(payload, "id"), consequence.TargetEntityId, "marked")!,
            "marked");
        var value = payload.TryGetValue("value", out var raw) && raw is not null ? raw : true;
        _state.WorldFlags[flag] = value;
        var operation = ReadString(payload, "operation") ?? "setFlag";
        var description = FirstNonBlank(ReadString(payload, "description"), flag.Replace('_', ' '))!;
        var summary = $"A world flag is set: {description}.";
        var delta = new StateDelta(
            operation,
            flag,
            summary,
            Details(consequence, ("flag", flag), ("value", value), ("description", description)));
        return Applied(consequence, flag, MaybeVisibleMessage(consequence, summary), delta, ("flag", flag), ("value", value));
    }

    private WorldConsequenceApplyResult ApplyUpdateRunStatus(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var rawStatus = FirstNonBlank(ReadString(payload, "status"), ReadString(payload, "runStatus"));
        if (string.IsNullOrWhiteSpace(rawStatus))
        {
            return Reject(consequence, "Run-status consequence did not include a status.");
        }

        var previousStatus = _state.RunStatus;
        var previousConclusion = _state.RunConclusion;
        var status = NormalizeToken(rawStatus, "running");
        var conclusion = FirstNonBlank(ReadString(payload, "conclusion"), ReadString(payload, "runConclusion"), consequence.Evidence);
        _state.RunStatus = status;
        _state.RunConclusion = conclusion;

        var operation = ReadString(payload, "operation") ?? "updateRunStatus";
        var targetId = FirstNonBlank(ReadString(payload, "targetId"), consequence.TargetEntityId, _state.ControlledEntityId.Value)!;
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"Run status is now {status}.")!;
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("previousStatus", previousStatus),
                ("previousConclusion", previousConclusion),
                ("status", status),
                ("conclusion", conclusion),
                ("targetId", targetId)));
        return Applied(
            consequence,
            targetId,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("previousStatus", previousStatus),
            ("status", status),
            ("conclusion", conclusion));
    }

    private WorldConsequenceApplyResult ApplySetSelectedTarget(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var clear = ReadBool(payload, "clear") == true;
        var x = ReadInt(payload, "x");
        var y = ReadInt(payload, "y");
        if (!clear && (x is null || y is null))
        {
            return Reject(consequence, "Selected-target consequence needs both x and y, or clear=true.");
        }

        if (!clear
            && (x!.Value < 0 || y!.Value < 0 || x.Value >= _state.Width || y.Value >= _state.Height))
        {
            return Reject(consequence, $"Selected target is outside the encounter: {x},{y}.");
        }

        var previous = _state.SelectedTarget;
        var next = clear ? (GridPoint?)null : new GridPoint(x!.Value, y!.Value);
        _state.SelectedTarget = next;

        var operation = ReadString(payload, "operation") ?? (clear ? "clearSelectedTarget" : "setSelectedTarget");
        var target = next is { } point ? $"{point.X},{point.Y}" : "selected_target";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            next is { } selected ? $"Selected target set to {selected.X},{selected.Y}." : "Selected target cleared.")!;
        // Incidental clears (travel/possession/zone generation) set Hidden visibility and must
        // not narrate "Selected target cleared." into the log; the explicit target/untarget
        // commands render their own player message separately, so this only silences the noise.
        var playerVisible = consequence.Visibility != WorldConsequenceVisibility.Hidden;
        var delta = new StateDelta(
            operation,
            target,
            summary,
            Details(
                consequence,
                ("previousX", previous?.X),
                ("previousY", previous?.Y),
                ("x", next?.X),
                ("y", next?.Y),
                ("clear", clear),
                ("playerVisible", playerVisible)));
        return Applied(
            consequence,
            target,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("previousX", previous?.X),
            ("previousY", previous?.Y),
            ("x", next?.X),
            ("y", next?.Y),
            ("clear", clear));
    }

    private WorldConsequenceApplyResult ApplyAdjustFactionStanding(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var factionOrRole = CleanLedgerKey(
            FirstNonBlank(ReadString(payload, "factionId"), ReadString(payload, "role"), consequence.TargetEntityId, "unknown")!,
            "unknown");
        var axis = CleanLedgerKey(FirstNonBlank(ReadString(payload, "axis"), ReadString(payload, "standing"), "standing")!, "standing");
        var deltaValue = Math.Clamp(ReadInt(payload, "delta") ?? 0, -999, 999);
        if (deltaValue == 0)
        {
            return Reject(consequence, "Faction-standing consequence had zero delta.");
        }

        var targetIsRole = ReadBool(payload, "targetIsRole") ?? !string.IsNullOrWhiteSpace(ReadString(payload, "role"));
        if (targetIsRole)
        {
            _state.Factions.AdjustStandingByRole(factionOrRole, axis, deltaValue);
        }
        else
        {
            _state.Factions.AdjustStanding(factionOrRole, axis, deltaValue);
        }

        var operation = ReadString(payload, "operation") ?? "adjustFactionStanding";
        var summary = targetIsRole
            ? $"Factions with role {factionOrRole} shift {axis} by {deltaValue}."
            : $"{factionOrRole} shifts {axis} by {deltaValue}.";
        var delta = new StateDelta(
            operation,
            factionOrRole,
            summary,
            Details(
                consequence,
                ("axis", axis),
                ("delta", deltaValue),
                ("targetIsRole", targetIsRole),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, factionOrRole, MaybeVisibleMessage(consequence, summary), delta, ("axis", axis), ("delta", deltaValue));
    }

    private WorldConsequenceApplyResult ApplyAdjustFactionResource(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var factionId = CleanLedgerKey(FirstNonBlank(ReadString(payload, "factionId"), consequence.TargetEntityId, "unknown")!, "unknown");
        var resource = CleanLedgerKey(FirstNonBlank(ReadString(payload, "resource"), "heat")!, "heat");
        var rawDelta = ReadInt(payload, "delta") ?? 0;
        // Untrusted content (wild magic, dialogue) is clamped to a sane one-step swing; engine
        // callers that already bound their own delta (see WorldConsequence.AdjustFactionResource)
        // opt out explicitly instead of silently having it truncated out from under them.
        var deltaValue = ReadBool(payload, "allowLargeDelta") == true ? rawDelta : Math.Clamp(rawDelta, -999, 999);
        if (deltaValue == 0)
        {
            return Reject(consequence, "Faction-resource consequence had zero delta.");
        }

        var min = ReadInt(payload, "min") ?? 0;
        var max = ReadInt(payload, "max");
        _state.Factions.AdjustResource(factionId, resource, deltaValue, min, max);
        var value = _state.Factions.ResourceValue(factionId, resource);
        var operation = ReadString(payload, "operation") ?? "adjustFactionResource";
        var summary = $"{factionId} {resource} shifts by {deltaValue} to {value}.";
        var delta = new StateDelta(
            operation,
            factionId,
            summary,
            Details(
                consequence,
                ("resource", resource),
                ("delta", deltaValue),
                ("value", value),
                ("min", min),
                ("max", max),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? IsVisible(consequence.Visibility))));
        return Applied(consequence, factionId, MaybeVisibleMessage(consequence, summary), delta, ("resource", resource), ("delta", deltaValue), ("value", value));
    }

    private WorldConsequenceApplyResult ApplyRecordWorldTurn(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var reason = FirstNonBlank(
            ReadString(payload, "worldTurnReason"),
            ReadString(payload, "world_turn_reason"),
            ReadString(payload, "moveReason"),
            ReadString(payload, "reason"),
            consequence.Reason,
            "turn")!;
        var kind = FirstNonBlank(ReadString(payload, "kind"), ReadString(payload, "moveKind"), "move")!;
        var sourceId = FirstNonBlank(
            ReadString(payload, "worldTurnSourceId"),
            ReadString(payload, "world_turn_source_id"),
            ReadString(payload, "sourceId"),
            ReadString(payload, "source_id"),
            consequence.TargetEntityId,
            consequence.SourceEntityId,
            consequence.Source,
            "unknown")!;
        var summary = FirstNonBlank(ReadString(payload, "summary"), consequence.Evidence);
        if (string.IsNullOrWhiteSpace(summary))
        {
            return Reject(consequence, "World-turn consequence did not include a summary.");
        }

        var recordDetails = ReadDictionary(payload, "details")
            ?? ReadDictionary(payload, "recordDetails")
            ?? ReadDictionary(payload, "record_details")
            ?? PayloadWithoutWorldTurnKeys(payload);
        var record = _state.WorldTurns.Add(_state.Turn, reason, kind, sourceId, summary, recordDetails);
        var operation = ReadString(payload, "operation") ?? "worldTurn";
        var deltaDetails = new Dictionary<string, object?>(Details(
            consequence,
            ("worldTurnId", record.Id),
            ("worldTurnReason", record.Reason),
            ("kind", record.Kind),
            ("worldTurnSourceId", record.SourceId),
            ("sourceId", record.SourceId),
            ("recordDetails", record.Details),
            ("auditOnly", ReadBool(payload, "auditOnly") ?? true),
            ("playerVisible", ReadBool(payload, "playerVisible") ?? false)), StringComparer.OrdinalIgnoreCase);
        foreach (var pair in record.Details)
        {
            deltaDetails.TryAdd(pair.Key, pair.Value);
        }

        var delta = new StateDelta(
            operation,
            record.Id,
            record.Summary,
            deltaDetails);
        return Applied(consequence, record.Id, MaybeVisibleMessage(consequence, record.Summary), delta, ("worldTurnId", record.Id), ("kind", record.Kind));
    }

    private WorldConsequenceApplyResult ApplyRecordExploration(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var soulId = FirstNonBlank(ReadString(payload, "soulId"), ReadString(payload, "soul_id"), consequence.TargetEntityId);
        if (string.IsNullOrWhiteSpace(soulId))
        {
            return Reject(consequence, "Exploration consequence did not include a soul id.");
        }

        var tiles = ReadPointList(payload, "tiles")
            .Concat(ReadPointList(payload, "visibleTiles"))
            .Concat(ReadPointList(payload, "visible_tiles"))
            .Where(InBounds)
            .Distinct()
            .OrderBy(point => point.Y)
            .ThenBy(point => point.X)
            .ToArray();
        if (tiles.Length == 0)
        {
            return Reject(consequence, "Exploration consequence did not include any in-bounds tiles.");
        }

        if (!_state.ExploredBySoulId.TryGetValue(soulId, out var explored))
        {
            explored = new HashSet<GridPoint>();
            _state.ExploredBySoulId[soulId] = explored;
        }

        var before = explored.Count;
        foreach (var point in tiles)
        {
            explored.Add(point);
        }

        var newTileCount = explored.Count - before;
        var operation = ReadString(payload, "operation") ?? "recordExploration";
        var summary = newTileCount == 0
            ? $"{soulId} exploration is unchanged."
            : $"{soulId} explores {newTileCount} new tile(s).";
        var delta = new StateDelta(
            operation,
            soulId,
            summary,
            Details(
                consequence,
                ("soulId", soulId),
                ("tileCount", tiles.Length),
                ("newTileCount", newTileCount),
                ("totalExplored", explored.Count),
                ("auditOnly", ReadBool(payload, "auditOnly") ?? true),
                ("playerVisible", ReadBool(payload, "playerVisible") ?? false)));
        return Applied(
            consequence,
            soulId,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("soulId", soulId),
            ("tileCount", tiles.Length),
            ("newTileCount", newTileCount),
            ("totalExplored", explored.Count));
    }
}
