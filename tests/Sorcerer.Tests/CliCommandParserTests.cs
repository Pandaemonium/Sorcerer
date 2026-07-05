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

    [Fact]
    public void TextAwaitCastWithoutArgsKeepsPendingPerformance()
    {
        var parsed = TextCommandParser.Parse("await_cast");
        var awaitCast = Assert.IsType<AwaitCastCommand>(parsed);

        Assert.Null(awaitCast.Performance);
    }

    [Fact]
    public void TextAwaitCastParsesInjectedDebugPerformance()
    {
        var parsed = TextCommandParser.Parse("await_cast 1.2 0.8 1.4");
        var awaitCast = Assert.IsType<AwaitCastCommand>(parsed);

        Assert.NotNull(awaitCast.Performance);
        Assert.True(awaitCast.Performance!.Played);
        Assert.Equal(1.2f, awaitCast.Performance.PowerModifier, 0.001f);
        Assert.Equal(0.8f, awaitCast.Performance.ControlModifier, 0.001f);
        Assert.Equal(1.4f, awaitCast.Performance.WildnessModifier, 0.001f);
        Assert.Equal("debug_fixed", awaitCast.Performance.Source);
    }

    [Fact]
    public void TextAwaitCastRejectsMalformedPerformance()
    {
        var parsed = TextCommandParser.Parse("await_cast fast please");

        Assert.IsType<UnknownCommand>(parsed);
    }

    [Fact]
    public void JsonAwaitCastParsesInjectedDebugPerformance()
    {
        var parsed = Program.ParseCommand(
            """{"type":"await_cast","performance":{"power":1.2,"control":0.8,"wildness":1.4}}""");
        var awaitCast = Assert.IsType<AwaitCastCommand>(parsed);

        Assert.NotNull(awaitCast.Performance);
        Assert.Equal(1.2f, awaitCast.Performance!.PowerModifier, 0.001f);
        Assert.Equal(0.8f, awaitCast.Performance.ControlModifier, 0.001f);
        Assert.Equal(1.4f, awaitCast.Performance.WildnessModifier, 0.001f);
        Assert.Equal("debug_fixed", awaitCast.Performance.Source);
    }

    [Fact]
    public void JsonAwaitCastWithoutPerformanceKeepsPendingPerformance()
    {
        var parsed = Program.ParseCommand("""{"type":"await_cast"}""");
        var awaitCast = Assert.IsType<AwaitCastCommand>(parsed);

        Assert.Null(awaitCast.Performance);
    }
}
