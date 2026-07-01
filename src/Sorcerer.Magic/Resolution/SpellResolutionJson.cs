using System.Text.Json;
using System.Text.RegularExpressions;
using Sorcerer.Magic.Operations;

namespace Sorcerer.Magic.Resolution;

public static class SpellResolutionJson
{
    public static SpellResolution Parse(string raw, OperationRegistry registry)
    {
        using var document = JsonDocument.Parse(ExtractFirstJsonObject(raw));
        var root = document.RootElement;

        var accepted = ReadBool(root, "accepted", true);
        var severity = ReadString(root, "severity", "minor");
        var outcome = ReadString(root, "outcomeText", ReadString(root, "outcome_text", string.Empty));
        var rejectedReason = ReadNullableString(root, "rejectedReason")
            ?? ReadNullableString(root, "rejected_reason");

        var effectFields = ReadObjects(root, "effects")
            .Select(fields => NormalizeFields(fields, registry))
            .ToList();
        MergeSummonTraitFollowups(effectFields);

        var effects = effectFields
            .Select(fields => new SpellEffect(registry.Canonicalize(ReadType(fields)), fields))
            .ToArray();

        var costs = ReadCosts(root, "costs")
            .Select(NormalizeCostFields)
            .Select(fields =>
            {
                var type = ReadCostType(fields);
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

    private static string ExtractFirstJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        if (start < 0)
        {
            return raw;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < raw.Length; index++)
        {
            var current = raw[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '{')
            {
                depth++;
                continue;
            }

            if (current != '}')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return raw[start..(index + 1)];
            }
        }

        return raw[start..];
    }

    private static string ReadType(IReadOnlyDictionary<string, object?> fields)
    {
        foreach (var key in new[] { "type", "operation", "op", "kind", "effect", "effectType", "effect_type", "name" })
        {
            if (fields.TryGetValue(key, out var rawType))
            {
                return Convert.ToString(rawType) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static string ReadCostType(IReadOnlyDictionary<string, object?> fields)
    {
        foreach (var key in new[] { "type", "costType", "cost_type", "kind", "resource" })
        {
            if (fields.TryGetValue(key, out var rawType))
            {
                return Convert.ToString(rawType) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static Dictionary<string, object?> NormalizeFields(
        IReadOnlyDictionary<string, object?> fields,
        OperationRegistry registry)
    {
        var normalized = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase);
        ExpandKeyedOperation(normalized, registry);
        MergeNestedFields(normalized, "details");
        MergeNestedFields(normalized, "data");
        MergeNestedFields(normalized, "toState");
        MergeNestedFields(normalized, "to_state");

        if (normalized.TryGetValue("target/x/y", out var compactPoint)
            && TryReadPointPair(compactPoint, out var pointX, out var pointY))
        {
            normalized["x"] = pointX;
            normalized["y"] = pointY;
        }

        if (normalized.TryGetValue("targetId", out var targetId) && !normalized.ContainsKey("target"))
        {
            normalized["target"] = targetId;
        }

        if (normalized.TryGetValue("target_id", out var targetSnake) && !normalized.ContainsKey("target"))
        {
            normalized["target"] = targetSnake;
        }

        if (normalized.TryGetValue("statusName", out var statusNameValue) && !normalized.ContainsKey("status"))
        {
            normalized["status"] = statusNameValue;
        }

        if (normalized.TryGetValue("status_name", out var statusSnake) && !normalized.ContainsKey("status"))
        {
            normalized["status"] = statusSnake;
        }

        if (normalized.TryGetValue("entityName", out var entityName) && !normalized.ContainsKey("name"))
        {
            normalized["name"] = entityName;
        }

        if (normalized.TryGetValue("entity_name", out var entitySnake) && !normalized.ContainsKey("name"))
        {
            normalized["name"] = entitySnake;
        }

        if (string.IsNullOrWhiteSpace(ReadType(normalized))
            && normalized.TryGetValue("effectId", out var rawEffectId)
            && rawEffectId is not null)
        {
            ApplyCompactEffectId(Convert.ToString(rawEffectId) ?? string.Empty, normalized);
        }

        InferMissingEffectType(normalized);

        var explicitType = ReadExplicitType(normalized);
        if ((explicitType.Equals("addStatus", StringComparison.OrdinalIgnoreCase)
                || explicitType.Equals("status", StringComparison.OrdinalIgnoreCase)
                || explicitType.Equals("applyStatus", StringComparison.OrdinalIgnoreCase))
            && !normalized.ContainsKey("status")
            && normalized.TryGetValue("name", out var statusName))
        {
            normalized["status"] = statusName;
        }

        return normalized;
    }

    private static Dictionary<string, object?> NormalizeCostFields(
        IReadOnlyDictionary<string, object?> fields)
    {
        var normalized = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase);
        MergeNestedFields(normalized, "details");
        MergeNestedFields(normalized, "data");

        var explicitType = ReadCostType(normalized);
        var canonical = CanonicalCostType(explicitType);
        var name = TextField(normalized, "name")
            ?? TextField(normalized, "resource")
            ?? TextField(normalized, "item");

        if (!string.IsNullOrWhiteSpace(canonical))
        {
            normalized["type"] = canonical;
        }
        else if (!string.IsNullOrWhiteSpace(explicitType))
        {
            normalized["type"] = "item";
            normalized.TryAdd("item", explicitType);
        }
        else if (normalized.ContainsKey("item"))
        {
            normalized["type"] = "item";
        }
        else if (!string.IsNullOrWhiteSpace(name) && CanonicalCostType(name) is { Length: > 0 } namedCost)
        {
            normalized["type"] = namedCost;
        }
        else if (!string.IsNullOrWhiteSpace(name)
            && (normalized.ContainsKey("quantity") || normalized.ContainsKey("unitValue") || normalized.ContainsKey("unit_value")))
        {
            normalized["type"] = "item";
            normalized["item"] = name;
        }
        else if (normalized.ContainsKey("status"))
        {
            normalized["type"] = "status";
        }
        else if (normalized.ContainsKey("description"))
        {
            normalized["type"] = "curse";
        }
        else if (!string.IsNullOrWhiteSpace(name))
        {
            normalized["type"] = "item";
            normalized["item"] = name;
        }

        return normalized;
    }

    private static string CanonicalCostType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Trim()
            .Replace("_", "", StringComparison.OrdinalIgnoreCase)
            .Replace(" ", "", StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();
        return normalized switch
        {
            "mana" or "mp" => "mana",
            "health" or "hp" => "health",
            "maxhealth" or "maxhp" => "maxHealth",
            "maxmana" or "maxmp" => "maxMana",
            "item" or "reagent" or "material" => "item",
            "status" or "condition" => "status",
            "curse" or "debt" => "curse",
            _ => string.Empty,
        };
    }

    private static string? TextField(IReadOnlyDictionary<string, object?> fields, string key) =>
        fields.TryGetValue(key, out var value) && value is not null
            ? Convert.ToString(value)
            : null;

    private static void ExpandKeyedOperation(
        Dictionary<string, object?> fields,
        OperationRegistry registry)
    {
        if (!string.IsNullOrWhiteSpace(ReadExplicitType(fields)))
        {
            return;
        }

        foreach (var pair in fields.ToArray())
        {
            if (!registry.Supports(pair.Key)
                || pair.Value is not IReadOnlyDictionary<string, object?> nested)
            {
                continue;
            }

            fields.Remove(pair.Key);
            fields["type"] = pair.Key;
            foreach (var nestedPair in nested)
            {
                fields.TryAdd(nestedPair.Key, nestedPair.Value);
            }

            return;
        }
    }

    private static void InferMissingEffectType(Dictionary<string, object?> fields)
    {
        if (!string.IsNullOrWhiteSpace(ReadExplicitType(fields)))
        {
            return;
        }

        if (fields.ContainsKey("trait") || fields.ContainsKey("traits"))
        {
            fields["type"] = "addTrait";
            return;
        }

        if (fields.ContainsKey("status") || fields.ContainsKey("statusName") || fields.ContainsKey("status_name"))
        {
            fields["type"] = "addStatus";
            return;
        }

        if (fields.ContainsKey("text") || fields.ContainsKey("message"))
        {
            fields["type"] = "message";
            return;
        }

        if (fields.ContainsKey("terrain") && (fields.ContainsKey("x") || fields.ContainsKey("target")))
        {
            fields["type"] = "createTile";
        }
    }

    private static void MergeNestedFields(Dictionary<string, object?> normalized, string key)
    {
        if (!normalized.TryGetValue(key, out var nested)
            || nested is not IReadOnlyDictionary<string, object?> nestedFields)
        {
            return;
        }

        foreach (var pair in nestedFields)
        {
            normalized.TryAdd(pair.Key, pair.Value);
        }
    }

    private static string ReadExplicitType(IReadOnlyDictionary<string, object?> fields)
    {
        foreach (var key in new[] { "type", "operation", "op", "kind", "effect", "effectType", "effect_type" })
        {
            if (fields.TryGetValue(key, out var rawType))
            {
                return Convert.ToString(rawType) ?? string.Empty;
            }
        }

        return string.Empty;
    }

    private static void MergeSummonTraitFollowups(List<Dictionary<string, object?>> effects)
    {
        for (var index = effects.Count - 1; index >= 0; index--)
        {
            var fields = effects[index];
            if (!ReadType(fields).Equals("addTrait", StringComparison.OrdinalIgnoreCase)
                && !ReadType(fields).Equals("tag", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var target = TargetText(fields);
            if (string.IsNullOrWhiteSpace(target))
            {
                continue;
            }

            var summon = effects.FirstOrDefault(candidate =>
                ReadType(candidate).Equals("summon", StringComparison.OrdinalIgnoreCase)
                && FieldText(candidate, "name").Equals(target, StringComparison.OrdinalIgnoreCase));
            if (summon is null)
            {
                continue;
            }

            var tags = FieldValues(fields, "traits")
                .Concat(FieldValues(fields, "tags"))
                .Concat(FieldValues(fields, "trait"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            if (tags.Length == 0)
            {
                continue;
            }

            var existing = FieldValues(summon, "tags").Concat(tags).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            summon["tags"] = existing;
            effects.RemoveAt(index);
        }
    }

    private static string TargetText(IReadOnlyDictionary<string, object?> fields)
    {
        if (!fields.TryGetValue("target", out var target) || target is null)
        {
            return string.Empty;
        }

        if (target is IReadOnlyDictionary<string, object?> targetFields)
        {
            return FieldText(targetFields, "id", FieldText(targetFields, "name"));
        }

        return Convert.ToString(target) ?? string.Empty;
    }

    private static string FieldText(IReadOnlyDictionary<string, object?> fields, string key, string fallback = "")
    {
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            return fallback;
        }

        if (raw is IReadOnlyDictionary<string, object?> nested)
        {
            foreach (var nestedKey in new[] { "name", "id", "value", "text", "type" })
            {
                var value = FieldText(nested, nestedKey, "");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            foreach (var item in enumerable)
            {
                return Convert.ToString(item) ?? fallback;
            }
        }

        return Convert.ToString(raw) ?? fallback;
    }

    private static IEnumerable<string> FieldValues(IReadOnlyDictionary<string, object?> fields, string key)
    {
        if (!fields.TryGetValue(key, out var raw) || raw is null)
        {
            return Array.Empty<string>();
        }

        if (raw is System.Collections.IEnumerable enumerable && raw is not string)
        {
            return enumerable.Cast<object?>()
                .Select(value => Convert.ToString(value) ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value));
        }

        var text = Convert.ToString(raw) ?? string.Empty;
        return string.IsNullOrWhiteSpace(text) ? Array.Empty<string>() : new[] { text };
    }

    private static bool TryReadPointPair(object? raw, out int x, out int y)
    {
        x = 0;
        y = 0;
        if (raw is System.Collections.IEnumerable outer && raw is not string)
        {
            foreach (var first in outer)
            {
                if (first is System.Collections.IEnumerable inner && first is not string)
                {
                    var values = inner.Cast<object?>().Select(Convert.ToString).ToArray();
                    return values.Length >= 2
                        && int.TryParse(values[0], out x)
                        && int.TryParse(values[1], out y);
                }
            }
        }

        var text = Convert.ToString(raw) ?? string.Empty;
        var parts = text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            && int.TryParse(parts[0], out x)
            && int.TryParse(parts[1], out y);
    }

    private static void ApplyCompactEffectId(string text, Dictionary<string, object?> fields)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (text.StartsWith("status_", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("status-", StringComparison.OrdinalIgnoreCase))
        {
            fields["type"] = "addStatus";
            var status = Regex.Match(text, @"^status[_-](?<value>[^_:]+)", RegexOptions.IgnoreCase);
            if (status.Success)
            {
                fields["status"] = status.Groups["value"].Value;
            }

            var typedStatus = Regex.Match(text, @"(?:^|_)type:(?<value>.*?)(?:_(?:target|source|color|duration):|$)", RegexOptions.IgnoreCase);
            if (typedStatus.Success && !fields.ContainsKey("status"))
            {
                fields["status"] = typedStatus.Groups["value"].Value;
            }

            var target = Regex.Match(text, @"target[_-]?id:(?<value>.*?)(?:_(?:type|source|color|duration):|$)", RegexOptions.IgnoreCase);
            if (target.Success)
            {
                fields["target"] = target.Groups["value"].Value;
            }

            return;
        }

        if (text.StartsWith("message_", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("message-", StringComparison.OrdinalIgnoreCase))
        {
            fields["type"] = "message";
            var message = Regex.Match(text, @"(?:^message[_-])?text:(?<value>.+)$", RegexOptions.IgnoreCase);
            if (message.Success)
            {
                fields["text"] = message.Groups["value"].Value;
            }
        }
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

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadCosts(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        return array.EnumerateArray()
            .Select(element => element.ValueKind switch
            {
                JsonValueKind.Object => element.EnumerateObject()
                    .ToDictionary(
                        item => item.Name,
                        item => ConvertJsonValue(item.Value),
                        StringComparer.OrdinalIgnoreCase),
                JsonValueKind.String => ParseCostString(element.GetString() ?? string.Empty),
                JsonValueKind.Number when element.TryGetInt32(out var amount) => new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["type"] = "mana",
                    ["amount"] = amount,
                },
                _ => null,
            })
            .Where(fields => fields is not null)
            .Select(fields => (IReadOnlyDictionary<string, object?>)fields!)
            .ToArray();
    }

    private static Dictionary<string, object?> ParseCostString(string text)
    {
        var lower = text.Trim().ToLowerInvariant();
        var amountMatch = Regex.Match(lower, @"\d+");
        var amount = amountMatch.Success && int.TryParse(amountMatch.Value, out var parsed)
            ? parsed
            : 1;
        var type = lower switch
        {
            var value when value.Contains("max mana") || value.Contains("max_mana") => "maxMana",
            var value when value.Contains("max hp") || value.Contains("max health") || value.Contains("max_health") => "maxHealth",
            var value when value.Contains("mana") => "mana",
            var value when value.Contains("hp") || value.Contains("health") => "health",
            _ => "curse",
        };
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = type,
            ["amount"] = amount,
            ["description"] = text,
        };
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
