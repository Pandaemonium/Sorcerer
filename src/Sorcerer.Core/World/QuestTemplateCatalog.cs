using System.Text.Json;
using Sorcerer.Core.Entities;

namespace Sorcerer.Core.World;

public sealed record QuestTemplateDefinition(
    string Id,
    string WantPattern,
    string ClaimPattern,
    string TokenPattern,
    string PromiseKind,
    string RealizationKind,
    IReadOnlyList<string> Tags);

public sealed record GeneratedJourney(
    string TemplateId,
    string DestinationZoneId,
    string DestinationName,
    string WantText,
    ClaimSeed Claim);

public sealed class QuestTemplateCatalog
{
    private readonly Dictionary<string, QuestTemplateDefinition> _templates = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<QuestTemplateDefinition> Templates => _templates.Values;

    public void Add(QuestTemplateDefinition template) => _templates[template.Id] = template;

    public static QuestTemplateCatalog LoadDefault()
    {
        foreach (var root in CandidateRoots())
        {
            var catalog = LoadFrom(Path.Combine(root, "content", "quests"));
            if (catalog.Templates.Count > 0)
            {
                return catalog;
            }
        }

        return LoadBuiltIn();
    }

    public static QuestTemplateCatalog LoadFrom(string directory)
    {
        var catalog = new QuestTemplateCatalog();
        if (!Directory.Exists(directory))
        {
            return catalog;
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.json").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            ReadDocument(document.RootElement, catalog);
        }

        return catalog;
    }

    public static QuestTemplateCatalog LoadBuiltIn()
    {
        var catalog = new QuestTemplateCatalog();
        var assembly = typeof(QuestTemplateCatalog).Assembly;
        foreach (var resourceName in assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Content.Quests.", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream is null)
            {
                continue;
            }

            using var document = JsonDocument.Parse(stream);
            ReadDocument(document.RootElement, catalog);
        }

        return catalog;
    }

    private static void ReadDocument(JsonElement root, QuestTemplateCatalog catalog)
    {
        if (!root.TryGetProperty("templates", out var templates) || templates.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in templates.EnumerateArray())
        {
            var template = new QuestTemplateDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "wantPattern", ""),
                ReadString(item, "claimPattern", ""),
                ReadString(item, "tokenPattern", "witness token"),
                ReadString(item, "promiseKind", "lead"),
                ReadString(item, "realizationKind", "item"),
                ReadStrings(item, "tags"));
            if (!string.IsNullOrWhiteSpace(template.Id)
                && !string.IsNullOrWhiteSpace(template.WantPattern)
                && !string.IsNullOrWhiteSpace(template.ClaimPattern))
            {
                catalog.Add(template);
            }
        }
    }

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static IReadOnlyList<string> ReadStrings(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "")
                .Where(item => !string.IsNullOrWhiteSpace(item))
                .ToArray()
            : Array.Empty<string>();

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

public static class GeneratedJourneyFactory
{
    public static GeneratedJourney? Create(
        int worldSeed,
        WorldPlaceProfile source,
        WorldPlaceGraph graph,
        RegionDefinition region,
        GeneratedResidentProfile resident,
        QuestTemplateCatalog catalog)
    {
        if (source.Settlement is null
            || !IsSettlementCenter(source.ZoneId, source.Settlement)
            || catalog.Templates.Count == 0)
        {
            return null;
        }

        var destination = graph.Landmarks
            .Where(landmark => landmark.RegionId.Equals(region.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(landmark => landmark.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        if (destination is null || destination.ZoneId.Equals(source.ZoneId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var templates = catalog.Templates.OrderBy(template => template.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var template = templates[WorldRoll.StableSeed(
            worldSeed,
            source.ZoneId,
            resident.ArchetypeId,
            "journey_template") % templates.Length];
        var direction = DirectionAndDistance(source.ZoneId, destination.ZoneId);
        var token = Expand(template.TokenPattern, resident, source, destination, direction);
        var want = Expand(template.WantPattern, resident, source, destination, direction)
            .Replace("{token}", token, StringComparison.OrdinalIgnoreCase);
        var claimText = Expand(template.ClaimPattern, resident, source, destination, direction)
            .Replace("{token}", token, StringComparison.OrdinalIgnoreCase);
        var tags = template.Tags
            .Concat(new[]
            {
                "generated_journey",
                template.Id,
                region.Id,
                destination.Id,
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var claim = new ClaimSeed(
            claimText,
            "journey",
            token,
            Salience: 4,
            Confidence: 90,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: template.PromiseKind,
            RealizationKind: template.RealizationKind,
            TriggerHint: "travel",
            ClaimedPlace: destination.ZoneId,
            Tags: tags);
        return new GeneratedJourney(template.Id, destination.ZoneId, destination.Name, want, claim);
    }

    private static bool IsSettlementCenter(string zoneId, WorldSettlement settlement)
    {
        var point = ParseZoneId(zoneId);
        return point.X == settlement.CenterX && point.Y == settlement.CenterY;
    }

    private static string Expand(
        string pattern,
        GeneratedResidentProfile resident,
        WorldPlaceProfile source,
        WorldLandmark destination,
        string direction) =>
        pattern
            .Replace("{giver}", resident.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{role}", resident.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{settlement}", source.Settlement?.Name ?? "the settlement", StringComparison.OrdinalIgnoreCase)
            .Replace("{district}", source.District?.Name ?? "the district", StringComparison.OrdinalIgnoreCase)
            .Replace("{landmark}", destination.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("{destinationZone}", destination.ZoneId, StringComparison.OrdinalIgnoreCase)
            .Replace("{direction}", direction, StringComparison.OrdinalIgnoreCase);

    private static string DirectionAndDistance(string fromZoneId, string toZoneId)
    {
        var from = ParseZoneId(fromZoneId);
        var to = ParseZoneId(toZoneId);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var distance = Math.Abs(dx) + Math.Abs(dy);
        var vertical = dy > 0 ? "south" : dy < 0 ? "north" : "";
        var horizontal = dx > 0 ? "east" : dx < 0 ? "west" : "";
        var direction = string.IsNullOrWhiteSpace(vertical)
            ? horizontal
            : string.IsNullOrWhiteSpace(horizontal)
                ? vertical
                : $"{vertical}-{horizontal}";
        return $"{distance} zone(s) {direction}";
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }
}
