using Godot;
using Sorcerer.Core.Views;

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
        if (@event is InputEventKey { Pressed: true, Echo: false } key
            && (key.Keycode == Key.Escape
                || Keybindings.ActionForKey(key.Keycode) == BindableAction.OpenJournal))
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

        var view = SessionHost.Session?.View();
        body.Text = view?.StructuredJournal is { } journal
            ? FormatStructured(journal)
            : FormatLegacy(view?.Journal ?? Array.Empty<string>());
    }

    private static string FormatStructured(JournalView journal)
    {
        var sections = new List<string>();
        AddEntries(sections, "Objectives", journal.Objectives, UiTheme.Wild);
        AddEntries(sections, "Promises", journal.Promises, UiTheme.Focus);
        AddEntries(sections, "Rumors & secrets", journal.Rumors, UiTheme.Warning);

        if (journal.Pressures.Count > 0)
        {
            var lines = journal.Pressures.Select(pressure =>
                $"{UiTheme.Colorize(UiTheme.Escape(pressure.Kind.Replace('_', ' ')), UiTheme.Danger)}"
                + $" [turn {pressure.DueTurn}] — {UiTheme.Escape(pressure.Text)}");
            sections.Add(Section("Approaching pressure", lines));
        }

        AddEntries(sections, "Delivered & resolved threads", journal.Threads, UiTheme.Empire);
        return sections.Count == 0
            ? "No journal entries are visible yet."
            : string.Join("\n\n", sections);
    }

    private static void AddEntries(
        ICollection<string> sections,
        string title,
        IReadOnlyList<JournalEntryCard> entries,
        Color color)
    {
        if (entries.Count == 0)
        {
            return;
        }

        sections.Add(Section(title, entries.Select(entry => FormatEntry(entry, color))));
    }

    private static string Section(string title, IEnumerable<string> lines) =>
        $"[font_size=17]{UiTheme.Colorize(UiTheme.Escape(title), UiTheme.Wild)}[/font_size]\n"
        + string.Join("\n", lines.Select(line => $"  • {line}"));

    private static string FormatEntry(JournalEntryCard entry, Color color)
    {
        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(entry.Status))
        {
            metadata.Add(entry.Status);
        }

        if (!string.IsNullOrWhiteSpace(entry.Carrier))
        {
            metadata.Add($"carrier: {entry.Carrier}");
        }
        else if (!string.IsNullOrWhiteSpace(entry.Source))
        {
            metadata.Add(entry.Source);
        }

        if (!string.IsNullOrWhiteSpace(entry.Destination))
        {
            metadata.Add($"toward {entry.Destination}");
        }

        var suffix = metadata.Count == 0
            ? ""
            : $" [color={UiTheme.Muted.ToHtml()}]({UiTheme.Escape(string.Join("; ", metadata))})[/color]";
        return $"{UiTheme.Colorize(UiTheme.Escape(entry.Text), color)}{suffix}";
    }

    private static string FormatLegacy(IReadOnlyList<string> journal) =>
        journal.Count == 0
            ? "No journal entries are visible yet."
            : string.Join("\n\n", journal.Select(FormatLine));

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
