using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Magic;
using Sorcerer.Core.Results;
using Sorcerer.Core.Scenarios;
using Sorcerer.Core.Views;
using Sorcerer.Core.World;

namespace Sorcerer.Core;

public sealed class GameSession
{
    private readonly IWildMagicController _magic;

    public GameSession(GameState state, IWildMagicController? magic = null)
    {
        Engine = new GameEngine(state);
        _magic = magic ?? NullWildMagicController.Instance;
    }

    public GameEngine Engine { get; }

    public static GameSession CreateImperialEncounter(IWildMagicController? magic = null) =>
        new(TestScenarios.ImperialEncounter(), magic);

    public async Task<ActionResult> ExecuteAsync(
        GameCommand command,
        CancellationToken cancellationToken = default)
    {
        return command switch
        {
            MoveCommand move => Engine.MoveControlled(move.Direction),
            WaitCommand => Engine.Wait(),
            InspectCommand => Engine.Inspect(),
            CastCommand cast => await _magic.CastAsync(Engine, cast, cancellationToken),
            TargetCommand target => SetTarget(target),
            QuitCommand => Quit(),
            UnknownCommand unknown => ActionResult.Simple(
                "unknown",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                $"Unknown command: {unknown.Text}"),
            _ => ActionResult.Simple(
                "unknown",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "Unknown command."),
        };
    }

    public GameView View() => Engine.View();

    public AgentObservation Observation(bool debug = false) => Engine.Observation(debug);

    private ActionResult SetTarget(TargetCommand command)
    {
        var turn = Engine.State.Turn;
        if (!Engine.InBounds(command.Position))
        {
            return ActionResult.Simple(
                "target",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "Target is outside the encounter.");
        }

        Engine.State.SelectedTarget = command.Position;
        var message = $"Target set to {command.Position.X},{command.Position.Y}.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "target",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult Quit() =>
        new()
        {
            Action = "quit",
            Success = true,
            ConsumedTurn = false,
            TurnBefore = Engine.State.Turn,
            TurnAfter = Engine.State.Turn,
            Messages = new[] { "Leaving Sorcerer." },
            ShouldQuit = true,
        };
}

