using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.World;

namespace Sorcerer.Core.Consequences;

public sealed class WorldConsequenceApplier
{
    private readonly GameState _state;
    private readonly int _defaultBondDeltaLimit;

    public WorldConsequenceApplier(GameState state, int defaultBondDeltaLimit = 2)
    {
        _state = state;
        _defaultBondDeltaLimit = defaultBondDeltaLimit;
    }

    public WorldConsequenceApplyResult Apply(WorldConsequence consequence) =>
        NormalizeToken(consequence.Type, "") switch
        {
            WorldConsequenceTypes.RecordMemory => ApplyRecordMemory(consequence),
            WorldConsequenceTypes.UpdateBond => ApplyUpdateBond(consequence),
            WorldConsequenceTypes.AddMerchantStock => ApplyAddMerchantStock(consequence),
            WorldConsequenceTypes.OfferTrade => ApplyOfferTrade(consequence),
            WorldConsequenceTypes.OfferService => ApplyOfferService(consequence),
            WorldConsequenceTypes.OpenOrUnlock => ApplyOpenOrUnlock(consequence),
            WorldConsequenceTypes.CreateRoute => ApplyCreateRoute(consequence),
            _ => Reject(consequence, $"Unknown world consequence type: {consequence.Type}"),
        };

    private WorldConsequenceApplyResult ApplyRecordMemory(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var text = ReadString(payload, "text")?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return Reject(consequence, "Memory consequence did not include text.");
        }

        var ownerId = FirstNonBlank(consequence.TargetEntityId, consequence.SourceEntityId, _state.ControlledEntityId.Value)!;
        var salience = Math.Clamp(consequence.Salience, 1, 5);
        var provenance = FirstNonBlank(ReadString(payload, "provenance"), consequence.Source) ?? consequence.Source;
        var shareable = ReadBool(payload, "shareable") ?? true;

        _state.Memories.Append(ownerId, text, provenance, salience, shareable);
        var owner = EntityById(ownerId);
        if (owner is not null)
        {
            var memories = owner.TryGet<MemoryComponent>(out var existing)
                ? existing.Records.ToList()
                : new List<EntityMemoryRecord>();
            memories.Add(new EntityMemoryRecord(
                $"memory_{NormalizeToken(consequence.Source, "source")}_{_state.Turn}_{memories.Count + 1}",
                text,
                consequence.Source,
                provenance,
                salience,
                shareable));
            owner.Set(new MemoryComponent(memories.TakeLast(24).ToArray()));
        }

