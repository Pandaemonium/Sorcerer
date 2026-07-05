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
            "travel" or "go" => ParseTravel(rest),
            "atlas" or "world" => new AtlasCommand(),
            "cast" => new CastCommand(rest, CastPerformance.Neutral),
            "begin_cast" or "submit_cast" or "start_cast" => new BeginCastCommand(rest, CastPerformance.Neutral),
            "await_cast" or "resolve_cast" => ParseAwaitCast(rest),
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
            "wares" or "browse" => new WaresCommand(NullIfEmpty(rest)),
            "buy" => ParseBuy(rest),
            "sell" => ParseSell(rest),
            "services" => new ServicesCommand(NullIfEmpty(rest)),
            "request" or "service" => ParseRequestService(rest),
            "journal" or "promises" => new JournalCommand(),
            "rumors" or "gossip" => new RumorsCommand(),
            "character" or "sheet" or "profile" => new CharacterCommand(),
            "talk" or "say" or "speak" => new TalkCommand(rest),
            "give" or "gift" => ParseGive(rest),
            "recruit" => new RecruitCommand(NullIfEmpty(rest)),
            "bonds" or "bond" => new BondsCommand(NullIfEmpty(rest)),
            "read" => new ReadCommand(NullIfEmpty(rest)),
            "examine" or "study" => new ExamineCommand(NullIfEmpty(rest)),
            "open" => new OpenCommand(NullIfEmpty(rest)),
            "possess" => new PossessCommand(NullIfEmpty(rest)),
            "standing" => new StandingCommand(),
            "followers" => new FollowersCommand(),
            "jobs" => new JobsCommand(),
            "save" => new SaveCommand(DefaultSavePath(rest)),
            "load" => new LoadCommand(DefaultSavePath(rest)),
            "help" or "?" => new HelpCommand(),
            "quit" or "exit" => new QuitCommand(),
            _ => new UnknownCommand(text),
        };
    }

    private static GameCommand ParseMove(string text) =>
        Parse(text) is MoveCommand move ? move : new UnknownCommand($"move {text}");

    // Debug/agent injection of a fixed casting performance: `await_cast <power> <control> <wildness>`.
    // Bare `await_cast` keeps whatever the pending cast carries (neutral by default).
    private static GameCommand ParseAwaitCast(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new AwaitCastCommand();
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 3
            && float.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var power)
            && float.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var control)
            && float.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out var wildness))
        {
            return new AwaitCastCommand(new CastPerformance(
                Played: true,
                PowerModifier: power,
                ControlModifier: control,
                WildnessModifier: wildness,
                Source: "debug_fixed"));
        }

        return new UnknownCommand($"await_cast {text}");
    }

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

    private static GameCommand ParseTravel(string text) =>
        Parse(text) is MoveCommand move ? new TravelCommand(move.Direction) : new UnknownCommand($"travel {text}");

    private static GameCommand ParseGive(string text)
    {
        var marker = text.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return new GiveCommand(text.Trim(), null);
        }

        return new GiveCommand(
            text[..marker].Trim(),
            NullIfEmpty(text[(marker + 4)..]));
    }

    private static GameCommand ParseBuy(string text)
    {
        var marker = text.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return new BuyCommand(text.Trim());
        }

        return new BuyCommand(
            text[..marker].Trim(),
            NullIfEmpty(text[(marker + 6)..]));
    }

    private static GameCommand ParseSell(string text)
    {
        var marker = text.IndexOf(" to ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return new SellCommand(text.Trim());
        }

        return new SellCommand(
            text[..marker].Trim(),
            NullIfEmpty(text[(marker + 4)..]));
    }

    private static GameCommand ParseRequestService(string text)
    {
        var marker = text.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return new RequestServiceCommand(text.Trim());
        }

        return new RequestServiceCommand(
            text[..marker].Trim(),
            NullIfEmpty(text[(marker + 6)..]));
    }

    private static string? NullIfEmpty(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string DefaultSavePath(string value) =>
        string.IsNullOrWhiteSpace(value) ? Path.Combine("runs", "quicksave.json") : value.Trim();
}
