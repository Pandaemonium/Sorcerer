using Sorcerer.Core.Commands;
using Sorcerer.Core.Consequences;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Results;
using Sorcerer.Core.Telemetry;

namespace Sorcerer.Core.Magic;

public sealed record MaterializedMagicResolution(
    string Provider,
    string SpellText,
    CastPerformance Performance,
    string RawText,
    bool Accepted,
    bool TechnicalFailure,
    string? Error,
    IReadOnlyList<string> EffectTypes,
    string? ResolvedMagicJson,
    ProviderCallStats? ProviderStats = null,
    // Which deed the apply boundary records for a successful cast. Wild casts keep the
    // default; charter casts pass "charter_magic" so witnesses read them as plausibly
    // licensed work instead of uncanny wild magic (docs/CHARTER_MAGIC.md, witnessing).
    string DeedKind = "wild_magic");

public interface IWildMagicController
{
    Task<MaterializedMagicResolution> ResolveAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken);

    ActionResult ApplyResolved(
        GameEngine engine,
        MaterializedMagicResolution resolution);

    Task<ActionResult> CastAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken);
}

public sealed class NullWildMagicController : IWildMagicController
{
    public static NullWildMagicController Instance { get; } = new();

    private NullWildMagicController()
    {
    }

    public Task<ActionResult> CastAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(ApplyResolved(
            engine,
            new MaterializedMagicResolution(
                "none",
                command.Text,
                command.Performance ?? CastPerformance.Neutral,
                "",
                Accepted: false,
                TechnicalFailure: true,
                Error: "Wild magic has not been wired into this session.",
                EffectTypes: Array.Empty<string>(),
                ResolvedMagicJson: null)));

    public Task<MaterializedMagicResolution> ResolveAsync(
        GameEngine engine,
        CastCommand command,
        CancellationToken cancellationToken) =>
        Task.FromResult(new MaterializedMagicResolution(
            "none",
            command.Text,
            command.Performance ?? CastPerformance.Neutral,
            "",
            Accepted: false,
            TechnicalFailure: true,
            Error: "Wild magic has not been wired into this session.",
            EffectTypes: Array.Empty<string>(),
            ResolvedMagicJson: null));

    public ActionResult ApplyResolved(
        GameEngine engine,
        MaterializedMagicResolution resolution)
    {
        var message = resolution.Error ?? "Wild magic has not been wired into this session.";
        var applied = engine.ApplyConsequence(WorldConsequence.Message(
            "wild_magic",
            message,
            targetEntityId: engine.State.ControlledEntityId.Value,
            visibility: WorldConsequenceVisibility.Message,
            sourceEntityId: engine.State.ControlledEntityId.Value,
            evidence: message,
            reason: "Wild magic controller is absent.",
            operation: "wildMagicTechnicalFailure",
            details: new Dictionary<string, object?>
            {
                ["playerVisible"] = true,
            }));
        return new ActionResult
        {
            Action = "cast",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = engine.State.Turn,
            TurnAfter = engine.State.Turn,
            Messages = applied.Messages.Count == 0 ? new[] { message } : applied.Messages,
            Deltas = applied.Deltas,
            TechnicalFailure = true,
            FailureCode = Sorcerer.Core.Results.FailureCode.ProviderFailure,
            Magic = new MagicResolutionRecord(
                "none",
                Accepted: false,
                TechnicalFailure: true,
                EffectTypes: Array.Empty<string>(),
                Error: message),
        };
    }
}
