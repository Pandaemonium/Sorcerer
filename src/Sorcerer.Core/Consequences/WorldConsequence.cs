using Sorcerer.Core.Results;

namespace Sorcerer.Core.Consequences;

public static class WorldConsequenceTypes
{
    public const string RecordMemory = "record_memory";
    public const string UpdateBond = "update_bond";
    public const string AddMerchantStock = "add_merchant_stock";
    public const string OfferTrade = "offer_trade";
    public const string OfferService = "offer_service";
    public const string OpenOrUnlock = "open_or_unlock";
    public const string CreateRoute = "create_route";
}

public static class WorldConsequenceVisibility
{
    public const string Hidden = "hidden";
    public const string Message = "message";
    public const string Journal = "journal";
    public const string Lead = "lead";
}

public sealed record WorldConsequence(
    string Type,
    string Source,
    string? SourceEntityId = null,
    string? TargetEntityId = null,
    int Salience = 1,
    int Confidence = 100,
    string Visibility = WorldConsequenceVisibility.Hidden,
    string? Evidence = null,
    string? Reason = null,
    IReadOnlyDictionary<string, object?>? Payload = null)
{
    public static WorldConsequence RecordMemory(
        string source,
        string ownerEntityId,
        string text,
        string provenance,
        int salience,
        bool shareable,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "recordMemory",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.RecordMemory,
            source,
            sourceEntityId,
            ownerEntityId,
            salience,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("text", text),
                ("provenance", provenance),
                ("shareable", shareable),
                ("operation", operation)));

    public static WorldConsequence UpdateBond(
        string source,
        string entityId,
        string targetSoulId,
        int loyaltyDelta,
        int fearDelta,
        int admirationDelta,
        int resentmentDelta,
        string? posture,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "updateBond",
        int maxDelta = 2,
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.UpdateBond,
            source,
            sourceEntityId,
            entityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("targetSoulId", targetSoulId),
                ("loyaltyDelta", loyaltyDelta),
                ("fearDelta", fearDelta),
                ("admirationDelta", admirationDelta),
                ("resentmentDelta", resentmentDelta),
                ("posture", posture),
                ("operation", operation),
                ("maxDelta", maxDelta)));

    public static WorldConsequence AddMerchantStock(
        string source,
        string merchantId,
        string itemName,
        int quantity = 1,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "addMerchantStock",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.AddMerchantStock,
            source,
            sourceEntityId,
            merchantId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("itemName", itemName),
                ("quantity", quantity),
                ("operation", operation)));

    public static WorldConsequence OfferTrade(
        string source,
        string merchantId,
        string? itemName = null,
        int quantity = 1,
        int gold = 30,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "offerTrade",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OfferTrade,
            source,
            sourceEntityId,
            merchantId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("itemName", itemName),
                ("quantity", quantity),
                ("gold", gold),
                ("operation", operation)));

    public static WorldConsequence OfferService(
        string source,
        string providerId,
        string serviceId,
        string name,
        string description,
        string effectKind,
        int goldCost = 0,
        string? itemCost = null,
        string? targetHint = null,
        bool revealed = true,
        IReadOnlyList<string>? tags = null,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "offerService",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OfferService,
            source,
            sourceEntityId,
            providerId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("serviceId", serviceId),
                ("name", name),
                ("description", description),
                ("effectKind", effectKind),
                ("goldCost", goldCost),
                ("itemCost", itemCost),
                ("targetHint", targetHint),
                ("revealed", revealed),
                ("tags", tags?.ToArray() ?? Array.Empty<string>()),
                ("operation", operation)));

    public static WorldConsequence OpenOrUnlock(
        string source,
        string doorId,
        string? actorId = null,
        bool unlock = true,
        bool open = true,
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "openOrUnlock",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.OpenOrUnlock,
            source,
            sourceEntityId ?? actorId,
            doorId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("actorId", actorId),
                ("unlock", unlock),
                ("open", open),
                ("operation", operation)));

    public static WorldConsequence CreateRoute(
        string source,
        string anchorEntityId,
        string name,
        string description,
        string routeKind = "hidden_route",
        string visibility = WorldConsequenceVisibility.Hidden,
        string? sourceEntityId = null,
        string? evidence = null,
        string? reason = null,
        string operation = "createRoute",
        IReadOnlyDictionary<string, object?>? details = null) =>
        new(
            WorldConsequenceTypes.CreateRoute,
            source,
            sourceEntityId,
            anchorEntityId,
            Visibility: visibility,
            Evidence: evidence,
            Reason: reason,
            Payload: MergePayload(
                details,
                ("name", name),
                ("description", description),
                ("routeKind", routeKind),
                ("operation", operation)));

    private static IReadOnlyDictionary<string, object?> MergePayload(
        IReadOnlyDictionary<string, object?>? details,
        params (string Key, object? Value)[] fields)
    {
        var payload = details is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(details, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in fields)
        {
            payload[key] = value;
        }

        return payload;
    }
}

public sealed record WorldConsequenceApplyResult(
    bool Applied,
    string? TargetId,
    string? Error,
    IReadOnlyList<string> Messages,
    IReadOnlyList<StateDelta> Deltas,
    IReadOnlyDictionary<string, object?> Details)
{
    public static WorldConsequenceApplyResult Empty(string? error = null) =>
        new(false, null, error, Array.Empty<string>(), Array.Empty<StateDelta>(), new Dictionary<string, object?>());
}
