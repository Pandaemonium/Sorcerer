using Sorcerer.Core.Entities;
using Sorcerer.Core.Primitives;

namespace Sorcerer.Core.References;

public abstract record GameReference;

public sealed record EntityRef(string Kind, string Value, int? Radius = null, string? Filter = null)
{
    public static EntityRef Self { get; } = new("selector", "self");
}

public sealed record EntityReference(string EntityId) : GameReference;

public sealed record TileReference(GridPoint Position) : GameReference;

public sealed record SelectorReference(string Selector) : GameReference;

public sealed record FactionReference(string FactionId) : GameReference;

public sealed record BoundReference(
    GameReference Reference,
    Entity? Entity,
    GridPoint? Position,
    IReadOnlyList<Entity> Group,
    string? Error,
    string? FailureCode = null)
{
    public bool Success => Error is null;

    // FailureCode is the machine-stable family from Results.FailureCode; Error is the precise player
    // message. Callers should pass a code so GUI/CLI/transcript/resolver read one vocabulary.
    public static BoundReference Failure(GameReference reference, string error, string? failureCode = null) =>
        new(reference, null, null, Array.Empty<Entity>(), error, failureCode);
}

public sealed record ResolvedEntitySet(
    EntityRef Reference,
    IReadOnlyList<Entity> Entities,
    GridPoint? Position,
    string? Error,
    string? FailureCode = null)
{
    public bool Success => Error is null;

    public static ResolvedEntitySet Failure(EntityRef reference, string error, string? failureCode = null) =>
        new(reference, Array.Empty<Entity>(), null, error, failureCode);
}

public interface IReferenceResolver
{
    ResolvedEntitySet Resolve(EntityRef reference);
}
