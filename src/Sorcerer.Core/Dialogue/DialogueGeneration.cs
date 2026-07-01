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
    string? BondSummary);

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
    IReadOnlyList<string>? Services = null);

public sealed record DialogueSceneCard(
    string RegionId,
    string CurrentZoneId,
    IReadOnlyList<string> VisibleEntities,
    IReadOnlyList<string> NearbyItems,
    IReadOnlyList<string> RecentEvents);

public sealed record DialogueRequest(
    int Turn,
    string PlayerText,
    DialogueParticipantCard Speaker,
    DialogueParticipantCard Listener,
    DialogueSceneCard Scene,
    IReadOnlyList<string> RecentMemories,
    IReadOnlyList<string> RecentClaims,
    IReadOnlyList<string> CapabilityCards);

public sealed record DialogueProposalSet(
    IReadOnlyList<DialogueClaimProposal>? Claims = null,
    IReadOnlyList<DialogueMemoryProposal>? Memories = null,
    DialogueBondProposal? Bond = null,
    IReadOnlyList<DialogueActionProposal>? Actions = null);

public sealed record DialogueResponse(
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

public sealed record DialogueActionProposal(
    string Type,
    string? TargetEntityId = null,
    string? ItemName = null,
    string? Reason = null);

public sealed record DialogueProviderResult(
    string Provider,
    string RawText,
    bool TechnicalFailure,
    string? Error,
    DialogueResponse? Response);

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
    IReadOnlyList<string> DeltaOperations);

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
