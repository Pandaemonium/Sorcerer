namespace Sorcerer.Core.World;

/// <summary>
/// Durable, player-visible progress for an intentional overland journey.  Empty map legs may be
/// compressed, but the route can only spend this many pauses on ambient scenes; authored promise
/// payoffs, destinations, and active pursuers always interrupt independently of that budget.
/// </summary>
public sealed record JourneyPlan(
    string DestinationId,
    string DestinationName,
    string DestinationZoneId,
    int SceneBudget = 2,
    int ScenesSpent = 0,
    int ZonesCrossed = 0,
    int StartedTurn = 0);

public static class BargainTermKinds
{
    public const string Currency = "currency";
    public const string Item = "item";
    public const string Service = "service";
    public const string Standing = "standing";
    public const string Concession = "concession";
    public const string Deadline = "deadline";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Currency, Item, Service, Standing, Concession, Deadline,
    };
}

/// <summary>A concrete, engine-verifiable term.  Text explains it; the other fields enforce it.</summary>
public sealed record BargainTerm(
    string Id,
    string Kind,
    string Text,
    int Quantity = 1,
    string? ResourceId = null,
    string? FactionId = null,
    string? StandingAxis = null,
    int StandingDelta = 0,
    int? DueTurn = null,
    string Status = "pending");

public sealed record BargainOption(
    string Id,
    string Label,
    IReadOnlyList<BargainTerm> Terms);

public sealed record BargainOffer(
    string ClaimantEntityId,
    string Summary,
    IReadOnlyList<BargainOption> Options,
    int CreatedTurn,
    int? ExpiresTurn = null);

public sealed record BargainAgreement(
    string ClaimantEntityId,
    string OptionId,
    IReadOnlyList<BargainTerm> Terms,
    int AcceptedTurn,
    string Status = "active",
    string? ActorSoulId = null);

public sealed record WorldPromise(
    string Id,
    string Kind,
    string Text,
    string Status,
    bool PlayerVisible,
    string Source = "unknown",
    int Salience = 1,
    string Subject = "",
    string? ClaimedPlace = null,
    string? BoundPlace = null,
    string? BoundTargetId = null,
    string? TriggerHint = null,
    string? RealizationKind = null,
    string? RealizedIn = null,
    int Stacks = 1,
    string? LastEligibilityFailure = null,
    string? LastEligibilityContext = null,
    int? LastEligibilityTurn = null,
    string? SourceClaimId = null,
    string? SourceSpeakerId = null,
    string? SourceListenerSoulId = null,
    int? SourceConfidence = null,
    JourneyPlan? Journey = null,
    BargainOffer? BargainOffer = null,
    BargainAgreement? BargainAgreement = null,
    string? CostProfileId = null);

public sealed class PromiseLedger
{
    private readonly List<WorldPromise> _promises = new();

    public IReadOnlyList<WorldPromise> Promises => _promises;

    public WorldPromise Add(
        string kind,
        string text,
        bool playerVisible = false,
        string source = "unknown",
        int salience = 1,
        string subject = "",
        string? claimedPlace = null,
        string? triggerHint = null,
        string? realizationKind = null,
        string? sourceClaimId = null,
        string? sourceSpeakerId = null,
        string? sourceListenerSoulId = null,
        int? sourceConfidence = null)
    {
        var promise = new WorldPromise(
            Id: $"promise_{_promises.Count + 1}",
            Kind: string.IsNullOrWhiteSpace(kind) ? "omen" : kind.Trim(),
            Text: text.Trim(),
            Status: "unbound",
            PlayerVisible: playerVisible,
            Source: string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim(),
            Salience: Math.Max(1, salience),
            Subject: subject.Trim(),
            ClaimedPlace: string.IsNullOrWhiteSpace(claimedPlace) ? null : claimedPlace.Trim(),
            TriggerHint: string.IsNullOrWhiteSpace(triggerHint) ? null : triggerHint.Trim(),
            RealizationKind: string.IsNullOrWhiteSpace(realizationKind) ? null : realizationKind.Trim(),
            SourceClaimId: string.IsNullOrWhiteSpace(sourceClaimId) ? null : sourceClaimId.Trim(),
            SourceSpeakerId: string.IsNullOrWhiteSpace(sourceSpeakerId) ? null : sourceSpeakerId.Trim(),
            SourceListenerSoulId: string.IsNullOrWhiteSpace(sourceListenerSoulId) ? null : sourceListenerSoulId.Trim(),
            SourceConfidence: sourceConfidence is null ? null : Math.Clamp(sourceConfidence.Value, 0, 100));

        _promises.Add(promise);
        return promise;
    }

