using System.Text.RegularExpressions;

namespace Sorcerer.Core.Views;

/// <summary>
/// The semantic kind of a log message, used by renderers to colour and group the message log.
/// Classification is text-based (the message stream is plain strings), keyed off the fixed shapes
/// the engine emits, so it stays a pure view concern with no dependency on how a message was made.
/// </summary>
public enum MessageKind
{
    System,
    DamageTaken,
    DamageDealt,
    Death,
    PlayerSpeech,
    Standing,
}

/// <summary>A run of message text sharing one colour. Colour is a hex string without '#', or null for the line's accent.</summary>
public sealed record MessageSegment(string Text, string? Color = null);

/// <summary>
/// One curated log message: its full text, its semantic kind, an accent colour for the whole line,
/// and coloured segments (e.g. the damage-type word) a renderer can lay out inline.
/// </summary>
public sealed record MessageCard(
    string Text,
    MessageKind Kind,
    string? AccentColor,
    IReadOnlyList<MessageSegment> Segments);

/// <summary>
/// Turns the raw message stream into an immersive log (docs/OPTIMIZATION_PLAN.md follow-up: message
/// log pass): drops chaff the player does not need (rumour propagation, bare movement), removes
/// near-duplicate lines, and classifies each survivor so damage stands out — damage you take reads
/// red, damage you deal reads orange with the damage type tinted by element (cold blue, fire
/// orange-red, and so on). Renderer-agnostic: colours are hex strings the UI maps to its own type.
/// </summary>
public static class MessageLog
{
    // Accent colours per kind (hex, no leading '#').
    public const string DamageTakenColor = "e5534b"; // red
    public const string DamageDealtColor = "e08a3c";  // orange
    public const string DeathColor = "ff5252";        // bright red
    public const string PlayerSpeechColor = "7fb2ff"; // player's own voice, cool blue
    public const string StandingColor = "d4af37";     // gold: reputation earned

    private const int DedupeLookback = 4;

