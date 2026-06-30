using System.Text.Json;
using Sorcerer.Magic.Operations;

namespace Sorcerer.Magic.Resolution;

public static class SpellResolutionJson
{
    public static SpellResolution Parse(string raw, OperationRegistry registry)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;

        var accepted = ReadBool(root, "accepted", true);
        var severity = ReadString(root, "severity", "minor");
        var outcome = ReadString(root, "outcomeText", ReadString(root, "outcome_text", string.Empty));
        var rejectedReason = ReadNullableString(root, "rejectedReason")
            ?? ReadNullableString(root, "rejected_reason");

        var effects = ReadObjects(root, "effects")
            .Select(fields =>
            {
                var type = fields.TryGetValue("type", out var rawType)
                    ? Convert.ToString(rawType) ?? string.Empty
                    : string.Empty;
                return new SpellEffect(registry.Canonicalize(type), fields);
            })
            .ToArray();

        var costs = ReadObjects(root, "costs")
            .Select(fields =>
            {
                var type = fields.TryGetValue("type", out var rawType)
                    ? Convert.ToString(rawType) ?? string.Empty
                    : string.Empty;
                return new SpellCost(type, fields);
            })
            .ToArray();

        return new SpellResolution(
            accepted,
            severity,
            outcome,
            effects,
            costs,
            rejectedReason);
    }

    private static bool ReadBool(JsonElement root, string property, bool fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean()
            : fallback;

    private static string ReadString(JsonElement root, string property, string fallback) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static string? ReadNullableString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadObjects(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        return array.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.Object)
            .Select(element => element.EnumerateObject()
                .ToDictionary(
                    item => item.Name,
                    item => ConvertJsonValue(item.Value),
                    StringComparer.OrdinalIgnoreCase))
            .ToArray();
    }

    private static object? ConvertJsonValue(JsonElement element) =>
        element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number when element.TryGetInt32(out var integer) => integer,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(
                    item => item.Name,
                    item => ConvertJsonValue(item.Value),
                    StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonValue).ToArray(),
            _ => null,
        };
}