    public WorldPromise? Bind(
        string id,
        string? boundPlace,
        string? boundTargetId,
        string? triggerHint = null,
        string? realizationKind = null)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var existing = _promises[index];
        var updated = existing with
        {
            Status = "bound",
            BoundPlace = string.IsNullOrWhiteSpace(boundPlace) ? existing.BoundPlace : boundPlace.Trim(),
            BoundTargetId = string.IsNullOrWhiteSpace(boundTargetId) ? existing.BoundTargetId : boundTargetId.Trim(),
            TriggerHint = string.IsNullOrWhiteSpace(triggerHint) ? existing.TriggerHint : triggerHint.Trim(),
            RealizationKind = string.IsNullOrWhiteSpace(realizationKind) ? existing.RealizationKind : realizationKind.Trim(),
        };
        _promises[index] = updated;
        return updated;
    }

    /// <summary>Re-anchors where the promise claims to pay off — used when a payoff moves
    /// behind a threshold (e.g. a fetch objective deferred into an interior zone).</summary>
    public WorldPromise? SetClaimedPlace(string id, string claimedPlace)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || string.IsNullOrWhiteSpace(claimedPlace))
        {
            return null;
        }

        var updated = _promises[index] with { ClaimedPlace = claimedPlace.Trim() };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetStatus(string id, string status, string? realizedIn = null)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with
        {
            Status = status,
            RealizedIn = realizedIn ?? _promises[index].RealizedIn,
        };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetEligibilityFailure(
        string id,
        string? failure,
        string? context,
        int? turn)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with
        {
            LastEligibilityFailure = string.IsNullOrWhiteSpace(failure) ? null : failure.Trim(),
            LastEligibilityContext = string.IsNullOrWhiteSpace(context) ? null : context.Trim(),
            LastEligibilityTurn = string.IsNullOrWhiteSpace(failure) ? null : turn,
        };
        _promises[index] = updated;
        return updated;
    }

    /// <summary>
    /// Finds an active (not cleared/realized) promise of the given kind whose text and bound
    /// target already match, so a repeat curse can stack instead of duplicating.
    /// </summary>
    public WorldPromise? FindActive(string kind, string text, string? boundTargetId) =>
        _promises.FirstOrDefault(promise =>
            promise.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
            && !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
            && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
            && promise.Text.Equals(text, StringComparison.OrdinalIgnoreCase)
            && string.Equals(promise.BoundTargetId, boundTargetId, StringComparison.OrdinalIgnoreCase));

    public WorldPromise Stack(string id)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Promise {id} does not exist.");
        }

        var updated = _promises[index] with { Stacks = _promises[index].Stacks + 1 };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetJourney(string id, JourneyPlan journey, string? status = null)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with
        {
            Journey = journey,
            Status = string.IsNullOrWhiteSpace(status) ? _promises[index].Status : status.Trim(),
            BoundPlace = journey.DestinationZoneId,
        };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetBargainOffer(string id, BargainOffer? offer)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with { BargainOffer = offer };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetBargainAgreement(string id, BargainAgreement? agreement, string? status = null)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with
        {
            BargainAgreement = agreement,
            Status = string.IsNullOrWhiteSpace(status) ? _promises[index].Status : status.Trim(),
        };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetCostProfile(string id, string? profileId)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with
        {
            CostProfileId = string.IsNullOrWhiteSpace(profileId) ? null : profileId.Trim(),
        };
        _promises[index] = updated;
        return updated;
    }

    public IReadOnlyList<WorldPromise> Snapshot() => _promises.ToArray();

    public void ReplaceAll(IEnumerable<WorldPromise> promises)
    {
        _promises.Clear();
        _promises.AddRange(promises);
    }
}
