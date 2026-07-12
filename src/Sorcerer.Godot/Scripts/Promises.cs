using Godot;
using Sorcerer.Core.Views;

namespace Sorcerer.Godot;

public partial class Promises : Control
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

        var title = new Label { Text = "Promises", SizeFlagsHorizontal = SizeFlags.ExpandFill };
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

        var promises = SessionHost.Session?.View().Promises ?? Array.Empty<PromiseCard>();
        body.Text = promises.Count == 0
            ? "none"
            : string.Join(
                "\n\n",
                promises.Select(FormatPromise));
    }

    private static string FormatPromise(PromiseCard promise)
    {
        var state = $"{promise.Status}"
            + (string.IsNullOrWhiteSpace(promise.TriggerHint) ? "" : $" / {promise.TriggerHint}")
            + (string.IsNullOrWhiteSpace(promise.RealizationKind) ? "" : $" / {promise.RealizationKind}");
        var failure = string.IsNullOrWhiteSpace(promise.LastEligibilityFailure)
            ? ""
            : $"\n{UiTheme.Colorize("waiting", UiTheme.Muted)} {UiTheme.Escape(promise.LastEligibilityFailure)}"
                + (string.IsNullOrWhiteSpace(promise.LastEligibilityContext)
                    ? ""
                    : $" {UiTheme.Colorize(promise.LastEligibilityContext, UiTheme.Muted)}");
        var source = string.IsNullOrWhiteSpace(promise.SourceClaimId)
            ? ""
            : $"\n{UiTheme.Colorize("from", UiTheme.Muted)} {UiTheme.Escape(promise.SourceClaimId)}"
                + (promise.SourceConfidence is null
                    ? ""
                    : $" {UiTheme.Colorize($"{promise.SourceConfidence}% confidence", UiTheme.Muted)}");
        return $"{UiTheme.Colorize(promise.Kind, UiTheme.Wild)} {UiTheme.Colorize(state, UiTheme.Muted)}\n{UiTheme.Escape(promise.Text)}{source}{failure}";
    }

    private void GoBack() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        return control;
    }
}
