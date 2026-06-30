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
    private PendingCast? _pendingCast;
    private int _pendingCastSerial;

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
        if (_pendingCast is not null && !CanExecuteDuringPendingCast(command))
        {
            return ActionResult.Simple(
                "pending_cast",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "A spell is waiting to resolve; use await_cast or cancel_cast.");
        }

        var result = command switch
        {
            MoveCommand move => Engine.MoveControlled(move.Direction),
            WaitCommand => Engine.Wait(),
            InspectCommand => Engine.Inspect(),
            CastCommand cast => await _magic.CastAsync(Engine, cast, cancellationToken),
            BeginCastCommand cast => BeginCast(cast),
            AwaitCastCommand => await AwaitCast(cancellationToken),
            CancelCastCommand => CancelCast(),
            TargetCommand target => SetTarget(target),
            ClearTargetCommand => ClearTarget(),
            MapCommand => Engine.Inspect(),
            PickupCommand pickup => Engine.Pickup(pickup.Target),
            DropCommand drop => Engine.DropItem(drop.Item),
            UseItemCommand use => Engine.UseItem(use.Item),
            EquipCommand equip => Engine.EquipItem(equip.Item),
            UnequipCommand unequip => Engine.UnequipItem(unequip.SlotOrItem),
            FocusCommand focus => Engine.FocusItem(focus.SlotOrItem),
            UnfocusCommand unfocus => Engine.UnfocusItem(unfocus.SlotOrItem),
            ProtectItemCommand protect => ProtectItem(protect.Item, protectedState: true),
            UnprotectItemCommand unprotect => ProtectItem(unprotect.Item, protectedState: false),
            ReagentsCommand => Engine.Reagents(),
            JournalCommand => Engine.Journal(),
            TalkCommand talk => Engine.Talk(talk.Text),
            ReadCommand read => Engine.Read(read.Target),
            ExamineCommand examine => Engine.Examine(examine.Target),
            OpenCommand open => Engine.Open(open.Target),
            PossessCommand possess => Engine.Possess(possess.Target),
            StandingCommand => Engine.Standing(),
            FollowersCommand => Engine.Followers(),
            JobsCommand => Engine.Jobs(),
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

        return AddActorTurns(result);
    }

    public GameView View() => Engine.View();

    public AgentObservation Observation(bool debug = false)
    {
        var observation = Engine.Observation(debug);
        return observation with
        {
            PendingCast = _pendingCast is null
                ? null
                : new PendingCastView(_pendingCast.Id, _pendingCast.Command.Text, "waiting"),
        };
    }

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

    private ActionResult BeginCast(BeginCastCommand command)
    {
        var turn = Engine.State.Turn;
        if (string.IsNullOrWhiteSpace(command.Text))
        {
            return ActionResult.Simple(
                "begin_cast",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "No spell was spoken.");
        }

        var id = $"cast_{++_pendingCastSerial}";
        _pendingCast = new PendingCast(
            id,
            new CastCommand(command.Text, command.Performance ?? CastPerformance.Neutral));
        var message = $"Pending cast {id} is waiting to resolve.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "begin_cast",
            success: true,
            consumedTurn: false,
            turn,
            turn,
            message);
    }

    private async Task<ActionResult> AwaitCast(CancellationToken cancellationToken)
    {
        var pending = _pendingCast;
        if (pending is null)
        {
            return ActionResult.Simple(
                "await_cast",
                success: false,
                consumedTurn: false,
                Engine.State.Turn,
                Engine.State.Turn,
                "No spell is waiting to resolve.");
        }

        _pendingCast = null;
        return await _magic.CastAsync(Engine, pending.Command, cancellationToken);
    }

    private ActionResult CancelCast()
    {
        var turn = Engine.State.Turn;
        if (_pendingCast is null)
        {
            return ActionResult.Simple(
                "cancel_cast",
                success: false,
                consumedTurn: false,
                turn,
                turn,
                "No spell is waiting to cancel.");
        }

        var id = _pendingCast.Id;
        _pendingCast = null;
        var message = $"Pending cast {id} dissipates.";
        Engine.AddMessage(message);
        return ActionResult.Simple(
            "cancel_cast",
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
            "Commands: inspect, map, move, wait, target, pickup, drop, use, equip, focus, open, read, examine, talk, possess, cast, begin_cast, await_cast, cancel_cast, protect, unprotect, reagents, journal, standing, followers, jobs, quit.");

    private ActionResult AddActorTurns(ActionResult result)
    {
        if (!result.ConsumedTurn || result.ShouldQuit)
        {
            return result;
        }

        var deltas = Engine.RunActorTurns();
        if (deltas.Count == 0)
        {
            return result;
        }

        return result with
        {
            Messages = result.Messages.Concat(deltas.Select(delta => delta.Summary)).ToArray(),
            Deltas = result.Deltas.Concat(deltas).ToArray(),
        };
    }

    private static bool CanExecuteDuringPendingCast(GameCommand command) =>
        command is AwaitCastCommand
            or CancelCastCommand
            or InspectCommand
            or MapCommand
            or HelpCommand
            or QuitCommand;

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

    private sealed record PendingCast(string Id, CastCommand Command);
}
