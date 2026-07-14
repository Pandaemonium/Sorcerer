namespace Sorcerer.Core.Consequences;

public static class WorldConsequencePayloadBuilder
{
    public static Dictionary<string, object?> MergeNestedWithTopLevelFields(
        IReadOnlyDictionary<string, object?> fields,
        IEnumerable<string> envelopeKeys,
        params string[] nestedPayloadKeys)
    {
        var envelope = envelopeKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var payload = FirstNestedPayload(fields, nestedPayloadKeys) is { } nested
            ? new Dictionary<string, object?>(nested, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in fields)
        {
            if (!envelope.Contains(pair.Key))
            {
                payload.TryAdd(pair.Key, pair.Value);
            }
        }

        // Some models deliver the nested payload as a positional array instead of a keyed object
        // (e.g. spawn_entity as ["entity","Lio","human","fugitive",13,4,...]). Positional slots
        // can't be trusted for names or tags, but a missing tile coordinate is exactly what
        // hard-rejects spatial consequences, so recover the first integer pair as x/y when they
        // are otherwise absent. Better a creative cast that lands (with a generic entity) than one
        // that silently collapses because its coordinates arrived in an unexpected shape.
        if (!payload.ContainsKey("x") && !payload.ContainsKey("y")
            && FirstPositionalArray(fields, nestedPayloadKeys) is { } array
            && FirstIntegerPair(array) is { } coordinate)
        {
            payload["x"] = coordinate.X;
            payload["y"] = coordinate.Y;
        }

        return payload;
    }

    private static IReadOnlyList<object?>? FirstPositionalArray(
        IReadOnlyDictionary<string, object?> fields,
        IEnumerable<string> nestedPayloadKeys)
    {
        foreach (var key in nestedPayloadKeys)
        {
            if (fields.TryGetValue(key, out var raw) && raw is IReadOnlyList<object?> list)
            {
                return list;
            }
        }

        return null;
    }

    private static (int X, int Y)? FirstIntegerPair(IReadOnlyList<object?> values)
    {
        int? first = null;
        foreach (var value in values)
        {
            if (value is int integer)
            {
                if (first is null)
                {
                    first = integer;
                }
                else
                {
                    return (first.Value, integer);
                }
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, object?>? FirstNestedPayload(
        IReadOnlyDictionary<string, object?> fields,
        IEnumerable<string> nestedPayloadKeys)
    {
        foreach (var key in nestedPayloadKeys)
        {
            if (!fields.TryGetValue(key, out var raw))
            {
                continue;
            }

            if (raw is IReadOnlyDictionary<string, object?> nested)
            {
                return nested;
            }

            if (raw is IDictionary<string, object?> dictionary)
            {
                return new Dictionary<string, object?>(dictionary, StringComparer.OrdinalIgnoreCase);
            }
        }

        return null;
    }
}
