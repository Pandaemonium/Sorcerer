namespace Sorcerer.Core.Primitives;

public interface IRng
{
    int NextInt(int minInclusive, int maxExclusive);

    double NextDouble();

    ulong State { get; }
}

public sealed class DeterministicRng : IRng
{
    private ulong _state;

    public DeterministicRng(int seed)
        : this((ulong)Math.Max(1, seed))
    {
    }

    public DeterministicRng(ulong state)
    {
        _state = state == 0 ? 0x9E3779B97F4A7C15UL : state;
    }

    public ulong State => _state;

    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            return minInclusive;
        }

        var span = (uint)(maxExclusive - minInclusive);
        return minInclusive + (int)(NextUInt() % span);
    }

    public double NextDouble() =>
        (NextUInt() >> 11) * (1.0 / (1UL << 53));

    private ulong NextUInt()
    {
        var x = _state;
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        _state = x;
        return x;
    }
}
