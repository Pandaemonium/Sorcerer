namespace Sorcerer.Core.World;

public sealed record WorldPromise(
    string Id,
    string Kind,
    string Text,
    string Status,
    bool PlayerVisible,
    string Source = "unknown",
    int Salience = 1,
    string Subject = "",
    string? ClaimedPlace = null,
    string? BoundPlace = null,
    string? BoundTargetId = null,
    string? TriggerHint = null,
    string? RealizationKind = null,
    string? RealizedIn = null,
    int Stacks = 1);

public sealed class PromiseLedger
{
    private readonly List<WorldPromise> _promises = new();

    public IReadOnlyList<WorldPromise> Promises => _promises;

    public WorldPromise Add(
        string kind,
        string text,
        bool playerVisible = false,
        string source = "unknown",
        int salience = 1,
        string subject = "",
        string? claimedPlace = null,
        string? triggerHint = null,
        string? realizationKind = null)
    {
        var promise = new WorldPromise(
            Id: $"promise_{_promises.Count + 1}",
            Kind: string.IsNullOrWhiteSpace(kind) ? "omen" : kind.Trim(),
            Text: text.Trim(),
            Status: "unbound",
            PlayerVisible: playerVisible,
            Source: string.IsNullOrWhiteSpace(source) ? "unknown" : source.Trim(),
            Salience: Math.Max(1, salience),
            Subject: subject.Trim(),
            ClaimedPlace: string.IsNullOrWhiteSpace(claimedPlace) ? null : claimedPlace.Trim(),
            TriggerHint: string.IsNullOrWhiteSpace(triggerHint) ? null : triggerHint.Trim(),
            RealizationKind: string.IsNullOrWhiteSpace(realizationKind) ? null : realizationKind.Trim());

        _promises.Add(promise);
        return promise;
    }

    public WorldPromise? Bind(
        string id,
        string? boundPlace,
        string? boundTargetId,
        string? triggerHint = null,
        string? realizationKind = null)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var existing = _promises[index];
        var updated = existing with
        {
            Status = "bound",
            BoundPlace = string.IsNullOrWhiteSpace(boundPlace) ? existing.BoundPlace : boundPlace.Trim(),
            BoundTargetId = string.IsNullOrWhiteSpace(boundTargetId) ? existing.BoundTargetId : boundTargetId.Trim(),
            TriggerHint = string.IsNullOrWhiteSpace(triggerHint) ? existing.TriggerHint : triggerHint.Trim(),
            RealizationKind = string.IsNullOrWhiteSpace(realizationKind) ? existing.RealizationKind : realizationKind.Trim(),
        };
        _promises[index] = updated;
        return updated;
    }

    public WorldPromise? SetStatus(string id, string status, string? realizedIn = null)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return null;
        }

        var updated = _promises[index] with
        {
            Status = status,
            RealizedIn = realizedIn ?? _promises[index].RealizedIn,
        };
        _promises[index] = updated;
        return updated;
    }

    /// <summary>
    /// Finds an active (not cleared/realized) promise of the given kind whose text and bound
    /// target already match, so a repeat curse can stack instead of duplicating.
    /// </summary>
    public WorldPromise? FindActive(string kind, string text, string? boundTargetId) =>
        _promises.FirstOrDefault(promise =>
            promise.Kind.Equals(kind, StringComparison.OrdinalIgnoreCase)
            && !promise.Status.Equals("cleared", StringComparison.OrdinalIgnoreCase)
            && !promise.Status.Equals("realized", StringComparison.OrdinalIgnoreCase)
            && promise.Text.Equals(text, StringComparison.OrdinalIgnoreCase)
            && string.Equals(promise.BoundTargetId, boundTargetId, StringComparison.OrdinalIgnoreCase));

    public WorldPromise Stack(string id)
    {
        var index = _promises.FindIndex(promise => promise.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new InvalidOperationException($"Promise {id} does not exist.");
        }

        var updated = _promises[index] with { Stacks = _promises[index].Stacks + 1 };
        _promises[index] = updated;
        return updated;
    }

    public IReadOnlyList<WorldPromise> Snapshot() => _promises.ToArray();

    public void ReplaceAll(IEnumerable<WorldPromise> promises)
    {
        _promises.Clear();
        _promises.AddRange(promises);
    }
}
