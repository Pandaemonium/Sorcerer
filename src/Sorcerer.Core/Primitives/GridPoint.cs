namespace Sorcerer.Core.Primitives;

public readonly record struct GridPoint(int X, int Y)
{
    public GridPoint Translate(int dx, int dy) => new(X + dx, Y + dy);
}

