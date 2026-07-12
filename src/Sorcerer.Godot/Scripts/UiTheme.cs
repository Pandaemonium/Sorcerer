using Godot;

namespace Sorcerer.Godot;

/// <summary>
/// Single source of styling data for the Godot UI: palette, fonts, styleboxes, theme type
/// variations, and BBCode-safe text formatting. Main.cs consumes this for layout and semantic
/// state only; it should not define its own colors or styleboxes.
/// </summary>
public static class UiTheme
{
    // "Color under marble" (docs/AESTHETICS_AND_TONE.md): a deep violet-black ground so jewel
    // tones glow, brass for the Empire's clean chrome, saturated accents for the wild side.
    public static readonly Color Background = new("0d0a17");
    public static readonly Color Panel = new("151022");
    public static readonly Color PanelAlt = new("1d1730");
    public static readonly Color Border = new("3a2f5c");
    public static readonly Color Text = new("e9e4f4");
    public static readonly Color Muted = new("8d84a6");
    public static readonly Color Wild = new("5cf2b4");
    public static readonly Color Empire = new("d9c8a0");
    public static readonly Color Warning = new("ffb054");
    public static readonly Color Danger = new("ff5d7a");
    public static readonly Color Focus = new("82b3ff");
    public static readonly Color Arcane = new("b48cff");
    public static readonly Color Gold = new("e8c05f");
    public static readonly Color OffMap = new("0a0812");
    public static readonly Color UnknownTile = new("120e1e");

    public const int SpaceXs = 4;
    public const int SpaceSm = 8;
    public const int SpaceMd = 12;
    public const int SpaceLg = 16;
    public const int SpaceXl = 20;

    public static Font UiFont { get; } = new SystemFont
    {
        FontNames = new[] { "Segoe UI Variable Text", "Segoe UI", "Noto Sans" },
    };

    public static Font MonoFont { get; } = new SystemFont
    {
        FontNames = new[] { "Cascadia Mono", "Consolas", "Courier New" },
    };

    public static Theme Build()
    {
        var theme = new Theme
        {
            DefaultFont = UiFont,
            DefaultFontSize = 13,
        };

        theme.SetFont("font", "Label", UiFont);
        theme.SetFontSize("font_size", "Label", 13);
        theme.SetColor("font_color", "Label", Text);

        theme.SetTypeVariation("SectionHeader", "Label");
        theme.SetFont("font", "SectionHeader", UiFont);
        theme.SetFontSize("font_size", "SectionHeader", 14);
        theme.SetColor("font_color", "SectionHeader", Empire);

        theme.SetFont("font", "Button", UiFont);
        theme.SetFontSize("font_size", "Button", 13);
        theme.SetStylebox("normal", "Button", ButtonBox(Panel, Border));
        theme.SetStylebox("hover", "Button", ButtonBox(PanelAlt.Lightened(0.06f), Arcane.Darkened(0.2f)));
        theme.SetStylebox("pressed", "Button", ButtonBox(Panel.Darkened(0.15f), Border));
        theme.SetStylebox("disabled", "Button", ButtonBox(Panel.Darkened(0.1f), Border.Darkened(0.3f)));
        theme.SetStylebox("focus", "Button", ButtonBox(Panel, Focus));
        theme.SetColor("font_color", "Button", Text);
        theme.SetColor("font_hover_color", "Button", Text);
        theme.SetColor("font_pressed_color", "Button", Muted);
        theme.SetColor("font_disabled_color", "Button", Muted.Darkened(0.4f));
        theme.SetColor("font_focus_color", "Button", Text);

        theme.SetTypeVariation("MapCell", "Button");
        theme.SetFont("font", "MapCell", MonoFont);
        theme.SetFontSize("font_size", "MapCell", 14);

        theme.SetFont("font", "LineEdit", UiFont);
        theme.SetFontSize("font_size", "LineEdit", 13);
        theme.SetStylebox("normal", "LineEdit", ButtonBox(PanelAlt, Border));
        theme.SetStylebox("focus", "LineEdit", ButtonBox(PanelAlt, Focus));
        theme.SetStylebox("read_only", "LineEdit", ButtonBox(Panel.Darkened(0.1f), Border.Darkened(0.2f)));
        theme.SetColor("font_color", "LineEdit", Text);
        theme.SetColor("font_placeholder_color", "LineEdit", Muted);

        theme.SetStylebox("panel", "PanelContainer", CardBox(Panel, Border));

        theme.SetTypeVariation("MapFrame", "PanelContainer");
        theme.SetStylebox("panel", "MapFrame", WellBox(OffMap, new Color("2e2650")));

        theme.SetTypeVariation("Pill", "PanelContainer");
        theme.SetStylebox("panel", "Pill", PillBox(Muted));

        theme.SetFont("normal_font", "RichTextLabel", MonoFont);
        theme.SetFontSize("normal_font_size", "RichTextLabel", 13);
        theme.SetColor("default_color", "RichTextLabel", Text);
        theme.SetStylebox("normal", "RichTextLabel", WellBox(PanelAlt, new Color("2a2344")));

        theme.SetStylebox("background", "ProgressBar", WellBox(PanelAlt, new Color("2a2344")));
        theme.SetStylebox("fill", "ProgressBar", Box(Wild, Wild, borderWidth: 0, radius: 6, shadow: false, marginX: 0, marginY: 0));
        theme.SetFont("font", "ProgressBar", UiFont);
        theme.SetFontSize("font_size", "ProgressBar", 11);
        theme.SetColor("font_color", "ProgressBar", Text);

        return theme;
    }

