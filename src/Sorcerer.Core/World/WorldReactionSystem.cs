using Sorcerer.Core.Consequences;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Transactions;

namespace Sorcerer.Core.World;

public sealed record WorldReactionSummary(
    IReadOnlyList<string> StandingChanges,
    IReadOnlyList<string> NewPromises,
    IReadOnlyList<string> NarrationHooks);

public sealed record WorldReactionApplication(
    IReadOnlyList<string> Messages,
    IReadOnlyList<StateDelta> Deltas,
    bool AppliedAny);

public sealed record DeedCapturePlan(
    int Turn,
    string ActorSoulId,
    string Kind,
    int Magnitude,
    string PlaceKey,
    string Visibility,
    IReadOnlyList<string> Witnesses,
    IReadOnlyList<string> Tags,
    IReadOnlyList<string> EffectWitnesses,
    string? AttributedSoulId,
    string AttributionStatus);

public sealed class WorldReactionSystem
{
    public DeedCapturePlan PlanDeed(
        GameState state,
        Entity actor,
        string kind,
        int magnitude,
        GridPoint origin,
        GridPoint? effectPoint,
        IReadOnlyList<Entity> actorWitnesses,
        IReadOnlyList<Entity> effectWitnesses,
        IEnumerable<string>? tags = null)
    {
        var actorSoulId = actor.TryGet<SoulComponent>(out var soul) ? soul.SoulId : actor.Id.Value;
        var actorWitnessIds = actorWitnesses
            .Select(WitnessId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
        var effectWitnessIds = effectWitnesses
            .Select(WitnessId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id)
            .ToArray();
        var visibility = ClassifyVisibility(actorWitnessIds, effectWitnessIds, magnitude);
        var attributionStatus = visibility.Equals("suspicious", StringComparison.OrdinalIgnoreCase)
            ? "unattributed"
            : actorWitnessIds.Length > 0 ? "attributed" : "secret";

        return new DeedCapturePlan(
            state.Turn,
            actorSoulId,
            kind,
            Math.Max(1, magnitude),
            $"{state.RegionId}:{origin.X},{origin.Y}",
            visibility,
            actorWitnessIds,
            NormalizeTags(tags),
            effectWitnessIds,
            attributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase) ? actorSoulId : null,
            attributionStatus);
    }

    public WorldReactionApplication ApplyPending(
        GameState state,
        Func<WorldConsequence, WorldConsequenceApplyResult>? applyConsequence = null)
    {
        var apply = applyConsequence ?? (consequence => WorldConsequenceGuard.ApplyWithNewApplier(state, consequence));
        var messages = new List<string>();
        var deltas = new List<StateDelta>();
        var appliedAny = false;
        foreach (var deed in state.Deeds.Records
            .Where(deed => !state.Deeds.IsApplied(deed.Id))
            .OrderBy(deed => deed.Turn)
            .ThenBy(deed => deed.Id)
            .ToArray())
        {
            var snapshot = GameStateSnapshot.Capture(state);
            var deedMessages = new List<string>();
            var deedDeltas = new List<StateDelta>();
            ApplyDeed(state, deed, deedMessages, deedDeltas, apply);
            if (!HasRejected(deedDeltas))
            {
                ApplyConsequence(deedMessages, deedDeltas, apply, WorldConsequence.UpdateDeed(
                "world_reaction",
                deed.Id,
                action: "mark_applied",
                evidence: $"World reactions were applied for deed {deed.Id}.",
                reason: "World reaction processing marks each deed once its consequences have been submitted.",
                operation: "deedApplied"));
            }

            if (HasRejected(deedDeltas))
            {
                snapshot.Restore(state);
                deltas.AddRange(deedDeltas.Where(IsRejectedDelta));
                deltas.Add(WorldReactionSkippedDelta(deed, deedDeltas));
                continue;
            }

            messages.AddRange(deedMessages);
            deltas.AddRange(deedDeltas);
            appliedAny = true;
        }

        return new WorldReactionApplication(messages, deltas, appliedAny);
    }

    public WorldReactionSummary PreviewDailyTick(GameState state)
    {
        var standingChanges = state.Deeds.Records
            .Where(deed => deed.Kind.Contains("imperial", StringComparison.OrdinalIgnoreCase))
            .Select(deed => $"Empire pressure notices {deed.Kind}.")
            .ToArray();

        return new WorldReactionSummary(
            standingChanges,
            Array.Empty<string>(),
            Array.Empty<string>());
    }

