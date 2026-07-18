using Sorcerer.Core.World;

namespace Sorcerer.Core.Dialogue;

public sealed record PreparedDialogueTurn(
    int TurnBefore,
    string PlayerText,
    string SpeakerId,
    string SpeakerName,
    IReadOnlyList<string> SpeakerTags,
    string ListenerSoulId,
    bool SpeakerHostile,
    string? SpeakerProfile,
    string? SpeakerFaction,
    string? BondSummary,
    string? SpeakerWant = null);

public sealed record DialoguePreparation(
    PreparedDialogueTurn? Turn,
    Sorcerer.Core.Results.ActionResult? ImmediateResult);

public sealed record DialogueParticipantCard(
    string EntityId,
    string Name,
    IReadOnlyList<string> Tags,
    string? Faction = null,
    string? Profile = null,
    string? Description = null,
    string? BondSummary = null,
    IReadOnlyList<string>? Inventory = null,
    IReadOnlyList<string>? Wares = null,
    IReadOnlyList<string>? Services = null,
    string? Want = null);

public sealed record DialogueSceneCard(
    string RegionId,
    string CurrentZoneId,
    IReadOnlyList<string> VisibleEntities,
    IReadOnlyList<string> NearbyItems,
    IReadOnlyList<string> RecentEvents,
    string? RegionVoice = null,
    IReadOnlyList<string>? Scenery = null);

public sealed record DialogueContextCardPayload(
    string Id,
    string Kind,
    string Title,
    string Summary,
    IReadOnlyList<string> Lines,
    IReadOnlyList<string>? Topics = null,
    bool Truncated = false);

public sealed record DialogueContextCardSpec(
    string Id,
    string Topic,
    string Kind,
    string Title,
    string Summary);

public sealed record DialogueRouteCandidate(
    string Id,
    string Kind,
    string Title,
    string Summary,
    IReadOnlyList<string>? Topics = null);

public sealed record DialogueRouteRequest(
    int Turn,
    string PlayerText,
    DialogueParticipantCard Speaker,
    DialogueParticipantCard Listener,
    DialogueSceneCard Scene,
    IReadOnlyList<DialogueRouteCandidate> AvailableCards);

public sealed record DialogueRouteResult(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    IReadOnlyList<string> SelectedCardIds,
    string? Reason = null);

public interface IDialogueRouter
{
    string Name { get; }

    Task<DialogueRouteResult> RouteAsync(
        DialogueRouteRequest request,
        CancellationToken cancellationToken);
}

public sealed record DialogueRequest(
    int Turn,
    string PlayerText,
    DialogueParticipantCard Speaker,
    DialogueParticipantCard Listener,
    DialogueSceneCard Scene,
    IReadOnlyList<string> RecentMemories,
    IReadOnlyList<string> RecentClaims,
    IReadOnlyList<string> CapabilityCards,
    IReadOnlyList<string>? RecentRumors = null,
    IReadOnlyList<DialogueContextCardPayload>? ContextCards = null,
    IReadOnlyList<string>? SelectedContextCardIds = null,
    IReadOnlyList<string>? RecentDialogue = null,
    IReadOnlyList<DialogueParticipantCard>? Participants = null);

public sealed record DialogueProposalSet(
    IReadOnlyList<DialogueClaimProposal>? Claims = null,
    IReadOnlyList<DialogueMemoryProposal>? Memories = null,
    DialogueBondProposal? Bond = null,
    DialogueWantProposal? Want = null,
    IReadOnlyList<DialogueActionProposal>? Actions = null,
    BargainOffer? Bargain = null);

public sealed record DialogueResponse(
    string SpokenText,
    string? Delivery = null,
    string? Intent = null,
    DialogueProposalSet? Proposals = null,
    IReadOnlyList<DialogueUtteranceResponse>? Utterances = null);

public sealed record DialogueUtteranceResponse(
    string SpeakerEntityId,
    string SpokenText,
    string? Delivery = null,
    string? Intent = null,
    DialogueProposalSet? Proposals = null);

public sealed record DialogueMemoryProposal(
    string OwnerEntityId,
    string Text,
    string Provenance = "conversation",
    int Salience = 1,
    bool Shareable = true);

