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
/// <see cref="WorldConsequenceApplier"/> handlers for space effects: movement, terrain, tile flows, doors/unlocking, and routes.
/// Split from the monolithic applier (Phase 0.2); shared helpers live in
/// WorldConsequenceApplier.Shared.cs and dispatch in WorldConsequenceApplier.cs.
/// </summary>
public sealed partial class WorldConsequenceApplier
{
    private WorldConsequenceApplyResult ApplyMoveEntity(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var target = RequiredEntity(consequence, "Move consequence did not include a target entity id.");
        if (target.Result is not null)
        {
            return target.Result;
        }

        if (!target.Entity!.TryGet<PositionComponent>(out var position))
        {
            return Reject(consequence, "Move consequence target has no position.");
        }

        if (!TryReadPoint(payload, null, out var point))
        {
            return Reject(consequence, "Move consequence did not include a destination coordinate.");
        }

        var operation = FirstNonBlank(ReadString(payload, "operation"), "move")!;
        var emitMessage = ReadBool(payload, "emitMessage") ?? true;
        var swapWithEntityId = FirstNonBlank(
            ReadString(payload, "swapWithEntityId"),
            ReadString(payload, "swap_with_entity_id"));
        StateDelta delta;
        var blocker = BlockingEntityAt(point);
        if (!InBounds(point) || _state.BlockingTerrain.Contains(point))
        {
            var blocked = $"{target.Entity.Name} cannot move to {point.X},{point.Y}.";
            delta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                blocked,
                new Dictionary<string, object?>
                {
                    ["fromX"] = position.Position.X,
                    ["fromY"] = position.Position.Y,
                    ["blocked"] = true,
                });
        }
        else if (blocker is not null && blocker.Id != target.Entity.Id)
        {
            if (!string.IsNullOrWhiteSpace(swapWithEntityId)
                && blocker.Id.Value.Equals(swapWithEntityId, StringComparison.OrdinalIgnoreCase)
                && blocker.TryGet<PositionComponent>(out var blockerPosition))
            {
                var previous = position.Position;
                target.Entity.Set(new PositionComponent(point));
                blocker.Set(new PositionComponent(previous));
                var movementDelta = new GridPoint(point.X - previous.X, point.Y - previous.Y);
                var recordedControlledMovement = false;
                if (ReadBool(payload, "recordControlledMovement") == true
                    && target.Entity.Id == _state.ControlledEntityId
                    && movementDelta != new GridPoint(0, 0))
                {
                    _state.LastControlledMoveDelta = movementDelta;
                    recordedControlledMovement = true;
                }

                var message = FirstNonBlank(
                    ReadString(payload, "message"),
                    ReadString(payload, "summary"),
                    $"{Subject(target.Entity)} {Verb(target.Entity, "trade", "trades")} places with {blocker.Name}.")!;
                delta = new StateDelta(
                    operation,
                    target.Entity.Id.Value,
                    message,
                    new Dictionary<string, object?>
                    {
                        ["fromX"] = previous.X,
                        ["fromY"] = previous.Y,
                        ["toX"] = point.X,
                        ["toY"] = point.Y,
                        ["dx"] = movementDelta.X,
                        ["dy"] = movementDelta.Y,
                        ["recordControlledMovement"] = recordedControlledMovement,
                        ["swappedWithEntityId"] = blocker.Id.Value,
                        ["swappedWithName"] = blocker.Name,
                        ["swappedFromX"] = blockerPosition.Position.X,
                        ["swappedFromY"] = blockerPosition.Position.Y,
                        ["swappedToX"] = previous.X,
                        ["swappedToY"] = previous.Y,
                    });
            }
            else
            {
                var blocked = $"{target.Entity.Name} cannot move to {point.X},{point.Y}.";
                delta = new StateDelta(
                    operation,
                    target.Entity.Id.Value,
                    blocked,
                    new Dictionary<string, object?>
                    {
                        ["fromX"] = position.Position.X,
                        ["fromY"] = position.Position.Y,
                        ["blocked"] = true,
                        ["blockerId"] = blocker.Id.Value,
                    });
            }
        }
        else
        {
            var previous = position.Position;
            target.Entity.Set(new PositionComponent(point));
            var movementDelta = new GridPoint(point.X - previous.X, point.Y - previous.Y);
            var recordedControlledMovement = false;
            if (ReadBool(payload, "recordControlledMovement") == true
                && target.Entity.Id == _state.ControlledEntityId
                && movementDelta != new GridPoint(0, 0))
            {
                _state.LastControlledMoveDelta = movementDelta;
                recordedControlledMovement = true;
            }

            var message = FirstNonBlank(
                ReadString(payload, "message"),
                ReadString(payload, "summary"),
                $"{Subject(target.Entity)} {Verb(target.Entity, "move", "moves")} to {point.X},{point.Y}.")!;
            delta = new StateDelta(
                operation,
                target.Entity.Id.Value,
                message,
                new Dictionary<string, object?>
                {
                    ["fromX"] = previous.X,
                    ["fromY"] = previous.Y,
                    ["toX"] = point.X,
                    ["toY"] = point.Y,
                    ["dx"] = movementDelta.X,
                    ["dy"] = movementDelta.Y,
                    ["recordControlledMovement"] = recordedControlledMovement,
                });
        }