        var operation = ReadString(payload, "operation") ?? "recordMemory";
        var summary = ReadString(payload, "summary") ?? $"Memory recorded: {text}";
        var delta = new StateDelta(operation, ownerId, summary, Details(consequence, ("salience", salience), ("provenance", provenance)));
        return Applied(consequence, ownerId, MaybeVisibleMessage(consequence, summary), delta, ("salience", salience), ("provenance", provenance));
    }

    private WorldConsequenceApplyResult ApplyUpdateBond(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var entityId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(entityId))
        {
            return Reject(consequence, "Bond consequence did not include an entity id.");
        }

        var operation = ReadString(payload, "operation") ?? "updateBond";
        var entity = EntityById(entityId);
        if (entity is null)
        {
            var skipped = new StateDelta(
                "dialogueProposalSkipped",
                entityId,
                "Dialogue bond proposal skipped because the entity no longer exists.",
                Details(consequence, ("proposalType", "bond"), ("operation", operation)));
            return new WorldConsequenceApplyResult(
                false,
                entityId,
                "missing_entity",
                Array.Empty<string>(),
                new[] { skipped },
                Details(consequence, ("proposalType", "bond"), ("operation", operation)));
        }

        var targetSoulId = ReadString(payload, "targetSoulId");
        if (string.IsNullOrWhiteSpace(targetSoulId))
        {
            return Reject(consequence, "Bond consequence did not include a target soul id.");
        }

        var maxDelta = Math.Max(0, ReadInt(payload, "maxDelta") ?? _defaultBondDeltaLimit);
        var bond = _state.Bonds.Adjust(
            SoulIdFor(entity),
            targetSoulId,
            ClampDelta(ReadInt(payload, "loyaltyDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "fearDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "admirationDelta") ?? 0, maxDelta),
            ClampDelta(ReadInt(payload, "resentmentDelta") ?? 0, maxDelta),
            FirstNonBlank(ReadString(payload, "posture")));
        var summary = $"{entity.Name}'s posture shifts: {BondSummary(bond)}.";
        var delta = new StateDelta(
            operation,
            entity.Id.Value,
            summary,
            Details(
                consequence,
                ("loyalty", bond.Loyalty),
                ("fear", bond.Fear),
                ("admiration", bond.Admiration),
                ("resentment", bond.Resentment),
                ("posture", bond.Posture)));
        return Applied(
            consequence,
            entity.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("loyalty", bond.Loyalty),
            ("fear", bond.Fear),
            ("admiration", bond.Admiration),
            ("resentment", bond.Resentment),
            ("posture", bond.Posture));
    }

    private WorldConsequenceApplyResult ApplyAddMerchantStock(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Merchant stock consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null || !merchant.TryGet<MerchantComponent>(out var stock))
        {
            return Reject(consequence, "Merchant stock consequence target is not a merchant.");
        }

        var itemName = ReadString(payload, "itemName")?.Trim();
        if (string.IsNullOrWhiteSpace(itemName))
        {
            return Reject(consequence, "Merchant stock consequence did not include an item name.");
        }

        var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
        stock.Wares.TryGetValue(itemName, out var current);
        stock.Wares[itemName] = current + quantity;

        var operation = ReadString(payload, "operation") ?? "addMerchantStock";
        var summary = $"{merchant.Name}'s stock now includes {itemName}.";
        var delta = new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(consequence, ("item", itemName), ("quantity", stock.Wares[itemName])));
        return Applied(
            consequence,
            merchant.Id.Value,
            MaybeVisibleMessage(consequence, summary),
            delta,
            ("item", itemName),
            ("quantity", stock.Wares[itemName]));
    }

    private WorldConsequenceApplyResult ApplyOfferTrade(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var merchantId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(merchantId))
        {
            return Reject(consequence, "Trade offer consequence did not include a merchant id.");
        }

        var merchant = EntityById(merchantId);
        if (merchant is null)
        {
            return Reject(consequence, "Trade offer consequence target does not exist.");
        }

        if (!merchant.TryGet<MerchantComponent>(out var stock))
        {
            stock = new MerchantComponent(new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), ReadInt(payload, "gold") ?? 30);
            merchant.Set(stock);
        }

        var itemName = ReadString(payload, "itemName")?.Trim();
        if (!string.IsNullOrWhiteSpace(itemName))
        {
            var quantity = Math.Max(1, ReadInt(payload, "quantity") ?? 1);
            stock.Wares.TryGetValue(itemName, out var current);
            stock.Wares[itemName] = current + quantity;
        }

        EnsureInteractableVerbs(merchant, "wares", "buy", "sell", "talk");
        var operation = ReadString(payload, "operation") ?? "offerTrade";
        var summary = string.IsNullOrWhiteSpace(itemName)
            ? $"{merchant.Name} is ready to trade."
            : $"{merchant.Name} offers trade in {itemName}.";
        var delta = new StateDelta(
            operation,
            merchant.Id.Value,
            summary,
            Details(consequence, ("item", itemName), ("quantity", string.IsNullOrWhiteSpace(itemName) ? 0 : stock.Wares[itemName])));
        return Applied(consequence, merchant.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("item", itemName));
    }

    private WorldConsequenceApplyResult ApplyOfferService(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var providerId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return Reject(consequence, "Service offer consequence did not include a provider id.");
        }

        var provider = EntityById(providerId);
        if (provider is null)
        {
            return Reject(consequence, "Service offer consequence target does not exist.");
        }

        var serviceId = NormalizeToken(ReadString(payload, "serviceId") ?? ReadString(payload, "name") ?? "service", "service");
        var name = FirstNonBlank(ReadString(payload, "name"), serviceId) ?? serviceId;
        var service = new ServiceOffer(
            serviceId,
            name,
            ReadString(payload, "description") ?? consequence.Evidence ?? name,
            NormalizeToken(ReadString(payload, "effectKind") ?? "record_memory", "record_memory"),
            Math.Max(0, ReadInt(payload, "goldCost") ?? 0),
            FirstNonBlank(ReadString(payload, "itemCost")),
            FirstNonBlank(ReadString(payload, "targetHint")),
            ReadBool(payload, "revealed") ?? true,
            ReadStringList(payload, "tags"));
        var services = provider.TryGet<ServiceComponent>(out var existing)
            ? existing.Offers.ToList()
            : new List<ServiceOffer>();
        var existingIndex = services.FindIndex(offer => offer.Id.Equals(service.Id, StringComparison.OrdinalIgnoreCase));
        if (existingIndex >= 0)
        {
            services[existingIndex] = service;
        }
        else
        {
            services.Add(service);
        }

        provider.Set(new ServiceComponent(services.OrderBy(offer => offer.Id, StringComparer.OrdinalIgnoreCase).ToArray()));
        EnsureInteractableVerbs(provider, "services", "request_service", "talk");
        var operation = ReadString(payload, "operation") ?? "offerService";
        var summary = $"{provider.Name} can offer {service.Name}.";
        var delta = new StateDelta(
            operation,
            provider.Id.Value,
            summary,
            Details(
                consequence,
                ("serviceId", service.Id),
                ("serviceName", service.Name),
                ("effectKind", service.EffectKind),
                ("goldCost", service.GoldCost),
                ("itemCost", service.ItemCost),
                ("targetHint", service.TargetHint)));
        return Applied(consequence, provider.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("serviceId", service.Id));
    }

    private WorldConsequenceApplyResult ApplyOpenOrUnlock(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var doorId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(doorId))
        {
            return Reject(consequence, "Open/unlock consequence did not include a door id.");
        }

        var door = EntityById(doorId);
        if (door is null || !door.TryGet<DoorComponent>(out var doorComponent))
        {
            return Reject(consequence, "Open/unlock consequence target is not a door.");
        }

        var actorId = ReadString(payload, "actorId") ?? consequence.SourceEntityId;
        var actor = string.IsNullOrWhiteSpace(actorId) ? null : EntityById(actorId);
        if (actor is not null && !CanReach(actor, door, range: 2))
        {
            return Reject(consequence, $"{actor.Name} cannot reach {door.Name}.");
        }

        var unlock = ReadBool(payload, "unlock") ?? true;
        var open = ReadBool(payload, "open") ?? true;
        if (!unlock && open && !string.IsNullOrWhiteSpace(doorComponent.KeyId))
        {
            return Reject(consequence, $"{door.Name} is locked.");
        }

        var wasLocked = !string.IsNullOrWhiteSpace(doorComponent.KeyId);
        var wasOpen = doorComponent.IsOpen;
        var nextDoor = doorComponent;
        if (unlock)
        {
            nextDoor = nextDoor with { KeyId = null };
        }

        if (open)
        {
            nextDoor = nextDoor with { IsOpen = true };
        }

        door.Set(nextDoor);
        if (nextDoor.IsOpen && door.TryGet<PhysicalComponent>(out var physical))
        {
            door.Set(physical with { BlocksMovement = false, BlocksSight = false });
        }

        if (nextDoor.IsOpen && door.TryGet<RenderableComponent>(out var renderable))
        {
            door.Set(renderable with { Glyph = '/', Palette = "open" });
        }

        var actorName = actor is null ? "Something" : actor.Name;
        var summary = nextDoor.IsOpen && !wasOpen
            ? $"{actorName} opens {door.Name}."
            : wasLocked && unlock
                ? $"{actorName} unlocks {door.Name}."
                : $"{door.Name} is already open.";
        var operation = ReadString(payload, "operation") ?? "openOrUnlock";
        var delta = new StateDelta(
            operation,
            door.Id.Value,
            summary,
            Details(
                consequence,
                ("actorId", actor?.Id.Value),
                ("unlocked", wasLocked && unlock),
                ("open", nextDoor.IsOpen)));
        return Applied(consequence, door.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("open", nextDoor.IsOpen));
    }

    private WorldConsequenceApplyResult ApplyCreateRoute(WorldConsequence consequence)
    {
        var payload = consequence.Payload ?? new Dictionary<string, object?>();
        var anchorId = consequence.TargetEntityId;
        if (string.IsNullOrWhiteSpace(anchorId))
        {
            return Reject(consequence, "Route consequence did not include an anchor id.");
        }

        var anchor = EntityById(anchorId);
        if (anchor is null || !anchor.TryGet<PositionComponent>(out var anchorPosition))
        {
            return Reject(consequence, "Route consequence anchor is missing or has no position.");
        }

        var name = FirstNonBlank(ReadString(payload, "name"), "hidden route")!;
        var description = FirstNonBlank(ReadString(payload, "description"), consequence.Evidence, name)!;
        var routeKind = NormalizeToken(ReadString(payload, "routeKind") ?? "hidden_route", "hidden_route");
        var position = FindOpenAdjacent(anchorPosition.Position) ?? anchorPosition.Position;
        var routeId = _state.NextEntityId("route");
        var tags = new[] { "route", "escape_route", routeKind, "promise_payoff" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var route = new Entity(routeId, name)
            .Set(new PositionComponent(position))
            .Set(new RenderableComponent('>', "route"))
            .Set(new TagsComponent(tags))
            .Set(new DescriptionComponent(description))
            .Set(new PhysicalComponent(BlocksMovement: false, Material: "passage"))
            .Set(new FixtureComponent(routeKind, tags))
            .Set(new InteractableComponent(new[] { "examine", "travel" }));
        _state.Entities[routeId] = route;

        var operation = ReadString(payload, "operation") ?? "createRoute";
        var summary = $"A route is now discoverable: {route.Name}.";
        var delta = new StateDelta(
            operation,
            route.Id.Value,
            summary,
            Details(
                consequence,
                ("routeId", route.Id.Value),
                ("routeKind", routeKind),
                ("x", position.X),
                ("y", position.Y)));
        return Applied(consequence, route.Id.Value, MaybeVisibleMessage(consequence, summary), delta, ("routeId", route.Id.Value));
    }

    private WorldConsequenceApplyResult Reject(WorldConsequence consequence, string error)
    {
        var delta = new StateDelta(
            "worldConsequenceRejected",
            consequence.TargetEntityId ?? consequence.SourceEntityId ?? consequence.Source,
            error,
            Details(consequence, ("error", error)));
        return new WorldConsequenceApplyResult(false, consequence.TargetEntityId, error, Array.Empty<string>(), new[] { delta }, Details(consequence, ("error", error)));
    }

    private WorldConsequenceApplyResult Applied(
        WorldConsequence consequence,
        string targetId,
        IReadOnlyList<string> messages,
        StateDelta delta,
        params (string Key, object? Value)[] fields) =>
        new(true, targetId, null, messages, new[] { delta }, Details(consequence, fields));

    private IReadOnlyList<string> MaybeVisibleMessage(WorldConsequence consequence, string message)
    {
        if (!IsVisible(consequence.Visibility))
        {
            return Array.Empty<string>();
        }

        _state.AddMessage(message);
        return new[] { message };
    }

    private static bool IsVisible(string visibility) =>
        NormalizeToken(visibility, WorldConsequenceVisibility.Hidden) is
            WorldConsequenceVisibility.Message or WorldConsequenceVisibility.Journal or WorldConsequenceVisibility.Lead or "visible";

    private IReadOnlyDictionary<string, object?> Details(WorldConsequence consequence, params (string Key, object? Value)[] fields)
    {
        var details = consequence.Payload is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(consequence.Payload, StringComparer.OrdinalIgnoreCase);
        details["consequenceType"] = consequence.Type;
        details["source"] = consequence.Source;
        details["sourceEntityId"] = consequence.SourceEntityId;
        details["visibility"] = consequence.Visibility;
        details["salience"] = consequence.Salience;
        details["confidence"] = consequence.Confidence;
        details["evidence"] = consequence.Evidence;
        details["reason"] = consequence.Reason;
        foreach (var (key, value) in fields)
        {
            details[key] = value;
        }

        return details;
    }

    private static void EnsureInteractableVerbs(Entity entity, params string[] verbs)
    {
        var existing = entity.TryGet<InteractableComponent>(out var interactable)
            ? interactable.Verbs
            : Array.Empty<string>();
        var merged = existing
            .Concat(verbs)
            .Where(verb => !string.IsNullOrWhiteSpace(verb))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(verb => verb, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        entity.Set(new InteractableComponent(merged));
    }

    private bool CanReach(Entity actor, Entity target, int range)
    {
        if (!actor.TryGet<PositionComponent>(out var actorPosition)
            || !target.TryGet<PositionComponent>(out var targetPosition))
        {
            return false;
        }

        return Distance(actorPosition.Position, targetPosition.Position) <= range;
    }

    private GridPoint? FindOpenAdjacent(GridPoint origin)
    {
        var offsets = new[]
        {
            new GridPoint(0, 1),
            new GridPoint(1, 0),
            new GridPoint(0, -1),
            new GridPoint(-1, 0),
            new GridPoint(1, 1),
            new GridPoint(-1, 1),
            new GridPoint(1, -1),
            new GridPoint(-1, -1),
        };
        foreach (var point in offsets
            .Select(offset => new GridPoint(origin.X + offset.X, origin.Y + offset.Y))
            .Where(point => point.X > 0 && point.Y > 0 && point.X < _state.Width - 1 && point.Y < _state.Height - 1)
            .Where(point => !_state.BlockingTerrain.Contains(point))
            .Where(point => !_state.Entities.Values.Any(entity =>
                entity.TryGet<PositionComponent>(out var position)
                && position.Position == point
                && entity.TryGet<PhysicalComponent>(out var physical)
                && physical.BlocksMovement)))
        {
            return point;
        }

        return null;
    }

    private static int Distance(GridPoint a, GridPoint b) =>
        Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    private Entity? EntityById(string entityId) =>
        _state.Entities.TryGetValue(EntityId.Create(entityId), out var entity) ? entity : null;

    private static int ClampDelta(int value, int maxDelta) =>
        Math.Clamp(value, -maxDelta, maxDelta);

    private static string SoulIdFor(Entity entity) =>
        entity.TryGet<SoulComponent>(out var soul) ? soul.SoulId : entity.Id.Value;

    private static string BondSummary(BondRecord bond)
    {
        if (bond.Posture.Equals("follower", StringComparison.OrdinalIgnoreCase))
        {
            return "following";
        }

        if (bond.Loyalty + bond.Admiration >= 5)
        {
            return "warm enough to risk something";
        }

        if (bond.Fear > bond.Loyalty + bond.Admiration)
        {
            return "afraid";
        }

        if (bond.Resentment >= 5)
        {
            return "resentful";
        }

        return bond.Posture;
    }

    private static string NormalizeToken(string text, string fallback)
    {
        var chars = text.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '_')
            .ToArray();
        var normalized = string.Join("_", new string(chars).Split('_', StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(normalized) ? fallback : normalized;
    }

    private static string? FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? ReadString(IReadOnlyDictionary<string, object?> map, string key) =>
        map.TryGetValue(key, out var value) ? value switch
        {
            string text => text,
            _ => value?.ToString(),
        } : null;

    private static int? ReadInt(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            int typed => typed,
            long typed => checked((int)typed),
            double typed => (int)Math.Round(typed),
            float typed => (int)Math.Round(typed),
            decimal typed => (int)Math.Round(typed),
            string text when int.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            bool typed => typed,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };
    }

    private static IReadOnlyList<string> ReadStringList(IReadOnlyDictionary<string, object?> map, string key)
    {
        if (!map.TryGetValue(key, out var value) || value is null)
        {
            return Array.Empty<string>();
        }

        return value switch
        {
            string text => new[] { text },
            IEnumerable<string> strings => strings.ToArray(),
            IEnumerable<object?> objects => objects.Select(item => item?.ToString() ?? "").Where(item => !string.IsNullOrWhiteSpace(item)).ToArray(),
            _ => Array.Empty<string>(),
        };
    }
}