    private static readonly IReadOnlyDictionary<string, string> DamageTypeColors =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["fire"] = "ff7043", ["flame"] = "ff7043", ["inferno"] = "ff7043", ["burning"] = "ff7043",
            ["cold"] = "4fc3f7", ["frost"] = "4fc3f7", ["ice"] = "4fc3f7", ["freezing"] = "4fc3f7",
            ["lightning"] = "ffd54f", ["thunder"] = "ffd54f", ["shock"] = "ffd54f",
            ["poison"] = "9ccc65", ["toxic"] = "9ccc65", ["acid"] = "aed581", ["venom"] = "9ccc65",
            ["arcane"] = "ba68c8", ["magic"] = "ba68c8", ["psychic"] = "ce93d8", ["force"] = "80deea",
            ["shadow"] = "9575cd", ["necrotic"] = "9575cd", ["dark"] = "9575cd",
            ["radiant"] = "ffe082", ["holy"] = "ffe082", ["divine"] = "ffe082",
            ["physical"] = "bdbdbd", ["blunt"] = "bdbdbd", ["slash"] = "bdbdbd", ["pierce"] = "bdbdbd",
            ["blood"] = "e57373",
        };

    // Rumour propagation, bare movement, and target-selection are world/UI bookkeeping the player
    // can already see on the map and target indicator; narrating them every step clogs the log
    // without adding to the moment (user feedback). Any actor "moves to x,y" / "cannot move to x,y",
    // and the "target set to" confirmation, are dropped.
    private static readonly Regex ChaffPattern = new(
        @"^(a rumou?r (reaches|changes hands|passes between)|a (censorate|clerkly) rumou?r|a rumou?r in .+ tries on|in .+, a local rumou?r improves your name|you move\.|(selected )?target set to\b|.+ (moves|cannot move) to \d+\s*,\s*\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Bare grid coordinates ("... the imperial soldier at 8,5 ...") leak internal state into prose.
    // The map shows where things are; strip the coordinates from the words the player reads.
    private static readonly Regex CoordinateAside = new(
        @"\s+at \d+\s*,\s*\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TakenPattern = new(
        @"^you (?:take|suffer) (\d+)(?: (\w+))? (?:damage|harm)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OngoingTakenPattern = new(
        @"^you take (\d+) ongoing harm\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StrikePattern = new(
        @"^you strike .+? for (\d+) (\w+) damage\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TargetTakesPattern = new(
        @"^(?!you\b).+? takes? (\d+) (\w+) damage\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DeathPattern = new(
        @"\b(falls|dies|is slain|is destroyed|is killed|collapses, dead)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool IsChaff(string message) =>
        !string.IsNullOrWhiteSpace(message) && ChaffPattern.IsMatch(message.TrimStart());

    /// <summary>Cleans a raw message for display: trims and strips bare "at x,y" grid coordinates.</summary>
    public static string Sanitize(string? message) =>
        string.IsNullOrWhiteSpace(message)
            ? string.Empty
            : CoordinateAside.Replace(message.Trim(), string.Empty);

    /// <summary>
    /// Curates the newest <paramref name="limit"/> log lines: drops chaff, removes a line that
    /// repeats one of the last few kept lines, then classifies each. Order is preserved, newest last.
    /// </summary>
    public static IReadOnlyList<MessageCard> Curate(IEnumerable<string> messages, int limit = 40)
    {
        var kept = new List<string>();
        foreach (var raw in messages)
        {
            var message = Sanitize(raw);
            if (string.IsNullOrEmpty(message) || IsChaff(message))
            {
                continue;
            }

            var isDuplicate = false;
            for (var i = kept.Count - 1; i >= 0 && i >= kept.Count - DedupeLookback; i--)
            {
                if (string.Equals(kept[i], message, StringComparison.Ordinal))
                {
                    isDuplicate = true;
                    break;
                }
            }

            if (!isDuplicate)
            {
                kept.Add(message);
            }
        }

        if (limit > 0 && kept.Count > limit)
        {
            kept.RemoveRange(0, kept.Count - limit);
        }

        return kept.Select(Classify).ToArray();
    }

    /// <summary>The curated text lines only, for renderers that do not colour (CLI, plain views).</summary>
    public static IReadOnlyList<string> CurateText(IEnumerable<string> messages, int limit = 40) =>
        Curate(messages, limit).Select(card => card.Text).ToArray();

    public static MessageCard Classify(string text)
    {
        text = text?.Trim() ?? string.Empty;

        if (text.StartsWith("You say,", StringComparison.OrdinalIgnoreCase))
        {
            return new MessageCard(text, MessageKind.PlayerSpeech, PlayerSpeechColor, Whole(text, PlayerSpeechColor));
        }

        if (OngoingTakenPattern.IsMatch(text))
        {
            return new MessageCard(text, MessageKind.DamageTaken, DamageTakenColor, Whole(text, DamageTakenColor));
        }

        if (TakenPattern.Match(text) is { Success: true } taken)
        {
            var type = taken.Groups[2].Success ? taken.Groups[2].Value : null;
            return DamageCard(text, MessageKind.DamageTaken, DamageTakenColor, type);
        }

        if (StrikePattern.Match(text) is { Success: true } strike)
        {
            return DamageCard(text, MessageKind.DamageDealt, DamageDealtColor, strike.Groups[2].Value);
        }

        if (TargetTakesPattern.Match(text) is { Success: true } dealt)
        {
            return DamageCard(text, MessageKind.DamageDealt, DamageDealtColor, dealt.Groups[2].Value);
        }

        if (DeathPattern.IsMatch(text))
        {
            return new MessageCard(text, MessageKind.Death, DeathColor, Whole(text, DeathColor));
        }

        // Reputation earned reads gold: the fixed shape the objective-return path emits when a
        // faction's standing shifts in the player's favour.
        if (text.EndsWith("will remember this.", StringComparison.OrdinalIgnoreCase))
        {
            return new MessageCard(text, MessageKind.Standing, StandingColor, Whole(text, StandingColor));
        }

        return new MessageCard(text, MessageKind.System, null, new[] { new MessageSegment(text) });
    }

    private static IReadOnlyList<MessageSegment> Whole(string text, string color) =>
        new[] { new MessageSegment(text, color) };

    /// <summary>
    /// Builds a damage line: the whole line takes the kind's accent, and the damage-type word (if it
    /// has an element colour) is split out as its own tinted segment, so "the censor takes 5 cold
    /// damage" reads orange with a blue "cold".
    /// </summary>
    private static MessageCard DamageCard(string text, MessageKind kind, string accent, string? damageType)
    {
        if (string.IsNullOrWhiteSpace(damageType)
            || !DamageTypeColors.TryGetValue(damageType, out var typeColor))
        {
            return new MessageCard(text, kind, accent, Whole(text, accent));
        }

        var index = text.IndexOf(damageType, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return new MessageCard(text, kind, accent, Whole(text, accent));
        }

        var segments = new List<MessageSegment>(3);
        if (index > 0)
        {
            segments.Add(new MessageSegment(text[..index], accent));
        }

        segments.Add(new MessageSegment(text.Substring(index, damageType.Length), typeColor));
        var tail = index + damageType.Length;
        if (tail < text.Length)
        {
            segments.Add(new MessageSegment(text[tail..], accent));
        }

        return new MessageCard(text, kind, accent, segments);
    }
}
