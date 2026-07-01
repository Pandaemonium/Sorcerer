using Godot;

namespace Sorcerer.Godot;

/// <summary>
/// Single source of styling data for the Godot UI: palette, fonts, styleboxes, theme type
/// variations, and BBCode-safe text formatting. Main.cs consumes this for layout and semantic
/// state only; it should not define its own colors or styleboxes.
/// </summary>
public static class UiTheme
{
    public static readonly Color Background = new("101216");
    public static readonly Color Panel = new("171b22");
    public static readonly Color PanelAlt = new("1e242d");
    public static readonly Color Border = new("33404d");
    public static readonly Color Text = new("dce6ec");
    public static readonly Color Muted = new("87929d");
    public static readonly Color Wild = new("85f0c5");
    public static readonly Color Empire = new("e6d8c5");
    public static readonly Color Warning = new("ffb86b");
    public static readonly Color Danger = new("ff6b6b");
    public static readonly Color Focus = new("8ab4ff");
    public static readonly Color OffMap = new("0b0e12");
    public static readonly Color UnknownTile = new("0f1318");

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
        theme.SetColor("font_color", "SectionHeader", Text);

        theme.SetFont("font", "Button", UiFont);
        theme.SetFontSize("font_size", "Button", 13);
        theme.SetStylebox("normal", "Button", ButtonBox(Panel, Border));
        theme.SetStylebox("hover", "Button", ButtonBox(PanelAlt.Lightened(0.06f), Focus.Darkened(0.2f)));
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
        theme.SetStylebox("panel", "MapFrame", WellBox(OffMap, new Color("222933")));

        theme.SetTypeVariation("Pill", "PanelContainer");
        theme.SetStylebox("panel", "Pill", PillBox(Muted));

        theme.SetFont("normal_font", "RichTextLabel", MonoFont);
        theme.SetFontSize("normal_font_size", "RichTextLabel", 13);
        theme.SetColor("default_color", "RichTextLabel", Text);
        theme.SetStylebox("normal", "RichTextLabel", WellBox(PanelAlt, new Color("26313d")));

        theme.SetStylebox("background", "ProgressBar", WellBox(PanelAlt, new Color("26313d")));
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

    public static GradientTexture2D BackgroundGradient()
    {
        var gradient = new Gradient();
        gradient.SetColor(0, new Color("12141a"));
        gradient.SetColor(1, new Color("0a0b0e"));
        return new GradientTexture2D
        {
            Gradient = gradient,
            Width = 8,
            Height = 512,
            FillFrom = new Vector2(0, 0),
            FillTo = new Vector2(0, 1),
        };
    }
}
