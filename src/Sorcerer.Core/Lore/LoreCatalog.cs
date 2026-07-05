namespace Sorcerer.Core.Lore;

public sealed record LoreSection(
    int Level,
    string Body,
    bool Draft = false);

public sealed record LoreCard(
    string Id,
    string Title,
    IReadOnlyList<string> Subjects,
    IReadOnlyList<string> Triggers,
    IReadOnlyList<LoreSection> Sections,
    bool Draft = false);

public sealed class LoreCatalog
{
    private readonly IReadOnlyList<LoreCard> _cards;

    public LoreCatalog(IEnumerable<LoreCard> cards)
    {
        _cards = cards
            .Where(card => !card.Draft)
            .Select(card => card with
            {
                Subjects = NormalizeList(card.Subjects),
                Triggers = NormalizeList(card.Triggers),
                Sections = card.Sections
                    .Where(section => !section.Draft && !string.IsNullOrWhiteSpace(section.Body))
                    .OrderBy(section => section.Level)
                    .ToArray(),
            })
            .Where(card => card.Sections.Count > 0)
            .OrderBy(card => card.Id)
            .ToArray();
    }

    public IReadOnlyList<LoreCard> Cards => _cards;

    public static LoreCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var directory = Path.Combine(root, "content", "lore");
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var cards = LoreCardLoader.LoadDirectory(directory).ToArray();
            if (cards.Length > 0)
            {
                return new LoreCatalog(cards);
            }
        }

        return CreateMinimal();
    }

    public static LoreCatalog CreateMinimal() => new(new[]
    {
        new LoreCard(
            "vigovian_containment",
            "Marble Law of Vigovia",
            new[] { "empire", "vigovia", "marble", "law", "containment", "censorate", "soldier" },
            new[] { "imperial_encounter", "empire", "law", "magic_context", "background" },
            new[]
            {
                new LoreSection(
                    1,
                    "Vigovia teaches that wild magic is not evil, only uncivic: label it, measure it, and rescue ordinary people from its weather."),
                new LoreSection(
                    2,
                    "Containment clerks leave deliberate blind spots because a law that watches everything eventually becomes another superstition."),
            }),
        new LoreCard(
            "hollowmere_reed_memory",
            "Hollowmere Reed-Memory",
            new[] { "hollowmere", "reeds", "water", "mud", "memory", "oath" },
            new[] { "hollowmere_margin", "reed_cover", "water", "memory", "magic_context", "background" },
            new[]
            {
                new LoreSection(
                    0,
                    "People around the marsh say Hollowmere names travel strangely over water, and that oaths made near reeds should not be mocked."),
                new LoreSection(
                    1,
                    "Hollowmere keeps names in water and takes oaths seriously when the reeds are listening."),
                new LoreSection(
                    2,
                    "A promise spoken into Hollowmere mud may later surface as a path, a witness, or a debt with wet hands."),
                new LoreSection(
                    4,
                    "A hidden water-name can be stored below a moving current so dry law cannot call it cleanly; speaking it aloud creates a debt before it creates an answer."),
            }),
        new LoreCard(
            "wild_border_broken_law",
            "Wild Border Broken-Law",
            new[] { "wild_border", "flowers", "bone", "rain", "broken-law", "wild_magic" },
            new[] { "wild_border", "loose_reality", "transformation", "magic_context", "background" },
            new[]
            {
                new LoreSection(
                    1,
                    "At the wild border, flowers bloom in arguments and old rules work only when personally persuaded."),
                new LoreSection(
                    2,
                    "Bone rain here is considered weather, warning, and invitation in the same breath."),
            }),
        new LoreCard(
            "soul_over_body",
            "The Soul Keeps the Thread",
            new[] { "soul", "body", "memory", "mana", "vigor", "body_swap" },
            new[] { "body_swap", "soul", "memory", "magic_context" },
            new[]
            {
                new LoreSection(
                    1,
                    "A body carries breath, bruises, pockets, and Vigor; the soul carries memory, mana, Attunement, and Composure."),
            }),
    });

    private static IReadOnlyList<string> NormalizeList(IEnumerable<string> values) =>
        values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(NormalizeToken)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value)
            .ToArray();

    internal static string NormalizeToken(string value)
    {
        var chars = value.Trim().ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_')
            .ToArray();
        return string.Join("_", new string(chars)
            .Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static IEnumerable<string> CandidateRoots()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory is not null)
            {
                yield return directory.FullName;
                directory = directory.Parent;
            }
        }
    }
}
