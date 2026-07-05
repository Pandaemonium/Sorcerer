using Godot;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// A procedurally generated wild-magic rune: one continuous polyline the player can trace.
/// Shapes are angular random walks over a jittered lattice, normalized to a unit box
/// (coordinates roughly in [-1, 1]); the renderer scales them to pixels.
/// Generation is deterministic per seed so a given cast always burns the same sigils.
/// </summary>
public sealed class RuneShape
{
    private const int Columns = 3;
    private const int Rows = 4;
    private const int MinSegments = 4;
    private const int MaxSegments = 7;
    private const float MinSegmentLength = 0.55f;
    private const float MinTurnDegrees = 32f;
    private const int MaxNodeVisits = 2;
    private const int GenerationAttempts = 24;

    public Vector2[] Points { get; }

    private RuneShape(Vector2[] points)
    {
        Points = points;
    }

    /// <summary>Stable seed from the spell text and rune index (string.GetHashCode is per-process).</summary>
    public static int SeedFor(string spellText, int runeIndex)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var ch in spellText)
            {
                hash = (hash ^ ch) * 16777619u;
            }

            hash = (hash ^ (uint)runeIndex) * 16777619u;
            return (int)hash;
        }
    }

    public static RuneShape Generate(int seed)
    {
        var rng = new Random(seed);
        for (var attempt = 0; attempt < GenerationAttempts; attempt++)
        {
            var walk = TryWalk(rng);
            if (walk is not null)
            {
                return new RuneShape(Normalize(walk));
            }
        }

        return new RuneShape(Normalize(FallbackZigzag(rng)));
    }

    private static List<Vector2>? TryWalk(Random rng)
    {
        var nodes = Lattice(rng);
        var visits = new int[nodes.Length];
        var usedEdges = new HashSet<(int, int)>();
        var targetSegments = rng.Next(MinSegments, MaxSegments + 1);

        var current = rng.Next(nodes.Length);
        visits[current]++;
        var path = new List<Vector2> { nodes[current] };
        var previousDirection = Vector2.Zero;

        for (var segment = 0; segment < targetSegments; segment++)
        {
            var candidates = new List<int>();
            for (var next = 0; next < nodes.Length; next++)
            {
                if (next == current
                    || visits[next] >= MaxNodeVisits
                    || usedEdges.Contains(EdgeKey(current, next)))
                {
                    continue;
                }

                var step = nodes[next] - nodes[current];
                if (step.Length() < MinSegmentLength)
                {
                    continue;
                }

                if (previousDirection != Vector2.Zero)
                {
                    var turn = Mathf.RadToDeg(previousDirection.AngleTo(step));
                    if (Mathf.Abs(turn) < MinTurnDegrees || Mathf.Abs(turn) > 180f - MinTurnDegrees)
                    {
                        continue;
                    }
                }

                candidates.Add(next);
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var chosen = candidates[rng.Next(candidates.Count)];
            usedEdges.Add(EdgeKey(current, chosen));
            visits[chosen]++;
            previousDirection = nodes[chosen] - nodes[current];
            current = chosen;
            path.Add(nodes[current]);
        }

        return path.Count - 1 >= MinSegments ? path : null;
    }

    private static Vector2[] Lattice(Random rng)
    {
        var nodes = new Vector2[Columns * Rows];
        for (var row = 0; row < Rows; row++)
        {
            for (var column = 0; column < Columns; column++)
            {
                var x = -0.62f + column * (1.24f / (Columns - 1));
                var y = -1.0f + row * (2.0f / (Rows - 1));
                nodes[row * Columns + column] = new Vector2(
                    x + Jitter(rng, 0.07f),
                    y + Jitter(rng, 0.07f));
            }
        }

        return nodes;
    }

    private static List<Vector2> FallbackZigzag(Random rng)
    {
        var path = new List<Vector2>();
        var left = rng.Next(2) == 0;
        for (var row = 0; row < Rows; row++)
        {
            var y = -1.0f + row * (2.0f / (Rows - 1));
            path.Add(new Vector2(left ? -0.62f : 0.62f, y + Jitter(rng, 0.05f)));
            left = !left;
        }

        return path;
    }

    private static float Jitter(Random rng, float amount) =>
        (float)((rng.NextDouble() * 2.0) - 1.0) * amount;

    private static (int, int) EdgeKey(int a, int b) => a < b ? (a, b) : (b, a);

    private static Vector2[] Normalize(List<Vector2> path)
    {
        var min = path[0];
        var max = path[0];
        foreach (var point in path)
        {
            min = new Vector2(Mathf.Min(min.X, point.X), Mathf.Min(min.Y, point.Y));
            max = new Vector2(Mathf.Max(max.X, point.X), Mathf.Max(max.Y, point.Y));
        }

        var center = (min + max) / 2f;
        var halfExtent = Mathf.Max(Mathf.Max(max.X - min.X, max.Y - min.Y) / 2f, 0.001f);
        var normalized = new Vector2[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            normalized[i] = (path[i] - center) / halfExtent;
        }

        return normalized;
    }
}
