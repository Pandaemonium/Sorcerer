using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.Commands;

public abstract record GameCommand;

public sealed record MoveCommand(Direction Direction) : GameCommand;

public sealed record WaitCommand() : GameCommand;

public sealed record InspectCommand() : GameCommand;

public sealed record CastCommand(string Text, CastPerformance? Performance = null) : GameCommand;

public sealed record TargetCommand(GridPoint Position) : GameCommand;

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