public sealed record DialogueBondProposal(
    string EntityId,
    int LoyaltyDelta = 0,
    int FearDelta = 0,
    int AdmirationDelta = 0,
    int ResentmentDelta = 0,
    string? Posture = null,
    string? Reason = null);

public sealed record DialogueWantProposal(
    string EntityId,
    string? Text = null,
    int? Salience = null,
    string? Status = null,
    string? Stakes = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? AddTags = null,
    IReadOnlyList<string>? RemoveTags = null,
    string? Reason = null);

public sealed record DialogueActionProposal(
    string Type,
    string? TargetEntityId = null,
    string? ItemName = null,
    string? Reason = null,
    int? Quantity = null,
    int? Gold = null,
    string? Name = null,
    string? Description = null,
    string? FixtureType = null,
    string? Material = null,
    IReadOnlyList<string>? Tags = null,
    IReadOnlyList<string>? InteractableVerbs = null,
    int? X = null,
    int? Y = null,
    bool? BlocksMovement = null,
    bool? BlocksSight = null,
    string? ServiceId = null,
    string? EffectKind = null,
    string? TargetHint = null,
    string? ItemCost = null,
    int? GoldCost = null,
    string? PromiseKind = null,
    string? PromiseText = null,
    string? TriggerHint = null,
    string? RealizationKind = null,
    string? ClaimedPlace = null,
    string? Subject = null,
    bool? PlayerVisible = null,
    int? Salience = null,
    bool? AutoBind = null,
    bool? StackExisting = null,
    string? WantStatusOnComplete = null,
    string? WantStakesOnComplete = null,
    IReadOnlyList<string>? WantAddTagsOnComplete = null,
    IReadOnlyList<string>? WantRemoveTagsOnComplete = null,
    string? CanonKind = null,
    string? CanonText = null,
    string? CanonSummary = null,
    string? ConsequenceType = null,
    string? ConsequenceTiming = null,
    string? ConsequenceVisibility = null,
    int? ConsequenceConfidence = null,
    IReadOnlyDictionary<string, object?>? ConsequencePayload = null);

public sealed record DialogueProviderResult(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    DialogueResponse? Response,
    Sorcerer.Core.Telemetry.ProviderCallStats? Stats = null);

public interface IDialogueProvider
{
    string Name { get; }

    Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken);
}

public sealed record DialogueAuditEntry(
    DateTimeOffset Timestamp,
    string Provider,
    string SpeakerId,
    string SpeakerName,
    string PlayerText,
    DialogueRequest Request,
    string RawText,
    DialogueResponse? Response,
    bool TechnicalFailure,
    string? Error,
    string ResultAction,
    bool ResultSuccess,
    bool ConsumedTurn,
    IReadOnlyList<string> ValidationIssues,
    IReadOnlyList<string> DeltaOperations,
    Sorcerer.Core.Results.DialogueRouteRecord? Route = null,
    Sorcerer.Core.Telemetry.ProviderCallStats? ProviderStats = null);

public interface IDialogueAuditSink
{
    void Record(DialogueAuditEntry entry);
}

public sealed class NullDialogueAuditSink : IDialogueAuditSink
{
    public static readonly NullDialogueAuditSink Instance = new();

    private NullDialogueAuditSink()
    {
    }

    public void Record(DialogueAuditEntry entry)
    {
    }
}

public sealed class NullDialogueProvider : IDialogueProvider
{
    public static readonly NullDialogueProvider Instance = new();

    private NullDialogueProvider()
    {
    }

    public string Name => "none";

    public Task<DialogueProviderResult> ResolveAsync(
        DialogueRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueProviderResult(
            Name,
            "",
            TechnicalFailure: true,
            Error: "No dialogue provider is configured.",
            Response: null));
}

public sealed class NullDialogueRouter : IDialogueRouter
{
    public static readonly NullDialogueRouter Instance = new();

    private NullDialogueRouter()
    {
    }

    public string Name => "none";

    public Task<DialogueRouteResult> RouteAsync(
        DialogueRouteRequest request,
        CancellationToken cancellationToken) =>
        Task.FromResult(new DialogueRouteResult(
            Name,
            "",
            TechnicalFailure: true,
            Error: "No dialogue router is configured.",
            SelectedCardIds: Array.Empty<string>()));
}
