using Godot;
using Sorcerer.Core.Views;

namespace Sorcerer.Godot;

public partial class Rumors : Control
{
    public override void _Ready()
    {
        Theme = UiTheme.Build();
        BuildUi();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            GoBack();
            GetViewport().SetInputAsHandled();
        }
    }

    private void BuildUi()
    {
        AddChild(FullRect(new TextureRect
        {
            Texture = UiTheme.BackgroundGradient(),
            StretchMode = TextureRect.StretchModeEnum.Scale,
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
        }));

        var outer = FullRect(new MarginContainer());
        outer.AddThemeConstantOverride("margin_left", UiTheme.SpaceLg);
        outer.AddThemeConstantOverride("margin_top", UiTheme.SpaceLg);
        outer.AddThemeConstantOverride("margin_right", UiTheme.SpaceLg);
        outer.AddThemeConstantOverride("margin_bottom", UiTheme.SpaceLg);
        AddChild(outer);

        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        outer.AddChild(panel);

        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        panel.AddChild(stack);

        var header = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(header);

        var title = new Label { Text = "Rumors", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", UiTheme.Warning);
        header.AddChild(title);

        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(74, 34) };
        back.Pressed += GoBack;
        header.AddChild(back);

        var body = new RichTextLabel
        {
            BbcodeEnabled = true,
            ScrollActive = true,
            SelectionEnabled = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        stack.AddChild(body);

        var rumors = SessionHost.Session?.View().Rumors ?? Array.Empty<RumorCard>();
        body.Text = rumors.Count == 0
            ? "No rumors have reached you yet."
            : string.Join("\n\n", rumors.Select(FormatRumor));
    }

    private static string FormatRumor(RumorCard rumor)
    {
        var source = $"{rumor.SourceKind}:{rumor.SourceId}";
        var route = $"{rumor.CurrentRegionId}, {rumor.Status}, hops {rumor.Hops}, salience {rumor.Salience}";
        var tags = rumor.Tags.Count == 0
            ? ""
            : $"\n{UiTheme.Colorize("tags", UiTheme.Muted)} {UiTheme.Escape(string.Join(", ", rumor.Tags))}";
        var retelling = rumor.DistortionHistory.Count == 0
            ? ""
            : $"\n{UiTheme.Colorize("retelling", UiTheme.Muted)} {UiTheme.Escape(rumor.DistortionHistory.Last())}";
        return $"{UiTheme.Colorize(source, UiTheme.Warning)}\n{UiTheme.Escape(rumor.Text)}\n{UiTheme.Colorize(route, UiTheme.Muted)}{tags}{retelling}";
    }

    private void GoBack() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return control;
    }
}
