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

public sealed record ObjectiveHandoffTemplateDefinition(
    string Id,
    string ObjectiveKind,
    string RealizationKind,
    string SubjectPattern,
    string SpokenPattern,
    string ObjectivePattern,
    bool OpeningHandoff,
    bool ReturnToGiver,
    IReadOnlyList<string> Tags);

public sealed class QuestTemplateCatalog
{
    private readonly Dictionary<string, QuestTemplateDefinition> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ObjectiveHandoffTemplateDefinition> _handoffs = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<QuestTemplateDefinition> Templates => _templates.Values;

    public IReadOnlyCollection<ObjectiveHandoffTemplateDefinition> Handoffs => _handoffs.Values;

    public void Add(QuestTemplateDefinition template) => _templates[template.Id] = template;

    public void Add(ObjectiveHandoffTemplateDefinition handoff) => _handoffs[handoff.Id] = handoff;

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

        if (!root.TryGetProperty("handoffs", out var handoffs) || handoffs.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var item in handoffs.EnumerateArray())
        {
            var handoff = new ObjectiveHandoffTemplateDefinition(
                ReadString(item, "id", ""),
                ReadString(item, "objectiveKind", "meet"),
                ReadString(item, "realizationKind", "person"),
                ReadString(item, "subjectPattern", "{contact}"),
                ReadString(item, "spokenPattern", ""),
                ReadString(item, "objectivePattern", ""),
                ReadBool(item, "openingHandoff", false),
                ReadBool(item, "returnToGiver", false),
                ReadStrings(item, "tags"));
            if (!string.IsNullOrWhiteSpace(handoff.Id)
                && !string.IsNullOrWhiteSpace(handoff.SpokenPattern)
                && !string.IsNullOrWhiteSpace(handoff.ObjectivePattern))
            {
                catalog.Add(handoff);
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

    private static bool ReadBool(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

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
                "generated_objective",
                "objective_kind:fetch",
                "objective_return_to_giver",
                $"objective_item:{token}",
                $"objective_giver_name:{resident.Name}",
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
            Tags: tags,
            SpokenText: claimText,
            ObjectiveText: $"Travel {direction} from {source.Settlement?.Name ?? "here"} to {destination.Name} and recover {token} for {resident.Name}.");
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
        return $"{distance} {(distance == 1 ? "length" : "lengths")} {direction}";
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }
}

public static class GeneratedObjectiveHandoffFactory
{
    /// <summary>
    /// The settlement a rescue handoff would send the player to from this zone: the same
    /// deterministic refuge selection the handoff itself uses. Exposed so a standing
    /// operation (the Provincial Reconciliation Sweep) can be aimed at the same place the
    /// captive's warning will name, without coupling the two systems.
    /// </summary>
    public static WorldSettlement? RescueDestination(
        int worldSeed,
        string sourceZoneId,
        string sourceRegionId,
        WorldPlaceGraph graph,
        RegionRegistry regions) =>
        SelectDestination(worldSeed, sourceZoneId, sourceRegionId, graph, regions, "scenario", "rescue", null);

