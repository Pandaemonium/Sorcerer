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
            "journey" or "route" => string.IsNullOrWhiteSpace(rest)
                ? new AtlasCommand()
                : new JourneyCommand(rest),
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
            "inventory" or "items" or "inv" => new InventoryCommand(),
            "reagents" => new ReagentsCommand(),
            "threats" or "danger" or "intents" => new ThreatsCommand(),
            "wares" or "browse" => new WaresCommand(NullIfEmpty(rest)),
            "buy" => ParseBuy(rest),
            "sell" => ParseSell(rest),
            "services" => new ServicesCommand(NullIfEmpty(rest)),
            "request" or "service" => ParseRequestService(rest),
            "charter" or "forms" => new CharterCommand(NullIfEmpty(rest)),
            "echoes" or "grimoire" => new EchoesCommand(),
            "echo" => string.IsNullOrWhiteSpace(rest) ? new EchoesCommand() : new EchoCommand(rest.Trim()),
            "journal" or "promises" => new JournalCommand(),
            "rumors" or "gossip" => new RumorsCommand(),
            "character" or "sheet" or "profile" => new CharacterCommand(),
            "talk" or "say" or "speak" => new TalkCommand(rest),
            "group" or "group-talk" or "grouptalk" or "gather" => new GroupTalkCommand(rest),
            "settle" or "agree" or "accept-terms" => new SettleCommand(NullIfEmpty(rest)),
            "bargains" or "terms" => new BargainsCommand(NullIfEmpty(rest)),
            "fulfill" or "perform" => new FulfillCommand(rest),
            "offer" => new OfferCommand(rest),
            "bargain" or "negotiate" => new BargainCommand(NullIfEmpty(rest)),
            "concede" or "yield" => new ConcedeCommand(NullIfEmpty(rest)),
            "intimidate" or "threaten" => new IntimidateCommand(NullIfEmpty(rest)),
            "exchange" or "swap-items" => new ExchangeCommand(rest),
            "cleanse" or "lift-curse" or "unalter" => new CleanseCommand(NullIfEmpty(rest)),
            "brace" or "guard" => new BraceCommand(),
            "counter" or "disrupt" => new CounterCommand(rest),
            "breach" or "force" => new BreachCommand(NullIfEmpty(rest)),
            "forge" => new ForgeCommand(string.IsNullOrWhiteSpace(rest) ? "permit" : rest),
            "give" or "gift" => ParseGive(rest),
            "recruit" => new RecruitCommand(NullIfEmpty(rest)),
            "bonds" or "bond" => new BondsCommand(NullIfEmpty(rest)),
            "read" => new ReadCommand(NullIfEmpty(rest)),
            "examine" or "study" => new ExamineCommand(NullIfEmpty(rest)),
            "open" => new OpenCommand(NullIfEmpty(rest)),
            "enter" => new EnterCommand(NullIfEmpty(rest)),
            "leave" or "exit_interior" => new LeaveCommand(),
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
        // Bare "travel"/"go" is a question ("where can I go?"), not an error: show the atlas rather
        // than a dead-end "unknown command". A named direction crosses to the next zone.
        string.IsNullOrWhiteSpace(text)
            ? new AtlasCommand()
            : Parse(text) is MoveCommand move
                ? new TravelCommand(move.Direction)
                : new UnknownCommand($"travel {text}");

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
