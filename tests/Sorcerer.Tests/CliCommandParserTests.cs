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
    [InlineData("travel")]
    [InlineData("go")]
    public void BareTravelShowsTheAtlasInsteadOfAnUnknownCommand(string input)
    {
        // "travel" with no direction is a question ("where can I go?"), not an error.
        Assert.IsType<AtlasCommand>(TextCommandParser.Parse(input));
    }

    [Fact]
    public void TravelWithADirectionCrossesZones()
    {
        Assert.IsType<TravelCommand>(TextCommandParser.Parse("travel east"));
    }

    [Fact]
    public void SettleParsesAcrossTextAndJsonCliSurfaces()
    {
        var text = Assert.IsType<SettleCommand>(TextCommandParser.Parse("settle water-memoried claimant"));
        var json = Assert.IsType<SettleCommand>(Program.ParseCommand(
            """{"type":"settle","target":"water-memoried claimant"}"""));

        Assert.Equal("water-memoried claimant", text.Target);
        Assert.Equal(text, json);
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

    [Fact]
    public void InteriorCommandsHaveTextAndJsonParity()
    {
        Assert.IsType<EnterCommand>(TextCommandParser.Parse("enter palace"));
        Assert.IsType<LeaveCommand>(TextCommandParser.Parse("leave"));
        Assert.IsType<EnterCommand>(Program.ParseCommand("""{"type":"enter","target":"palace"}"""));
        Assert.IsType<LeaveCommand>(Program.ParseCommand("""{"type":"leave"}"""));
    }

    [Fact]
    public void FollowupCommandsHaveTextAndJsonParity()
    {
        Assert.Equal(
            TextCommandParser.Parse("journey Hollowmere Margin"),
            Program.ParseCommand("""{"type":"journey","destination":"Hollowmere Margin"}"""));
        Assert.Equal(
            TextCommandParser.Parse("counter warden with compliance_writ"),
            Program.ParseCommand("""{"type":"counter","text":"warden with compliance_writ"}"""));
        Assert.Equal(
            TextCommandParser.Parse("breach waystation"),
            Program.ParseCommand("""{"type":"breach","target":"waystation"}"""));
        Assert.Equal(
            TextCommandParser.Parse("forge relay permit"),
            Program.ParseCommand("""{"type":"forge","text":"relay permit"}"""));
        Assert.IsType<BraceCommand>(TextCommandParser.Parse("brace"));
        Assert.IsType<BraceCommand>(Program.ParseCommand("""{"type":"brace"}"""));
    }

    [Theory]
    [InlineData("inventory", typeof(InventoryCommand))]
    [InlineData("items", typeof(InventoryCommand))]
    [InlineData("threats", typeof(ThreatsCommand))]
    [InlineData("intents", typeof(ThreatsCommand))]
    public void ReadableCombatAndInventoryAliasesHaveTextAndJsonParity(string alias, Type expected)
    {
        Assert.IsType(expected, TextCommandParser.Parse(alias));
        Assert.IsType(expected, Program.ParseCommand($$"""{"type":"{{alias}}"}"""));
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
