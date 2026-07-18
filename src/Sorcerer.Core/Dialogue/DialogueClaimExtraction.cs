using Sorcerer.Core.World;

namespace Sorcerer.Core.Dialogue;

public sealed record DialogueClaimRequest(
    int Turn,
    string RegionId,
    string CurrentZoneId,
    string SpeakerId,
    string SpeakerName,
    IReadOnlyList<string> SpeakerTags,
    string ListenerSoulId,
    string PlayerText,
    IReadOnlyList<string> DialogueLines,
    IReadOnlyList<WorldMemoryRecord> RecentMemories,
    IReadOnlyList<ClaimRecord> RecentClaims,
    string? ListenerEntityId = null,
    IReadOnlyList<string>? SelectedParserCapabilityIds = null,
    IReadOnlyList<DialogueParserCapabilityCard>? ParserCapabilityCards = null,
    string? SpeakerWant = null,
    IReadOnlyList<string>? ListenerInventory = null);

public sealed record DialogueParserCapabilityCard(
    string Id,
    string Title,
    string Summary,
    IReadOnlyList<string> Lines);

public sealed record DialogueParserRouteCandidate(
    string Id,
    string Title,
    string Summary);

public sealed record DialogueParserRouteRequest(
    int Turn,
    string RegionId,
    string CurrentZoneId,
    string SpeakerId,
    string SpeakerName,
    IReadOnlyList<string> SpeakerTags,
    string ListenerSoulId,
    string PlayerText,
    IReadOnlyList<string> DialogueLines,
    IReadOnlyList<DialogueParserRouteCandidate> AvailableCapabilities);

public sealed record DialogueParserRouteResult(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    bool HasMechanics,
    IReadOnlyList<string> SelectedCapabilityIds,
    string? Reason = null);

public interface IDialogueParserRouter
{
    string Name { get; }

    Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken);
}

public sealed record DialogueParserCapabilitySelection(
    bool HasMechanics,
    IReadOnlyList<DialogueParserCapabilityCard> SelectedCards,
    IReadOnlyList<string> SelectedCapabilityIds,
    IReadOnlyList<string> FallbackCapabilityIds,
    IReadOnlyList<string> UnknownSelectedCapabilityIds,
    bool UsedFallback);

public static class DialogueParserCapabilityCatalog
{
    private const int MaxRoutedCapabilityIds = 3;

    public static IReadOnlyList<DialogueParserCapabilityCard> All { get; } = new[]
    {
        new DialogueParserCapabilityCard(
            "claims",
            "Claims",
            "NPC-spoken/reported facts about places, people, stock, threats, laws, routes, or events.",
            new[]
            {
                "Claim only NPC-supported facts; never bind player inventions.",
                "Fields: text, category, subject, salience, confidence, playerVisible, targetEntityId, merchantId, itemName, tags, playerAuthored.",
                "Categories: town, landmark, person, item, merchant_stock, threat, rumor, local_law, custom, history, relationship, escape_route.",
            }),
        new DialogueParserCapabilityCard(
            "promises",
            "Promises",
            "Actionable claims that should later become sites, people, items, stock, services, threats, routes, or doors.",
            new[]
            {
                "Set bindAsPromise true for useful later content; use triggerHint such as travel, talk, buy, request, open, inspect.",
                "realizationKind: site, town, landmark, person, item, merchant_stock, service, threat, escape_route, door_rule, route.",
                "Use claimedPlace for direction/place language.",
            }),
        new DialogueParserCapabilityCard(
            "canon",
            "Canon",
            "Durable lore or local facts that should become world knowledge without promising future delivery.",
            new[]
            {
                "Set bindAsCanon true for supported local law, custom, lineage, taboo, history, faction procedure, or known relationship.",
                "Use canonKind and canonSummary; omit unsupported flourish.",
            }),
        new DialogueParserCapabilityCard(
            "memory",
            "Memory",
            "Durable memories the NPC or listener should keep because this exchange materially matters.",
            new[]
            {
                "Record only future-relevant memories. Fields: ownerEntityId, text, provenance, salience, shareable.",
            }),
        new DialogueParserCapabilityCard(
            "bond_want",
            "Bond And Want",
            "Material relationship changes or changes to the speaker's active desire.",
            new[]
            {
                "Use bond only for material relationship change; deltas small.",
                "Use want when the speaker desire/status is satisfied, blocked, redirected, or escalated. Include reason.",
            }),
        new DialogueParserCapabilityCard(
            "local_actions",
            "Local Actions",
            "Immediate local actions the speaking NPC can plausibly perform now.",
            new[]
            {
                "Allowed: none, step_aside, flee, call_help, give_item, open_door, attack, recruit, mark_location, spawn_fixture, create_promise.",
                "Only local plausible now-actions. Fields as needed: targetEntityId, itemName, quantity, gold, x/y, targetHint, name, description, fixtureType, material, tags, interactableVerbs.",
            }),
        new DialogueParserCapabilityCard(
            "services_trade",
            "Services And Trade",
            "Services, wares, merchant stock, and trade offers revealed by speech.",
            new[]
            {
                "Use reveal_service for later request; request_service only for an already-known service performed now.",
                "Merchant stock claim: category merchant_stock, merchantId speakerId, itemName, bindAsPromise false.",
                "Use offer_trade for offers only; buy/sell/request commands execute transactions.",
            }),
        new DialogueParserCapabilityCard(
            "bargains",
            "Typed Bargains",
            "Concrete alternative settlements proposed by this speaker for an active debt or negotiation.",
            new[]
            {
                "Use proposals.bargain only when the NPC clearly states terms. claimantEntityId must be the speaker id.",
                "Each option has id, label, and 1-5 typed terms. Kinds: currency, item, service, standing, concession, deadline.",
                "currency: quantity; item/service: resourceId plus quantity when relevant; standing: factionId, standingAxis, standingDelta; deadline: dueTurn or dueInTurns.",
                "A deadline constrains a service; it is never a settlement by itself. Use the speaker's exact want id as a service resourceId when one is supplied.",
            }),
        new DialogueParserCapabilityCard(
            "typed_consequences",
            "Typed Consequences",
            "Generic typed consequences when no narrower dialogue action fits.",
            new[]
            {
                "Use action type consequence with consequenceType and consequencePayload only when no narrower action fits.",
                "Engine validates through WorldConsequence.",
            }),
    };

