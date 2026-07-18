using Sorcerer.Core.Primitives;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Views;

public static class JournalViewBuilder
{
    public static IReadOnlyList<string> Build(GameState state)
    {
        var messages = new List<string>();
        var visiblePromises = state.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible)
            .ToArray();
        var leads = visiblePromises
            .Where(IsLeadPromise)
            .Select(promise => ObjectiveIsComplete(state, promise)
                ? $"Completed objective: [{PromiseJournalStatus(state, promise)}] {promise.Text}"
                : $"Objective: [{PromiseJournalStatus(state, promise)}] {promise.Text}")
            .ToArray();
        var otherPromises = visiblePromises
            .Where(promise => !IsLeadPromise(promise))
            .Select(promise => $"Promise: [{PromiseJournalStatus(state, promise)}] {promise.Text}")
            .ToArray();
        if (leads.Length == 0 && otherPromises.Length == 0)
        {
            messages.Add("No promises are visible yet.");
        }
        else
        {
            messages.AddRange(leads);
            messages.AddRange(otherPromises);
        }

        var claims = state.Claims.Records
            .Where(claim => claim.PlayerVisible)
            .Where(claim => claim.Salience >= 3)
            .OrderBy(claim => claim.Id, StringComparer.OrdinalIgnoreCase)
            .Select(claim => $"{claim.Id} [{ClaimJournalStatus(state, claim)}] {claim.Text}")
            .ToArray();
        if (claims.Length > 0)
        {
            messages.AddRange(claims.Select(claim => $"Claim: {claim}"));
        }

        var rumors = RumorViewBuilder.BuildJournalLines(state, limit: 8);
        if (rumors.Count > 0)
        {
            messages.AddRange(rumors);
        }

        var soulId = state.ControlledEntity.TryGet<Sorcerer.Core.Entities.SoulComponent>(out var soul)
            ? soul.SoulId
            : state.ControlledEntityId.Value;
        var legend = state.Legend.Tags
            .Where(tag => tag.ActorSoulId.Equals(soulId, StringComparison.OrdinalIgnoreCase))
            .GroupBy(tag => tag.Tag)
            .Select(group => $"{group.Key}:{group.Sum(tag => tag.Weight)}")
            .OrderBy(text => text)
            .ToArray();
        if (legend.Length > 0)
        {
            messages.Add($"Legend: {string.Join(", ", legend)}");
        }

        // The journal is what the character knows. The reaping is real from turn one, but its
        // schedule only appears here once a claim about it has been read or heard
        // (docs/FREE_FOLK_MOVEMENT.md, Beat 1).
        var knowsReaping = state.Claims.Records.Any(claim =>
            claim.Tags.Contains("reaping", StringComparer.OrdinalIgnoreCase));
        var warrants = state.ScheduledEvents.Events
            .Where(item => item.Kind.StartsWith("empire_", StringComparison.OrdinalIgnoreCase))
            .Where(item => knowsReaping || !item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DueTurn)
            .Select(item => item.Kind switch
            {
                "empire_warrant" => $"Warrant: a wanted poster is expected around turn {item.DueTurn}.",
                "empire_patrol" => $"Pressure: an imperial patrol is expected around turn {item.DueTurn}.",
                "empire_cordon" => $"Cordon: a manhunt cordon is expected to close around turn {item.DueTurn}.",
                "empire_report" => IsOverdueReport(item)
                    ? $"Silence: an imperial post will notice a missing patrol around turn {item.DueTurn}."
                    : $"Word travels: someone who saw you is carrying a report toward an imperial desk (around turn {item.DueTurn}).",
                "empire_hunter_trace" => $"Pursuit: road talk of a Censorate witchhunter should reach you around turn {item.DueTurn}.",
                "empire_hunter" => $"Pursuit: a witchhunter is expected to reach the district around turn {item.DueTurn}.",
                "empire_sweep" => $"The reaping: the imperial sweep is expected to reach {SweepTarget(item)} around turn {item.DueTurn}.",
                _ => $"Pressure: {item.Kind} is expected around turn {item.DueTurn}.",
            })
            .ToArray();
        messages.AddRange(warrants);

