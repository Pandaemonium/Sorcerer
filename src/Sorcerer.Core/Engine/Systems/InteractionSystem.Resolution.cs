using Sorcerer.Core.Consequences;
using Sorcerer.Core.Dialogue;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Engine.Systems;

/// <summary>
/// <see cref="InteractionSystem"/> target and service resolution: nearby-entity/actor lookup, reach and out-of-reach hints, dialogue-intent resolution, and service-provider/promise matching.
/// Split from the interaction system (Phase 0.4); the ctor, the Talk/dialogue interaction
/// core, and shared entity/bond/message helpers stay in the base file. All state changes
/// still go through _engine.ApplyConsequence.
/// </summary>
public sealed partial class InteractionSystem
{
    private Entity? ResolveNearbyEntity(
        string? target,
        Func<Entity, bool> predicate,
        int range)
    {
        var candidates = NearbyCandidates(predicate, range);

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            return candidates.FirstOrDefault(entity =>
                entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                || (entity.TryGet<TagsComponent>(out var tags)
                    && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))));
        }

        if (State.SelectedTarget is { } selected)
        {
            var selectedEntity = candidates.FirstOrDefault(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == selected);
            if (selectedEntity is not null)
            {
                return selectedEntity;
            }
        }

        return candidates.FirstOrDefault();
    }

    private IReadOnlyList<Entity> NearbyCandidates(Func<Entity, bool> predicate, int range)
    {
        var origin = State.ControlledEntity.Get<PositionComponent>().Position;
        return State.Entities.Values
            .Where(predicate)
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && InteractionDistance(origin, position.Position) <= range)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position) ? GameEngine.Distance(origin, position.Position) : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
    }

    // Names the nearest visible-to-the-player candidate matching the same predicate/target used
    // by an interaction that just failed for being out of range, so the failure message can say
    // where the thing actually is instead of a bare "nothing here." Perception-bound (only
    // entities in PerceptionSystem's current visible set are candidates), so a hidden entity is
    // never named here even if it happens to match. Shared (public static, taking the engine
    // explicitly) because ItemSystem's Pickup lives in a different class but wants the same hint.
    public static string? OutOfReachHint(GameEngine engine, GameState state, string? target, Func<Entity, bool> predicate)
    {
        if (!state.ControlledEntity.TryGet<PositionComponent>(out var originPosition))
        {
            return null;
        }

        var origin = originPosition.Position;
        var visibleIds = engine.Perception().VisibleEntityIds;
        var candidates = state.Entities.Values
            .Where(entity => entity.Id != state.ControlledEntityId)
            .Where(predicate)
            .Where(entity => visibleIds.Contains(entity.Id))
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .ToArray();

        if (!string.IsNullOrWhiteSpace(target))
        {
            var normalizedTarget = target.Trim();
            var normalizedSearchText = NormalizeSearchText(normalizedTarget);
            candidates = candidates
                .Where(entity =>
                    entity.Id.Value.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    || entity.Name.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    // Short target words (e.g. "notice", "the door"): does the entity's name
                    // contain the search text?
                    || entity.Name.Contains(normalizedTarget, StringComparison.OrdinalIgnoreCase)
                    // Conversational mentions (e.g. "Lio, do you trust me?"): does the search
                    // text mention one of the entity's name tokens?
                    || EntityNameTokens(entity).Any(token => normalizedSearchText.Contains(token, StringComparison.OrdinalIgnoreCase))
                    || (entity.TryGet<TagsComponent>(out var tags)
                        && tags.Tags.Any(tag => tag.Equals(normalizedTarget, StringComparison.OrdinalIgnoreCase))))
                .ToArray();
        }

        var nearest = candidates
            .Select(entity => new { Entity = entity, Position = entity.Get<PositionComponent>().Position })
            .OrderBy(match => GameEngine.StepDistance(origin, match.Position))
            .ThenBy(match => match.Entity.Id.Value)
            .FirstOrDefault();
        if (nearest is null)
        {
            return null;
        }

        // Report reach in steps (Chebyshev), matching the reach checks: a diagonal neighbor reads
        // as "1 tile", not "2 tiles", so the hint never contradicts what the player can reach.
        var distance = GameEngine.StepDistance(origin, nearest.Position);
        var direction = CompassDirection(origin, nearest.Position);
        var tiles = distance == 1 ? "tile" : "tiles";
        return $"{nearest.Entity.Name} is out of reach - {distance} {tiles} {direction}.";
    }

    private static string CompassDirection(GridPoint from, GridPoint to)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var vertical = dy < 0 ? "north" : dy > 0 ? "south" : "";
        var horizontal = dx < 0 ? "west" : dx > 0 ? "east" : "";
        if (vertical.Length > 0 && horizontal.Length > 0)
        {
            return $"{vertical}{horizontal}";
        }

        return vertical.Length > 0 ? vertical : horizontal.Length > 0 ? horizontal : "here";
    }

    private static bool CanReach(Entity actor, Entity target, int range) =>
        actor.TryGet<PositionComponent>(out var actorPosition)
        && target.TryGet<PositionComponent>(out var targetPosition)
        && InteractionDistance(actorPosition.Position, targetPosition.Position) <= range;

    private Entity? ResolveNearbyActorMention(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var normalized = NormalizeSearchText(text);
        var candidates = NearbyCandidates(
            entity => entity.Id != State.ControlledEntityId && entity.Has<ActorComponent>(),
            range: 2);
        return candidates.FirstOrDefault(entity =>
            normalized.Contains(NormalizeSearchText(entity.Id.Value), StringComparison.OrdinalIgnoreCase)
            || EntityNameTokens(entity).Any(token => normalized.Contains(token, StringComparison.OrdinalIgnoreCase))
            || (entity.TryGet<TagsComponent>(out var tags)
                && tags.Tags.Any(tag => normalized.Contains(NormalizeSearchText(tag), StringComparison.OrdinalIgnoreCase))));
    }

    private static string NormalizeDialoguePlayerText(string text, Entity target) =>
        IsTargetOnlyDialogueText(text, target)
            ? "I approach and wait for you to speak."
            : text.Trim();

    private static bool IsTargetOnlyDialogueText(string text, Entity target)
    {
        var normalized = NormalizeDialogueMatchText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        IEnumerable<string> candidates = new[] { target.Id.Value, target.Name }
            .Concat(EntityNameTokens(target));
        if (target.TryGet<ProfileComponent>(out var profile))
        {
            candidates = candidates.Append(profile.PublicName);
        }

        if (target.TryGet<TagsComponent>(out var tags))
        {
            candidates = candidates.Concat(tags.Tags);
        }

        return candidates
            .Select(NormalizeDialogueMatchText)
            .Any(candidate => candidate.Length > 0
                && candidate.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> EntityNameTokens(Entity entity) =>
        entity.Name
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeSearchText)
            .Where(token => token.Length >= 3);

    private static string NormalizeSearchText(string text) =>
        new(text
            .ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : ' ')
            .ToArray());

    private static string NormalizeDialogueMatchText(string text) =>
        string.Join(' ', NormalizeSearchText(text).Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private void ResolveDialogueIntent(
        Entity target,
        string text,
        List<string> messages,
        List<StateDelta> deltas)
    {
        var lower = text.ToLowerInvariant();
        if (lower.Contains("threat", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("obey", StringComparison.OrdinalIgnoreCase)
            || lower.Contains("fear", StringComparison.OrdinalIgnoreCase))
        {
            var transaction = GameTransaction.Begin(State);
            var deltaStart = deltas.Count;
            var messageStart = messages.Count;
            var bond = ApplyBondUpdate(
                target,
                "dialogue:threat",
                fear: 2,
                resentment: 1,
                posture: "afraid",
                operation: "bondShift");
            if (!bond.Applied)
            {
                RollBackThreatDialogueTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    target,
                    bond.Deltas,
                    bond.Error ?? "threat_bond_rejected");
                return;
            }

            var message = $"{target.Name} hears the threat and will not forget it.";
            deltas.AddRange(bond.Deltas);
            var memory = _engine.ApplyConsequence(WorldConsequence.RecordMemory(
                "dialogue:threat",
                target.Id.Value,
                message,
                "dialogue:threat",
                2,
                shareable: true,
                sourceEntityId: State.ControlledEntityId.Value,
                evidence: text,
                operation: "threatMemory",
                details: new Dictionary<string, object?>
                {
                    ["requireOwnerEntity"] = true,
                }));
            if (!memory.Applied)
            {
                RollBackThreatDialogueTransaction(
                    transaction,
                    deltas,
                    deltaStart,
                    messages,
                    messageStart,
                    target,
                    memory.Deltas,
                    memory.Error ?? "threat_memory_rejected");
                return;
            }

            messages.Add(message);
            deltas.AddRange(memory.Deltas);
            transaction.Commit();
            return;
        }

        var line = DialogueLine(target, deltas);
        messages.Add(line);
    }

    private static void RollBackThreatDialogueTransaction(
        GameTransaction transaction,
        List<StateDelta> deltas,
        int deltaStart,
        List<string> messages,
        int messageStart,
        Entity target,
        IReadOnlyList<StateDelta> failedDeltas,
        string failure)
    {
        transaction.Rollback();
        RemoveRangeFrom(deltas, deltaStart);
        RemoveRangeFrom(messages, messageStart);
        var diagnostics = FailureDiagnostics(failedDeltas);
        deltas.AddRange(diagnostics);
        deltas.Add(new StateDelta(
            "threatDialogueSkipped",
            target.Id.Value,
            $"Threat dialogue rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["targetEntityId"] = target.Id.Value,
                ["failure"] = failure,
                ["rejectedCount"] = diagnostics.Count,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            }));
    }

    private IEnumerable<Entity> NearbyServiceProviders(
        string? targetText,
        string trigger,
        string? serviceText = null)
    {
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            var provider = ResolveNearbyEntity(
                targetText,
                entity => entity.Has<ServiceComponent>() || HasServicePromise(entity, trigger, serviceText),
                range: 2);
            return provider is null ? Array.Empty<Entity>() : new[] { provider };
        }

        var origin = State.ControlledEntity.Get<PositionComponent>().Position;
        return State.Entities.Values
            .Where(entity => entity.Has<ServiceComponent>() || HasServicePromise(entity, trigger, serviceText))
            .Where(entity => entity.TryGet<PositionComponent>(out var position)
                && GameEngine.Distance(origin, position.Position) <= 2)
            .OrderBy(entity => entity.TryGet<PositionComponent>(out var position)
                ? GameEngine.Distance(origin, position.Position)
                : int.MaxValue)
            .ThenBy(entity => entity.Id.Value)
            .ToArray();
    }

    private Entity? ResolveServiceProvider(string? targetText, string serviceText, string trigger)
    {
        var providers = NearbyServiceProviders(targetText, trigger, serviceText).ToArray();
        if (!string.IsNullOrWhiteSpace(targetText))
        {
            return providers.FirstOrDefault();
        }

        return providers.FirstOrDefault(provider => FindService(VisibleServices(provider), serviceText) is not null)
            ?? providers.FirstOrDefault(provider => HasServicePromise(provider, trigger, serviceText));
    }

    private bool HasServicePromise(Entity entity, string trigger, string? serviceText = null)
    {
        if (!entity.TryGet<PromiseAnchorComponent>(out var anchor))
        {
            return false;
        }

        return anchor.PromiseIds.Any(promiseId =>
            State.PromiseLedger.Promises.Any(promise =>
                promise.Id.Equals(promiseId, StringComparison.OrdinalIgnoreCase)
                && promise.Status.Equals("bound", StringComparison.OrdinalIgnoreCase)
                && ServiceRealizationKind(promise)
                && ServiceTriggerMatches(promise.TriggerHint, trigger)
                && ServiceTextMatches(promise, serviceText)));
    }

    private static bool ServiceRealizationKind(WorldPromise promise)
    {
        var text = NormalizeToken(promise.RealizationKind ?? promise.Kind, "");
        return text is "service" or "folk_magic" or "folk_magic_service";
    }

    private static bool ServiceTriggerMatches(string? triggerHint, string trigger)
    {
        if (string.IsNullOrWhiteSpace(triggerHint))
        {
            return true;
        }

        var normalizedTrigger = NormalizeToken(trigger, "");
        var parts = triggerHint
            .Split(new[] { ',', '/', '|', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => NormalizeToken(part, ""))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return parts.Contains(normalizedTrigger)
            || parts.Contains("encounter")
            || (normalizedTrigger is "services" or "request" or "service"
                && parts.Overlaps(new[] { "service", "services", "request", "offer", "folk_magic", "door", "lock", "ward", "mend", "heal", "guide" }));
    }

    private static bool ServiceTextMatches(WorldPromise promise, string? serviceText)
    {
        if (string.IsNullOrWhiteSpace(serviceText))
        {
            return true;
        }

        var normalized = serviceText.Trim();
        var subject = promise.Subject?.Trim();
        return (!string.IsNullOrWhiteSpace(subject)
                && (subject.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                    || normalized.Contains(subject, StringComparison.OrdinalIgnoreCase)))
            || promise.Text.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || PromiseServiceNameForMatch(promise).Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(PromiseServiceNameForMatch(promise), StringComparison.OrdinalIgnoreCase);
    }

    private static string PromiseServiceNameForMatch(WorldPromise promise)
    {
        var lower = promise.Text.ToLowerInvariant();
        if (lower.Contains("door") || lower.Contains("lock") || lower.Contains("ward"))
        {
            return "ward-breaking";
        }

        if (lower.Contains("route") || lower.Contains("drain") || lower.Contains("tunnel") || lower.Contains("escape"))
        {
            return "hidden-route finding";
        }

        return string.IsNullOrWhiteSpace(promise.Subject) ? "service" : promise.Subject;
    }

    private static IEnumerable<ServiceOffer> VisibleServices(Entity provider) =>
        provider.TryGet<ServiceComponent>(out var services)
            ? services.Offers.Where(service => service.Revealed)
            : Array.Empty<ServiceOffer>();

    private static ServiceOffer? FindService(IEnumerable<ServiceOffer> services, string serviceText)
    {
        var normalized = serviceText.Trim();
        return services.FirstOrDefault(service =>
            service.Id.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || service.Name.Contains(normalized, StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(service.Name, StringComparison.OrdinalIgnoreCase)
            || service.Tags?.Any(tag => tag.Equals(normalized, StringComparison.OrdinalIgnoreCase)) == true);
    }

    private static string FormatServiceLine(Entity provider, ServiceOffer service)
    {
        var costs = new List<string>();
        if (service.GoldCost > 0)
        {
            costs.Add($"{service.GoldCost} gold");
        }

        if (!string.IsNullOrWhiteSpace(service.ItemCost))
        {
            costs.Add(service.ItemCost);
        }

        var cost = costs.Count == 0 ? "no listed price" : string.Join(" and ", costs);
        return $"{provider.Name} offers {service.Name}: {service.Description} ({cost}).";
    }
}
