using System.Linq;
using Sorcerer.Core.Views;
using Xunit;

namespace Sorcerer.Tests;

/// <summary>
/// Locks the message-log curation used by renderers: chaff removal, near-duplicate collapse, and
/// damage classification/colouring (message-log immersion pass).
/// </summary>
public sealed class MessageLogTests
{
    [Fact]
    public void DropsRumorAndMovementChaff()
    {
        var messages = new[]
        {
            "You move.",
            "A rumor reaches hollowmere margin: the wild sorcerer worked wild magic in Imperial Encounter.",
            "In Hollowmere Margin, a local rumor improves your name before you arrive: uncanny, but maybe useful.",
            "The patrol-censor takes 5 cold damage.",
        };

        var texts = MessageLog.CurateText(messages);

        Assert.Single(texts);
        Assert.Equal("The patrol-censor takes 5 cold damage.", texts[0]);
    }

    [Fact]
    public void CollapsesNearDuplicateLines()
    {
        var messages = new[]
        {
            "A promised route becomes visible: burned oak road.",
            "You travel east into Hollowmere Margin.",
            "A promised route becomes visible: burned oak road.",
        };

        var texts = MessageLog.CurateText(messages);

        Assert.Equal(2, texts.Count);
        Assert.Equal("A promised route becomes visible: burned oak road.", texts[0]);
        Assert.Equal("You travel east into Hollowmere Margin.", texts[1]);
    }

    [Fact]
    public void DamageYouTakeIsRed()
    {
        var card = MessageLog.Classify("You take 4 fire damage.");

        Assert.Equal(MessageKind.DamageTaken, card.Kind);
        Assert.Equal(MessageLog.DamageTakenColor, card.AccentColor);
    }

    [Fact]
    public void StandingEarnedReadsGold()
    {
        var card = MessageLog.Classify("Hollowmere will remember this.");

        Assert.Equal(MessageKind.Standing, card.Kind);
        Assert.Equal(MessageLog.StandingColor, card.AccentColor);
    }

    [Fact]
    public void DamageYouDealIsOrangeWithTintedType()
    {
        var card = MessageLog.Classify("The patrol-censor takes 5 cold damage.");

        Assert.Equal(MessageKind.DamageDealt, card.Kind);
        Assert.Equal(MessageLog.DamageDealtColor, card.AccentColor);
        // "cold" is split into its own blue segment; the rest carries the orange accent.
        var coldSegment = Assert.Single(card.Segments, segment => segment.Text == "cold");
        Assert.Equal("4fc3f7", coldSegment.Color);
        Assert.Contains(card.Segments, segment => segment.Color == MessageLog.DamageDealtColor);
        Assert.Equal("The patrol-censor takes 5 cold damage.", string.Concat(card.Segments.Select(s => s.Text)));
    }

    [Fact]
    public void PlayerSpeechIsClassifiedAndKept()
    {
        var card = MessageLog.Classify("You say, \"where is the south road?\"");

        Assert.Equal(MessageKind.PlayerSpeech, card.Kind);
        Assert.Equal(MessageLog.PlayerSpeechColor, card.AccentColor);
    }

    [Fact]
    public void OrdinarySystemLineHasNoAccent()
    {
        var card = MessageLog.Classify("You travel east into Hollowmere Margin.");

        Assert.Equal(MessageKind.System, card.Kind);
        Assert.Null(card.AccentColor);
    }

    [Fact]
    public void DropsEnemyMovementAndTargetSelection()
    {
        var messages = new[]
        {
            "imperial containment soldier moves to 8,5.",
            "imperial ward-captain moves to 10,5.",
            "imperial containment soldier cannot move to 10,5.",
            "Target set to 10,5.",
            "Selected target set to 4,4.",
            "Cost: 8 mana.",
        };

        var texts = MessageLog.CurateText(messages);

        Assert.Single(texts);
        Assert.Equal("Cost: 8 mana.", texts[0]);
    }

    [Fact]
    public void StripsBareCoordinatesFromProse()
    {
        var text = "A jagged burst shatters outward. The imperial soldier at 8,5 and ward-captain at 10,5 are struck by a concussive blast.";

        var texts = MessageLog.CurateText(new[] { text });

        Assert.Single(texts);
        Assert.Equal(
            "A jagged burst shatters outward. The imperial soldier and ward-captain are struck by a concussive blast.",
            texts[0]);
    }
}