    public static IReadOnlyList<DialogueParserRouteCandidate> Candidates() =>
        All.Select(card => new DialogueParserRouteCandidate(card.Id, card.Title, card.Summary)).ToArray();

    public static DialogueParserRouteRequest BuildRouteRequest(DialogueClaimRequest request) =>
        new(
            request.Turn,
            request.RegionId,
            request.CurrentZoneId,
            request.SpeakerId,
            request.SpeakerName,
            request.SpeakerTags,
            request.ListenerSoulId,
            request.PlayerText,
            request.DialogueLines,
            Candidates());

    public static DialogueParserCapabilitySelection Select(
        DialogueParserRouteResult result,
        DialogueParserRouteRequest request)
    {
        var fallbackIds = DeterministicCapabilityIds(request).ToArray();
        var requestedIds = (result.SelectedCapabilityIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var available = All.Select(card => card.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = requestedIds
            .Where(available.Contains)
            .Take(MaxRoutedCapabilityIds)
            .ToArray();
        var hasMechanics = result.HasMechanics;
        var usedFallback = result.TechnicalFailure || (hasMechanics && selectedIds.Length == 0);
        if (usedFallback)
        {
            selectedIds = fallbackIds;
            hasMechanics = selectedIds.Length > 0;
        }

        if (!hasMechanics)
        {
            selectedIds = Array.Empty<string>();
        }

        var selected = CardsFor(selectedIds);
        var unknown = requestedIds
            .Where(id => !available.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new DialogueParserCapabilitySelection(
            hasMechanics,
            selected,
            selectedIds,
            fallbackIds,
            unknown,
            usedFallback);
    }

    public static IReadOnlyList<DialogueParserCapabilityCard> CardsFor(IEnumerable<string> ids)
    {
        var wanted = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Where(card => wanted.Contains(card.Id)).ToArray();
    }

    public static IReadOnlyList<string> DeterministicCapabilityIds(DialogueParserRouteRequest request)
    {
        var text = $"{request.PlayerText} {string.Join(" ", request.DialogueLines)}".ToLowerInvariant();
        var selected = new List<string>();
        if (ContainsAny(text, "heard", "rumor", "saw", "know", "place", "road", "route", "town", "person", "stock", "threat", "law", "custom", "secret", "sell"))
        {
            selected.Add("claims");
        }

        if (ContainsAny(text, "promise", "owe", "will", "later", "north", "south", "east", "west", "road", "route", "find", "deliver", "remember this"))
        {
            selected.Add("promises");
        }

        if (ContainsAny(text, "law", "custom", "taboo", "history", "canon", "tradition", "vigovia", "hollowmere"))
        {
            selected.Add("canon");
        }

        if (ContainsAny(text, "remember", "won't forget", "will not forget", "saw you", "gift", "trust", "betray"))
        {
            selected.Add("memory");
        }

        if (ContainsAny(text, "trust", "afraid", "fear", "admire", "hate", "grateful", "want", "need", "stakes", "owe"))
        {
            selected.Add("bond_want");
        }

        if (ContainsAny(text, "step aside", "move", "run", "flee", "help", "open", "attack", "follow", "give", "mark", "spawn", "door", "lock"))
        {
            selected.Add("local_actions");
        }

        if (ContainsAny(text, "service", "trade", "sell", "buy", "wares", "stock", "merchant", "cost", "gold", "grave salt"))
        {
            selected.Add("services_trade");
        }

        if (ContainsAny(text, "bargain", "terms", "settle", "settlement", "deal", "agreement", "pay", "payment", "concession", "deadline", "in exchange", "stand down"))
        {
            selected.Add("bargains");
        }

        if (ContainsAny(text, "curse", "status", "transform", "consequence", "mark you", "mark myself"))
        {
            selected.Add("typed_consequences");
        }

        return selected
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static bool ContainsAny(string text, params string[] needles) =>
        needles.Any(needle => text.Contains(needle, StringComparison.OrdinalIgnoreCase));
}

public sealed class DeterministicDialogueParserRouter : IDialogueParserRouter
{
    public static readonly DeterministicDialogueParserRouter Instance = new();

    private DeterministicDialogueParserRouter()
    {
    }

    public string Name => "deterministic-dialogue-parser-router";

    public Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken)
    {
        var selected = DialogueParserCapabilityCatalog.DeterministicCapabilityIds(request);
        return Task.FromResult(new DialogueParserRouteResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            HasMechanics: selected.Count > 0,
            SelectedCapabilityIds: selected,
            Reason: selected.Count > 0 ? "deterministic parser capability route" : "no mechanical parser capabilities detected"));
    }
}

public sealed class NullDialogueParserRouter : IDialogueParserRouter
{
    public static readonly NullDialogueParserRouter Instance = new();

    private NullDialogueParserRouter()
    {
    }

    public string Name => "none";

    public Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueParserRouteResult(
            Name,
            "",
            TechnicalFailure: true,
            Error: "No dialogue parser router is configured.",
            HasMechanics: false,
            SelectedCapabilityIds: Array.Empty<string>()));
}

public sealed class ClaimExtractorCompatibilityDialogueParserRouter : IDialogueParserRouter
{
    public static readonly ClaimExtractorCompatibilityDialogueParserRouter Instance = new();

