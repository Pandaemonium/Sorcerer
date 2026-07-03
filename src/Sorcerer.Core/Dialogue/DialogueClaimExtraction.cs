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
    IReadOnlyList<ClaimRecord> RecentClaims);

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

public interface IDialogueClaimExtractor
{
    string Name { get; }

    bool RequiresSpokenTextSupport => true;

    Task<DialogueClaimExtractionResult> ExtractAsync(
        DialogueClaimRequest request,
        CancellationToken cancellationToken);
}

public sealed class NullDialogueClaimExtractor : IDialogueClaimExtractor
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
}
