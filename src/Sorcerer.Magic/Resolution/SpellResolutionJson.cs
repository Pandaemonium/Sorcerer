using System.Text.Json;
using System.Text.RegularExpressions;
using Sorcerer.Magic.Operations;

namespace Sorcerer.Magic.Resolution;

public static class SpellResolutionJson
{
    private static readonly string[] CommonWrapperKeys =
    {
        "resolution", "spell_resolution", "result", "response", "output",
    };

    private static readonly HashSet<string> JunkOutcomeWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "success", "ok", "done", "true", "false", "null", "error", "failed", "none", "n/a", "unknown",
    };

    private static readonly IReadOnlyDictionary<string, string> ElementDamageAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["lightning"] = "lightning",
            ["thunder"] = "lightning",
            ["fire"] = "fire",
            ["flame"] = "fire",
            ["inferno"] = "fire",
            ["ice"] = "frost",
            ["frost"] = "frost",
            ["cold"] = "frost",
            ["poison"] = "poison",
            ["toxic"] = "poison",
            ["acid"] = "acid",
            ["arcane"] = "arcane",
            ["magic"] = "arcane",
            ["psychic"] = "arcane",
            ["force"] = "force",
            ["wind"] = "force",
            ["sonic"] = "force",
            ["radiant"] = "radiant",
            ["holy"] = "radiant",
            ["divine"] = "radiant",
            ["shadow"] = "shadow",
            ["necrotic"] = "shadow",
            ["dark"] = "shadow",
            ["physical"] = "physical",
            ["blunt"] = "physical",
            ["slash"] = "physical",
            ["pierce"] = "physical",
            ["blood"] = "blood",
        };

    public static SpellResolution Parse(string raw, OperationRegistry registry)
    {
        using var document = JsonDocument.Parse(ExtractFirstJsonObject(raw));
        var root = UnwrapCommonWrapper(document.RootElement);
        root = UnwrapOutcomeContainer(root);

        var accepted = ReadBool(root, "accepted", true);
        var severity = ReadString(root, "severity", "minor");
        var outcome = CleanOutcomeText(ReadString(root, "outcomeText", ReadString(root, "outcome_text", string.Empty)));
        var rejectedReason = ReadNullableString(root, "rejectedReason")
            ?? ReadNullableString(root, "rejected_reason");

        var rawCosts = ReadCosts(root, "costs")
            .Select(fields => new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var rescuedEffects = RescueEffectShapedCosts(rawCosts, registry);

        var effectFields = ReadEffectObjects(root, registry)
            .Select(fields => NormalizeFields(fields, registry))
            .Concat(rescuedEffects)
            .ToList();
        BindSummonFollowupIds(effectFields);
        MergeSummonTraitFollowups(effectFields);

        var effects = effectFields
            .Select(fields =>
            {
                var type = registry.Canonicalize(ReadType(fields));
                fields.Remove("type");
                return new SpellEffect(type, fields);
            })
            .ToArray();

        var costs = rawCosts
            .Select(NormalizeCostFields)
            .Select(fields =>
            {
                var type = ReadCostType(fields);
                fields.Remove("type");
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

    /// <summary>
    /// Some live models wrap the resolution object in an envelope key such as
    /// <c>{"resolution": {...}}</c>. Hoist the inner object when the outer one doesn't already
    /// look like a resolution itself (so a legitimately-nested field named e.g. "result" inside a
    /// real resolution is never mistaken for a wrapper).
    /// </summary>
    private static JsonElement UnwrapCommonWrapper(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || root.TryGetProperty("accepted", out _)
            || root.TryGetProperty("effects", out _))
        {
            return root;
        }

        foreach (var key in CommonWrapperKeys)
        {
            if (root.TryGetProperty(key, out var wrapped) && wrapped.ValueKind == JsonValueKind.Object)
            {
                return wrapped;
            }
        }

        return root;
    }

    /// <summary>
    /// Some models nest the whole resolution body inside an <c>"outcome"</c> object, e.g.
    /// <c>{"outcome":{"effects":[...],"costs":[...]}}</c>. Hoist it only when the outer object has
    /// no <c>effects</c> of its own, so a real resolution that merely carries an outcome sub-object
    /// is never hijacked. (A string <c>outcome</c>/<c>outcome_text</c> is left for text reading.)
    /// </summary>
    private static JsonElement UnwrapOutcomeContainer(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || root.TryGetProperty("effects", out _))
        {
            return root;
        }

        if (root.TryGetProperty("outcome", out var outcome)
            && outcome.ValueKind == JsonValueKind.Object
            && (outcome.TryGetProperty("effects", out _)
                || outcome.TryGetProperty("effect", out _)
                || outcome.TryGetProperty("costs", out _)))
        {
            return outcome;
        }

        return root;
    }

    private static readonly string[] EnvelopeMarkers =
    {
        "accepted", "effects", "effect", "rejected_reason", "rejectedReason",
    };

    /// <summary>
    /// Reads the resolution's effect objects, tolerating three envelope malformations beyond the
    /// canonical <c>"effects"</c> array: a singular <c>"effect"</c> (object or array), and a bare
    /// effect object with no envelope at all (<c>{"type":"damage",...}</c> as the whole response).
    /// A bare object is only accepted when its type is positively identifiable (a registered
    /// operation, element-damage alias, or known status word), never for arbitrary JSON.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadEffectObjects(
        JsonElement root,
        OperationRegistry registry)
    {
        var effects = ReadObjects(root, "effects");
        if (effects.Count > 0)
        {
            return effects;
        }

        if (root.TryGetProperty("effect", out var singular))
        {
            if (singular.ValueKind == JsonValueKind.Array)
            {
                var fromArray = ReadObjects(root, "effect");
                if (fromArray.Count > 0)
                {
                    return fromArray;
                }
            }
            else if (singular.ValueKind == JsonValueKind.Object)
            {
                return new[] { ReadObject(singular) };
            }
        }

        if (LooksLikeBareEffect(root, registry))
        {
            return new[] { ReadObject(root) };
        }

        return Array.Empty<IReadOnlyDictionary<string, object?>>();
    }

    private static bool LooksLikeBareEffect(JsonElement root, OperationRegistry registry)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var marker in EnvelopeMarkers)
        {
            if (root.TryGetProperty(marker, out _))
            {
                return false;
            }
        }

        var type = ReadType(ReadObject(root)).Trim();
        return type.Length > 0
            && (registry.Supports(type)
                || ElementDamageAliases.ContainsKey(type)
                || StatusWordTypes.Contains(type));
    }

    /// <summary>
    /// One-word/placeholder outcome text ("success", "ok", "true"...) or anything under 4
    /// characters is treated as absent rather than shown to the player; the caller's own
    /// deterministic fallback message (or the applied effects' own summaries) takes over instead.
    /// </summary>
    private static string CleanOutcomeText(string outcome)
    {
        var trimmed = outcome.Trim();
        return trimmed.Length < 4 || JunkOutcomeWords.Contains(trimmed) ? string.Empty : outcome;
    }

    /// <summary>
    /// A model sometimes expresses a mechanical consequence (e.g. addWeakness) as an entry in
    /// "costs" instead of "effects". Rescue any cost object whose declared type is actually a
    /// registered operation name before cost normalization can mangle its type field.
    /// </summary>
    private static List<Dictionary<string, object?>> RescueEffectShapedCosts(
        List<Dictionary<string, object?>> rawCosts,
        OperationRegistry registry)
    {
        var rescued = new List<Dictionary<string, object?>>();
        for (var index = rawCosts.Count - 1; index >= 0; index--)
        {
            var type = ReadCostType(rawCosts[index]);
            if (string.IsNullOrWhiteSpace(type)
                || !string.IsNullOrWhiteSpace(CanonicalCostType(type))
                || !registry.Supports(type))
            {
                // A blank type, a recognized cost keyword (mana/health/item/status/curse, even
                // when it happens to also be a registered operation alias like restoreMana's
                // "mana"), or an unregistered type is left as an ordinary cost.
                continue;
            }

            var effectFields = new Dictionary<string, object?>(rawCosts[index], StringComparer.OrdinalIgnoreCase)
            {
                ["type"] = type,
            };
            rescued.Add(NormalizeFields(effectFields, registry));
            rawCosts.RemoveAt(index);
        }

        return rescued;
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
        MergeNestedFields(normalized, "fields");
        normalized.Remove("fields");
        MergeNestedFields(normalized, "details");
        MergeNestedFields(normalized, "data");
        MergeNestedFields(normalized, "toState");
        MergeNestedFields(normalized, "to_state");
        NormalizeGenericConsequenceType(normalized, registry);

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
        ApplyElementDamageAlias(normalized);
        ApplyStatusWordType(normalized, registry);

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

    private static void NormalizeGenericConsequenceType(
        Dictionary<string, object?> fields,
        OperationRegistry registry)
    {
        if (!HasAny(fields, "consequenceType", "consequence_type", "worldConsequenceType", "world_consequence_type"))
        {
            return;
        }

        var explicitType = ReadExplicitType(fields);
        if (string.IsNullOrWhiteSpace(explicitType)
            || !registry.Supports(explicitType)
            || explicitType.Equals("worldConsequence", StringComparison.OrdinalIgnoreCase)
            || explicitType.Equals("world_consequence", StringComparison.OrdinalIgnoreCase)
            || explicitType.Equals("typedConsequence", StringComparison.OrdinalIgnoreCase)
            || explicitType.Equals("applyConsequence", StringComparison.OrdinalIgnoreCase))
        {
            fields["type"] = "consequence";
        }
    }

    private static Dictionary<string, object?> NormalizeCostFields(
        IReadOnlyDictionary<string, object?> fields)
    {
        var normalized = new Dictionary<string, object?>(fields, StringComparer.OrdinalIgnoreCase);
        MergeNestedFields(normalized, "fields");
        normalized.Remove("fields");
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

    private static bool HasAny(IReadOnlyDictionary<string, object?> fields, params string[] keys) =>
        keys.Any(fields.ContainsKey);

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

    /// <summary>
    /// A live model sometimes names the element directly as the effect type, e.g.
    /// <c>{"type":"fire","target":"nearest_enemy","amount":5}</c> instead of
    /// <c>{"type":"damage","damageType":"fire",...}</c>. Rewrite that into a supported damage
    /// effect instead of failing as an unsupported effect type.
    /// </summary>
    private static void ApplyElementDamageAlias(Dictionary<string, object?> fields)
    {
        var explicitType = ReadExplicitType(fields).Trim();
        if (explicitType.Length == 0 || !ElementDamageAliases.TryGetValue(explicitType, out var canonical))
        {
            return;
        }

        fields["type"] = "damage";
        fields.TryAdd("damageType", canonical);
    }

    private static readonly HashSet<string> StatusWordTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "regenerating", "regenerate", "burning", "poisoned", "frozen", "stunned", "slowed",
        "hasted", "invisible", "berserk", "empowered", "warded", "cursed", "bleeding", "rooted",
        "webbed", "confused", "frightened", "marked", "silenced",
    };

    /// <summary>
    /// A model sometimes names a status directly as the effect type, e.g. <c>{"type":"burning"}</c>
    /// instead of <c>{"type":"addStatus","status":"burning"}</c>. Rewrite the recognized status
    /// words into an addStatus effect; whether the status itself is valid is left to later
    /// validation. Never overrides a type that is already a registered operation.
    /// </summary>
    private static void ApplyStatusWordType(Dictionary<string, object?> fields, OperationRegistry registry)
    {
        var explicitType = ReadExplicitType(fields).Trim();
        if (explicitType.Length == 0 || registry.Supports(explicitType) || !StatusWordTypes.Contains(explicitType))
        {
            return;
        }

        fields["type"] = "addStatus";
        if (!fields.ContainsKey("status"))
        {
            fields["status"] = explicitType.Equals("regenerate", StringComparison.OrdinalIgnoreCase)
                ? "regenerating"
                : explicitType.ToLowerInvariant();
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

    private static void BindSummonFollowupIds(List<Dictionary<string, object?>> effects)
    {
        for (var summonIndex = 0; summonIndex < effects.Count; summonIndex++)
        {
            var summon = effects[summonIndex];
            if (!ReadType(summon).Equals("summon", StringComparison.OrdinalIgnoreCase)
                || HasExplicitEntityId(summon))
            {
                continue;
            }

            var name = FieldText(summon, "name", FieldText(summon, "entityName", FieldText(summon, "entity_name")));
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var prefix = OperationHelpers.NormalizeToken(name, "summon");
            var followupId = effects
                .Where((_, index) => index != summonIndex)
                .Select(TargetText)
                .Select(target => OperationHelpers.NormalizeToken(target, ""))
                .FirstOrDefault(target => IsSummonFollowupId(target, prefix));
            if (!string.IsNullOrWhiteSpace(followupId))
            {
                summon["entityId"] = followupId;
            }
        }
    }

    private static bool HasExplicitEntityId(IReadOnlyDictionary<string, object?> fields) =>
        !string.IsNullOrWhiteSpace(FieldText(fields, "entityId"))
        || !string.IsNullOrWhiteSpace(FieldText(fields, "entity_id"))
        || !string.IsNullOrWhiteSpace(FieldText(fields, "id"));

    private static bool IsSummonFollowupId(string target, string summonPrefix)
    {
        if (string.IsNullOrWhiteSpace(target) || string.IsNullOrWhiteSpace(summonPrefix))
        {
            return false;
        }

        return target.Equals(summonPrefix, StringComparison.OrdinalIgnoreCase)
            || target.StartsWith($"{summonPrefix}_", StringComparison.OrdinalIgnoreCase);
    }

    private static string TargetText(IReadOnlyDictionary<string, object?> fields)
    {
        object? target = null;
        foreach (var key in new[] { "target", "targetEntityId", "target_entity_id", "targetId", "target_id", "entityId", "entity_id" })
        {
            if (fields.TryGetValue(key, out target) && target is not null)
            {
                break;
            }
        }

        if (target is null)
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
        if (raw is IReadOnlyDictionary<string, object?> pointObject
            && pointObject.TryGetValue("x", out var xValue)
            && pointObject.TryGetValue("y", out var yValue)
            && int.TryParse(Convert.ToString(xValue), out x)
            && int.TryParse(Convert.ToString(yValue), out y))
        {
            return true;
        }

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
            .Select(ReadObject)
            .ToArray();
    }

    private static IReadOnlyDictionary<string, object?> ReadObject(JsonElement element) =>
        element.EnumerateObject()
            .ToDictionary(
                item => item.Name,
                item => ConvertJsonValue(item.Value),
                StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> ReadCosts(
        JsonElement root,
        string property)
    {
        if (!root.TryGetProperty(property, out var array))
        {
            return Array.Empty<IReadOnlyDictionary<string, object?>>();
        }

        if (array.ValueKind == JsonValueKind.Object)
        {
            return CoerceCostObject(array);
        }

        if (array.ValueKind != JsonValueKind.Array)
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

    /// <summary>
    /// Coerces a costs object (<c>{"mana":5}</c> or <c>{"item":"herb","quantity":2}</c>) into the
    /// canonical list-of-cost-objects shape. A single already-typed cost object
    /// (<c>{"type":"mana","amount":5}</c>) is passed through untouched for the normal pipeline.
    /// </summary>
    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> CoerceCostObject(JsonElement element)
    {
        var dict = ReadObject(element);
        foreach (var typeKey in new[] { "type", "costType", "cost_type", "kind", "resource" })
        {
            if (dict.ContainsKey(typeKey))
            {
                return new[] { dict };
            }
        }

        var quantity = dict.TryGetValue("quantity", out var rawQuantity)
            && int.TryParse(Convert.ToString(rawQuantity), out var parsedQuantity)
            ? parsedQuantity
            : 1;

        var costs = new List<IReadOnlyDictionary<string, object?>>();
        foreach (var pair in dict)
        {
            if (pair.Key.Equals("quantity", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var canonical = CanonicalCostType(pair.Key);
            if (canonical == "item" || pair.Key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                var itemName = Convert.ToString(pair.Value);
                if (!string.IsNullOrWhiteSpace(itemName))
                {
                    costs.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["type"] = "item",
                        ["item"] = itemName,
                        ["amount"] = quantity,
                    });
                }
            }
            else if (canonical.Length > 0 && int.TryParse(Convert.ToString(pair.Value), out var amount) && amount > 0)
            {
                costs.Add(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["type"] = canonical,
                    ["amount"] = amount,
                });
            }
        }

        return costs;
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