        return messages;
    }

    /// <summary>WP9: the same visible knowledge the flat journal lists, typed into navigable
    /// sections with stable ids and provenance. Renderers filter/section this; the flat list stays
    /// for back-compatible human output.</summary>
    public static JournalView BuildStructured(GameState state)
    {
        var visiblePromises = state.PromiseLedger.Promises
            .Where(promise => promise.PlayerVisible)
            .ToArray();

        var objectives = visiblePromises
            .Where(IsLeadPromise)
            .Where(promise => !ObjectiveIsComplete(state, promise))
            .OrderByDescending(promise => promise.Salience)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .Select(promise => Entry(state, promise, "objective"))
            .ToArray();

        var promises = visiblePromises
            .Where(promise => !IsLeadPromise(promise))
            .Where(promise => !ObjectiveIsComplete(state, promise))
            .OrderByDescending(promise => promise.Salience)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .Select(promise => Entry(state, promise, "promise"))
            .ToArray();

        var threads = visiblePromises
            .Where(promise => ObjectiveIsComplete(state, promise))
            .OrderBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .Select(promise => Entry(state, promise, "thread"))
            .ToArray();

        var rumors = state.Claims.Records
            .Where(claim => claim.PlayerVisible && claim.Salience >= 3)
            .OrderByDescending(claim => claim.Salience)
            .ThenBy(claim => claim.Id, StringComparer.OrdinalIgnoreCase)
            .Select(claim => new JournalEntryCard(
                claim.Id,
                "rumor",
                claim.Text,
                ClaimJournalStatus(state, claim),
                claim.Salience,
                SourcePhrase(state, claim.SpeakerId, claim.Source),
                EntityName(state, claim.SpeakerId),
                null,
                claim.Tags.ToArray()))
            .ToArray();

        var knowsReaping = state.Claims.Records.Any(claim =>
            claim.Tags.Contains("reaping", StringComparer.OrdinalIgnoreCase));
        var pressures = state.ScheduledEvents.Events
            .Where(item => item.Kind.StartsWith("empire_", StringComparison.OrdinalIgnoreCase))
            .Where(item => knowsReaping || !item.Kind.Equals("empire_sweep", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DueTurn)
            .Select(item => new JournalPressureCard(item.Id, item.Kind, PressureText(item), item.DueTurn))
            .ToArray();

        return new JournalView(objectives, promises, rumors, pressures, threads);
    }

    private static JournalEntryCard Entry(GameState state, WorldPromise promise, string category)
    {
        var destination = string.IsNullOrWhiteSpace(promise.ClaimedPlace)
            ? null
            : RegionCatalog.ReadablePlace(promise.ClaimedPlace);
        return new JournalEntryCard(
            promise.Id,
            category,
            promise.Text,
            PromiseJournalStatus(state, promise),
            promise.Salience,
            SourcePhrase(state, promise.SourceSpeakerId, promise.Source),
            EntityName(state, promise.SourceSpeakerId),
            destination,
            Array.Empty<string>());
    }

    private static string PressureText(ScheduledEventRecord item) => item.Kind switch
    {
        "empire_warrant" => $"A wanted poster is expected around turn {item.DueTurn}.",
        "empire_patrol" => $"An imperial patrol is expected around turn {item.DueTurn}.",
        "empire_cordon" => $"A manhunt cordon is expected to close around turn {item.DueTurn}.",
        "empire_report" => IsOverdueReport(item)
            ? $"An imperial post will notice a missing patrol around turn {item.DueTurn}."
            : $"Someone who saw you is carrying a report toward an imperial desk (around turn {item.DueTurn}).",
        "empire_hunter_trace" => $"Road talk of a Censorate witchhunter should reach you around turn {item.DueTurn}.",
        "empire_hunter" => $"A witchhunter is expected to reach the district around turn {item.DueTurn}.",
        "empire_sweep" => $"The imperial sweep is expected to reach {SweepTarget(item)} around turn {item.DueTurn}.",
        _ => $"{item.Kind} is expected around turn {item.DueTurn}.",
    };

    private static bool IsOverdueReport(ScheduledEventRecord item) =>
        item.Payload.TryGetValue("cause", out var cause)
        && WorldReactionSystem.OverdueReportCause.Equals(Convert.ToString(cause), StringComparison.OrdinalIgnoreCase);

    private static string SweepTarget(ScheduledEventRecord item) =>
        item.Payload.TryGetValue("settlementName", out var name)
        && Convert.ToString(name) is { Length: > 0 } settlement
            ? settlement
            : "a frontier settlement";

    private static bool IsLeadPromise(WorldPromise promise) =>
        promise.Salience >= 3
        && NormalizeJournalToken(promise.RealizationKind ?? promise.Kind) is
            "site" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "quest" or "door_rule" or "escape_route" or "prophecy";

    private static bool ObjectiveIsComplete(GameState state, WorldPromise promise)
    {
        if (PromiseObjectiveContracts.For(state, promise) is not null)
        {
            return promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase);
        }

        return promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
            || (promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
                && !NormalizeJournalToken(promise.RealizationKind ?? promise.Kind).Equals("person", StringComparison.OrdinalIgnoreCase));
    }

    private static string PromiseJournalStatus(GameState state, WorldPromise promise)
    {
        var parts = new List<string> { promise.Status };
        var source = SourcePhrase(state, promise.SourceSpeakerId, promise.Source);
        if (!string.IsNullOrWhiteSpace(source))
        {
            parts.Add(source);
        }

        if (promise.SourceConfidence is not null)
        {
            parts.Add($"{promise.SourceConfidence}% confidence");
        }

        return string.Join(", ", parts);
    }

    private static string ClaimJournalStatus(GameState state, ClaimRecord claim)
    {
        var parts = new List<string> { claim.Status };
        var source = SourcePhrase(state, claim.SpeakerId, claim.Source);
        if (!string.IsNullOrWhiteSpace(source))
        {
            parts.Add(source);
        }

        parts.Add($"{claim.Confidence}% confidence");
        return string.Join(", ", parts);
    }

    private static string? SourcePhrase(GameState state, string? speakerId, string? fallback)
    {
        var name = EntityName(state, speakerId);
        if (!string.IsNullOrWhiteSpace(name))
        {
            return $"heard from {name}";
        }

        if (string.IsNullOrWhiteSpace(fallback)
            || fallback.Equals("unknown", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"from {fallback.Trim()}";
    }

    private static string? EntityName(GameState state, string? entityId)
    {
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return null;
        }

        var id = EntityId.Create(entityId);
        return state.Entities.TryGetValue(id, out var entity)
            ? entity.Name
            : null;
    }

    private static string NormalizeJournalToken(string text)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        return string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
    }
}
