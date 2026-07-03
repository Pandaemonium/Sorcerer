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
            .Select(promise => $"Lead: {promise.Id} [{PromiseJournalStatus(state, promise)}] {promise.Text}")
            .ToArray();
        var otherPromises = visiblePromises
            .Where(promise => !IsLeadPromise(promise))
            .Select(promise => $"Promise: {promise.Id} [{PromiseJournalStatus(state, promise)}] {promise.Text}")
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

        var warrants = state.ScheduledEvents.Events
            .Where(item => item.Kind.StartsWith("empire_", StringComparison.OrdinalIgnoreCase))
            .OrderBy(item => item.DueTurn)
            .Select(item => item.Kind switch
            {
                "empire_warrant" => $"Warrant: a wanted poster is expected around turn {item.DueTurn}.",
                "empire_patrol" => $"Pressure: an imperial patrol is expected around turn {item.DueTurn}.",
                _ => $"Pressure: {item.Kind} is expected around turn {item.DueTurn}.",
            })
            .ToArray();
        messages.AddRange(warrants);

        return messages;
    }

    private static bool IsLeadPromise(WorldPromise promise) =>
        promise.Salience >= 3
        && NormalizeJournalToken(promise.RealizationKind ?? promise.Kind) is
            "site" or "town" or "landmark" or "item" or "person" or "threat" or "merchant_stock" or "stock" or "trade" or "quest" or "door_rule" or "escape_route" or "prophecy";

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
