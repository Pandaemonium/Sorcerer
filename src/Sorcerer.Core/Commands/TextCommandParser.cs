using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.Commands;

public static class TextCommandParser
{
    public static GameCommand Parse(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return new UnknownCommand(text);
        }

        var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        var verb = parts[0].ToLowerInvariant();
        var rest = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        return verb switch
        {
            "n" or "north" => new MoveCommand(Direction.North),
            "s" or "south" => new MoveCommand(Direction.South),
            "e" or "east" => new MoveCommand(Direction.East),
            "w" or "west" => new MoveCommand(Direction.West),
            "ne" or "northeast" => new MoveCommand(Direction.NorthEast),
            "nw" or "northwest" => new MoveCommand(Direction.NorthWest),
            "se" or "southeast" => new MoveCommand(Direction.SouthEast),
            "sw" or "southwest" => new MoveCommand(Direction.SouthWest),
            "move" => ParseMove(rest),
            "wait" or "." => new WaitCommand(),
            "inspect" or "look" => new InspectCommand(),
            "map" => ParseMap(rest),
            "cast" => new CastCommand(rest, CastPerformance.Neutral),
            "begin_cast" or "submit_cast" or "start_cast" => new BeginCastCommand(rest, CastPerformance.Neutral),
            "await_cast" or "resolve_cast" => new AwaitCastCommand(),
            "cancel_cast" => new CancelCastCommand(),
            "target" => ParseTarget(rest),
            "untarget" or "cleartarget" => new ClearTargetCommand(),
            "pickup" or "get" => new PickupCommand(NullIfEmpty(rest)),
            "drop" => new DropCommand(rest),
            "use" => new UseItemCommand(rest),
            "equip" => new EquipCommand(rest),
            "unequip" => new UnequipCommand(rest),
            "focus" => new FocusCommand(rest),
            "unfocus" => new UnfocusCommand(NullIfEmpty(rest)),
            "protect" => new ProtectItemCommand(rest),
            "unprotect" => new UnprotectItemCommand(rest),
            "reagents" => new ReagentsCommand(),
            "journal" or "promises" or "rumors" => new JournalCommand(),
            "talk" or "say" or "speak" => new TalkCommand(rest),
            "read" => new ReadCommand(NullIfEmpty(rest)),
            "examine" or "study" => new ExamineCommand(NullIfEmpty(rest)),
            "open" => new OpenCommand(NullIfEmpty(rest)),
            "possess" => new PossessCommand(NullIfEmpty(rest)),
            "standing" => new StandingCommand(),
            "followers" => new FollowersCommand(),
            "jobs" => new JobsCommand(),
            "help" or "?" => new HelpCommand(),
            "quit" or "exit" => new QuitCommand(),
            _ => new UnknownCommand(text),
        };
    }

    private static GameCommand ParseMove(string text) =>
        Parse(text) is MoveCommand move ? move : new UnknownCommand($"move {text}");

    private static GameCommand ParseTarget(string text)
    {
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2
            && int.TryParse(parts[0], out var x)
            && int.TryParse(parts[1], out var y))
        {
            return new TargetCommand(new GridPoint(x, y));
        }

        return new UnknownCommand($"target {text}");
    }

    private static GameCommand ParseMap(string text) =>
        int.TryParse(text, out var radius) ? new MapCommand(radius) : new MapCommand();

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
