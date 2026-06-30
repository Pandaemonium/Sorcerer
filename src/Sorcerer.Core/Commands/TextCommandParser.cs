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
            "cast" => new CastCommand(rest, CastPerformance.Neutral),
            "target" => ParseTarget(rest),
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
}

