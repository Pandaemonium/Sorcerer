using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.World;

public sealed record WorldReactionSummary(
    IReadOnlyList<string> StandingChanges,
    IReadOnlyList<string> NewPromises,
    IReadOnlyList<string> NarrationHooks);

public sealed class WorldReactionSystem
{
    public DeedRecord CaptureDeed(
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

        return state.Deeds.Append(
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

    public IReadOnlyList<string> ApplyPending(GameState state)
    {
        var messages = new List<string>();
        var appliedAny = false;
        foreach (var deed in state.Deeds.Records
            .Where(deed => !state.Deeds.IsApplied(deed.Id))
            .OrderBy(deed => deed.Turn)
            .ThenBy(deed => deed.Id))
        {
            ApplyDeed(state, deed, messages);
            state.Deeds.MarkApplied(deed.Id);
            appliedAny = true;
        }

        if (!appliedAny)
        {
            RegenerateFactionPressure(state);
        }

        ApplyFactionPressure(state, messages);
        return messages;
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

    private static void ApplyDeed(GameState state, DeedRecord deed, List<string> messages)
    {
        if (deed.Visibility.Equals("secret", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        switch (deed.Kind)
        {
            case "freed_prisoner":
                AddLegend(state, deed, "merciful", 2);
                AddLegend(state, deed, "defiant", 1);
                state.Factions.AdjustStanding("hollowmere", "gratitude", 2);
                AdjustEmpireBloc(state, "suspicion", 1);
                RaiseEmpireHeat(state, 1);
                AddMessage(state, messages, "Word of the prisoner's rescue finds a road.");
                break;
            case "body_swap":
                AddLegend(state, deed, "uncanny", 2);
                AdjustEmpireBloc(state, "suspicion", deed.Magnitude);
                RaiseEmpireHeat(state, Math.Max(1, deed.Magnitude - 1));
                AddMessage(state, messages, "The story of stolen eyes starts looking for listeners.");
                break;
            case "kill":
                AddLegend(state, deed, "butcher", Math.Max(1, deed.Magnitude));
                AdjustEmpireBloc(state, "fear", deed.Magnitude);
                AdjustEmpireBloc(state, "notoriety", deed.Magnitude);
                RaiseEmpireHeat(state, Math.Max(1, deed.Magnitude));
                AddMessage(state, messages, "The killing will travel farther than the body.");
                break;
            case "attack":
                AdjustEmpireBloc(state, "fear", Math.Max(1, deed.Magnitude - 1));
                RaiseEmpireHeat(state, 1);
                AddLegend(state, deed, "dangerous", 1);
                break;
            case "wild_magic":
                ApplyWildMagic(state, deed, messages);
                break;
        }
    }

    private static void ApplyWildMagic(GameState state, DeedRecord deed, List<string> messages)
    {
        if (deed.Visibility.Equals("suspicious", StringComparison.OrdinalIgnoreCase))
        {
            AdjustEmpireBloc(state, "suspicion", Math.Max(1, deed.Magnitude));
            RaiseEmpireHeat(state, 1);
            AddMessage(state, messages, "Someone saw the magic, but not the hand that loosed it.");
            return;
        }

        AddLegend(state, deed, "uncanny", Math.Max(1, deed.Magnitude));
        if (deed.Tags.Any(tag => tag.Contains("damage", StringComparison.OrdinalIgnoreCase)))
        {
            AddLegend(state, deed, "dangerous", 1);
            AdjustEmpireBloc(state, "fear", 1);
        }

        AdjustEmpireBloc(state, "imperial-threat", Math.Max(1, deed.Magnitude));
        AdjustEmpireBloc(state, "notoriety", 1);
        RaiseEmpireHeat(state, Math.Max(1, deed.Magnitude));
        AddMessage(state, messages, "Word of your wild magic takes on a sharper color.");
    }

    private static void ApplyFactionPressure(GameState state, List<string> messages)
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

            var spentResponse = false;
            if (heat >= 5 && state.Factions.TrySpendResource(faction.Id, "warrants", 1))
            {
                state.Factions.TrySpendResource(faction.Id, "defenses", 1);
                state.ScheduledEvents.Schedule(
                    state.Turn + 3,
                    "empire_warrant",
                    null,
                    new Dictionary<string, object?>
                    {
                        ["factionId"] = faction.Id,
                        ["text"] = "A wanted poster learns your outline before the ink dries.",
                    });
                state.Canon.Add(
                    "censorate_memo",
                    faction.Id,
                    "Censorate memorandum: the fugitive's legend is to be named, copied, and pinned where color gathers.",
                    "Censorate prepares a wanted poster.",
                    new[] { "empire", "warrant", "legend" },
                    "world_reaction",
                    state.Turn);
                AddMessage(state, messages, "A Censorate clerk starts drafting a wanted poster in your shape.");
                state.Factions.AdjustResource(faction.Id, "heat", -2);
                spentResponse = true;
            }

            heat = state.Factions.ResourceValue(faction.Id, "heat");
            if (heat >= 3
                && !HasActiveOrPendingPatrol(state, faction.Id)
                && state.Factions.TrySpendResource(faction.Id, "patrols", 1))
            {
                state.ScheduledEvents.Schedule(
                    state.Turn + 2,
                    "empire_patrol",
                    null,
                    new Dictionary<string, object?>
                    {
                        ["factionId"] = faction.Id,
                        ["text"] = "An imperial patrol follows the color of your last working.",
                    });
                AddMessage(state, messages, "The Empire spends a patrol to answer your legend.");
                state.Factions.AdjustResource(faction.Id, "heat", -2);
                spentResponse = true;
            }

            if (spentResponse)
            {
                faction.Resources["response_cooldown_until"] = state.Turn + 8;
            }

            if (!spentResponse)
            {
                state.Canon.Add(
                    "censorate_memo",
                    faction.Id,
                    "Censorate memorandum: no patrol is currently free; keep the file warm.",
                    "Censorate lacks a free patrol.",
                    new[] { "empire", "resource_shortage" },
                    "world_reaction",
                    state.Turn);
            }
        }
    }

    private static void RegenerateFactionPressure(GameState state)
    {
        foreach (var faction in state.Factions.FactionsByRole("empire_bloc"))
        {
            RegenerateOne(state, faction.Id, "patrols");
            RegenerateOne(state, faction.Id, "informants");
            RegenerateOne(state, faction.Id, "warrants");
            state.Factions.AdjustResource(faction.Id, "heat", -1);
        }
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

    private static void RegenerateOne(GameState state, string factionId, string resource)
    {
        var max = state.Factions.ResourceValue(factionId, $"max_{resource}");
        if (max <= 0)
        {
            return;
        }

        var current = state.Factions.ResourceValue(factionId, resource);
        if (current < max)
        {
            state.Factions.AdjustResource(factionId, resource, 1, max: max);
        }
    }

    private static void AdjustEmpireBloc(GameState state, string axis, int delta) =>
        state.Factions.AdjustStandingByRole("empire_bloc", axis, delta);

    private static void RaiseEmpireHeat(GameState state, int amount)
    {
        foreach (var faction in state.Factions.FactionsByRole("empire_bloc"))
        {
            state.Factions.AdjustResource(faction.Id, "heat", Math.Max(0, amount));
        }
    }

    private static void AddLegend(GameState state, DeedRecord deed, string tag, int weight)
    {
        if (!deed.AttributionStatus.Equals("attributed", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("witnessed", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("public", StringComparison.OrdinalIgnoreCase)
            && !deed.Visibility.Equals("mythic", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        state.Legend.Add(deed.ActorSoulId, tag, weight, deed.Id);
    }

    private static void AddMessage(GameState state, List<string> messages, string message)
    {
        state.AddMessage(message);
        messages.Add(message);
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
