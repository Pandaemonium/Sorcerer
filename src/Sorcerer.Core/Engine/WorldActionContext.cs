namespace Sorcerer.Core.Engine;

public sealed record WorldActionContext(
    string Source,
    bool ConsumeTurn,
    string ResultAction,
    string DeltaOperation,
    string? Provider = null)
{
    public static WorldActionContext PlayerCommand(string verb) =>
        new("player_command", ConsumeTurn: true, verb, verb);

    public static WorldActionContext Dialogue(string provider, string verb, string deltaOperation) =>
        new("dialogue", ConsumeTurn: false, verb, deltaOperation, provider);
}