        AddMessageIfAllowed(consequence, payload, delta.Summary, defaultEmitMessage: emitMessage);

        return AppliedFromDelta(
            consequence,
            delta);
    }

    private WorldConsequenceApplyResult ApplySetTerrain(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Terrain consequence did not include a tile coordinate.");
        }

        if (!InBounds(point))
        {
            return Reject(consequence, "Terrain consequence target is out of bounds.");
        }

        var terrain = NormalizeToken(
            FirstNonBlank(ReadString(payload, "terrain"), ReadString(payload, "tile"), "wild_growth")!,
            "wild_growth");
        var duration = ReadInt(payload, "duration");
        _state.Terrain[point] = terrain;
        if (duration is > 0)
        {
            _state.TerrainExpirations[point] = _state.Turn + duration.Value;
        }
        else
        {
            _state.TerrainExpirations.Remove(point);
        }

        if (TerrainBlocksMovement(terrain))
        {
            _state.BlockingTerrain.Add(point);
        }
        else
        {
            _state.BlockingTerrain.Remove(point);
        }

        var operation = ReadString(payload, "operation") ?? "createTile";
        var summary = FirstNonBlank(
            ReadString(payload, "message"),
            ReadString(payload, "summary"),
            $"The tile at {point.X},{point.Y} becomes {terrain.Replace('_', ' ')}.")!;
        var delta = new StateDelta(
            operation,
            $"tile:{point.X},{point.Y}",
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("terrain", terrain),
                ("duration", duration)));
        AddMessageIfAllowed(consequence, payload, delta.Summary);

        return Applied(
            consequence,
            delta.Target,
            IsVisible(consequence.Visibility) && PayloadAllowsPlayerMessage(consequence)
                ? new[] { delta.Summary }
                : Array.Empty<string>(),
            delta,
            ("x", point.X),
            ("y", point.Y),
            ("terrain", terrain),
            ("duration", duration));
    }

    private WorldConsequenceApplyResult ApplyUpdateTerrain(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Terrain update consequence did not include a tile coordinate.");
        }

        if (!engine.InBounds(point))
        {
            return Reject(consequence, "Terrain update consequence target is out of bounds.");
        }

        if (!_state.Terrain.ContainsKey(point) && !_state.TerrainExpirations.ContainsKey(point))
        {
            return Reject(consequence, $"Terrain update target does not exist: {point.X},{point.Y}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "expire")!, "expire");
        var terrain = _state.Terrain.TryGetValue(point, out var existing)
            ? existing
            : "terrain";
        var verb = action switch
        {
            "expire" or "expired" => "fades",
            "remove" or "clear" or "delete" => "is removed",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported terrain update action: {action}.");
        }

        _state.TerrainExpirations.Remove(point);
        _state.Terrain.Remove(point);
        if (!IsBoundaryWall(point))
        {
            _state.BlockingTerrain.Remove(point);
        }

        var operation = ReadString(payload, "operation") ?? "updateTerrain";
        var targetId = $"tile:{point.X},{point.Y}";
        var summary = $"The {terrain.Replace('_', ' ')} at {point.X},{point.Y} {verb}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("terrain", terrain),
                ("action", action)));
        return Applied(consequence, targetId, MaybeVisibleMessage(consequence, summary), delta, ("x", point.X), ("y", point.Y), ("terrain", terrain), ("action", action));
    }

    private WorldConsequenceApplyResult ApplyCreateFlow(WorldConsequence consequence)
    {
        if (!RequireEngine(consequence, out var engine, out var missing))
        {
            return missing;
        }

        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var origin))
        {
            return Reject(consequence, "Flow consequence did not include a tile coordinate.");
        }

        if (!engine.InBounds(origin))
        {
            return Reject(consequence, "Flow consequence target is out of bounds.");
        }

        var radius = Math.Clamp(ReadInt(payload, "radius") ?? 1, 0, 5);
        var dx = Math.Clamp(ReadInt(payload, "dx") ?? 1, -1, 1);
        var dy = Math.Clamp(ReadInt(payload, "dy") ?? 0, -1, 1);
        var duration = Math.Clamp(ReadInt(payload, "duration") ?? 5, 1, 999);
        var expiresTurn = _state.Turn + duration;
        var changed = 0;
        for (var y = origin.Y - radius; y <= origin.Y + radius; y++)
        {
            for (var x = origin.X - radius; x <= origin.X + radius; x++)
            {
                var point = new GridPoint(x, y);
                if (engine.InBounds(point)
                    && Distance(origin, point) <= radius
                    && !_state.BlockingTerrain.Contains(point))
                {
                    _state.TileFlows[point] = new TileFlow(dx, dy, expiresTurn);
                    changed++;
                }
            }
        }

        var operation = ReadString(payload, "operation") ?? "createFlow";
        var targetId = $"tile:{origin.X},{origin.Y}";
        var summary = $"The ground begins to flow near {origin.X},{origin.Y}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(consequence, ("dx", dx), ("dy", dy), ("radius", radius), ("duration", duration), ("tiles", changed)));
        return Applied(consequence, targetId, MaybeVisibleMessage(consequence, summary), delta, ("tiles", changed));
    }

    private WorldConsequenceApplyResult ApplyUpdateFlow(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        if (!TryReadPoint(payload, consequence.TargetEntityId, out var point))
        {
            return Reject(consequence, "Flow update consequence did not include a tile coordinate.");
        }

        if (!_state.TileFlows.TryGetValue(point, out var flow))
        {
            return Reject(consequence, $"Flow update target does not exist: {point.X},{point.Y}.");
        }

        var action = NormalizeToken(FirstNonBlank(ReadString(payload, "action"), "expire")!, "expire");
        var verb = ClassifyTerminalAction(action) switch
        {
            TerminalUpdateAction.Complete => "completes",
            TerminalUpdateAction.Expire => "expires",
            TerminalUpdateAction.Remove => "is removed",
            _ => null,
        };
        if (verb is null)
        {
            return Reject(consequence, $"Unsupported flow update action: {action}.");
        }

        _state.TileFlows.Remove(point);
        var operation = ReadString(payload, "operation") ?? "updateFlow";
        var targetId = $"tile:{point.X},{point.Y}";
        var summary = $"The tile flow at {point.X},{point.Y} {verb}.";
        var delta = new StateDelta(
            operation,
            targetId,
            summary,
            Details(
                consequence,
                ("x", point.X),
                ("y", point.Y),
                ("dx", flow.Dx),
                ("dy", flow.Dy),
                ("expiresTurn", flow.ExpiresTurn),
                ("action", action)));
        return Applied(consequence, targetId, MaybeVisibleMessage(consequence, summary), delta, ("x", point.X), ("y", point.Y), ("action", action));
    }

    private WorldConsequenceApplyResult ApplyOpenOrUnlock(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var doorId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(doorId))
        {
            return Reject(consequence, "Open/unlock consequence did not include a door id.");
        }

        var door = EntityById(doorId);
        if (door is null || !door.TryGet<DoorComponent>(out var doorComponent))
        {
            return Reject(consequence, "Open/unlock consequence target is not a door.");
        }

        var actorId = ReadString(payload, "actorId") ?? consequence.SourceEntityId;
        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        // Wild magic may work a lock it can see across the room; social/engine paths stay
        // adjacency-bound so dialogue proposals cannot teleport-open distant doors.
        var reach = consequence.Source.Equals("wild_magic", StringComparison.OrdinalIgnoreCase) ? 12 : 2;
        if (actor is not null && !CanReach(actor, door, range: reach))
        {
            return Reject(consequence, $"{actor.Name} cannot reach {door.Name}.");
        }

        var unlock = ReadBool(payload, "unlock") ?? true;
        var open = ReadBool(payload, "open") ?? true;
        if (!unlock && open && !string.IsNullOrWhiteSpace(doorComponent.KeyId))
        {
            return Reject(consequence, $"{door.Name} is locked.");
        }

        var wasLocked = !string.IsNullOrWhiteSpace(doorComponent.KeyId);
        var wasOpen = doorComponent.IsOpen;
        var releaseCaptives = ReadBool(payload, "releaseCaptives") ?? ReadBool(payload, "release_captives") ?? true;
        var shouldTryCaptiveRelease = releaseCaptives
            && open
            && !wasOpen
            && DoorCanReleaseCaptives(door);
        var snapshot = shouldTryCaptiveRelease
            ? GameStateSnapshot.Capture(_state)
            : null;
        var nextDoor = doorComponent;
        if (unlock)
        {
            nextDoor = nextDoor with { KeyId = null };
        }

        if (open)
        {
            nextDoor = nextDoor with { IsOpen = true };
        }

        door.Set(nextDoor);
        if (nextDoor.IsOpen && door.TryGet<PhysicalComponent>(out var physical))
        {
            door.Set(physical with { BlocksMovement = false, BlocksSight = false });
        }

        if (nextDoor.IsOpen && door.TryGet<RenderableComponent>(out var renderable))
        {
            door.Set(renderable with { Glyph = '/', Palette = "open" });
        }

        var actorName = actor is null ? "Something" : actor.Name;
        var defaultSummary = nextDoor.IsOpen && !wasOpen
            ? $"{actorName} opens {door.Name}."
            : wasLocked && unlock
                ? $"{actorName} unlocks {door.Name}."
                : $"{door.Name} is already open.";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), defaultSummary)!;
        var operation = ReadString(payload, "operation") ?? "openOrUnlock";
        var delta = new StateDelta(
            operation,
            door.Id.Value,
            summary,
            Details(
                consequence,
                ("actorId", actor?.Id.Value),
                ("unlocked", wasLocked && unlock),
                ("open", nextDoor.IsOpen)));
        var messages = (IsVisible(consequence.Visibility) || ReadBool(payload, "emitMessage") == true
            ? MaybeVisibleMessage(consequence, summary)
            : Array.Empty<string>()).ToList();
        var deltas = new List<StateDelta> { delta };
        if (shouldTryCaptiveRelease && nextDoor.IsOpen && !wasOpen)
        {
            var captiveRelease = ApplyDoorCaptiveRelease(consequence, payload, door, actor, operation);
            if (!captiveRelease.Applied
                && (!string.IsNullOrWhiteSpace(captiveRelease.Error) || captiveRelease.Deltas.Count > 0))
            {
                return RollBackOpenOrUnlock(
                    consequence,
                    snapshot!,
                    door.Id.Value,
                    operation,
                    captiveRelease.Deltas,
                    captiveRelease.Messages,
                    captiveRelease.Error ?? "captive_release_rejected");
            }

            messages.AddRange(captiveRelease.Messages);
            deltas.AddRange(captiveRelease.Deltas);
        }

        return new WorldConsequenceApplyResult(
            true,
            door.Id.Value,
            null,
            messages,
            deltas,
            Details(consequence, ("open", nextDoor.IsOpen), ("unlocked", wasLocked && unlock)));
    }

    private WorldConsequenceApplyResult RollBackOpenOrUnlock(
        WorldConsequence consequence,
        GameStateSnapshot snapshot,
        string doorId,
        string operation,
        IReadOnlyList<StateDelta> failedDeltas,
        IReadOnlyList<string> failedMessages,
        string failure)
    {
        snapshot.Restore(_state);
        var skipped = new StateDelta(
            "openOrUnlockSkipped",
            doorId,
            $"Open/unlock rolled back: {failure}.",
            Details(
                consequence,
                ("operation", operation),
                ("failure", failure),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count),
                ("rejectedCount", failedDeltas.Count(delta =>
                    delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase))),
                ("auditOnly", true),
                ("playerVisible", false)));
        return new WorldConsequenceApplyResult(
            false,
            doorId,
            failure,
            Array.Empty<string>(),
            failedDeltas.Concat(new[] { skipped }).ToArray(),
            Details(
                consequence,
                ("error", failure),
                ("operation", operation),
                ("rolledBackDeltaCount", failedDeltas.Count),
                ("rolledBackMessageCount", failedMessages.Count)));
    }

    private WorldConsequenceApplyResult ApplyDoorCaptiveRelease(
        WorldConsequence parent,
        IReadOnlyDictionary<string, object?> payload,
        Entity door,
        Entity? actor,
        string parentOperation)
    {
        if (!DoorCanReleaseCaptives(door)
            || !door.TryGet<PositionComponent>(out var doorPosition))
        {
            return WorldConsequenceApplyResult.Empty();
        }

        var captive = _state.Entities.Values
            .Where(IsUnreleasedCaptive)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && Distance(position.Position, doorPosition.Position) <= 2)
            .OrderBy(entity => entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (captive is null)
        {
            return WorldConsequenceApplyResult.Empty();
        }

        var beneficiaryId = FirstNonBlank(
            ReadString(payload, "beneficiaryId"),
            ReadString(payload, "beneficiary_id"),
            ReadString(payload, "liberatorId"),
            ReadString(payload, "liberator_id"),
            actor is not null && actor.Has<ActorComponent>() ? actor.Id.Value : null,
            _state.ControlledEntityId.Value);
        var beneficiary = string.IsNullOrWhiteSpace(beneficiaryId) ? null : EntityById(beneficiaryId);
        if (beneficiary is null)
        {
            return Reject(parent, $"Captive-release beneficiary does not exist: {beneficiaryId}");
        }

        return Apply(WorldConsequence.FreeCaptive(
            parent.Source,
            captive.Id.Value,
            beneficiary.Id.Value,
            anchorEntityId: door.Id.Value,
            deedTags: FreeCaptiveDeedTags(captive),
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: beneficiary.Id.Value,
            evidence: FirstNonBlank(parent.Evidence, $"{captive.Name} was released by opening {door.Name}."),
            reason: "Opening a captive-door requested the shared free_captive consequence.",
            operation: "freeCaptive",
            message: beneficiary.Id == _state.ControlledEntityId
                ? $"{captive.Name} is free enough to choose you, for now."
                : $"{captive.Name} is free enough to choose {beneficiary.Name}, for now.",
            details: new Dictionary<string, object?>
            {
                ["parentConsequenceType"] = parent.Type,
                ["parentOperation"] = parentOperation,
                ["doorId"] = door.Id.Value,
                ["doorName"] = door.Name,
            }));
    }

    private static bool DoorCanReleaseCaptives(Entity door)
    {
        if (door.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => tag.Equals("cell", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("jail", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("cage", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("prison", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("captive_door", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return door.Name.Contains("cell", StringComparison.OrdinalIgnoreCase)
            || door.Name.Contains("jail", StringComparison.OrdinalIgnoreCase)
            || door.Name.Contains("cage", StringComparison.OrdinalIgnoreCase)
            || door.Name.Contains("prison", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnreleasedCaptive(Entity entity)
    {
        if (!entity.TryGet<ActorComponent>(out var actor) || !actor.Alive)
        {
            return false;
        }

        var taggedCaptive = entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag => tag.Equals("prisoner", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("captive", StringComparison.OrdinalIgnoreCase));
        var captivePolicy = entity.TryGet<AiComponent>(out var ai)
            && ai.PolicyId.Equals("captive", StringComparison.OrdinalIgnoreCase);
        if (!taggedCaptive && !captivePolicy)
        {
            return false;
        }

        return !actor.Faction.Equals("player", StringComparison.OrdinalIgnoreCase)
            && (!entity.TryGet<AiComponent>(out var updatedAi)
                || !updatedAi.PolicyId.Equals("follower", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> FreeCaptiveDeedTags(Entity captive)
    {
        var tags = new List<string> { "mercy", "anti_empire", "rescued" };
        if (captive.TryGet<FactionComponent>(out var faction))
        {
            tags.Add(faction.FactionId);
        }

        if (captive.TryGet<TagsComponent>(out var entityTags))
        {
            tags.AddRange(entityTags.Tags.Where(tag =>
                tag.Equals("hollowmere", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("folk_magic", StringComparison.OrdinalIgnoreCase)));
        }

        return tags.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private WorldConsequenceApplyResult ApplyCreateRoute(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var anchorId = consequence.TargetEntityId;
        var name = FirstNonBlank(ReadString(payload, "name"), "hidden route")!;
        var description = FirstNonBlank(ReadString(payload, "description"), consequence.Evidence, name)!;
        var routeKind = NormalizeToken(ReadString(payload, "routeKind") ?? "hidden_route", "hidden_route");
        var anchor = string.IsNullOrWhiteSpace(anchorId) ? null : EntityById(anchorId);
        GridPoint position;
        string? anchorEntityId = null;
        if (TryReadPoint(payload, null, out var explicitPosition))
        {
            position = explicitPosition;
        }
        else if (anchor is not null && anchor.TryGet<PositionComponent>(out var anchorPosition))
        {
            position = FindOpenAdjacent(anchorPosition.Position) ?? anchorPosition.Position;
            anchorEntityId = anchor.Id.Value;
        }
        else
        {
            return Reject(consequence, "Route consequence needs an anchor with a position or explicit x/y coordinates.");
        }

        if (!InBounds(position))
        {
            return Reject(consequence, "Route consequence target is out of bounds.");
        }

        var routeId = _state.NextEntityId("route");
        var tags = new[] { "route", "escape_route", routeKind, "promise_payoff" }
            .Concat(ReadStringList(payload, "tags"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var promiseIds = ReadStringList(payload, "promiseIds")
            .Concat(ReadStringList(payload, "promise_ids"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var material = NormalizeToken(FirstNonBlank(ReadString(payload, "material"), "passage")!, "passage");
        var route = new Entity(routeId, name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('>', "route"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(description))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: material))
            .Set(new FixtureComponent(routeKind, tags))
            .Set(new InteractableComponent(new[] { "examine", "travel" }));
        if (promiseIds.Length > 0)
        {
            route.Set(new PromiseAnchorComponent(promiseIds));
        }

        _state.Entities[routeId] = route;

        var operation = ReadString(payload, "operation") ?? "createRoute";
        var summary = FirstNonBlank(ReadString(payload, "message"), ReadString(payload, "summary"), $"A route is now discoverable: {route.Name}.")!;
        var delta = new StateDelta(
            operation,
            route.Id.Value,
            summary,
            Details(
                consequence,
                ("routeId", route.Id.Value),
                ("routeKind", routeKind),
                ("x", position.X),
                ("y", position.Y),
                ("tags", tags),
                ("promiseIds", promiseIds),
                ("material", material),
                ("anchorEntityId", anchorEntityId ?? anchorId)));
        return Applied(consequence, route.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("routeId", route.Id.Value));
    }
}