    private static readonly IReadOnlyList<string> CapabilityIds = new[]
    {
        "claims",
        "promises",
        "canon",
        "bond_want",
    };

    private ClaimExtractorCompatibilityDialogueParserRouter()
    {
    }

    public string Name => "claim-extractor-compatibility-parser-router";

    public Task<DialogueParserRouteResult> RouteAsync(
        DialogueParserRouteRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueParserRouteResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            HasMechanics: true,
            SelectedCapabilityIds: CapabilityIds,
            Reason: "legacy claim extractor compatibility route"));
}

public sealed record DialogueClaimProposal(
    string Text,
    string Category,
    string Subject,
    int Salience = 1,
    int Confidence = 50,
    bool PlayerVisible = false,
    bool BindAsPromise = false,
    string PromiseKind = "rumor",
    string? RealizationKind = null,
    string? TriggerHint = null,
    string? ClaimedPlace = null,
    string? TargetEntityId = null,
    string? MerchantId = null,
    string? ItemName = null,
    bool PlayerAuthored = false,
    IReadOnlyList<string>? Tags = null,
    bool UpdateBond = false,
    int LoyaltyDelta = 0,
    int FearDelta = 0,
    int AdmirationDelta = 0,
    int ResentmentDelta = 0,
    string? BondPosture = null,
    bool BindAsCanon = false,
    string? CanonKind = null,
    string? CanonSummary = null);

public sealed record DialogueClaimExtractionResult(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    IReadOnlyList<DialogueClaimProposal> Claims);

public sealed record DialogueParseResult(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    DialogueProposalSet? Proposals);

public interface IDialogueClaimExtractor
{
    string Name { get; }

    bool RequiresSpokenTextSupport => true;

    Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken);
}

public interface IDialogueParser
{
    string Name { get; }

    bool RequiresSpokenTextSupport => true;

    Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken);
}

public sealed class DialogueClaimExtractorParserAdapter : IDialogueParser
{
    private readonly IDialogueClaimExtractor _extractor;

    public DialogueClaimExtractorParserAdapter(IDialogueClaimExtractor extractor)
    {
        _extractor = extractor;
    }

    public string Name => _extractor.Name;

    public bool RequiresSpokenTextSupport => _extractor.RequiresSpokenTextSupport;

    public async Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _extractor.ExtractAsync(request, cancellationToken);
        return new DialogueParseResult(
            result.Provider,
            result.RawText,
            result.TechnicalFailure,
            result.Error,
            new DialogueProposalSet(Claims: result.Claims));
    }
}

public sealed class NullDialogueClaimExtractor : IDialogueClaimExtractor, IDialogueParser
{
    public static readonly NullDialogueClaimExtractor Instance = new();

    private NullDialogueClaimExtractor()
    {
    }

    public string Name => "none";

    public bool RequiresSpokenTextSupport => false;

    public Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueClaimExtractionResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            Claims: Array.Empty<DialogueClaimProposal>()));

    public Task<DialogueParseResult> ParseAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueParseResult(
            Name,
            "",
            TechnicalFailure: false,
            Error: null,
            Proposals: null));
}
