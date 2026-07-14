namespace Sorcerer.Core.Results;

/// <summary>
/// The one stable failure vocabulary (Phase 1.3). Command, reference, and validation boundaries tag
/// a failure with one of these codes so GUI hints, CLI JSON, transcripts, and resolver feedback all
/// read the same distinct family instead of matching on ad-hoc message text. Codes are stable
/// strings (not an enum) so serialized results and transcripts stay readable and forward-compatible;
/// the precise, in-fiction player message rides alongside the code as the human-facing half.
///
/// <para>
/// Turn semantics: <see cref="ProviderFailure"/> is the technical-failure family and never consumes
/// a turn; <see cref="Rejected"/> is an intentional in-fiction rejection and does consume a turn.
/// The remaining codes are eligibility/targeting failures caught before the action commits and are
/// non-turn-consuming.
/// </para>
/// </summary>
public static class FailureCode
{
    /// <summary>No such target is present or visible.</summary>
    public const string MissingTarget = "missing_target";

    /// <summary>More than one candidate matches; the player must disambiguate.</summary>
    public const string AmbiguousTarget = "ambiguous_target";

    /// <summary>A target exists but is the wrong kind for this action.</summary>
    public const string WrongType = "wrong_type";

    /// <summary>A referenced id no longer exists (moved out of view, died, or was consumed).</summary>
    public const string StaleReference = "stale_reference";

    /// <summary>The target or point is beyond reach or off the current map.</summary>
    public const string OutOfRange = "out_of_range";

    /// <summary>Line of sight or the path to the target is blocked.</summary>
    public const string BlockedLine = "blocked_line";

    /// <summary>The action was refused because the item is protected.</summary>
    public const string ProtectedItem = "protected_item";

    /// <summary>A required resource or item cost is not available.</summary>
    public const string UnpaidCost = "unpaid_cost";

    /// <summary>The action, consequence, or selector is not supported.</summary>
    public const string Unsupported = "unsupported";

    /// <summary>The input could not be parsed into a valid target.</summary>
    public const string Malformed = "malformed";

    /// <summary>The action needs a selected target and none is set.</summary>
    public const string NoSelection = "no_selection";

    /// <summary>A model/provider call failed technically. Never consumes a turn.</summary>
    public const string ProviderFailure = "provider_failure";

    /// <summary>An intentional, in-fiction rejection of a valid request. Consumes a turn.</summary>
    public const string Rejected = "rejected";
}
