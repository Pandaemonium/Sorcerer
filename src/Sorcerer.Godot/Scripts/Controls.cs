using Godot;

namespace Sorcerer.Godot;

/// <summary>
/// The controls screen: rebindable actions (persisted via Keybindings), the fixed keys that
/// cannot move, and a map of the typed command surface. Reads nothing from the session, so it
/// opens safely from any state; Back returns to Main, which resumes the stashed session.
/// </summary>
public partial class Controls : Control
{
    private BindableAction? _capturing;
    private readonly Dictionary<BindableAction, Button> _chips = new();
    private Label _notice = null!;

    public override void _Ready()
    {
        Theme = UiTheme.Build();
        BuildUi();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        if (_capturing is { } action)
        {
            HandleCapture(action, key.Keycode);
            GetViewport().SetInputAsHandled();
            return;
        }

        if (key.Keycode == Key.Escape)
        {
            // Handled-flag first: GoBack swaps scenes, which detaches this node and nulls GetViewport().
            GetViewport().SetInputAsHandled();
            GoBack();
        }
    }

    private void HandleCapture(BindableAction action, Key key)
    {
        if (key == Key.Escape)
        {
            EndCapture(action);
            return;
        }

        if (Keybindings.IsReserved(key))
        {
            ShowNotice($"{Keybindings.DisplayName(key)} is reserved. Press another key, or Esc to keep {Keybindings.DisplayName(Keybindings.KeyFor(action))}.");
            return;
        }

        if (!Keybindings.TrySetKey(action, key, out var conflict))
        {
            ShowNotice(conflict is { } holder
                ? $"{Keybindings.DisplayName(key)} is already bound to \"{Keybindings.LabelFor(holder)}\". Press another key, or Esc to cancel."
                : "That key cannot be bound. Press another key, or Esc to cancel.");
            return;
        }

        ClearNotice();
        EndCapture(action);
    }

    private void EndCapture(BindableAction action)
    {
        _capturing = null;
        _chips[action].Text = Keybindings.DisplayName(Keybindings.KeyFor(action));
    }

    private void BeginCapture(BindableAction action)
    {
        if (_capturing is { } previous)
        {
            EndCapture(previous);
        }

        _capturing = action;
        _chips[action].Text = "press a key…";
        ClearNotice();
    }

    private void ShowNotice(string text)
    {
        _notice.Text = text;
        _notice.AddThemeColorOverride("font_color", UiTheme.Warning);
    }

    private void ClearNotice()
    {
        _notice.Text = "Click a key to rebind it. Esc cancels a capture.";
        _notice.AddThemeColorOverride("font_color", UiTheme.Muted);
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

        var title = new Label { Text = "Controls", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", UiTheme.Wild);
        header.AddChild(title);

        var reset = new Button { Text = "Reset to defaults", CustomMinimumSize = new Vector2(0, 34) };
        reset.Pressed += () =>
        {
            Keybindings.ResetToDefaults();
            foreach (var (action, chip) in _chips)
            {
                chip.Text = Keybindings.DisplayName(Keybindings.KeyFor(action));
            }

            _capturing = null;
            ClearNotice();
        };
        header.AddChild(reset);

        var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(74, 34) };
        back.Pressed += GoBack;
        header.AddChild(back);

        _notice = new Label { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _notice.AddThemeFontSizeOverride("font_size", 12);
        stack.AddChild(_notice);
        ClearNotice();

        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };
        stack.AddChild(scroll);

        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        scroll.AddChild(body);

        // Two columns: rebindable actions on the left, fixed keys + typed verbs on the right.
        var columns = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", UiTheme.SpaceXl);
        body.AddChild(columns);

        var left = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f,
        };
        left.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        columns.AddChild(left);

        var right = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.2f,
        };
        right.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        columns.AddChild(right);

        string? category = null;
        foreach (var spec in Keybindings.Specs)
        {
            if (spec.Category != category)
            {
                category = spec.Category;
                left.AddChild(SectionLabel(category));
            }

            left.AddChild(BindingRow(spec));
        }

        right.AddChild(SectionLabel("Fixed keys"));
        foreach (var (keys, meaning) in FixedKeys)
        {
            right.AddChild(FixedRow(keys, meaning));
        }

        right.AddChild(SectionLabel("Typed commands"));
        var verbs = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        verbs.Text = TypedCommandsText();
        right.AddChild(verbs);
    }

    private static readonly (string Keys, string Meaning)[] FixedKeys =
    {
        ("Esc", "menu · cancel a rebind · skip a casting minigame"),
        ("Enter", "submit the spell or command box"),
        ("Arrow keys", "move (always, alongside your bindings)"),
        ("Numpad 1–9", "move; 5 waits — works even while typing"),
        ("Ctrl + key", "use a bound key while a text box has focus"),
        ("Right-click", "context actions on a map tile"),
        ("Minigames", "keys are shown on screen; Esc skips"),
    };

    private Control BindingRow(ActionSpec spec)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", UiTheme.SpaceSm);

        var label = new Label { Text = spec.Label, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddChild(label);

        var chip = new Button
        {
            Text = Keybindings.DisplayName(Keybindings.KeyFor(spec.Action)),
            CustomMinimumSize = new Vector2(110, 30),
        };
        chip.AddThemeFontOverride("font", UiTheme.MonoFont);
        chip.Pressed += () => BeginCapture(spec.Action);
        row.AddChild(chip);
        _chips[spec.Action] = chip;

        return row;
    }

    private static Control FixedRow(string keys, string meaning)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", UiTheme.SpaceSm);

        var keyLabel = new Label { Text = keys, CustomMinimumSize = new Vector2(110, 0) };
        keyLabel.AddThemeFontOverride("font", UiTheme.MonoFont);
        keyLabel.AddThemeColorOverride("font_color", UiTheme.Text);
        row.AddChild(keyLabel);

        var meaningLabel = new Label
        {
            Text = meaning,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        meaningLabel.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(meaningLabel);

        return row;
    }

    private static Label SectionLabel(string text) =>
        new() { Text = text, ThemeTypeVariation = "SectionHeader" };

    private static string TypedCommandsText()
    {
        static string Group(string name, string verbs) =>
            $"{UiTheme.Colorize(name, UiTheme.Empire)}  {UiTheme.Escape(verbs)}";

        var lines = new[]
        {
            "The command box runs typed verbs; many have no button on purpose.",
            Group("Movement", "n s e w ne nw se sw · travel <dir> · enter · leave · map"),
            Group("Magic", "cast <any words> · charter [form] · echoes / echo <n> · target <x> <y> / untarget"),
            Group("Items", "pickup · drop · use · equip / unequip · focus / unfocus · protect / unprotect · reagents · read · examine"),
            Group("People", "talk <words> · give <item> to <who> · recruit · bonds · standing · followers · possess"),
            Group("World", "atlas · journal · rumors · character · services · request <service> · wares · buy / sell · open"),
            Group("Meta", "save · load · help · quit"),
            "Type 'help' in the command box for the authoritative list.",
        };
        return string.Join("\n", lines);
    }

    private void GoBack() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(LayoutPreset.FullRect);
        return control;
    }
}