    /// <summary>
    /// Escapes text for safe interpolation into a BBCode-enabled RichTextLabel. Any dynamic
    /// (player-typed, model-generated, or content-authored) text must pass through this before
    /// reaching a rich text control, or a stray '[' would be parsed as markup.
    /// </summary>
    public static string Escape(string? text) => (text ?? string.Empty).Replace("[", "[lb]");

    /// <summary>Wraps already-escaped-safe colored BBCode around dynamic text.</summary>
    public static string Colorize(string? text, Color color) =>
        $"[color=#{color.ToHtml(false)}]{Escape(text)}[/color]";

    public static StyleBoxFlat Box(
        Color background,
        Color border,
        int borderWidth = 1,
        int radius = 8,
        bool shadow = false,
        int marginX = SpaceMd,
        int marginY = SpaceSm)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
        };
        box.SetBorderWidthAll(borderWidth);
        box.SetCornerRadiusAll(radius);
        box.ContentMarginLeft = marginX;
        box.ContentMarginRight = marginX;
        box.ContentMarginTop = marginY;
        box.ContentMarginBottom = marginY;
        if (shadow)
        {
            box.ShadowColor = new Color(0f, 0f, 0f, 0.35f);
            box.ShadowSize = 6;
            box.ShadowOffset = new Vector2(0, 2);
        }

        return box;
    }

    /// <summary>Outer panel treatment: raised card with soft shadow. Used sparingly (toolbar, map, sidebar, command row) to avoid box-in-box clutter.</summary>
    public static StyleBoxFlat CardBox(Color background, Color border) =>
        Box(background, border, borderWidth: 1, radius: 8, shadow: true);

    /// <summary>Quiet recessed treatment for content nested inside a card (sidebar readouts, map frame). No shadow.</summary>
    public static StyleBoxFlat WellBox(Color background, Color border) =>
        Box(background, border, borderWidth: 1, radius: 6, shadow: false);

    public static StyleBoxFlat ButtonBox(Color background, Color border, int borderWidth = 1) =>
        Box(background, border, borderWidth, radius: 6, shadow: false);

    public static StyleBoxFlat PillBox(Color background)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = background,
        };
        box.SetCornerRadiusAll(999);
        box.ContentMarginLeft = 10;
        box.ContentMarginRight = 10;
        box.ContentMarginTop = 3;
        box.ContentMarginBottom = 3;
        return box;
    }

    /// <summary>
    /// Full-window animated backdrop: a deep violet ground with slow aurora washes and sparse
    /// twinkling motes, kept dim so panels and text stay readable on top. This is the "thrumming
    /// with ancient magic" layer; everything else sits above it.
    /// </summary>
    public static Control MagicBackdrop()
    {
        var rect = new ColorRect
        {
            Material = new ShaderMaterial { Shader = new Shader { Code = BackdropShaderCode } },
            MouseFilter = Control.MouseFilterEnum.Ignore,
        };
        rect.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return rect;
    }

    private const string BackdropShaderCode = """
        shader_type canvas_item;

        float hash(vec2 p) {
            return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
        }

        float vnoise(vec2 p) {
            vec2 i = floor(p);
            vec2 f = fract(p);
            vec2 u = f * f * (3.0 - 2.0 * f);
            return mix(
                mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x),
                mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x),
                u.y);
        }

        void fragment() {
            vec2 uv = UV;
            float t = TIME * 0.03;
            float wash1 = vnoise(uv * 3.0 + vec2(t, -t * 0.7));
            float wash2 = vnoise(uv * 5.0 - vec2(t * 0.6, t));

            vec3 ground = vec3(0.045, 0.034, 0.082);
            vec3 violet = vec3(0.10, 0.05, 0.21);
            vec3 teal = vec3(0.02, 0.11, 0.11);
            vec3 col = ground;
            col = mix(col, violet, smoothstep(0.45, 0.95, wash1) * 0.85);
            col = mix(col, teal, smoothstep(0.50, 0.95, wash2) * 0.65);

            float vig = smoothstep(1.25, 0.35, length(uv - 0.5) * 1.6);
            col *= mix(0.72, 1.0, vig);

            vec2 grid = uv * 60.0;
            vec2 gid = floor(grid);
            vec2 gv = fract(grid) - 0.5;
            float seed = hash(gid);
            vec2 offset = (vec2(hash(gid + 7.0), hash(gid + 13.0)) - 0.5) * 0.6;
            float mote = smoothstep(0.09, 0.0, length(gv - offset)) * step(0.94, seed);
            float twinkle = 0.5 + 0.5 * sin(TIME * (0.6 + seed * 2.0) + seed * 60.0);
            col += vec3(0.45, 0.38, 0.70) * mote * twinkle * 0.5;

            COLOR = vec4(col, 1.0);
        }
        """;
}
