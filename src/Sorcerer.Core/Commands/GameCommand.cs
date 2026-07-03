using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.Commands;

public abstract record GameCommand;

public sealed record MoveCommand(Direction Direction) : GameCommand;

public sealed record WaitCommand() : GameCommand;

public sealed record InspectCommand() : GameCommand;

public sealed record CastCommand(string Text, CastPerformance? Performance = null) : GameCommand;

public sealed record BeginCastCommand(string Text, CastPerformance? Performance = null) : GameCommand;

public sealed record AwaitCastCommand() : GameCommand;

public sealed record CancelCastCommand() : GameCommand;

public sealed record TargetCommand(GridPoint Position) : GameCommand;

public sealed record ClearTargetCommand() : GameCommand;

public sealed record MapCommand(int Radius = 8) : GameCommand;

public sealed record TravelCommand(Direction Direction) : GameCommand;

public sealed record AtlasCommand() : GameCommand;

public sealed record PickupCommand(string? Target = null) : GameCommand;

public sealed record DropCommand(string Item) : GameCommand;

public sealed record UseItemCommand(string Item) : GameCommand;

public sealed record EquipCommand(string Item) : GameCommand;

public sealed record UnequipCommand(string SlotOrItem) : GameCommand;

public sealed record FocusCommand(string SlotOrItem) : GameCommand;

public sealed record UnfocusCommand(string? SlotOrItem = null) : GameCommand;

public sealed record ProtectItemCommand(string Item) : GameCommand;

public sealed record UnprotectItemCommand(string Item) : GameCommand;

public sealed record ReagentsCommand() : GameCommand;

public sealed record WaresCommand(string? Target = null) : GameCommand;

public sealed record BuyCommand(string Item, string? Target = null) : GameCommand;

public sealed record SellCommand(string Item, string? Target = null) : GameCommand;

public sealed record ServicesCommand(string? Target = null) : GameCommand;

public sealed record RequestServiceCommand(string Service, string? Target = null) : GameCommand;

public sealed record JournalCommand() : GameCommand;

public sealed record RumorsCommand() : GameCommand;

public sealed record CharacterCommand() : GameCommand;

public sealed record TalkCommand(string Text) : GameCommand;

public sealed record GiveCommand(string Item, string? Target = null) : GameCommand;

public sealed record RecruitCommand(string? Target = null) : GameCommand;

public sealed record BondsCommand(string? Target = null) : GameCommand;

public sealed record ReadCommand(string? Target = null) : GameCommand;

public sealed record ExamineCommand(string? Target = null) : GameCommand;

public sealed record OpenCommand(string? Target = null) : GameCommand;

public sealed record PossessCommand(string? Target = null) : GameCommand;

public sealed record StandingCommand() : GameCommand;

public sealed record FollowersCommand() : GameCommand;

public sealed record JobsCommand() : GameCommand;

public sealed record SaveCommand(string Path) : GameCommand;

public sealed record LoadCommand(string Path) : GameCommand;

public sealed record HelpCommand() : GameCommand;

public sealed record QuitCommand() : GameCommand;

public sealed record UnknownCommand(string Text) : GameCommand;

public sealed record CastPerformance(
    bool Played,
    float PowerModifier,
    float ControlModifier,
    float WildnessModifier,
    string Source)
{
    public static CastPerformance Neutral { get; } = new(
        Played: false,
        PowerModifier: 1.0f,
        ControlModifier: 1.0f,
        WildnessModifier: 1.0f,
        Source: "neutral");
}
