using System.Text.Json;
using Sorcerer.Magic.Capabilities;

namespace Sorcerer.Llm;

/// <summary>
/// Shared prompt text and response parsing for the capability router. The router is a deliberately
/// tiny call: static instructions plus the capability index live in the system message (stable, so a
/// local backend can prompt-cache the whole prefix), and only the spell text varies per cast.
/// </summary>
internal static class SpellRouterPrompt
{
    public static string System(string capabilityIndex) =>
        "You route wild-magic spells to the capabilities they need. "
        + "You are given the player's spell and a menu of capabilities, one per line as 'name - description'. "
        + "Reply with ONLY a JSON object of the form {\"capabilities\":[\"name\",...]} listing the menu names "
        + "whose mechanics the spell needs. Use only names copied exactly from the menu. "
        + "Prefer to include a capability rather than miss one the spell might need; include every capability a "
        + "compositional spell touches. If no special capability applies, return an empty array. "
        + "Do not explain, and do not invent names.\n\nCapabilities:\n"
        + capabilityIndex;

    public static string User(string spellText) => $"Spell: {spellText}";

    /// <summary>
    /// Extracts capability names from the model's JSON. Tolerates a {"capabilities":[...]} object or a
    /// bare JSON array of strings; returns an empty list for anything else rather than throwing, since a
    /// router failure must never break a cast.
    /// </summary>
    public static IReadOnlyList<string> ParseNames(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return Array.Empty<string>();
        }

        try
        {
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;
            var array = root.ValueKind == JsonValueKind.Array
                ? root
                : root.ValueKind == JsonValueKind.Object
                    && root.TryGetProperty("capabilities", out var capabilities)
                    && capabilities.ValueKind == JsonValueKind.Array
                        ? capabilities
                        : (JsonElement?)null;
            if (array is null)
            {
                return Array.Empty<string>();
            }

            var names = new List<string>();
            foreach (var element in array.Value.EnumerateArray())
            {
                if (element.ValueKind == JsonValueKind.String
                    && element.GetString() is { Length: > 0 } name)
                {
                    names.Add(name.Trim());
                }
            }

            return names;
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static SpellRouteResult From(string routerName, string raw, string content) =>
        new(ParseNames(content), raw, TechnicalFailure: false, Error: null);

    public static SpellRouteResult Failure(string raw, string error) =>
        new(Array.Empty<string>(), raw, TechnicalFailure: true, Error: error);
}