    private static void ApplyDeed(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (deed.Visibility.Equals("secret", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (RumorSystem.ConsequenceFromDeed(state, deed) is { } rumor)
        {
            ApplyConsequence(messages, deltas, applyConsequence, rumor);
        }

        RecordWitnessMemories(state, deed, messages, deltas, applyConsequence);

        switch (deed.Kind)
        {
            case "freed_prisoner":
                AddLegend(messages, deltas, applyConsequence, deed, "merciful", 2);
                AddLegend(messages, deltas, applyConsequence, deed, "defiant", 1);
                AdjustFactionStanding(messages, deltas, applyConsequence, "hollowmere", "gratitude", 2);
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", 1);
                RaiseEmpireHeat(state, messages, deltas, applyConsequence, 1);
                AddMessage(messages, deltas, applyConsequence, deed, "freed_prisoner", "By morning someone will have carried word of the rescue down the road.");
                break;
            case "body_swap":
                AddLegend(messages, deltas, applyConsequence, deed, "uncanny", 2);
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", deed.Magnitude);
                RaiseEmpireHeat(state, messages, deltas, applyConsequence, Math.Max(1, deed.Magnitude - 1));
                AddMessage(messages, deltas, applyConsequence, deed, "body_swap", "Whoever watched that will be looking for someone willing to believe them.");
                break;
            case "kill":
                AddLegend(messages, deltas, applyConsequence, deed, "butcher", Math.Max(1, deed.Magnitude));
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "fear", deed.Magnitude);
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "notoriety", deed.Magnitude);
                RaiseEmpireHeat(state, messages, deltas, applyConsequence, Math.Max(1, deed.Magnitude));
                AddMessage(messages, deltas, applyConsequence, deed, "kill", "Someone will carry word of the killing to the next town before nightfall.");
                break;
            case "attack":
                AdjustEmpireBloc(state, messages, deltas, applyConsequence, "fear", Math.Max(1, deed.Magnitude - 1));
                RaiseEmpireHeat(state, messages, deltas, applyConsequence, 1);
                AddLegend(messages, deltas, applyConsequence, deed, "dangerous", 1);
                break;
            case "wild_magic":
                ApplyWildMagic(state, deed, messages, deltas, applyConsequence);
                break;
        }
    }

    private static void RecordWitnessMemories(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        foreach (var witnessId in deed.Witnesses
            .Concat(deed.EffectWitnesses ?? Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
        {
            var witness = ResolveWitnessEntity(state, witnessId);
            if (witness is null || witness.Id == state.ControlledEntityId)
            {
                continue;
            }

            var memoryText = $"{witness.Name} witnessed {DeedMemoryText(deed)}";
            ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.RecordMemory(
                "world_reaction",
                witness.Id.Value,
                memoryText,
                $"deed:{deed.Id}",
                Math.Clamp(deed.Magnitude, 1, 5),
                shareable: true,
                visibility: WorldConsequenceVisibility.Hidden,
                sourceEntityId: witness.Id.Value,
                evidence: deed.Id,
                reason: "A witnessed deed left durable NPC context through the shared memory lifecycle.",
                operation: "deedWitnessMemory",
                details: new Dictionary<string, object?>
                {
                    ["deedId"] = deed.Id,
                    ["deedKind"] = deed.Kind,
                    ["deedVisibility"] = deed.Visibility,
                    ["attributionStatus"] = deed.AttributionStatus,
                    ["actorSoulId"] = deed.ActorSoulId,
                    ["witnessSoulId"] = witnessId,
                    ["placeKey"] = deed.PlaceKey,
                    ["summary"] = $"{witness.Name} remembers witnessing {CleanKind(deed.Kind)}.",
                    ["playerVisible"] = false,
                }));
        }
    }

    private static Entity? ResolveWitnessEntity(GameState state, string witnessId) =>
        state.Entities.Values.FirstOrDefault(entity =>
            entity.Id.Value.Equals(witnessId, StringComparison.OrdinalIgnoreCase)
            || (entity.TryGet<SoulComponent>(out var soul)
                && soul.SoulId.Equals(witnessId, StringComparison.OrdinalIgnoreCase)));

    private static string DeedMemoryText(DeedRecord deed)
    {
        var actor = deed.AttributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase)
            ? deed.ActorSoulId
            : "someone unnamed";
        return $"{CleanKind(deed.Kind)} in {ReadablePlace(deed.PlaceKey)} by {actor}.";
    }

    private static string CleanKind(string kind) => kind.Replace('_', ' ');

    // Player-facing text must never show a raw place key like "imperial_encounter:13,29": take the
    // region portion, drop the coordinates, title-case (message-log immersion pass).
    private static string ReadablePlace(string placeKey)
    {
        var region = (placeKey ?? string.Empty).Split(':', 2, StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
        var readable = region.Replace('_', ' ').Trim();
        return string.IsNullOrWhiteSpace(readable)
            ? "the frontier"
            : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(readable);
    }

    private static void ApplyWildMagic(
        GameState state,
        DeedRecord deed,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence)
    {
        if (deed.Visibility.Equals("suspicious", StringComparison.OrdinalIgnoreCase))
        {
            AdjustEmpireBloc(state, messages, deltas, applyConsequence, "suspicion", Math.Max(1, deed.Magnitude));
            RaiseEmpireHeat(state, messages, deltas, applyConsequence, 1);
            AddMessage(messages, deltas, applyConsequence, deed, "suspicious_wild_magic", "Someone saw the magic, but not the hand that loosed it.");
            return;
        }

        AddLegend(messages, deltas, applyConsequence, deed, "uncanny", Math.Max(1, deed.Magnitude));
        if (deed.Tags.Any(tag => tag.Contains("damage", StringComparison.OrdinalIgnoreCase)))
        {
            AddLegend(messages, deltas, applyConsequence, deed, "dangerous", 1);
            AdjustEmpireBloc(state, messages, deltas, applyConsequence, "fear", 1);
        }

        AdjustEmpireBloc(state, messages, deltas, applyConsequence, "imperial-threat", Math.Max(1, deed.Magnitude));
        AdjustEmpireBloc(state, messages, deltas, applyConsequence, "notoriety", 1);
        RaiseEmpireHeat(state, messages, deltas, applyConsequence, Math.Max(1, deed.Magnitude));
        AddMessage(messages, deltas, applyConsequence, deed, "public_wild_magic", "The people who saw the wild magic are already telling each other what they think it was.");
    }

    private static void AdjustEmpireBloc(
        GameState state,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string axis,
        int delta) =>
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AdjustFactionStanding(
            "world_reaction",
            "empire_bloc",
            axis,
            delta,
            targetIsRole: true));

