namespace Sorcerer.Core.Lore;

public sealed record LoreQuery(
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Triggers,
    int AccessLevel = 1,
    int Limit = 3,
    IReadOnlyDictionary<string, int>? SubjectAccessLevels = null);

public sealed record RoutedLoreCard(
    string Id,
    string Title,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Triggers,
    int Level,
    string Body,
    int Score);

public static class LoreRouter
{
    public static IReadOnlyList<RoutedLoreCard> Select(LoreCatalog catalog, LoreQuery query)
    {
        var subjects = NormalizeSet(query.Subjects);
        var triggers = NormalizeSet(query.Triggers);
        var access = Math.Max(0, query.AccessLevel);
        var limit = Math.Max(1, query.Limit);
        var subjectAccess = NormalizeAccessMap(query.SubjectAccessLevels);

        return catalog.Cards
            .Select(card => Route(card, subjects, triggers, access, subjectAccess))
            .Where(card => card is not null)
            .Select(card => card!)
            .OrderByDescending(card => card.Score)
            .ThenBy(card => card.Title, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static RoutedLoreCard? Route(
        LoreCard card,
        IReadOnlySet<string> subjects,
        IReadOnlySet<string> triggers,
        int access,
        IReadOnlyDictionary<string, int> subjectAccess)
    {
        var score = 0;
        var cardAccess = access;
        foreach (var subject in card.Subjects)
        {
            if (subjects.Contains(subject))
            {
                score += 5;
            }

            if (subjectAccess.TryGetValue(subject, out var subjectTier))
            {
                cardAccess = Math.Max(cardAccess, subjectTier);
            }
        }

        foreach (var trigger in card.Triggers)
        {
            if (triggers.Contains(trigger))
            {
                score += 4;
            }
        }

        if (card.Triggers.Contains("magic_context", StringComparer.OrdinalIgnoreCase)
            && triggers.Contains("magic_context"))
        {
            score += 1;
        }

        if (score <= 0)
        {
            return null;
        }

        var sections = card.Sections
            .Where(section => section.Level <= cardAccess && !section.Draft)
            .OrderBy(section => section.Level)
            .ToArray();
        if (sections.Length == 0)
        {
            return null;
        }

        return new RoutedLoreCard(
            card.Id,
            card.Title,
            card.Subjects,
            card.Triggers,
            sections.Max(section => section.Level),
            string.Join("\n", sections.Select(section => section.Body)),
            score);
    }

    private static IReadOnlySet<string> NormalizeSet(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(LoreCatalog.NormalizeToken)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, int> NormalizeAccessMap(IReadOnlyDictionary<string, int>? values)
    {
        var normalized = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values ?? new Dictionary<string, int>())
        {
            var key = LoreCatalog.NormalizeToken(pair.Key);
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            normalized[key] = Math.Max(normalized.TryGetValue(key, out var existing) ? existing : 0, Math.Max(0, pair.Value));
        }

        return normalized;
    }
}
