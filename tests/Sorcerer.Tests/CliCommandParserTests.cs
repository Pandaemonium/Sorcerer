using Sorcerer.Cli;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Primitives;
using Xunit;

namespace Sorcerer.Tests;

public sealed class CliCommandParserTests
{
    [Theory]
    [InlineData("journal")]
    [InlineData("promises")]
    public void JsonJournalAliasesMatchTextCommandAliases(string type)
    {
        var parsed = Program.ParseCommand($$"""{"type":"{{type}}"}""");

        Assert.IsType<JournalCommand>(parsed);
    }

    [Theory]
    [InlineData("rumors")]
    [InlineData("gossip")]
    public void JsonRumorAliasesMatchTextCommandAliases(string type)
    {
        var parsed = Program.ParseCommand($$"""{"type":"{{type}}"}""");
        var text = TextCommandParser.Parse(type);

        Assert.IsType<RumorsCommand>(parsed);
        Assert.IsType<RumorsCommand>(text);
    }

    [Theory]
    [InlineData("look", typeof(InspectCommand))]
    [InlineData("get", typeof(PickupCommand))]
    [InlineData("cleartarget", typeof(ClearTargetCommand))]
    [InlineData("say", typeof(TalkCommand))]
    [InlineData("speak", typeof(TalkCommand))]
    [InlineData("study", typeof(ExamineCommand))]
    public void JsonAliasesMatchTextCommandSurface(string type, Type expectedCommand)
    {
        var parsed = Program.ParseCommand($$"""{"type":"{{type}}","text":"hello","target":"notice"}""");

        Assert.IsType(expectedCommand, parsed);
    }

    [Theory]
    [InlineData("n", Direction.North)]
    [InlineData("north", Direction.North)]
    [InlineData("sw", Direction.SouthWest)]
    [InlineData("southwest", Direction.SouthWest)]
    public void JsonDirectionAliasesMoveLikeTextDirectionAliases(string type, Direction expectedDirection)
    {
        var parsed = Program.ParseCommand($$"""{"type":"{{type}}"}""");
        var move = Assert.IsType<MoveCommand>(parsed);

        Assert.Equal(expectedDirection, move.Direction);
    }
}
