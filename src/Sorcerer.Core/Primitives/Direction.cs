namespace Sorcerer.Core.Primitives;

public enum Direction
{
    North,
    South,
    East,
    West,
    NorthEast,
    NorthWest,
    SouthEast,
    SouthWest,
}

public static class DirectionExtensions
{
    public static GridPoint Offset(this Direction direction) =>
        direction switch
        {
            Direction.North => new GridPoint(0, -1),
            Direction.South => new GridPoint(0, 1),
            Direction.East => new GridPoint(1, 0),
            Direction.West => new GridPoint(-1, 0),
            Direction.NorthEast => new GridPoint(1, -1),
            Direction.NorthWest => new GridPoint(-1, -1),
            Direction.SouthEast => new GridPoint(1, 1),
            Direction.SouthWest => new GridPoint(-1, 1),
            _ => new GridPoint(0, 0),
        };
}

