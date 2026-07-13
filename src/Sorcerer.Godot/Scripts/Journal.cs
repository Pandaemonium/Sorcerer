using Godot;

namespace Sorcerer.Godot;

public partial class Journal : Control
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
            // Handled-flag first: GoBack swaps scenes, which detaches this node and nulls GetViewport().
            GetViewport().SetInputAsHandled();
            GoBack();
        }
    }

    private void BuildUi()
    {
        AddChild(UiTheme.MagicBackdrop());

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

        var title = new Label { Text = "Journal", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", UiTheme.Wild);
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

        var journal = SessionHost.Session?.View().Journal ?? Array.Empty<string>();
        body.Text = journal.Count == 0
            ? "No journal entries are visible yet."
            : string.Join("\n\n", journal.Select(FormatLine));
    }

    private static string FormatLine(string line)
    {
        var color = line.StartsWith("Objective:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Completed objective:", StringComparison.OrdinalIgnoreCase)
            ? UiTheme.Wild
            : line.StartsWith("Claim:", StringComparison.OrdinalIgnoreCase)
                ? UiTheme.Focus
                : line.StartsWith("Rumor:", StringComparison.OrdinalIgnoreCase)
                    ? UiTheme.Warning
                    : line.StartsWith("Pressure:", StringComparison.OrdinalIgnoreCase)
                      || line.StartsWith("Warrant:", StringComparison.OrdinalIgnoreCase)
                        ? UiTheme.Danger
                        : line.StartsWith("Legend:", StringComparison.OrdinalIgnoreCase)
                            ? UiTheme.Empire
                            : UiTheme.Text;
        var separator = line.IndexOf(':');
        if (separator <= 0)
        {
            return UiTheme.Escape(line);
        }

        return $"{UiTheme.Colorize(line[..separator], color)}{UiTheme.Escape(line[separator..])}";
    }

    private void GoBack() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return control;
    }
}
