using Godot;
using Sorcerer.Llm.Diagnostics;

namespace Sorcerer.Godot;

/// <summary>
/// Developer overlay (F6) that lists every LLM call from <see cref="LlmTrace"/> and shows the exact
/// prompt and response for the selected one. The prompt appears the instant the call is dispatched
/// (LlmTrace records it before the request goes out), so a developer can read what was sent while
/// the model is still working.
/// </summary>
public partial class LlmDebugPanel : Control
{
    private Control _dockRoot = null!;
    private ItemList _list = null!;
    private RichTextLabel _detail = null!;

    private long _lastRevision = -1;
    private int _selectedId = -1;
    private bool _followLatest = true;

    // id of the entry at each list row, newest first, kept in sync with the ItemList rows.
    private readonly List<int> _rowIds = new();

    public bool IsOpen => _dockRoot.Visible;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsPreset(LayoutPreset.FullRect);
        BuildUi();
        SetProcess(true);
    }

    public void Toggle()
    {
        _dockRoot.Visible = !_dockRoot.Visible;
        if (IsOpen)
        {
            ForceRefresh();
        }
    }

    public override void _Process(double delta)
    {
        if (!IsOpen)
        {
            return;
        }

        var revision = LlmTrace.Revision;
        if (revision != _lastRevision)
        {
            _lastRevision = revision;
            Rebuild();
        }
    }

    private void ForceRefresh()
    {
        _lastRevision = -1;
    }

    private void BuildUi()
    {
        // Docked host: a dim full-rect panel pinned to the right two-thirds of the screen.
        _dockRoot = new PanelContainer
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop,
        };
        _dockRoot.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _dockRoot.OffsetLeft = 220;
        _dockRoot.AddThemeStyleboxOverride("panel", UiTheme.CardBox(new Color(UiTheme.Panel, 0.97f), UiTheme.Border));
        AddChild(_dockRoot);

        _dockRoot.AddChild(BuildBody());
    }

    private Control BuildBody()
    {
        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Stop };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        foreach (var side in new[] { "margin_left", "margin_top", "margin_right", "margin_bottom" })
        {
            margin.AddThemeConstantOverride(side, UiTheme.SpaceMd);
        }

        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        margin.AddChild(root);

        root.AddChild(BuildHeader());

        var split = new HSplitContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SplitOffsets = new[] { 320 },
        };
        root.AddChild(split);

        _list = new ItemList
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AllowReselect = true,
        };
        _list.AddThemeFontOverride("font", UiTheme.MonoFont);
        _list.AddThemeFontSizeOverride("font_size", 12);
        _list.ItemSelected += OnItemSelected;
        split.AddChild(_list);

        _detail = new RichTextLabel
        {
            BbcodeEnabled = true,
            SelectionEnabled = true,
            ScrollActive = true,
            FitContent = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _detail.AddThemeFontOverride("normal_font", UiTheme.MonoFont);
        _detail.AddThemeFontSizeOverride("normal_font_size", 12);
        split.AddChild(_detail);

        return margin;
    }

    private Control BuildHeader()
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", UiTheme.SpaceSm);

        var title = new Label { Text = "LLM Debug", VerticalAlignment = VerticalAlignment.Center };
        title.AddThemeFontSizeOverride("font_size", 16);
        title.AddThemeColorOverride("font_color", UiTheme.Wild);
        row.AddChild(title);

        var hint = new Label
        {
            Text = "every LLM call, prompt shown on dispatch",
            VerticalAlignment = VerticalAlignment.Center,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(hint);

        var clear = new Button { Text = "Clear" };
        clear.Pressed += () =>
        {
            LlmTrace.Clear();
            _selectedId = -1;
            _followLatest = true;
            ForceRefresh();
        };
        row.AddChild(clear);

        var close = new Button { Text = "Close (F6)" };
        close.Pressed += Toggle;
        row.AddChild(close);

        return row;
    }

    private void OnItemSelected(long index)
    {
        if (index < 0 || index >= _rowIds.Count)
        {
            return;
        }

        _selectedId = _rowIds[(int)index];
        // Selecting the newest row keeps following; picking an older one pins it.
        _followLatest = index == 0;
        RenderDetail(LlmTrace.Snapshot());
    }

    private void Rebuild()
    {
        var entries = LlmTrace.Snapshot();
        _list.Clear();
        _rowIds.Clear();

        // Newest first, so a resolving call sits at the top where the eye lands.
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            _rowIds.Add(entry.Id);
            var status = entry.Completed
                ? entry.Error is null ? "ok" : "ERR"
                : "…";
            var elapsed = entry.Completed ? $"{entry.ElapsedMs:0}ms" : "running";
            var row = _list.AddItem($"#{entry.Id} {entry.Purpose}  [{status}]  {elapsed}");
            _list.SetItemCustomFgColor(row, StatusColor(entry));
        }

        if (_followLatest && _rowIds.Count > 0)
        {
            _selectedId = _rowIds[0];
        }

        var selectedRow = _rowIds.IndexOf(_selectedId);
        if (selectedRow >= 0)
        {
            _list.Select(selectedRow);
        }

        RenderDetail(entries);
    }

    private void RenderDetail(IReadOnlyList<LlmTraceEntry> entries)
    {
        var entry = entries.FirstOrDefault(candidate => candidate.Id == _selectedId);
        if (entry is null)
        {
            _detail.Text = "[color=#87929d]Select a call to see its prompt and response.[/color]";
            return;
        }

        var status = entry.Completed
            ? entry.Error is null ? "completed" : "error"
            : "running…";
        var responseLabel = entry.Error is null ? "RESPONSE" : "ERROR";
        var responseBody = entry.Completed
            ? entry.Error ?? entry.Response ?? "(empty)"
            : "(waiting for the model…)";

        _detail.Text =
            $"[b][color=#85f0c5]#{entry.Id} {entry.Purpose}[/color][/b]  "
            + $"[color=#87929d]{entry.Model} · {status} · {entry.StartedAt:HH:mm:ss}"
            + (entry.Completed ? $" · {entry.ElapsedMs:0}ms" : "") + "[/color]\n\n"
            + $"[b][color=#8ab4ff]SYSTEM[/color][/b]\n{Escape(entry.SystemPrompt)}\n\n"
            + $"[b][color=#8ab4ff]USER[/color][/b]\n{Escape(entry.UserPrompt)}\n\n"
            + $"[b][color={(entry.Error is null ? "#85f0c5" : "#ff6b6b")}]{responseLabel}[/color][/b]\n{Escape(responseBody)}";
    }

    private static Color StatusColor(LlmTraceEntry entry) =>
        !entry.Completed ? UiTheme.Warning
        : entry.Error is null ? UiTheme.Wild
        : UiTheme.Danger;

    // RichTextLabel parses BBCode, so neutralize the '[' that JSON prompts are full of.
    private static string Escape(string text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace("[", "[lb]");
}
