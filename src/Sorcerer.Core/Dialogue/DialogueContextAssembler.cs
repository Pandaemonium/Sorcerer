using System.Text.Json;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
using Sorcerer.Core.Lore;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Dialogue;

public sealed record DialogueRouteSelection(
    IReadOnlyList<DialogueContextCardPayload> SelectedCards,
    IReadOnlyList<string> SelectedCardIds,
    IReadOnlyList<string> FallbackCardIds,
    IReadOnlyList<string> UnknownSelectedCardIds,
    IReadOnlyList<string> DeniedSelectedCardIds,
    bool UsedFallback);

public sealed record DialogueContextAssembly(
    DialogueRouteRequest RouteRequest,
    IReadOnlyList<DialogueContextCardPayload> AvailableCards,
    IReadOnlyList<DialogueContextCardPayload> DeniedCards)
{
    // Total byte budget for the selected context cards (the dominant request slice). With the
    // fixed slices (~2 KB) this keeps the whole dialogue request near the ~6 KB target
    // (docs/OPTIMIZATION_PLAN.md WS3.3).
    private const int DialogueContextCardsByteBudget = 4096;
    private const int DialogueCardLineMaxLength = 320;

    public DialogueRouteSelection Select(DialogueRouteResult result)
    {
        var fallbackIds = DeterministicDialogueContextCardIds(AvailableCards, RouteRequest.PlayerText);
        var requestedIds = NormalizeCardIds(result.SelectedCardIds).ToArray();
        var available = AvailableCards.Select(card => card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var denied = DeniedCards.Select(card => card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = requestedIds
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToArray();
        var usedFallback = result.TechnicalFailure || selectedIds.Length == 0;
        if (usedFallback)
        {
            selectedIds = fallbackIds.ToArray();
        }

        var selectedSet = selectedIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedCards = AvailableCards
            .Where(card => selectedSet.Contains(card.Id))
            .ToArray();
        var deniedSelectedIds = requestedIds
            .Where(denied.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unknownSelectedIds = requestedIds
            .Where(id => !available.Contains(id) && !denied.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DialogueRouteSelection(
            selectedCards,
            selectedIds,
            fallbackIds,
            unknownSelectedIds,
            deniedSelectedIds,
            usedFallback);
    }

    public DialogueRequest BuildDialogueRequest(DialogueRouteSelection selection)
    {
        // Enforce a total byte budget on the context cards (docs/OPTIMIZATION_PLAN.md WS3.3): keep
        // cards in router order, cap each line's length, and stop adding lines once the budget is
        // spent so a crowded selection can never balloon the prompt. The per-topic line lookups
        // below read from the trimmed cards so the whole request stays within budget.
        var budgetedCards = FitContextCardsToBudget(selection.SelectedCards);
        var cardLines = budgetedCards.ToDictionary(
            card => card.Id,
            card => card.Lines,
            StringComparer.OrdinalIgnoreCase);
        var zoneLines = cardLines.TryGetValue("zone.current", out var zoneCurrent)
            ? zoneCurrent
            : Array.Empty<string>();
        return new DialogueRequest(
            RouteRequest.Turn,
            RouteRequest.PlayerText,
            RouteRequest.Speaker,
            RouteRequest.Listener,
            new DialogueSceneCard(
                RouteRequest.Scene.RegionId,
                RouteRequest.Scene.CurrentZoneId,
                zoneLines.Where(line => line.StartsWith("Entity:", StringComparison.OrdinalIgnoreCase)).ToArray(),
                zoneLines.Where(line => line.StartsWith("Item:", StringComparison.OrdinalIgnoreCase)).ToArray(),
                zoneLines.Where(line => line.StartsWith("Event:", StringComparison.OrdinalIgnoreCase)).ToArray(),
                RouteRequest.Scene.RegionVoice),
            cardLines.TryGetValue("npc.relationship_memory", out var memoryLines)
                ? memoryLines
                : Array.Empty<string>(),
            cardLines.TryGetValue("claims.recent", out var claimLines)
                ? claimLines
                : Array.Empty<string>(),
            Array.Empty<string>(),
            cardLines.TryGetValue("rumors.full", out var rumorLines)
                ? rumorLines
                : Array.Empty<string>(),
            budgetedCards,
            selection.SelectedCardIds);
    }

    /// <summary>
    /// Trims the selected cards to <see cref="DialogueContextCardsByteBudget"/>: caps each line's
    /// length, keeps cards in router order, and stops adding lines once the budget is spent (each
    /// affected card keeps at least one line and is marked Truncated). A card whose budget ran out
    /// entirely keeps its header with no lines so the model still sees the topic was available.
    /// </summary>
    private static IReadOnlyList<DialogueContextCardPayload> FitContextCardsToBudget(
        IReadOnlyList<DialogueContextCardPayload> cards)
    {
        var trimmed = new List<DialogueContextCardPayload>(cards.Count);
        var usedBytes = 0;
        foreach (var card in cards)
        {
            var keptLines = new List<string>();
            var truncated = card.Truncated;
            foreach (var line in card.Lines)
            {
                var capped = line.Length > DialogueCardLineMaxLength
                    ? line[..DialogueCardLineMaxLength].TrimEnd() + "…"
                    : line;
                var lineBytes = System.Text.Encoding.UTF8.GetByteCount(capped) + 4;
                if (usedBytes + lineBytes > DialogueContextCardsByteBudget && keptLines.Count > 0)
                {
                    truncated = true;
                    break;
                }

                keptLines.Add(capped);
                usedBytes += lineBytes;
            }

            trimmed.Add(card with
            {
                Lines = keptLines,
                Truncated = truncated || keptLines.Count < card.Lines.Count,
            });
        }

        return trimmed;
    }

    public DialogueRouteMetrics CreateMetrics(
        DialogueRouteSelection selection,
        long routerElapsedMs,
        int? generatorRequestBytes = null) =>
        new(
            AvailableCards.Count,
            selection.SelectedCardIds.Count,
            selection.FallbackCardIds.Count,
            DeniedCards.Count,
            DialoguePayloadSizer.JsonUtf8Bytes(RouteRequest),
            DialoguePayloadSizer.JsonUtf8Bytes(AvailableCards),
            DialoguePayloadSizer.JsonUtf8Bytes(selection.SelectedCards),
            generatorRequestBytes,
            Math.Max(0, routerElapsedMs),
            DeniedCards.Select(card => card.Id).ToArray(),
            selection.UnknownSelectedCardIds,
            selection.DeniedSelectedCardIds);

    private static IEnumerable<string> NormalizeCardIds(IReadOnlyList<string>? selectedIds) =>
        (selectedIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim());

    private static IReadOnlyList<string> DeterministicDialogueContextCardIds(
        IReadOnlyList<DialogueContextCardPayload> cards,
        string playerText)
    {
        var lower = playerText.ToLowerInvariant();
        var preferred = new List<string> { "zone.current", "npc.relationship_memory" };
        if (lower.Contains("who", StringComparison.Ordinal)
            || lower.Contains("else", StringComparison.Ordinal)
            || lower.Contains("people", StringComparison.Ordinal)
            || lower.Contains("npc", StringComparison.Ordinal)
            || lower.Contains("guard", StringComparison.Ordinal)
            || lower.Contains("soldier", StringComparison.Ordinal)
            || lower.Contains("lio", StringComparison.Ordinal))
        {
            preferred.Add("zone.npcs");
        }

        if (lower.Contains("rumor", StringComparison.Ordinal)
            || lower.Contains("gossip", StringComparison.Ordinal)
            || lower.Contains("know", StringComparison.Ordinal))
        {
            preferred.Add("rumors.full");
            preferred.Add("npc.knowledge.region");
        }

        if (lower.Contains("object", StringComparison.Ordinal)
            || lower.Contains("door", StringComparison.Ordinal)
            || lower.Contains("room", StringComparison.Ordinal)
            || lower.Contains("here", StringComparison.Ordinal)
            || lower.Contains("look", StringComparison.Ordinal))
        {
            preferred.Add("scene.object_detail");
        }

        if (lower.Contains("law", StringComparison.Ordinal)
            || lower.Contains("empire", StringComparison.Ordinal)
            || lower.Contains("vigovia", StringComparison.Ordinal)
            || lower.Contains("faction", StringComparison.Ordinal))
        {
            preferred.Add("faction.law");
        }

        if (lower.Contains("service", StringComparison.Ordinal)
            || lower.Contains("trade", StringComparison.Ordinal)
            || lower.Contains("sell", StringComparison.Ordinal)
            || lower.Contains("buy", StringComparison.Ordinal)
            || lower.Contains("wares", StringComparison.Ordinal))
        {
            preferred.Add("services.available");
        }

        if (lower.Contains("road", StringComparison.Ordinal)
            || lower.Contains("route", StringComparison.Ordinal)
            || lower.Contains("travel", StringComparison.Ordinal)
            || lower.Contains("north", StringComparison.Ordinal)
            || lower.Contains("south", StringComparison.Ordinal)
            || lower.Contains("east", StringComparison.Ordinal)
            || lower.Contains("west", StringComparison.Ordinal)
            || lower.Contains("where", StringComparison.Ordinal)
            || lower.Contains("go", StringComparison.Ordinal)
            || lower.Contains("leave", StringComparison.Ordinal)
            || lower.Contains("escape", StringComparison.Ordinal))
        {
            preferred.Add("region.travel");
        }

        if (lower.Contains("promise", StringComparison.Ordinal)
            || lower.Contains("owe", StringComparison.Ordinal)
            || lower.Contains("debt", StringComparison.Ordinal)
            || lower.Contains("prophecy", StringComparison.Ordinal)
            || lower.Contains("claim", StringComparison.Ordinal)
            || lower.Contains("hook", StringComparison.Ordinal))
        {
            preferred.Add("promise.hooks");
        }

        if (lower.Contains("magic", StringComparison.Ordinal)
            || lower.Contains("spell", StringComparison.Ordinal)
            || lower.Contains("deed", StringComparison.Ordinal)
            || lower.Contains("saw", StringComparison.Ordinal)
            || lower.Contains("happened", StringComparison.Ordinal))
        {
            preferred.Add("recent.magic_deeds");
        }

        preferred.Add("claims.recent");
        preferred.AddRange(new[]
        {
            "zone.npcs",
            "rumors.full",
            "scene.object_detail",
            "region.travel",
            "promise.hooks",
            "recent.magic_deeds",
            "faction.law",
            "npc.knowledge.region",
            "services.available",
            "claims.recent",
        });
        var available = cards.Select(card => card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return preferred
            .Where(available.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

}

public sealed class DialogueContextAssembler
{
    private const int DialogueVisibleEntityLimit = 10;
    private const int DialogueNearbyItemLimit = 4;
    private const int DialogueRecentEventLimit = 4;
    private const int DialogueRecentMemoryLimit = 5;
    private const int DialogueRecentClaimLimit = 4;
    private const int DialogueFullRumorLimit = 8;
    private const int DialogueCardLineLimit = 12;

    private static readonly Lazy<LoreCatalog> DefaultLoreCatalog = new(LoreCatalog.LoadDefault);
    private static readonly IReadOnlyList<DialogueContextCardSpec> DefaultCardSpecs = new[]
    {
        new DialogueContextCardSpec(
            "zone.current",
            "zone.current",
            "zone",
            "Current Zone",
            "Visible zone state, nearby entities, items, and recent events."),
        new DialogueContextCardSpec(
            "zone.npcs",
            "zone.npcs",
            "zone",
            "Zone NPCs",
            "Other NPCs, factions, wants, and visible social context in the current zone."),
        new DialogueContextCardSpec(
            "rumors.full",
            "rumors",
            "rumors",
            "Rumors",
            "Rumors this NPC is allowed to have heard in this region."),
        new DialogueContextCardSpec(
            "scene.object_detail",
            "scene.object",
            "objects",
            "Objects In The Zone",
            "Visible object, fixture, door, and item details in the current zone."),
        new DialogueContextCardSpec(
            "region.travel",
            "region.travel",
            "travel",
            "Travel And Routes",
            "Known travel directions, region/zone path context, and route-like promises."),
        new DialogueContextCardSpec(
            "promise.hooks",
            "promise.hooks",
            "promise",
            "Promise Hooks",
            "Visible or speaker-linked promises and claims that could shape future delivery."),
        new DialogueContextCardSpec(
            "recent.magic_deeds",
            "recent.magic_deeds",
            "events",
            "Recent Magic And Deeds",
            "Recent notable deeds, magic, suspicion, and public consequences."),
        new DialogueContextCardSpec(
            "faction.law",
            "faction.law",
            "faction",
            "Faction Law And Procedure",
            "Faction, law, imperial procedure, and local rule context this NPC may know."),
        new DialogueContextCardSpec(
            "npc.relationship_memory",
            "npc.relationship",
            "memory",
            "Relationship Memory",
            "Recent memories, bond, and want context for this speaker."),
        new DialogueContextCardSpec(
            "npc.knowledge.region",
            "npc.knowledge.region",
            "knowledge",
            "Regional Knowledge",
            "Region, origin, and local canon known to this NPC."),
        new DialogueContextCardSpec(
            "services.available",
            "services",
            "services",
            "Services And Wares",
            "Services, wares, and trade affordances this NPC can discuss."),
        new DialogueContextCardSpec(
            "claims.recent",
            "claims",
            "claims",
            "Recent Claims",
            "Recent reported claims available as conversation context."),
    };

    private readonly GameEngine _engine;
    private readonly LoreCatalog _loreCatalog;

    private DialogueContextAssembler(GameEngine engine, LoreCatalog loreCatalog)
    {
        _engine = engine;
        _loreCatalog = loreCatalog;
    }

    public static DialogueContextAssembly Build(
        GameEngine engine,
        PreparedDialogueTurn turn,
        LoreCatalog? loreCatalog = null) =>
        new DialogueContextAssembler(engine, loreCatalog ?? DefaultLoreCatalog.Value).Build(turn);

    private DialogueContextAssembly Build(PreparedDialogueTurn turn)
    {
        var speaker = _engine.EntityById(turn.SpeakerId);
        var listener = _engine.State.ControlledEntity;
        var cards = BuildDialogueContextCards(turn, speaker);
        var request = new DialogueRouteRequest(
            turn.TurnBefore,
            turn.PlayerText,
            ParticipantCard(speaker, turn.BondSummary, turn.SpeakerWant),
            ParticipantCard(listener, null),
            new DialogueSceneCard(
                _engine.State.RegionId,
                _engine.State.CurrentZoneId,
                VisibleEntityLines().Take(8).ToArray(),
                NearbyItemLines().Take(3).ToArray(),
                _engine.State.Messages.TakeLast(2).ToArray(),
                _engine.CurrentRegionVoice,
                CompactSceneryLines().Take(8).ToArray()),
            cards.Available
                .Select(card => new DialogueRouteCandidate(
                    card.Id,
                    card.Kind,
                    card.Title,
                    card.Summary,
                    card.Topics))
                .ToArray());
        return new DialogueContextAssembly(request, cards.Available, cards.Denied);
    }

    private DialogueContextCardBuckets BuildDialogueContextCards(
        PreparedDialogueTurn turn,
        Entity? speaker)
    {
        var cards = new DialogueContextCardBuckets();
        foreach (var spec in DefaultCardSpecs)
        {
            AddCardIfAllowed(cards, speaker, spec, LinesFor(spec, turn, speaker));
        }

        return cards;
    }

    private IEnumerable<string> LinesFor(
        DialogueContextCardSpec spec,
        PreparedDialogueTurn turn,
        Entity? speaker) =>
        spec.Id switch
        {
            "zone.current" => ZoneCurrentLines(),
            "zone.npcs" => ZoneNpcLines(),
            "rumors.full" => RumorSystem.HeardRumorLines(
                _engine.State,
                turn.SpeakerId,
                _engine.State.RegionId,
                DialogueFullRumorLimit),
            "scene.object_detail" => SceneObjectDetailLines(),
            "region.travel" => RegionTravelLines(),
            "promise.hooks" => PromiseHookLines(speaker),
            "recent.magic_deeds" => RecentMagicAndDeedLines(),
            "faction.law" => FactionLawLines(speaker),
            "npc.relationship_memory" => RelationshipMemoryLines(turn),
            "npc.knowledge.region" => RegionKnowledgeLines(turn, speaker),
            "services.available" => ServiceAndWaresLines(speaker),
            "claims.recent" => RecentClaimLines(),
            _ => Array.Empty<string>(),
        };

    private void AddCardIfAllowed(
        DialogueContextCardBuckets cards,
        Entity? speaker,
        DialogueContextCardSpec spec,
        IEnumerable<string> lines)
    {
        var allLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var materialized = allLines
            .Take(DialogueCardLineLimit)
            .ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        var card = new DialogueContextCardPayload(
            spec.Id,
            spec.Kind,
            spec.Title,
            spec.Summary,
            materialized,
            Topics: new[] { spec.Topic },
            Truncated: allLines.Length > DialogueCardLineLimit);
        if (SpeakerKnowsTopic(speaker, spec.Topic))
        {
            cards.Available.Add(card);
        }
        else
        {
            cards.Denied.Add(card);
        }
    }

    private IEnumerable<string> ZoneCurrentLines()
    {
        var place = _engine.CurrentPlace;
        yield return $"Zone: {_engine.State.CurrentZoneId} in region {_engine.State.RegionId}.";
        yield return $"Place: {place.DisplayName}; kind {place.Kind}. {place.Summary}";
        if (place.Road is not null)
        {
            yield return $"Road: {place.Road.Name}.";
        }

        foreach (var entity in VisibleEntityLines())
        {
            yield return $"Entity: {entity}";
        }

        foreach (var item in NearbyItemLines())
        {
            yield return $"Item: {item}";
        }

        foreach (var scenery in CompactSceneryLines())
        {
            yield return $"Scenery: {scenery}";
        }

        foreach (var message in _engine.State.Messages.TakeLast(DialogueRecentEventLimit))
        {
            yield return $"Event: {message}";
        }
    }

    private IEnumerable<string> ZoneNpcLines()
    {
        var controlled = _engine.State.ControlledEntity;
        var origin = controlled.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        var perception = _engine.Perception();
        foreach (var item in _engine.State.Entities.Values
            .Where(entity => entity.Id != controlled.Id
                && perception.VisibleEntityIds.Contains(entity.Id)
                && IsDialogueNpc(entity)
                && entity.TryGet<PositionComponent>(out _))
            .Select(entity =>
            {
                var position = entity.Get<PositionComponent>().Position;
                return new
                {
                    Entity = entity,
                    Position = position,
                    Distance = GameEngine.Distance(origin, position),
                };
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(DialogueVisibleEntityLimit))
        {
            var line = new List<string>
            {
                $"{item.Entity.Name} ({item.Entity.Id.Value}) at {item.Position.X},{item.Position.Y}, range {item.Distance}",
                $"tags {string.Join(",", TagsFor(item.Entity))}",
            };
            if (item.Entity.TryGet<ActorComponent>(out var actor))
            {
                line.Add($"faction {actor.Faction}");
            }

            if (item.Entity.TryGet<FactionComponent>(out var faction))
            {
                line.Add($"roles {string.Join(",", faction.Roles)}");
            }

            if (item.Entity.TryGet<WantComponent>(out var want) && WantSummary(want) is { } wantSummary)
            {
                line.Add($"want {wantSummary}");
            }

            yield return string.Join("; ", line);
        }
    }

    private IEnumerable<string> SceneObjectDetailLines()
    {
        var controlled = _engine.State.ControlledEntity;
        var origin = controlled.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        var perception = _engine.Perception();
        foreach (var entity in _engine.State.Entities.Values
            .Where(entity => entity.Id != controlled.Id
                && perception.VisibleEntityIds.Contains(entity.Id)
                && entity.TryGet<PositionComponent>(out _))
            .Select(entity =>
            {
                var position = entity.Get<PositionComponent>().Position;
                var distance = GameEngine.Distance(origin, position);
                return new { Entity = entity, Position = position, Distance = distance };
            })
            .Where(item => item.Distance <= 6)
            .OrderBy(item => ContextEntityRouting.IsActor(item.Entity)
                ? 0
                : ContextEntityRouting.IsHookBearing(item.Entity)
                    ? 1
                    : 2)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(DialogueVisibleEntityLimit))
        {
            var details = new List<string>
            {
                $"{entity.Entity.Name} ({entity.Entity.Id.Value}) at {entity.Position.X},{entity.Position.Y}, range {entity.Distance}",
                $"tags {string.Join(",", TagsFor(entity.Entity))}",
            };
            if (entity.Entity.TryGet<DescriptionComponent>(out var description))
            {
                details.Add(description.Text);
            }

            if (entity.Entity.TryGet<ReadableComponent>(out var readable))
            {
                details.Add($"readable {readable.Title}: {readable.TextKey}");
            }

            if (entity.Entity.TryGet<DoorComponent>(out var door))
            {
                details.Add(door.IsOpen ? "door open" : "door closed/locked");
            }

            if (entity.Entity.TryGet<FixtureComponent>(out var fixture))
            {
                details.Add($"fixture {fixture.FixtureType}");
            }

            yield return string.Join("; ", details.Where(text => !string.IsNullOrWhiteSpace(text)));
        }
    }

    private IEnumerable<string> RegionTravelLines()
    {
        // NPC-facing geography is diegetic: places are named, and travel is measured in bearings
        // and "lengths", never in map coordinates. Objective distances reach the speaker through
        // the promise/lead text; this card only tells them where they stand and which ways lead on.
        yield return $"You are in {_engine.CurrentPlace.DisplayName}.";
        foreach (var direction in new[] { "north", "east", "south", "west" })
        {
            yield return $"Travel {direction}: a road leads onward from here.";
        }

        foreach (var promise in _engine.State.PromiseLedger.Promises
            .Where(IsTravelLikePromise)
            .OrderByDescending(promise => promise.Salience)
            .ThenBy(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .Take(5))
        {
            yield return PromiseLine(promise);
        }
    }

    private IEnumerable<string> PromiseHookLines(Entity? speaker)
    {
        var speakerId = speaker?.Id.Value;
        foreach (var promise in _engine.State.PromiseLedger.Promises
            .Where(promise => PromiseVisibleToSpeaker(promise, speakerId))
            .OrderByDescending(promise => promise.Salience)
            .ThenByDescending(promise => promise.Id, StringComparer.OrdinalIgnoreCase)
            .Take(6))
        {
            yield return PromiseLine(promise);
        }

        foreach (var claim in _engine.State.Claims.Records
            .Where(claim => !string.IsNullOrWhiteSpace(claim.BoundPromiseId)
                || claim.Tags.Contains("promise", StringComparer.OrdinalIgnoreCase)
                || claim.SpeakerId.Equals(speakerId ?? "", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4))
        {
            yield return $"Claim hook {claim.Id} [{claim.Category}/{claim.Status}, salience {claim.Salience}]: {claim.Text}";
        }
    }

    private IEnumerable<string> RecentMagicAndDeedLines()
    {
        foreach (var deed in _engine.State.Deeds.Records.TakeLast(5))
        {
            yield return $"Deed {deed.Id} turn {deed.Turn}: {deed.Kind} magnitude {deed.Magnitude} at {deed.PlaceKey}, {deed.Visibility}, tags {string.Join(",", deed.Tags)}.";
        }

        foreach (var suspicion in _engine.State.Suspicions.Records.TakeLast(4))
        {
            yield return $"Suspicion {suspicion.Id} turn {suspicion.Turn}: {suspicion.Kind} seen by {suspicion.WitnessSoulId}, status {suspicion.Status}.";
        }

        foreach (var message in _engine.State.Messages
            .Where(message =>
                message.Contains("magic", StringComparison.OrdinalIgnoreCase)
                || message.Contains("spell", StringComparison.OrdinalIgnoreCase)
                || message.Contains("suspicion", StringComparison.OrdinalIgnoreCase)
                || message.Contains("contain", StringComparison.OrdinalIgnoreCase)
                || message.Contains("promise", StringComparison.OrdinalIgnoreCase))
            .TakeLast(4))
        {
            yield return $"Event: {message}";
        }
    }

    private IEnumerable<string> RecentClaimLines() =>
        _engine.State.Claims.Records
            .TakeLast(DialogueRecentClaimLimit)
            .Select(claim => $"{claim.Subject} [{claim.Category}/{claim.Status}]: {claim.Text}");

    private IEnumerable<string> FactionLawLines(Entity? speaker)
    {
        var factionId = speaker?.TryGet<ActorComponent>(out var actor) == true
            ? actor.Faction
            : speaker?.TryGet<FactionComponent>(out var membership) == true
                ? membership.FactionId
                : null;
        if (!string.IsNullOrWhiteSpace(factionId))
        {
            var faction = _engine.State.Factions.Factions.FirstOrDefault(record =>
                record.Id.Equals(factionId, StringComparison.OrdinalIgnoreCase));
            if (faction is not null)
            {
                yield return $"{faction.Name} [{faction.Id}, role {faction.Role}], hostile roles {string.Join(",", faction.HostileRoles)}.";
                foreach (var standing in faction.Standing.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(4))
                {
                    yield return $"Standing {standing.Key}: {standing.Value}.";
                }

                foreach (var resource in faction.Resources.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(4))
                {
                    yield return $"Resource {resource.Key}: {resource.Value}.";
                }
            }
        }

        foreach (var canon in _engine.State.Canon.Records
            .Where(record =>
                record.Kind.Contains("law", StringComparison.OrdinalIgnoreCase)
                || record.Kind.Contains("custom", StringComparison.OrdinalIgnoreCase)
                || record.Tags.Contains("law", StringComparer.OrdinalIgnoreCase)
                || record.Tags.Contains("imperial", StringComparer.OrdinalIgnoreCase))
            .TakeLast(5))
        {
            yield return $"{canon.Kind}: {canon.Summary} - {canon.Text}";
        }
    }

    private IEnumerable<string> RelationshipMemoryLines(PreparedDialogueTurn turn)
    {
        foreach (var line in RecentDialogueMemoryLines(turn.SpeakerId))
        {
            yield return line;
        }

        if (!string.IsNullOrWhiteSpace(turn.BondSummary))
        {
            yield return $"Bond: {turn.BondSummary}";
        }

        if (!string.IsNullOrWhiteSpace(turn.SpeakerWant))
        {
            yield return $"Want: {turn.SpeakerWant}";
        }
    }

    private IEnumerable<string> RegionKnowledgeLines(PreparedDialogueTurn turn, Entity? speaker)
    {
        if (speaker?.TryGet<ProfileComponent>(out var profile) == true)
        {
            foreach (var line in new[]
            {
                $"Origin: {profile.Origin}",
                $"Magical signature: {profile.MagicalSignature}",
                $"Backstory: {profile.Backstory}",
            })
            {
                if (!string.IsNullOrWhiteSpace(line.Split(':').LastOrDefault()))
                {
                    yield return line;
                }
            }
        }

        foreach (var routedLore in RoutedLoreLines(turn, speaker))
        {
            yield return routedLore;
        }

        foreach (var canon in _engine.State.Canon.Records
            .Where(record =>
                record.AttachedTo.Equals(_engine.State.RegionId, StringComparison.OrdinalIgnoreCase)
                || record.Tags.Contains(_engine.State.RegionId, StringComparer.OrdinalIgnoreCase)
                || (speaker is not null && record.AttachedTo.Equals(speaker.Id.Value, StringComparison.OrdinalIgnoreCase)))
            .TakeLast(5))
        {
            yield return $"{canon.Kind}: {canon.Summary} - {canon.Text}";
        }
    }

    private IEnumerable<string> RoutedLoreLines(PreparedDialogueTurn turn, Entity? speaker)
    {
        if (!TryTopicTierFor(speaker, "npc.knowledge.region", out var access))
        {
            yield break;
        }

        var subjects = new List<string>
        {
            _engine.State.RegionId,
            _engine.State.CurrentZoneId,
        };
        var triggers = new List<string>
        {
                "background",
                "magic_context",
                _engine.State.RegionId,
                _engine.State.CurrentZoneId,
        };
        subjects.AddRange(TextTokens(turn.PlayerText));
        triggers.AddRange(TextTokens(turn.PlayerText));
        if (speaker is not null)
        {
            subjects.AddRange(TagsFor(speaker));
            triggers.AddRange(TagsFor(speaker));
            if (speaker.TryGet<ActorComponent>(out var actor))
            {
                subjects.Add(actor.Faction);
                triggers.Add(actor.Faction);
            }

            if (speaker.TryGet<FactionComponent>(out var faction))
            {
                subjects.Add(faction.FactionId);
                subjects.AddRange(faction.Roles);
                triggers.Add(faction.FactionId);
                triggers.AddRange(faction.Roles);
            }

            if (speaker.TryGet<ProfileComponent>(out var profile))
            {
                subjects.Add(profile.Origin);
                subjects.AddRange(TextTokens(profile.MagicalSignature));
                triggers.Add(profile.Origin);
                triggers.AddRange(TextTokens(profile.MagicalSignature));
            }
        }

        foreach (var lore in LoreRouter.Select(
            _loreCatalog,
            new LoreQuery(
                subjects,
                triggers,
                AccessLevel: access,
                Limit: 3,
                SubjectAccessLevels: LoreSubjectAccessFor(speaker, _engine.State.RegionId, _engine.State.CurrentZoneId))))
        {
            foreach (var line in lore.Body.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return $"Lore {lore.Title} [level {lore.Level}]: {line}";
            }
        }
    }

    private IEnumerable<string> ServiceAndWaresLines(Entity? speaker)
    {
        if (speaker is null)
        {
            yield break;
        }

        if (speaker.TryGet<ServiceComponent>(out var serviceComponent))
        {
            foreach (var service in serviceComponent.Offers
                .Where(service => service.Revealed)
                .OrderBy(service => service.Id, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"Service {service.Name} [{service.EffectKind}], cost {service.GoldCost} gold"
                    + (string.IsNullOrWhiteSpace(service.ItemCost) ? "" : $" plus {service.ItemCost}")
                    + $": {service.Description}";
            }
        }

        if (speaker.TryGet<MerchantComponent>(out var merchant))
        {
            foreach (var ware in merchant.Wares.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                yield return $"Ware {ware.Key} x{ware.Value}.";
            }
        }
    }

    private bool SpeakerKnowsTopic(Entity? speaker, string topic) =>
        TryTopicTierFor(speaker, topic, out _);

    private static int TopicTierFor(Entity? speaker, string topic)
    {
        return TryTopicTierFor(speaker, topic, out var tier) ? tier : -1;
    }

    private static bool TryTopicTierFor(Entity? speaker, string topic, out int tier)
    {
        if (speaker is null)
        {
            tier = -1;
            return false;
        }

        var topics = KnowledgeTopicsFor(speaker);
        var found = false;
        tier = -1;
        if (topics.TryGetValue("all", out var all))
        {
            found = true;
            tier = Math.Max(tier, all);
        }

        if (topics.TryGetValue(topic, out var exact))
        {
            found = true;
            tier = Math.Max(tier, exact);
        }

        if (topics.TryGetValue(topic.Split('.')[0], out var group))
        {
            found = true;
            tier = Math.Max(tier, group);
        }

        return found;
    }

    private static IReadOnlyDictionary<string, int> LoreSubjectAccessFor(
        Entity? speaker,
        string regionId,
        string zoneId)
    {
        var access = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (speaker is null)
        {
            return access;
        }

        foreach (var pair in KnowledgeTopicsFor(speaker))
        {
            AddKnowledgeSubjectAliases(access, pair.Key, pair.Value, regionId, zoneId);
        }

        return access;
    }

    private static void AddKnowledgeSubjectAliases(
        Dictionary<string, int> access,
        string topic,
        int tier,
        string regionId,
        string zoneId)
    {
        SetSubjectAccessAtLeast(access, topic, tier);
        SetSubjectAccessAtLeast(access, topic.Replace('.', '_'), tier);

        if (topic.Equals("current_zone", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("zone.current", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, regionId, tier);
            SetSubjectAccessAtLeast(access, zoneId, tier);
            SetSubjectAccessAtLeast(access, "containment", tier);
            SetSubjectAccessAtLeast(access, "imperial_encounter", tier);
        }

        if (topic.Equals("hollowmere", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("people.hollowmere", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "hollowmere", tier);
            SetSubjectAccessAtLeast(access, "hollowmere_margin", tier);
            SetSubjectAccessAtLeast(access, "reeds", tier);
        }

        if (topic.Equals("folk_magic.water", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("magic.water.hollowmere", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("water_magic", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "water", tier);
            SetSubjectAccessAtLeast(access, "reeds", tier);
            SetSubjectAccessAtLeast(access, "mud", tier);
            SetSubjectAccessAtLeast(access, "memory", tier);
            SetSubjectAccessAtLeast(access, "oath", tier);
            SetSubjectAccessAtLeast(access, "names", tier);
            SetSubjectAccessAtLeast(access, "folk_magic", tier);
        }

        if (topic.Equals("wild_magic", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "wild_magic", tier);
            SetSubjectAccessAtLeast(access, "wild_border", tier);
            SetSubjectAccessAtLeast(access, "loose_reality", tier);
            SetSubjectAccessAtLeast(access, "broken_law", tier);
        }

        if (topic.Equals("vigovia.public_law", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("vigovia.procedure", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("faction.law", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "vigovia", tier);
            SetSubjectAccessAtLeast(access, "empire", tier);
            SetSubjectAccessAtLeast(access, "law", tier);
            SetSubjectAccessAtLeast(access, "containment", tier);
            SetSubjectAccessAtLeast(access, "censorate", tier);
            SetSubjectAccessAtLeast(access, "soldier", tier);
        }

        if (topic.Equals("services.folk_magic", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("services", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "service", tier);
            SetSubjectAccessAtLeast(access, "services", tier);
            SetSubjectAccessAtLeast(access, "folk_magic", tier);
        }

        if (topic.Equals("promises.oaths", StringComparison.OrdinalIgnoreCase)
            || topic.Equals("promise.hooks", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "promise", tier);
            SetSubjectAccessAtLeast(access, "promises", tier);
            SetSubjectAccessAtLeast(access, "oath", tier);
            SetSubjectAccessAtLeast(access, "debt", tier);
        }

        if (topic.Equals("region.travel", StringComparison.OrdinalIgnoreCase))
        {
            SetSubjectAccessAtLeast(access, "route", tier);
            SetSubjectAccessAtLeast(access, "routes", tier);
            SetSubjectAccessAtLeast(access, "road", tier);
            SetSubjectAccessAtLeast(access, "roads", tier);
            SetSubjectAccessAtLeast(access, regionId, tier);
        }
    }

    private static void SetSubjectAccessAtLeast(Dictionary<string, int> access, string subject, int tier)
    {
        if (!access.TryGetValue(subject, out var existing) || existing < tier)
        {
            access[subject] = Math.Max(0, tier);
        }
    }

    private static IReadOnlyDictionary<string, int> KnowledgeTopicsFor(Entity speaker)
    {
        if (speaker.TryGet<KnowledgeComponent>(out var explicitKnowledge)
            && explicitKnowledge.TopicTiers.Count > 0)
        {
            return new Dictionary<string, int>(explicitKnowledge.TopicTiers, StringComparer.OrdinalIgnoreCase);
        }

        var topics = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["zone.current"] = 1,
            ["current_zone"] = 1,
            ["zone.npcs"] = 1,
            ["scene.object"] = 1,
            ["npc.relationship"] = 1,
            ["claims"] = 1,
            ["services"] = 1,
            ["region.travel"] = 1,
            ["promise.hooks"] = 1,
        };
        var tags = speaker.TryGet<TagsComponent>(out var tagComponent)
            ? tagComponent.Tags
            : Array.Empty<string>();
        if (tags.Any(tag => tag.Equals("resident", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("prisoner", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("witness", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("hollowmere", StringComparison.OrdinalIgnoreCase)))
        {
            topics["rumors"] = 1;
            topics["npc.knowledge.region"] = 1;
            topics["hollowmere"] = 1;
            topics["people.hollowmere"] = 1;
            topics["vigovia.public_law"] = 1;
            topics["recent.magic_deeds"] = 1;
        }

        if (tags.Any(tag => tag.Equals("imperial", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("law", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("soldier", StringComparison.OrdinalIgnoreCase))
            || (speaker.TryGet<ActorComponent>(out var actor)
                && actor.Faction.Equals("empire", StringComparison.OrdinalIgnoreCase)))
        {
            topics["faction.law"] = 1;
            topics["vigovia.public_law"] = 1;
            topics["vigovia.procedure"] = 1;
            topics["npc.knowledge.region"] = 1;
            topics["recent.magic_deeds"] = 1;
        }

        return topics;
    }

    private DialogueParticipantCard ParticipantCard(Entity? entity, string? bondSummary, string? preparedWant = null)
    {
        if (entity is null)
        {
            return new DialogueParticipantCard(
                "missing",
                "missing",
                Array.Empty<string>(),
                BondSummary: bondSummary);
        }

        var tags = TagsFor(entity).ToArray();
        var faction = entity.TryGet<ActorComponent>(out var actor) ? actor.Faction : null;
        var profile = entity.TryGet<ProfileComponent>(out var profileComponent)
            ? string.Join(
                " ",
                new[]
                {
                    profileComponent.PublicName,
                    profileComponent.Appearance,
                    profileComponent.Origin,
                    profileComponent.MagicalSignature,
                    profileComponent.Backstory,
                }.Where(text => !string.IsNullOrWhiteSpace(text)))
            : null;
        var description = entity.TryGet<DescriptionComponent>(out var descriptionComponent)
            ? descriptionComponent.Text
            : null;
        var inventory = entity.TryGet<InventoryComponent>(out var inventoryComponent)
            ? inventoryComponent.Items
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} x{pair.Value}")
                .ToArray()
            : Array.Empty<string>();
        var wares = entity.TryGet<MerchantComponent>(out var merchant)
            ? merchant.Wares
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key} x{pair.Value}")
                .ToArray()
            : Array.Empty<string>();
        var services = entity.TryGet<ServiceComponent>(out var serviceComponent)
            ? serviceComponent.Offers
                .Where(service => service.Revealed)
                .OrderBy(service => service.Id, StringComparer.OrdinalIgnoreCase)
                .Select(service => $"{service.Name} [{service.EffectKind}]")
                .ToArray()
            : Array.Empty<string>();
        var want = preparedWant ?? (entity.TryGet<WantComponent>(out var wantComponent)
            ? WantSummary(wantComponent)
            : null);
        return new DialogueParticipantCard(
            entity.Id.Value,
            entity.Name,
            tags,
            faction,
            profile,
            description,
            bondSummary,
            inventory,
            wares,
            services,
            want);
    }

    private IEnumerable<string> VisibleEntityLines()
    {
        var controlled = _engine.State.ControlledEntity;
        var origin = controlled.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        var perception = _engine.Perception();
        var ordered = _engine.State.Entities.Values
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Where(entity => entity.Id == controlled.Id || perception.VisibleEntityIds.Contains(entity.Id))
            .Where(entity => !ContextEntityRouting.IsCompactScenery(entity))
            .Select(entity =>
            {
                var position = entity.Get<PositionComponent>().Position;
                var distance = Math.Max(Math.Abs(position.X - origin.X), Math.Abs(position.Y - origin.Y));
                return new { Entity = entity, Position = position, Distance = distance };
            })
            .OrderBy(item => ContextEntityRouting.IsActor(item.Entity) ? 0 : 1)
            .ThenBy(item => item.Distance)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var actors = ordered.Where(item => ContextEntityRouting.IsActor(item.Entity)).ToArray();
        return actors
            .Concat(ordered
                .Where(item => !ContextEntityRouting.IsActor(item.Entity)))
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Select(entity =>
            {
                var tags = TagsFor(entity.Entity);
                return $"{entity.Entity.Name} ({entity.Entity.Id.Value}) at {entity.Position.X},{entity.Position.Y}, range {entity.Distance}, tags {string.Join(",", tags)}";
            });
    }

    private IEnumerable<string> CompactSceneryLines()
    {
        var controlled = _engine.State.ControlledEntity;
        var origin = controlled.TryGet<PositionComponent>(out var controlledPosition)
            ? controlledPosition.Position
            : new GridPoint(0, 0);
        var perception = _engine.Perception();
        return _engine.State.Entities.Values
            .Where(entity => ContextEntityRouting.IsCompactScenery(entity))
            .Where(entity => entity.TryGet<PositionComponent>(out _))
            .Where(entity => perception.VisibleEntityIds.Contains(entity.Id))
            .Select(entity =>
            {
                var position = entity.Get<PositionComponent>().Position;
                var distance = GameEngine.Distance(origin, position);
                var material = entity.TryGet<PhysicalComponent>(out var physical) ? physical.Material : "unknown";
                return new { Entity = entity, Distance = distance, Material = material };
            })
            .OrderBy(item => item.Distance)
            .ThenBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(DialogueVisibleEntityLimit)
            .Select(item =>
                $"{item.Entity.Name} ({item.Entity.Id.Value}), range {item.Distance}, material {item.Material}, tags {string.Join(",", TagsFor(item.Entity))}");
    }

    private IEnumerable<string> NearbyItemLines()
    {
        var controlled = _engine.State.ControlledEntity;
        if (!controlled.TryGet<PositionComponent>(out var controlledPosition))
        {
            return Array.Empty<string>();
        }

        return _engine.State.Entities.Values
            .Where(entity => entity.Has<ItemComponent>() && entity.TryGet<PositionComponent>(out _))
            .Select(entity => new
            {
                Entity = entity,
                Position = entity.Get<PositionComponent>().Position,
            })
            .Where(item => Math.Max(
                Math.Abs(item.Position.X - controlledPosition.Position.X),
                Math.Abs(item.Position.Y - controlledPosition.Position.Y)) <= 4)
            .OrderBy(item => item.Entity.Id.Value, StringComparer.OrdinalIgnoreCase)
            .Take(DialogueNearbyItemLimit)
            .Select(item => $"{item.Entity.Name} ({item.Entity.Id.Value}) at {item.Position.X},{item.Position.Y}");
    }

    private IEnumerable<string> RecentDialogueMemoryLines(string speakerId)
    {
        foreach (var memory in RecentMemoriesFor(speakerId))
        {
            yield return $"{memory.SubjectId} [{memory.Provenance}, salience {memory.Salience}]: {memory.Text}";
        }

        var speaker = _engine.EntityById(speakerId);
        if (speaker is null || !speaker.TryGet<MemoryComponent>(out var entityMemory))
        {
            yield break;
        }

        foreach (var memory in entityMemory.Records.TakeLast(DialogueRecentMemoryLimit))
        {
            yield return $"{speakerId} [{memory.Provenance}, salience {memory.Salience}]: {memory.Text}";
        }
    }

    private IReadOnlyList<WorldMemoryRecord> RecentMemoriesFor(string speakerId) =>
        _engine.State.Memories.Records
            .Where(record => record.SubjectId.Equals(speakerId, StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("gift", StringComparison.OrdinalIgnoreCase)
                || record.Provenance.StartsWith("claim:", StringComparison.OrdinalIgnoreCase))
            .TakeLast(DialogueRecentMemoryLimit)
            .ToArray();

    private static bool IsDialogueNpc(Entity entity)
    {
        if (entity.TryGet<TagsComponent>(out var tags)
            && tags.Tags.Any(tag =>
                tag.Equals("npc", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("resident", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("prisoner", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("witness", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("soldier", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("merchant", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return entity.TryGet<ActorComponent>(out var actor)
            && !actor.Faction.Equals("player", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTravelLikePromise(WorldPromise promise) =>
        ContainsAny(promise.TriggerHint, "travel", "route", "road")
        || ContainsAny(promise.RealizationKind, "route", "site", "town", "landmark", "person", "item", "threat", "service", "merchant_stock", "escape")
        || ContainsAny(promise.Text, "north", "south", "east", "west", "road", "route", "path", "travel", "find");

    private static bool PromiseVisibleToSpeaker(WorldPromise promise, string? speakerId) =>
        promise.PlayerVisible
        || (!string.IsNullOrWhiteSpace(speakerId)
            && (string.Equals(promise.SourceSpeakerId, speakerId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(promise.BoundTargetId, speakerId, StringComparison.OrdinalIgnoreCase)
                || string.Equals(promise.Subject, speakerId, StringComparison.OrdinalIgnoreCase)));

    private static string PromiseLine(WorldPromise promise)
    {
        var details = new[]
        {
            $"Promise {promise.Id} [{promise.Kind}/{promise.Status}, salience {promise.Salience}]",
            promise.Text,
            string.IsNullOrWhiteSpace(promise.RealizationKind) ? "" : $"realization {promise.RealizationKind}",
            string.IsNullOrWhiteSpace(promise.TriggerHint) ? "" : $"trigger {promise.TriggerHint}",
            string.IsNullOrWhiteSpace(promise.ClaimedPlace) ? "" : $"claimed place {promise.ClaimedPlace}",
            string.IsNullOrWhiteSpace(promise.BoundPlace) ? "" : $"bound place {promise.BoundPlace}",
        };
        return string.Join("; ", details.Where(detail => !string.IsNullOrWhiteSpace(detail)));
    }

    private static bool ContainsAny(string? text, params string[] needles) =>
        !string.IsNullOrWhiteSpace(text)
        && needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> TagsFor(Entity entity) =>
        entity.TryGet<TagsComponent>(out var tags) ? tags.Tags : Array.Empty<string>();

    private static string? WantSummary(WantComponent want)
    {
        if (!want.Status.Equals("active", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(want.Text))
        {
            return null;
        }

        var stakes = string.IsNullOrWhiteSpace(want.Stakes) ? "" : $" Stakes: {want.Stakes}";
        return $"{want.Text} (salience {want.Salience}).{stakes}";
    }

    private static IEnumerable<string> TextTokens(string text)
    {
        var token = new List<char>();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Add(ch);
                continue;
            }

            if (token.Count > 0)
            {
                yield return new string(token.ToArray());
                token.Clear();
            }
        }

        if (token.Count > 0)
        {
            yield return new string(token.ToArray());
        }
    }

    private sealed class DialogueContextCardBuckets
    {
        public List<DialogueContextCardPayload> Available { get; } = new();

        public List<DialogueContextCardPayload> Denied { get; } = new();
    }
}

internal static class DialoguePayloadSizer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    public static int JsonUtf8Bytes<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions).Length;
}
