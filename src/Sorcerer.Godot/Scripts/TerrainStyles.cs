using Godot;

namespace Sorcerer.Godot;

/// <summary>
/// Renderer-side styling for the open, content-authored terrain vocabulary ("resonant_crystal",
/// "tidepool_mosaic", "ochre_sand", ...). Styles are matched by keyword family, first match wins,
/// so new content terrains pick up a fitting look without renderer changes. All per-tile variation
/// (glyph variant, shade jitter, palette pick) is deterministic in the tile coordinates: the same
/// tile always draws the same way, so floors read as hand-set texture rather than noise.
///
/// Aesthetic contract (docs/AESTHETICS_AND_TONE.md): the wild world is jewel-toned and riotous,
/// imperial surfaces (marble, tile, registry) are clean and low-variance. Shimmer marks living
/// magic; it is an amplitude for a slow pulse the renderer animates.
/// </summary>
public static class TerrainStyles
{
    public sealed record TerrainStyle(
        Color Background,
        Color Glyph,
        string Glyphs = ".",
        Color[]? GlyphPalette = null,
        float Jitter = 0.06f,
        float Shimmer = 0f);

    private sealed record Rule(string[] Keywords, TerrainStyle Style);

    private static readonly Color[] MosaicJewels =
    {
        new("8fa8e8"), new("e8b45f"), new("6fd4c4"), new("d88ad4"),
    };

    private static readonly Color[] CrystalFacets =
    {
        new("c99aff"), new("8fd4ff"), new("ff9ade"),
    };

    private static readonly Color[] FestivalScraps =
    {
        new("ff7ad9"), new("ffd166"), new("7ae8ff"), new("b7ff7a"),
    };

    private static readonly Color[] BloomHeads =
    {
        new("ff8ad4"), new("ffb054"), new("c99aff"), new("ff6b8a"),
    };

    private static readonly TerrainStyle FloorDefault =
        new(new Color("181428"), new Color("b4aacc"), "....,'");

    private static readonly TerrainStyle WallDefault =
        new(new Color("2c2838"), new Color("9a90b4"), "#", Jitter: 0.05f);

    /// <summary>
    /// Ordered keyword table; first match wins, so specific materials (marble, plank, mosaic)
    /// must come before the broad families (stone, salt, water) their names may also contain.
    /// </summary>
    private static readonly Rule[] Rules =
    {
        // Living magic and elements.
        new(new[] { "wild_fire", "flame", "ember", "brazier", "lava" },
            new(new Color("4a2412"), new Color("ffa24f"), "^^^~", Shimmer: 0.45f)),
        new(new[] { "ash" },
            new(new Color("2c2626"), new Color("a89a92"), "..,%")),
        new(new[] { "ice_wall" },
            new(new Color("27506b"), new Color("bfeaff"), "#", Jitter: 0.04f, Shimmer: 0.1f)),
        new(new[] { "ice", "frost", "snow" },
            new(new Color("1a3d5c"), new Color("a5e3ff"), "~~-", Shimmer: 0.12f)),
        new(new[] { "crystal", "resonan" },
            new(new Color("2e1d52"), new Color("c99aff"), "**+^", CrystalFacets, Shimmer: 0.5f)),
        new(new[] { "steam", "mist", "fog", "smoke" },
            new(new Color("2c3e42"), new Color("9fd0cc"), "~", Shimmer: 0.3f)),
        new(new[] { "confetti", "remembered" },
            new(new Color("2e1a38"), new Color("ff7ad9"), "*'*,", FestivalScraps, Shimmer: 0.25f)),
        new(new[] { "flower", "bloom", "petal", "garden" },
            new(new Color("33172f"), new Color("ff8ad4"), "*'\"", BloomHeads, Shimmer: 0.15f)),

        // Imperial surfaces: clean geometry, low variance, marble and brass.
        new(new[] { "marble", "alabaster", "processional", "palace" },
            new(new Color("32303c"), new Color("d8d4e4"), "..,-", Jitter: 0.03f)),
        new(new[] { "mosaic", "patterned", "inlaid" },
            new(new Color("1c2a48"), new Color("8fa8e8"), "++x.", MosaicJewels, Jitter: 0.04f)),
        new(new[] { "tile", "archive", "registry", "inked", "scriptorium" },
            new(new Color("1e2238"), new Color("96a0d0"), "++..", Jitter: 0.03f)),

        // Growing things.
        new(new[] { "vine" },
            new(new Color("1b3a28"), new Color("5fce8a"), "\"'\"")),
        new(new[] { "grass", "turf", "meadow", "pasture", "sweetgrass", "herb", "moss" },
            new(new Color("143a20"), new Color("6fdc7f"), "..,'\"")),
        new(new[] { "reed", "wicker", "woven", "thatch", "straw" },
            new(new Color("2e3414"), new Color("c6cc6e"), "\"'==")),

        // Wood before salt so "salt_plank" reads as bleached boards, and bone before
        // plank so "bone_hall_floor" stays bone.
        new(new[] { "bone", "ivory", "skull" },
            new(new Color("322f26"), new Color("e2d8b8"), "..,%")),
        new(new[] { "plank", "wood", "board", "deck", "timber" },
            new(new Color("382818"), new Color("c89a63"), "==-.")),
        new(new[] { "salt" },
            new(new Color("3c3c44"), new Color("e8e8f0"), "..,-")),

        // Earth.
        new(new[] { "sand", "dune", "ochre", "desert" },
            new(new Color("4a3512"), new Color("ffd07a"), "..,~")),
        new(new[] { "red_earth", "clay", "terracotta", "adobe" },
            new(new Color("421f16"), new Color("e8865c"), "..,=")),
        new(new[] { "mud", "bog", "marsh" },
            new(new Color("30281a"), new Color("a08858"), "..,~")),

        // Stone family; specific names before the generic "stone" catch.
        new(new[] { "blue_stone", "bluestone" },
            new(new Color("1a2a4a"), new Color("7fa8e8"), "..,")),
        new(new[] { "shell" },
            new(new Color("362832"), new Color("f0c8d8"), "..,'")),
        new(new[] { "hearth", "warm" },
            new(new Color("3a2a1c"), new Color("e8a878"), "..,")),
        new(new[] { "cobble", "brick", "paved", "flagstone" },
            new(new Color("2a2532"), new Color("a394b4"), "..,:")),
        new(new[] { "shale", "slate", "scree", "gravel" },
            new(new Color("222834"), new Color("8e9ab0"), "..,-")),
        new(new[] { "rubble", "debris", "ruin" },
            new(new Color("38312a"), new Color("b09a7c"), "%%,.")),
        new(new[] { "road", "track", "causeway", "path", "street" },
            new(new Color("322a1e"), new Color("c2a478"), "..,:")),
        new(new[] { "wall" }, WallDefault),
        new(new[] { "stone", "rock", "granite" },
            new(new Color("262a34"), new Color("9aa4b8"), "..,")),

        // Open water last: many floors ("harbor_cobble", "tidepool_mosaic") name water they
        // merely sit beside, and their material rows above must win.
        new(new[] { "water", "canal", "tide", "pool", "wave", "mere", "river", "lagoon" },
            new(new Color("0e3450"), new Color("4fc8e8"), "~~~-", Shimmer: 0.2f)),
    };