    public static ClaimSeed? Create(
        int worldSeed,
        string sourceZoneId,
        string sourceRegionId,
        WorldPlaceGraph graph,
        RegionRegistry regions,
        QuestTemplateCatalog catalog,
        Entity speaker,
        string trigger,
        IReadOnlySet<string>? usedDestinationZones = null,
        string? preferredDestinationZone = null,
        string? preferredTemplateTag = null)
    {
        if (catalog.Handoffs.Count == 0)
        {
            return null;
        }

        // A preferred destination (e.g. the sweep's scheduled target) wins when it is a real,
        // still-unpromised settlement; otherwise ordinary selection applies.
        var destination = PreferredDestination(graph, preferredDestinationZone, usedDestinationZones)
            ?? SelectDestination(
                worldSeed,
                sourceZoneId,
                sourceRegionId,
                graph,
                regions,
                speaker.Id.Value,
                trigger,
                usedDestinationZones);
        var region = destination is null ? null : regions.Region(destination.RegionId);
        if (destination is null
            || region?.Population is not { } population
            || population.Archetypes.All(item => item.CenterWeight <= 0)
            || population.Names.GivenNames.Count == 0
            || population.Names.ByNames.Count == 0)
        {
            return null;
        }

        var world = WorldRoll.Create(worldSeed);
        var realm = world.RealmFor(region.RealmId);
        var contact = CreateContact(worldSeed, destination, region, realm);
        var opening = trigger.Equals("rescue", StringComparison.OrdinalIgnoreCase);
        var templates = catalog.Handoffs
            .Where(item => item.OpeningHandoff == opening)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!string.IsNullOrWhiteSpace(preferredTemplateTag))
        {
            var preferred = templates
                .Where(item => item.Tags.Contains(preferredTemplateTag, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (preferred.Length > 0)
            {
                templates = preferred;
            }
        }

        if (templates.Length == 0)
        {
            return null;
        }

        var template = templates[WorldRoll.StableSeed(
            worldSeed,
            sourceZoneId,
            speaker.Id.Value,
            trigger,
            "objective_handoff_template") % templates.Length];
        var direction = DirectionAndDistance(sourceZoneId, $"{destination.CenterX},{destination.CenterY}");
        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["speaker"] = speaker.Name,
            ["contact"] = contact.Name,
            ["role"] = contact.Title,
            ["settlement"] = destination.Name,
            ["direction"] = direction,
            ["zone"] = $"{destination.CenterX},{destination.CenterY}",
            ["region"] = region.Name,
            ["realm"] = realm.Name,
            ["ruler"] = realm.Ruler,
            ["item"] = $"{region.Name} witness parcel",
            ["threat"] = $"{destination.Name} oath-breaker",
            ["service"] = $"{region.Name} forbidden mending",
            ["rumor"] = $"the changed account of {destination.Name}",
        };
        var spoken = Expand(template.SpokenPattern, replacements);
        var objective = Expand(template.ObjectivePattern, replacements);
        var subject = Expand(template.SubjectPattern, replacements);
        var tags = template.Tags
            .Concat(new[]
            {
                "generated_objective",
                "objective_handoff",
                template.Id,
                region.Id,
                destination.Id,
                contact.ArchetypeId,
                trigger,
                $"objective_kind:{template.ObjectiveKind}",
                $"objective_giver_name:{speaker.Name}",
            })
            .Concat(template.ReturnToGiver ? new[] { "objective_return_to_giver" } : Array.Empty<string>())
            .Concat(template.ObjectiveKind is "fetch" or "delivery"
                ? new[] { $"objective_item:{(template.ObjectiveKind == "delivery" ? replacements["item"] : subject)}" }
                : Array.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ClaimSeed(
            spoken,
            "journey",
            subject,
            Salience: 5,
            Confidence: 95,
            PlayerVisible: true,
            BindAsPromise: true,
            PromiseKind: "lead",
            RealizationKind: template.RealizationKind,
            TriggerHint: "travel",
            ClaimedPlace: $"{destination.CenterX},{destination.CenterY}",
            Tags: tags,
            SpokenText: spoken,
            ObjectiveText: objective);
    }

    private static WorldSettlement? PreferredDestination(
        WorldPlaceGraph graph,
        string? preferredDestinationZone,
        IReadOnlySet<string>? usedDestinationZones)
    {
        if (string.IsNullOrWhiteSpace(preferredDestinationZone)
            || usedDestinationZones?.Contains(preferredDestinationZone) == true)
        {
            return null;
        }

        return graph.Settlements.FirstOrDefault(settlement =>
            $"{settlement.CenterX},{settlement.CenterY}".Equals(preferredDestinationZone, StringComparison.OrdinalIgnoreCase));
    }

    private static WorldSettlement? SelectDestination(
        int worldSeed,
        string sourceZoneId,
        string sourceRegionId,
        WorldPlaceGraph graph,
        RegionRegistry regions,
        string speakerId,
        string trigger,
        IReadOnlySet<string>? usedDestinationZones)
    {
        var source = ParseZoneId(sourceZoneId);
        var current = graph.Settlements
            .Where(settlement => settlement.RegionId.Equals(sourceRegionId, StringComparison.OrdinalIgnoreCase))
            .Where(settlement => Math.Max(
                Math.Abs(source.X - settlement.CenterX),
                Math.Abs(source.Y - settlement.CenterY)) <= settlement.Radius)
            .OrderByDescending(settlement => settlement.IsPrimary)
            .FirstOrDefault();
        var roadNeighbors = current is null
            ? Array.Empty<WorldSettlement>()
            : graph.Roads
                .Where(road => road.FromSettlementId.Equals(current.Id, StringComparison.OrdinalIgnoreCase)
                    || road.ToSettlementId.Equals(current.Id, StringComparison.OrdinalIgnoreCase))
                .Select(road => road.FromSettlementId.Equals(current.Id, StringComparison.OrdinalIgnoreCase)
                    ? road.ToSettlementId
                    : road.FromSettlementId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(id => graph.Settlements.First(settlement => settlement.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        var available = (roadNeighbors.Length > 0 && !trigger.Equals("rescue", StringComparison.OrdinalIgnoreCase)
                ? roadNeighbors
                : graph.Settlements)
            .Where(settlement => current is null || !settlement.Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase))
            .Where(settlement => usedDestinationZones is null
                || !usedDestinationZones.Contains($"{settlement.CenterX},{settlement.CenterY}"))
            .ToArray();
        if (trigger.Equals("rescue", StringComparison.OrdinalIgnoreCase))
        {
            var refugeCandidates = available
                .Where(settlement => regions.Region(settlement.RegionId)?.ImperialPresence <= 80)
                .ToArray();
            if (refugeCandidates.Length > 0)
            {
                available = refugeCandidates;
            }
        }

        var nearby = available
            .Where(settlement => Math.Abs(source.X - settlement.CenterX) + Math.Abs(source.Y - settlement.CenterY) <= 3)
            .ToArray();
        if (nearby.Length > 0)
        {
            available = nearby;
        }

        var candidates = available
            .OrderBy(settlement => Math.Abs(source.X - settlement.CenterX) + Math.Abs(source.Y - settlement.CenterY))
            .ThenByDescending(settlement => settlement.RegionId.Equals(sourceRegionId, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(settlement => settlement.IsPrimary)
            .ThenBy(settlement => settlement.Id, StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToArray();
        if (candidates.Length == 0 && usedDestinationZones is not null)
        {
            return SelectDestination(worldSeed, sourceZoneId, sourceRegionId, graph, regions, speakerId, trigger, null);
        }

        return candidates.Length == 0
            ? null
            : candidates[WorldRoll.StableSeed(
                worldSeed,
                sourceZoneId,
                speakerId,
                trigger,
                "objective_handoff_destination") % candidates.Length];
    }

    private static GeneratedResidentProfile CreateContact(
        int worldSeed,
        WorldSettlement destination,
        RegionDefinition region,
        RealmProfile realm)
    {
        var zoneId = $"{destination.CenterX},{destination.CenterY}";
        var batch = RegionPopulationGenerator.Generate(
            worldSeed,
            zoneId,
            region,
            realm,
            (destination.CenterX, destination.CenterY),
            RegionPopulationGenerator.CenterHabitat);
        var grammar = region.Population!;
        var archetypes = grammar.Archetypes
            .Where(item => item.CenterWeight > 0)
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var archetype = archetypes[WorldRoll.StableSeed(
            worldSeed,
            zoneId,
            region.Id,
            "objective_contact_archetype") % archetypes.Length];
        var used = batch.Residents.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var combinations = grammar.Names.GivenNames
            .SelectMany(given => grammar.Names.ByNames.Select(byName => $"{given} {byName}, {archetype.Title}"))
            .ToArray();
        var start = WorldRoll.StableSeed(worldSeed, zoneId, archetype.Id, "objective_contact_name") % combinations.Length;
        var name = Enumerable.Range(0, combinations.Length)
            .Select(offset => combinations[(start + offset) % combinations.Length])
            .FirstOrDefault(candidate => !used.Contains(candidate))
            ?? $"{grammar.Names.GivenNames[0]} {grammar.Names.ByNames[0]}, {archetype.Title} of {destination.Name}";
        return new GeneratedResidentProfile(
            archetype.Id,
            name,
            archetype.Title,
            archetype.Glyph,
            string.IsNullOrWhiteSpace(archetype.FactionId) || archetype.FactionId.Equals("realm", StringComparison.OrdinalIgnoreCase)
                ? region.RealmId
                : archetype.FactionId,
            archetype.Descriptions.FirstOrDefault() ?? $"A {archetype.Title} from {destination.Name}.",
            archetype.Wants.FirstOrDefault() ?? $"Learn why a stranger was sent to {destination.Name}.",
            archetype.MinHitPoints,
            archetype.MinAttack,
            archetype.KnowledgeTier,
            archetype.Tags,
            archetype.Roles,
            Array.Empty<GeneratedResidentWare>(),
            Array.Empty<RegionServiceDefinition>());
    }

    private static string Expand(string pattern, IReadOnlyDictionary<string, string> replacements)
    {
        var result = pattern;
        foreach (var (key, value) in replacements)
        {
            result = result.Replace($"{{{key}}}", value, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string DirectionAndDistance(string fromZoneId, string toZoneId)
    {
        var from = ParseZoneId(fromZoneId);
        var to = ParseZoneId(toZoneId);
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var vertical = dy > 0 ? "south" : dy < 0 ? "north" : "";
        var horizontal = dx > 0 ? "east" : dx < 0 ? "west" : "";
        var direction = string.IsNullOrWhiteSpace(vertical)
            ? horizontal
            : string.IsNullOrWhiteSpace(horizontal)
                ? vertical
                : $"{vertical}-{horizontal}";
        var distance = Math.Abs(dx) + Math.Abs(dy);
        return $"{distance} {(distance == 1 ? "length" : "lengths")} {direction}".Trim();
    }

    private static (int X, int Y) ParseZoneId(string zoneId)
    {
        var parts = zoneId.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)
            ? (x, y)
            : (0, 0);
    }
}
