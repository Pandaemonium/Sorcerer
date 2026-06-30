using Sorcerer.Core.Commands;
using Sorcerer.Core.Engine;
using Sorcerer.Core.Entities;
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
            ClearTargetCommand => ClearTarget(),
            MapCommand => Engine.Inspect(),
            PickupCommand => Engine.Unsupported("pickup"),
            DropCommand => Engine.Unsupported("drop"),
            UseItemCommand => Engine.Unsupported("use"),
            EquipCommand => Engine.Unsupported("equip"),
            UnequipCommand => Engine.Unsupported("unequip"),
            FocusCommand => Engine.Unsupported("focus"),
            UnfocusCommand => Engine.Unsupported("unfocus"),
            ProtectItemCommand protect => ProtectItem(protect.Item, protectedState: true),
            UnprotectItemCommand unprotect => ProtectItem(unprotect.Item, protectedState: false),
            ReagentsCommand => Engine.Inspect(),
            JournalCommand => Engine.Unsupported("journal"),
            TalkCommand => Engine.Unsupported("talk", free: false),
            ReadCommand => Engine.Unsupported("read", free: false),
            ExamineCommand => Engine.Unsupported("examine", free: false),
            OpenCommand => Engine.Unsupported("open"),
            PossessCommand => Engine.Unsupported("possess", free: false),
            StandingCommand => Engine.Unsupported("standing"),
            FollowersCommand => Engine.Unsupported("followers"),
            JobsCommand => Engine.Unsupported("jobs"),
            HelpCommand => Help(),
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

    private ActionResult ClearTarget()
    {
        var turn = Engine.State.Turn;
        Engine.State.SelectedTarget = null;
        var message = "Target cleared.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "target",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult ProtectItem(string item, bool protectedState)
    {
        var turn = Engine.State.Turn;
        var actor = Engine.State.ControlledEntity;
        if (!actor.TryGet<InventoryComponent>(out var inventory)
            || !inventory.Items.ContainsKey(item))
        {
            return ActionResult.Simple(
                protectedState ? "protect" : "unprotect",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                $"You are not carrying {item}.");
        }

        if (protectedState)
        {
            inventory.TreasuredItems.Add(item);
        }
        else
        {
            inventory.TreasuredItems.Remove(item);
        }

        var message = protectedState
            ? $"{item} is protected from wild magic costs."
            : $"{item} is available as ordinary spell fuel.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            protectedState ? "protect" : "unprotect",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private ActionResult Help() =>
        ActionResult.Simple(
            "help",
            success: true,
            consumedTurn: false,
            Engine.State.Turn,
            Engine.State.Turn,
            "Commands: inspect, map, move, wait, target, cast, protect, unprotect, reagents, journal, quit.");

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