    private static readonly Dictionary<string, TerrainStyle> Cache = new(StringComparer.Ordinal);

    public static TerrainStyle Resolve(string terrain, bool wallLike)
    {
        var key = wallLike ? terrain + "|w" : terrain;
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var lower = terrain.ToLowerInvariant();
        TerrainStyle? matched = null;
        foreach (var rule in Rules)
        {
            if (rule.Keywords.Any(keyword => lower.Contains(keyword, StringComparison.Ordinal)))
            {
                matched = rule.Style;
                break;
            }
        }

        var style = matched ?? (wallLike ? WallDefault : FloorDefault);

        // A blocking tile whose terrain name matched a floor family (an ice wall of "ice", a
        // "crystal" outcrop) keeps the family's colors but draws as mass, not texture.
        if (wallLike && style.Glyphs != "#")
        {
            style = style with { Glyphs = "#", Glyph = style.Glyph.Lightened(0.1f) };
        }

        Cache[key] = style;
        return style;
    }

    public static string GlyphFor(TerrainStyle style, int x, int y)
    {
        var glyphs = style.Glyphs;
        return glyphs.Length <= 1 ? glyphs : glyphs[Hash(x, y) % glyphs.Length].ToString();
    }

    /// <summary>Per-tile shade of the family background so large floors read as texture.</summary>
    public static Color BackgroundFor(TerrainStyle style, int x, int y)
    {
        var t = (Hash(x, y, salt: 1) % 1000 / 999f * 2f) - 1f;
        return t >= 0
            ? style.Background.Lightened(t * style.Jitter)
            : style.Background.Darkened(-t * style.Jitter);
    }

    public static Color GlyphColorFor(TerrainStyle style, int x, int y)
    {
        if (style.GlyphPalette is { Length: > 0 } palette)
        {
            return palette[Hash(x, y, salt: 2) % palette.Length];
        }

        var t = (Hash(x, y, salt: 3) % 1000 / 999f * 2f) - 1f;
        return t >= 0
            ? style.Glyph.Lightened(t * style.Jitter)
            : style.Glyph.Darkened(-t * style.Jitter);
    }

    /// <summary>Stable phase offset so neighboring shimmer tiles pulse out of step.</summary>
    public static float PhaseFor(int x, int y) => Hash(x, y, salt: 4) % 628 / 100f;

    private static int Hash(int x, int y, int salt = 0)
    {
        unchecked
        {
            var h = (uint)(x * 374761393 + y * 668265263 + salt * 2246822519);
            h = (h ^ (h >> 13)) * 1274126177u;
            return (int)((h ^ (h >> 16)) & int.MaxValue);
        }
    }
}
