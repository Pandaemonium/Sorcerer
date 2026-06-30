namespace Sorcerer.Core.Primitives;

public readonly record struct EntityId(string Value)
{
    public override string ToString() => Value;

    public static EntityId Create(string value) => new(value);
}