    private static void RaiseEmpireHeat(
        GameState state,
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        int amount)
    {
        foreach (var faction in state.Factions.FactionsByRole("empire_bloc"))
        {
            AdjustFactionResource(messages, deltas, applyConsequence, faction.Id, "heat", Math.Max(0, amount));
        }
    }

    private static void AddLegend(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        DeedRecord deed,
        string tag,
        int weight)
    {
        if (!deed.AttributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("witnessed", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("mythic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AddLegend(
            "world_reaction",
            deed.ActorSoulId,
            tag,
            weight,
            deed.Id));
    }

    private static void AddMessage(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        DeedRecord deed,
        string reactionKind,
        string message)
    {
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.Message(
            "world_reaction",
            message,
            targetEntityId: deed.Id,
            visibility: WorldConsequenceVisibility.Message,
            evidence: $"{deed.Kind} deed {deed.Id} became {deed.Visibility}.",
            reason: "A deed became visible enough for world-reaction narration.",
            operation: "worldReactionMessage",
            details: new Dictionary<string, object?>
            {
                ["deedId"] = deed.Id,
                ["deedKind"] = deed.Kind,
                ["actorSoulId"] = deed.ActorSoulId,
                ["deedVisibility"] = deed.Visibility,
                ["attributionStatus"] = deed.AttributionStatus,
                ["reactionKind"] = reactionKind,
                ["playerVisible"] = true,
            }));
    }

    private static void AdjustFactionStanding(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string factionId,
        string axis,
        int delta) =>
        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AdjustFactionStanding(
            "world_reaction",
            factionId,
            axis,
            delta));

    private static void AdjustFactionResource(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        string factionId,
        string resource,
        int delta,
        int min = 0,
        int? max = null)
    {
        if (delta == 0)
        {
            return;
        }

        ApplyConsequence(messages, deltas, applyConsequence, WorldConsequence.AdjustFactionResource(
            "world_reaction",
            factionId,
            resource,
            delta,
            min,
            max));
    }

    private static void ApplyConsequence(
        List<string> messages,
        List<StateDelta> deltas,
        Func<WorldConsequence, WorldConsequenceApplyResult> applyConsequence,
        WorldConsequence consequence)
    {
        var applied = applyConsequence(consequence);
        messages.AddRange(applied.Messages);
        deltas.AddRange(applied.Deltas);
    }

    private static bool HasRejected(IEnumerable<StateDelta> deltas) =>
        deltas.Any(IsRejectedDelta);

    private static bool IsRejectedDelta(StateDelta delta) =>
        delta.Operation.Equals("worldConsequenceRejected", StringComparison.OrdinalIgnoreCase);

    private static StateDelta WorldReactionSkippedDelta(DeedRecord deed, IReadOnlyList<StateDelta> rejectedDeltas)
    {
        var errors = rejectedDeltas
            .Where(IsRejectedDelta)
            .Select(delta => delta.Summary)
            .Where(summary => !string.IsNullOrWhiteSpace(summary))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new StateDelta(
            "worldReactionSkipped",
            deed.Id,
            $"World reaction for {deed.Id} was rolled back after a rejected consequence.",
            new Dictionary<string, object?>
            {
                ["deedId"] = deed.Id,
                ["deedKind"] = deed.Kind,
                ["deedVisibility"] = deed.Visibility,
                ["attributionStatus"] = deed.AttributionStatus,
                ["rejectedCount"] = errors.Length,
                ["errors"] = errors,
                ["auditOnly"] = true,
                ["playerVisible"] = false,
            });
    }

    private static string ClassifyVisibility(
        IReadOnlyList<string> actorWitnesses,
        IReadOnlyList<string> effectWitnesses,
        int magnitude)
    {
        if (magnitude >= 8 && actorWitnesses.Count > 0)
        {
            return "mythic";
        }

        if (actorWitnesses.Count >= 2)
        {
            return "public";
        }

        if (actorWitnesses.Count == 1)
        {
            return "witnessed";
        }

        return effectWitnesses.Count > 0 ? "suspicious" : "secret";
    }

    private static string WitnessId(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? Array.Empty<string>())
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim().ToLowerInvariant().Replace(' ', '_'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(tag => tag)
            .ToArray();
}
