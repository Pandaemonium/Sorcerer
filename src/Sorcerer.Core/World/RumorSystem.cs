using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;
using Sorcerer.Core.Validation;

namespace Sorcerer.Core.World;

public static class RumorSystem
{
    public static WorldConsequence? ConsequenceFromDeed(GameState state, DeedRecord deed)
    {
        if (deed.Visibility.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || state.Rumors.HasSource("deed", deed.Id))
        {
            return null;
        }

        var regionId = RegionFromPlace(deed.PlaceKey, state.RegionId);
        var carriers = deed.Witnesses
            .Concat(deed.EffectWitnesses ?? Array.Empty<string>())
            .Append(RegionCarrier(regionId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tags = deed.Tags
            .Concat(new[] { "rumor", "deed", deed.Kind, deed.Visibility, deed.AttributionStatus })
            .ToArray();
        return WorldConsequence.RecordRumor(
            "world_reaction",
            "deed",
            deed.Id,
            regionId,
            regionId,
            DeedText(deed),
            Math.Clamp(deed.Magnitude + (deed.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase) ? 1 : 0), 1, 5),
            carriers,
            tags,
            evidence: deed.Id,
            details: new Dictionary<string, object?>
            {
                ["deedId"] = deed.Id,
                ["visibility"] = deed.Visibility,
                ["attributionStatus"] = deed.AttributionStatus,
            });
    }

    public static WorldConsequence? ConsequenceFromClaim(
        GameState state,
        ClaimRecord claim,
        string source = "dialogue_claim")
    {
        if (!claim.PlayerVisible
            || claim.Salience < 3
            || state.Rumors.HasSource("claim", claim.Id))
        {
            return null;
        }

        var carriers = new[]
        {
            claim.SpeakerId,
            claim.ListenerSoulId,
            RegionCarrier(state.RegionId),
        };
        var tags = claim.Tags
            .Concat(new[] { "rumor", "claim", claim.Category })
            .ToArray();
        return WorldConsequence.RecordRumor(
            source,
            "claim",
            claim.Id,
            state.RegionId,
            state.RegionId,
            claim.Text,
            claim.Salience,
            carriers,
            tags,
            evidence: claim.Text,
            operation: "rumorMinted",
            details: new Dictionary<string, object?>
            {
                ["claimId"] = claim.Id,
                ["claimSource"] = claim.Source,
                ["speakerId"] = claim.SpeakerId,
                ["summary"] = $"A rumor begins: {claim.Text}",
            });
    }

    public static RumorRecord? MintFromDeed(
        GameState state,
        DeedRecord deed,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        if (ConsequenceFromDeed(state, deed) is not { } consequence)
        {
            return null;
        }

        var applied = Apply(state, applyConsequence, consequence);
        return string.IsNullOrWhiteSpace(applied.TargetId)
            ? null
            : state.Rumors.Records.FirstOrDefault(rumor => rumor.Id.Equals(applied.TargetId, StringComparison.OrdinalIgnoreCase));
    }

    public static RumorRecord? MintFromClaim(
        GameState state,
        ClaimRecord claim,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        if (ConsequenceFromClaim(state, claim) is not { } consequence)
        {
            return null;
        }

        var applied = Apply(state, applyConsequence, consequence);
        return string.IsNullOrWhiteSpace(applied.TargetId)
            ? null
            : state.Rumors.Records.FirstOrDefault(rumor => rumor.Id.Equals(applied.TargetId, StringComparison.OrdinalIgnoreCase));
    }

    public static IReadOnlyList<StateDelta> Propagate(
        GameState state,
        string reason,
        int maxRumors = 2,
        int maxCarriersPerRumor = 2,
        bool announce = true,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        var spreadBudget = Math.Max(0, maxRumors);
        if (spreadBudget == 0)
        {
            return Array.Empty<StateDelta>();
        }

        var deltas = new List<StateDelta>();
        var regionCarrier = RegionCarrier(state.RegionId);
        var spreadCount = 0;
        foreach (var rumor in state.Rumors.Records
            .Where(IsActive)
            .Where(rumor => rumor.Salience >= 3)
            .Where(rumor => CanReachCurrentRegion(state, rumor, state.RegionId))
            .OrderBy(rumor => rumor.LastTurn)
            .ThenByDescending(rumor => rumor.Salience)
            .ThenBy(rumor => rumor.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray())
        {
            if (spreadCount >= spreadBudget)
            {
                break;
            }

            var carriers = new HashSet<string>(rumor.CarrierIds, StringComparer.OrdinalIgnoreCase);
            var newCarriers = new List<string>();
            if (carriers.Add(regionCarrier))
            {
                newCarriers.Add(regionCarrier);
            }

            var localCarriers = LocalCarrierIds(state, rumor).ToArray();
            foreach (var carrier in localCarriers)
            {
                if (newCarriers.Count >= maxCarriersPerRumor)
                {
                    break;
                }

                if (carriers.Add(carrier))
                {
                    newCarriers.Add(carrier);
                }
            }

            if (newCarriers.Count == 0)
            {
                continue;
            }

            var road = RoadBetweenRegions(state, rumor.CurrentRegionId, state.RegionId);
            var routeName = road?.Name ?? (rumor.CurrentRegionId.Equals(state.RegionId, StringComparison.OrdinalIgnoreCase)
                ? "local paths"
                : "the connected road");
            var historyEntry = $"{NormalizeReason(reason)} traveled by {routeName} to {string.Join(", ", newCarriers)} in {state.RegionId} on turn {state.Turn}.";
            var message = RumorSpreadMessage(state, regionCarrier, newCarriers, rumor, routeName);
            var nextSalience = SalienceAfterSpread(rumor);
            var nextStatus = StatusAfterSpread(rumor, nextSalience);
            var beforeValidation = StateValidator.Validate(state);
            var snapshot = GameStateSnapshot.Capture(state);
            var localDeltas = new List<StateDelta>();
            WorldConsequenceApplyResult applied;
            using (WorldConsequenceGuard.EnterScope())
            {
                // The snapshot above already covers this rumor's whole spread attempt (the
                // update plus any heard-memory writes), so nested ApplyConsequence calls skip
                // their own per-consequence snapshot (see EnterScope).
                applied = Apply(state, applyConsequence, WorldConsequence.UpdateRumor(
                    "world_turn",
                    rumor.Id,
                    currentRegionId: state.RegionId,
                    salience: nextSalience,
                    status: nextStatus,
                    carrierIds: carriers.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray(),
                    appendDistortionHistory: new[] { historyEntry },
                    incrementHops: true,
                    visibility: announce ? WorldConsequenceVisibility.Message : WorldConsequenceVisibility.Hidden,
                    reason: reason,
                    operation: "rumorSpread",
                    message: message,
                    details: new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["regionId"] = state.RegionId,
                        ["newCarriers"] = newCarriers.ToArray(),
                        ["roadId"] = road?.Id,
                        ["roadName"] = routeName,
                        ["fromRegionId"] = rumor.CurrentRegionId,
                        ["salienceBefore"] = rumor.Salience,
                        ["salienceAfter"] = nextSalience,
                        ["statusBefore"] = rumor.Status,
                        ["statusAfter"] = nextStatus,
                        ["playerVisible"] = announce,
                    }));
                localDeltas.AddRange(applied.Deltas);
                if (applied.Applied)
                {
                    localDeltas.AddRange(RecordHeardMemories(state, rumor, reason, newCarriers, applyConsequence));
                }
            }

            if (applied.Applied && !localDeltas.Any(IsRejectedDelta))
            {
                var afterValidation = StateValidator.Validate(state);
                if (!beforeValidation.IsValid || afterValidation.IsValid)
                {
                    deltas.AddRange(localDeltas);
                    spreadCount++;
                    continue;
                }
            }

            snapshot.Restore(state);
            var rejectedDeltas = localDeltas.Where(IsRejectedDelta).ToArray();
            deltas.AddRange(rejectedDeltas);
            if (!applied.Applied && localDeltas.Count > 0 && rejectedDeltas.Length == 0)
            {
                deltas.AddRange(localDeltas);
            }

            deltas.Add(RumorPropagationSkippedDelta(
                rumor,
                reason,
                applied.Error ?? "rumor_propagation_rejected",
                rejectedDeltas.Length));
        }

        return deltas;
    }

    private static int SalienceAfterSpread(RumorRecord rumor) =>
        rumor.Hops < 2
            ? rumor.Salience
            : Math.Max(1, rumor.Salience - 1);

    private static string StatusAfterSpread(RumorRecord rumor, int nextSalience) =>
        nextSalience < 3
            ? "stale"
            : rumor.Status;

    public static IReadOnlyList<string> HeardRumorLines(
        GameState state,
        string entityId,
        string regionId,
        int limit)
    {
        var carriers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            entityId,
            RegionCarrier(regionId),
        };
        if (state.Entities.Values.FirstOrDefault(entity =>
                entity.Id.Value.Equals(entityId, StringComparison.OrdinalIgnoreCase)) is { } entity
            && entity.TryGet<SoulComponent>(out var soul))
        {
            carriers.Add(soul.SoulId);
        }

        return state.Rumors.Records
            .Where(IsActive)
            .Where(rumor => rumor.CarrierIds.Any(carriers.Contains))
            .OrderByDescending(rumor => rumor.Salience)
            .ThenByDescending(rumor => rumor.LastTurn)
            .ThenBy(rumor => rumor.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .Select(rumor => $"{rumor.Id} [salience {rumor.Salience}, source {rumor.SourceKind}:{rumor.SourceId}]: {rumor.Text}")
            .ToArray();
    }

    public static IReadOnlyList<RumorRecord> VisibleToPlayer(GameState state, int limit)
    {
        var playerSoul = state.ControlledEntity.TryGet<SoulComponent>(out var soul)
            ? soul.SoulId
            : state.ControlledEntityId.Value;
        var carriers = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            playerSoul,
            state.ControlledEntityId.Value,
            RegionCarrier(state.RegionId),
        };
        return state.Rumors.Records
            .Where(IsActive)
            .Where(rumor => rumor.Salience >= 3)
            .Where(rumor => rumor.CarrierIds.Any(carriers.Contains))
            .OrderByDescending(rumor => rumor.Salience)
            .ThenByDescending(rumor => rumor.LastTurn)
            .ThenBy(rumor => rumor.Id, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, limit))
            .ToArray();
    }

    private static bool IsActive(RumorRecord rumor) =>
        rumor.Status.Equals("active", StringComparison.OrdinalIgnoreCase);

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta RumorPropagationSkippedDelta(
        RumorRecord rumor,
        string reason,
        string failure,
        int rejectedCount) =>
        new(
            "rumorPropagationSkipped",
            rumor.Id,
            $"Rumor propagation rolled back: {failure}.",
            new Dictionary<string, object?>
            {
                ["rumorId"] = rumor.Id,
                ["sourceKind"] = rumor.SourceKind,
                ["sourceId"] = rumor.SourceId,
                ["reason"] = reason,
                ["failure"] = failure,
                ["rejectedCount"] = rejectedCount,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });

    private static WorldConsequenceApplyResult Apply(
        GameState state,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence,
        WorldConsequence consequence) =>
        applyConsequence is not null
            ? applyConsequence(consequence)
            : WorldConsequenceGuard.ApplyWithNewApplier(state, consequence);

    private static bool CanReachCurrentRegion(GameState state, RumorRecord rumor, string regionId) =>
        rumor.CurrentRegionId.Equals(regionId, StringComparison.OrdinalIgnoreCase)
        || RoadBetweenRegions(state, rumor.CurrentRegionId, regionId) is not null;

    private static WorldRoad? RoadBetweenRegions(GameState state, string fromRegionId, string toRegionId)
    {
        if (fromRegionId.Equals(toRegionId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var graph = WorldPlaceGraph.Create(state.Seed, RegionCatalog.LoadDefault());
        var settlements = graph.Settlements.ToDictionary(settlement => settlement.Id, StringComparer.OrdinalIgnoreCase);
        return graph.Roads.FirstOrDefault(road =>
        {
            var from = settlements[road.FromSettlementId].RegionId;
            var to = settlements[road.ToSettlementId].RegionId;
            return (from.Equals(fromRegionId, StringComparison.OrdinalIgnoreCase)
                    && to.Equals(toRegionId, StringComparison.OrdinalIgnoreCase))
                || (from.Equals(toRegionId, StringComparison.OrdinalIgnoreCase)
                    && to.Equals(fromRegionId, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static IEnumerable<string> LocalCarrierIds(GameState state, RumorRecord rumor) =>
        state.Entities.Values
            .Where(entity => entity.Id != state.ControlledEntityId)
            .Where(entity => entity.TryGet<ActorComponent>(out var actor) && actor.Alive)
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                CarrierId = entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value,
                Score = RumorCarrierScore(entity, rumor),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.CarrierId);

    private static int RumorCarrierScore(Entity entity, RumorRecord rumor)
    {
        var score = 0;
        if (entity.TryGet<WantComponent>(out var want)
            && want.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            score += Math.Clamp(want.Salience, 1, 5) * 6;
            score += SharedTagScore(want.Tags, rumor.Tags, 10);
            score += TextContainsTagScore($"{want.Text} {want.Stakes}", rumor.Tags, 4);
            score += TextContainsTagScore(rumor.Text, want.Tags, 4);
        }

        if (entity.TryGet<TagsComponent>(out var tags))
        {
            score += SharedTagScore(tags.Tags, rumor.Tags, 3);
        }

        if (entity.TryGet<FactionComponent>(out var faction))
        {
            score += SharedTagScore(faction.Roles.Append(faction.FactionId), rumor.Tags, 2);
        }

        if (entity.TryGet<ProfileComponent>(out var profile))
        {
            score += TextContainsTagScore($"{profile.PublicName} {profile.Origin} {profile.Backstory}", rumor.Tags, 1);
        }

        return score;
    }

    private static int SharedTagScore(IEnumerable<string> left, IEnumerable<string> right, int weight)
    {
        var normalizedLeft = left
            .Select(NormalizeToken)
            .Where(UsefulCarrierTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var matches = right
            .Select(NormalizeToken)
            .Where(UsefulCarrierTag)
            .Where(tag => normalizedLeft.Any(leftTag => TagsMatch(leftTag, tag)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return matches * weight;
    }

    private static int TextContainsTagScore(string text, IEnumerable<string> tags, int weight)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var lower = text.ToLowerInvariant();
        var matches = tags
            .Select(NormalizeToken)
            .Where(UsefulCarrierTag)
            .Where(tag => TextContainsToken(lower, tag))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return matches * weight;
    }

    private static bool TextContainsToken(string lowerText, string token)
    {
        var phrase = token.Replace('_', ' ');
        return lowerText.Contains(phrase, StringComparison.OrdinalIgnoreCase)
            || lowerText.Contains(token, StringComparison.OrdinalIgnoreCase)
            || (token.EndsWith('s') && lowerText.Contains(token[..^1], StringComparison.OrdinalIgnoreCase));
    }

    private static bool TagsMatch(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase)
        || Singular(left).Equals(Singular(right), StringComparison.OrdinalIgnoreCase);

    private static string Singular(string value) =>
        value.Length > 3 && value.EndsWith('s') ? value[..^1] : value;

    private static bool UsefulCarrierTag(string tag) =>
        !string.IsNullOrWhiteSpace(tag)
        && tag.Length > 2
        && tag is not "rumor"
        && tag is not "claim"
        && tag is not "deed"
        && tag is not "test"
        && tag is not "active"
        && tag is not "promise_source"
        && tag is not "player_visible";

    private static IReadOnlyList<StateDelta> RecordHeardMemories(
        GameState state,
        RumorRecord rumor,
        string reason,
        IReadOnlyList<string> newCarriers,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence)
    {
        var deltas = new List<StateDelta>();
        foreach (var carrier in newCarriers)
        {
            if (carrier.StartsWith("region:", StringComparison.OrdinalIgnoreCase)
                || ResolveCarrierEntity(state, carrier) is not { } listener)
            {
                continue;
            }

            var memoryText = $"{listener.Name} heard a rumor: {rumor.Text}";
            var applied = Apply(state, applyConsequence, WorldConsequence.RecordMemory(
                "world_turn",
                listener.Id.Value,
                memoryText,
                $"rumor:{rumor.Id}",
                rumor.Salience,
                shareable: true,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: listener.Id.Value,
                evidence: rumor.Text,
                reason: "Rumor propagation leaves durable NPC context through the shared memory lifecycle.",
                operation: "rumorHeardMemory",
                details: new Dictionary<string, object?>
                {
                    ["rumorId"] = rumor.Id,
                    ["sourceKind"] = rumor.SourceKind,
                    ["sourceId"] = rumor.SourceId,
                    ["carrierId"] = carrier,
                    ["listenerEntityId"] = listener.Id.Value,
                    ["spreadReason"] = reason,
                    ["summary"] = $"{listener.Name} remembers hearing a rumor.",
                }));
            deltas.AddRange(applied.Deltas);
        }

        return deltas;
    }

    private static Entity? ResolveCarrierEntity(GameState state, string carrierId) =>
        state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(carrierId, StringComparison.OrdinalIgnoreCase)
            || (entity.TryGet<SoulComponent>(out var soul)
                && soul.SoulId.Equals(carrierId, StringComparison.OrdinalIgnoreCase)));

    private static string DeedText(DeedRecord deed)
    {
        var actor = deed.AttributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase)
            ? "the wild sorcerer"
            : "someone bright and unnamed";
        var place = ReadablePlace(deed.PlaceKey);
        return deed.Kind switch
        {
            "freed_prisoner" => $"{actor} freed a prisoner in {place}.",
            "body_swap" => $"{actor} walked out wearing another face in {place}.",
            "kill" => $"{actor} left a death in {place}.",
            "attack" => $"{actor} struck first in {place}.",
            "wild_magic" => $"{actor} worked wild magic in {place}.",
            _ => $"people in {place} are still talking about what {actor} did: {deed.Kind}.",
        };
    }

    private static string RegionFromPlace(string placeKey, string fallback)
    {
        var parts = placeKey.Split(':', 2, StringSplitOptions.TrimEntries);
        return parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0]) ? parts[0] : fallback;
    }

    /// <summary>
    /// Turns an internal place key like "imperial_encounter:13,29" into a readable place name
    /// ("Imperial Encounter") for player-facing text: takes the region portion, drops the raw
    /// coordinates, and title-cases. Message-log immersion pass — coordinates and snake_case ids
    /// must never surface to the player.
    /// </summary>
    private static string ReadablePlace(string placeKey)
    {
        var region = RegionFromPlace(placeKey ?? string.Empty, placeKey ?? string.Empty);
        var readable = region.Replace('_', ' ').Trim();
        if (string.IsNullOrWhiteSpace(readable))
        {
            return "the frontier";
        }

        return System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(readable);
    }

    private static string RegionCarrier(string regionId) => $"region:{regionId}";

    private static string NormalizeReason(string reason) =>
        string.IsNullOrWhiteSpace(reason) ? "pump" : reason.Trim();

    private static string NormalizeToken(string text)
    {
        var chars = text
            .Trim()
            .ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }

    private static string ReadableRegion(string regionId) =>
        regionId.Replace('_', ' ');

    private static string RumorSpreadMessage(
        GameState state,
        string regionCarrier,
        IReadOnlyList<string> newCarriers,
        RumorRecord rumor,
        string routeName)
    {
        var listeners = newCarriers
            .Where(carrier => !carrier.Equals(regionCarrier, StringComparison.OrdinalIgnoreCase))
            .Select(carrier => ReadableCarrier(state, carrier))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();
        return listeners.Length switch
        {
            0 => $"A rumor reaches {ReadableRegion(state.RegionId)} along {routeName}: {rumor.Text}",
            1 => $"A rumor reaches {listeners[0]} along {routeName} because {CarrierCause(state, listeners[0], rumor)}: {rumor.Text}",
            _ => $"A rumor passes along {routeName} between {string.Join(" and ", listeners)}: {rumor.Text}",
        };
    }

    private static string CarrierCause(GameState state, string listenerName, RumorRecord rumor)
    {
        var listener = state.Entities.Values.FirstOrDefault(entity =>
            entity.Name.Equals(listenerName, StringComparison.OrdinalIgnoreCase));
        if (listener?.TryGet<WantComponent>(out var want) == true)
        {
            var shared = want.Tags.FirstOrDefault(tag => rumor.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(shared))
            {
                return $"the story touches their concern for {shared.Replace('_', ' ')}";
            }

            return "it bears on something they already want";
        }

        return "they are the nearest willing carrier";
    }

    private static string ReadableCarrier(GameState state, string carrierId)
    {
        var entity = state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(carrierId, StringComparison.OrdinalIgnoreCase)
            || (entity.TryGet<SoulComponent>(out var soul)
                && soul.SoulId.Equals(carrierId, StringComparison.OrdinalIgnoreCase)));
        if (entity is not null)
        {
            return entity.Name;
        }

        return carrierId.StartsWith("region:", StringComparison.OrdinalIgnoreCase)
            ? ReadableRegion(carrierId["region:".Length..])
            : carrierId.Replace('_', ' ');
    }
}
