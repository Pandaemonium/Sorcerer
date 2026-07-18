using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.Commands;

public abstract record GameCommand;

public sealed record MoveCommand(Direction Direction) : GameCommand;

public sealed record WaitCommand() : GameCommand;

public sealed record InspectCommand() : GameCommand;

public sealed record CastCommand(string Text, CastPerformance? Performance = null) : GameCommand;

public sealed record BeginCastCommand(string Text, CastPerformance? Performance = null) : GameCommand;

/// <summary>
/// Resolves the pending cast. <paramref name="Performance"/> is the casting-minigame score,
/// attached here because it does not exist yet when the cast is submitted: the minigame plays
/// while the provider resolves, and the engine applies the final score at the apply boundary.
/// Null keeps whatever performance the pending cast already carries (neutral by default).
/// </summary>
public sealed record AwaitCastCommand(CastPerformance? Performance = null) : GameCommand;

public sealed record CancelCastCommand() : GameCommand;

public sealed record TargetCommand(GridPoint Position) : GameCommand;

public sealed record ClearTargetCommand() : GameCommand;

public sealed record MapCommand(int Radius = 8) : GameCommand;

public sealed record TravelCommand(Direction Direction) : GameCommand;

/// <summary>Follow a named overland route, compressing empty legs with a bounded scene budget.</summary>
public sealed record JourneyCommand(string Destination) : GameCommand;

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

/// <summary>Lists every carried item, including equipment, protection, and alterations.</summary>
public sealed record InventoryCommand() : GameCommand;

public sealed record ReagentsCommand() : GameCommand;

/// <summary>Lists the currently perceived hostile intents and their legible counters.</summary>
public sealed record ThreatsCommand() : GameCommand;

public sealed record WaresCommand(string? Target = null) : GameCommand;

public sealed record BuyCommand(string Item, string? Target = null) : GameCommand;

public sealed record SellCommand(string Item, string? Target = null) : GameCommand;

public sealed record ServicesCommand(string? Target = null) : GameCommand;

public sealed record RequestServiceCommand(string Service, string? Target = null) : GameCommand;

/// <summary>Null spell lists the known charter repertoire; a spell id/name casts it instantly.</summary>
public sealed record CharterCommand(string? Spell = null) : GameCommand;

/// <summary>Lists the run's recorded spell echoes (the grimoire).</summary>
public sealed record EchoesCommand() : GameCommand;

/// <summary>Re-casts a recorded echo by number or name fragment, instantly, no model call.</summary>
public sealed record EchoCommand(string Reference) : GameCommand;

public sealed record JournalCommand() : GameCommand;

public sealed record RumorsCommand() : GameCommand;

public sealed record CharacterCommand() : GameCommand;

public sealed record TalkCommand(string Text) : GameCommand;

/// <summary>WP6: open the floor to the nearby group — the player plus two to four eligible
/// participants exchange short, state-grounded utterances (a Bralli tale-circle one-up, a Hollowmere
/// disagreement) through the shared command surface.</summary>
public sealed record GroupTalkCommand(string Text = "") : GameCommand;

/// <summary>Accept the concrete terms established in conversation with a nearby entity that
/// anchors a realized debt or obligation.</summary>
public sealed record SettleCommand(string? Target = null) : GameCommand;

public sealed record BargainsCommand(string? Target = null) : GameCommand;

/// <summary>Perform a pending service term in the claimant's presence.</summary>
public sealed record FulfillCommand(string Reference = "") : GameCommand;

public sealed record OfferCommand(string Text) : GameCommand;

public sealed record BargainCommand(string? Target = null) : GameCommand;

public sealed record ConcedeCommand(string? Target = null) : GameCommand;

public sealed record IntimidateCommand(string? Target = null) : GameCommand;

public sealed record ExchangeCommand(string Text) : GameCommand;

public sealed record CleanseCommand(string? Reference = null) : GameCommand;

/// <summary>Commit to a defensive stance. Worn protective equipment determines its strength.</summary>
public sealed record BraceCommand() : GameCommand;

/// <summary>Interrupt a telegraphed enemy action with an inventory or equipped item.</summary>
public sealed record CounterCommand(string Text) : GameCommand;

public sealed record BreachCommand(string? Target = null) : GameCommand;

public sealed record ForgeCommand(string Text = "permit") : GameCommand;

public sealed record GiveCommand(string Item, string? Target = null) : GameCommand;

public sealed record RecruitCommand(string? Target = null) : GameCommand;

public sealed record BondsCommand(string? Target = null) : GameCommand;

public sealed record ReadCommand(string? Target = null) : GameCommand;

public sealed record ExamineCommand(string? Target = null) : GameCommand;

public sealed record OpenCommand(string? Target = null) : GameCommand;

public sealed record EnterCommand(string? Target = null) : GameCommand;

public sealed record LeaveCommand() : GameCommand;

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
