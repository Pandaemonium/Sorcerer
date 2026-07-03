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

        return payload;
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
