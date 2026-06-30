namespace Sorcerer.Core.World;

public sealed record WorldPromise(
    string Id,
    string Kind,
    string Text,
    string Status,
    bool PlayerVisible,
    string Source = "unknown",
    int Salience = 1,
    string? BoundPlace = null,
    string? RealizedIn = null);

public sealed class PromiseLedger
{
    private readonly List<WorldPromise> _promises = new();

    public IReadOnlyList<WorldPromise> Promises => _promises;

    public WorldPromise Add(string kind, string text, bool playerVisible = false)
    {
        var promise = new WorldPromise(
            Id: $"promise_{_promises.Count + 1}",
            Kind: string.IsNullOrWhiteSpace(kind) ? "omen" : kind.Trim(),
            Text: text.Trim(),
            Status: "unbound",
            PlayerVisible: playerVisible);

        _promises.Add(promise);
        return promise;
    }

    public IReadOnlyList<WorldPromise> Snapshot() => _promises.ToArray();

    public void ReplaceAll(IEnumerable<WorldPromise> promises)
    {
        _promises.Clear();
        _promises.AddRange(promises);
    }
}
