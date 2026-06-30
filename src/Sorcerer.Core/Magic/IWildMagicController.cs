using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Results;

namespace Sorcerer.Core.Magic;

public interface IWildMagicController
{
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
        CancellationToken cancellationToken)
    {
        var message = "Wild magic has not been wired into this session.";
        engine.AddMessage(message);
        return Task.FromResult(new ActionResult
        {
            Action = "cast",
            Success = false,
            ConsumedTurn = false,
            TurnBefore = engine.State.Turn,
            TurnAfter = engine.State.Turn,
            Messages = new[] { message },
            TechnicalFailure = true,
            Magic = new MagicResolutionRecord(
                "none",
                Accepted: false,
                TechnicalFailure: true,
                EffectTypes: Array.Empty<string>(),
                Error: message),
        });
    }
}

