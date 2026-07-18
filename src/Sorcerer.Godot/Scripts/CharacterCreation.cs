using Godot;
using Sorcerer.Core.Characters;
using Sorcerer.Core.Magic;
using Sorcerer.Godot.Portraits;
using Sorcerer.Llm.Configuration;

namespace Sorcerer.Godot;

/// <summary>
/// Single-screen character creation (design lineage: WildMagic's creation scene). Three effort
/// tiers: Enter begins with the selected origin untouched, the Random button rolls and starts
/// instantly, or the player tunes stats/identity/charter form/portrait at leisure. The screen
/// never builds the GameSession itself — it stashes a sanitized CharacterBuild in
/// SessionHost.PendingBuild and returns to Main, which owns all provider wiring. Validation is
/// prevention-by-construction: the steppers cannot overspend, and no field is required.
/// </summary>
public partial class CharacterCreation : Control
{
    private const int StatCount = 3;
    private static readonly string[] StatNames = { "Vigor", "Attunement", "Composure" };
    private static readonly string[] StatHints =
    {
        "HP, melee, and how much cost your body can shoulder",
        "mana, and how large your magic dares to be",
        "how hard wild magic bites back when it answers",
    };

    private readonly OriginCatalog _catalog = OriginCatalog.LoadDefault();
    private readonly CharterSpellbook _spellbook = CharterSpellbook.Default;

    private OriginDefinition[] _origins = Array.Empty<OriginDefinition>();
    private readonly List<Button> _originCards = new();
    private int _selectedOrigin;
    private readonly int[] _stats = new int[StatCount];

    private Label _pointsLabel = null!;
    private RichTextLabel _blurb = null!;
    private readonly Button[] _minusButtons = new Button[StatCount];
    private readonly Button[] _plusButtons = new Button[StatCount];
    private readonly Label[] _barLabels = new Label[StatCount];
    private readonly Label[] _valueLabels = new Label[StatCount];
    private readonly Label[] _previewValues = new Label[4];

    private LineEdit _name = null!;
    private TextEdit _appearance = null!;
    private TextEdit _backstory = null!;
    private LineEdit _signature = null!;
    private OptionButton _charterPick = null!;
    private Label _charterSummary = null!;

    private PortraitClient? _portraits;
    private TextureRect _portraitRect = null!;
    private Label _portraitStatus = null!;
    private Button _portraitButton = null!;
    private string? _portraitRequestId;
    private string? _portraitPath;

    public override void _Ready()
    {
        DotEnv.Load();
        Theme = UiTheme.Build();
        _origins = _catalog.Origins.ToArray();
        _portraits = new PortraitClient(ResolveOllamaHost());
        BuildUi();
        SelectOrigin(Array.FindIndex(_origins, origin => origin.Id == _catalog.Default.Id) is var index && index >= 0 ? index : 0);
        GeminiSetupNotice.ShowIfNeeded(this, EffectiveProviderName());
    }

    public override void _ExitTree()
    {
        // Frees the worker (and with it the GPU) whether we leave via Begin, Random, or Esc.
        _portraits?.Close();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
        {
            return;
        }

        // Handled-flag before Begin/GoBack: both swap scenes, which detaches this node and
        // nulls GetViewport().
        switch (key.Keycode)
        {
            case Key.Escape when SessionHost.Session is not null:
                GetViewport().SetInputAsHandled();
                GoBack();
                break;
            case Key.Enter or Key.KpEnter:
                // Plain Enter inside a multi-line box types a newline; Ctrl+Enter begins anywhere.
                if (key.CtrlPressed || GetViewport().GuiGetFocusOwner() is not TextEdit)
                {
                    GetViewport().SetInputAsHandled();
                    Begin();
                }

                break;
        }
    }

    public override void _Process(double delta)
    {
        if (_portraits is null || _portraitRequestId is null)
        {
            return;
        }

        var poll = _portraits.Poll(_portraitRequestId);
        switch (poll.Status)
        {
            case PortraitStatus.Pending:
                _portraitStatus.Text = _portraits.Warming
                    ? "model warming — the first portrait is slow"
                    : "painting…";
                break;
            case PortraitStatus.Done:
                _portraitRequestId = null;
                _portraitPath = poll.PathOrError;
                var image = Image.LoadFromFile(_portraitPath);
                if (image is not null)
                {
                    _portraitRect.Texture = ImageTexture.CreateFromImage(image);
                }

                _portraitStatus.Text = "portrait ready";
                _portraitStatus.AddThemeColorOverride("font_color", UiTheme.Muted);
                _portraitButton.Text = "Regenerate portrait";
                _portraitButton.Disabled = false;
                break;
            default:
                _portraitRequestId = null;
                _portraitStatus.Text = poll.PathOrError ?? "portrait failed";
                _portraitStatus.AddThemeColorOverride("font_color", UiTheme.Danger);
                _portraitButton.Disabled = false;
                break;
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

        var title = new Label
        {
            Text = "Who slips the Censorate's net?",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", UiTheme.Wild);
        header.AddChild(title);

        if (SessionHost.Session is not null)
        {
            var back = new Button { Text = "Back", CustomMinimumSize = new Vector2(74, 34) };
            back.Pressed += GoBack;
            header.AddChild(back);
        }

        var body = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        body.AddThemeConstantOverride("separation", UiTheme.SpaceMd);
        stack.AddChild(body);

        body.AddChild(BuildOriginColumn());
        body.AddChild(BuildBuildColumn());
        body.AddChild(BuildPortraitColumn());
    }

    private Control BuildOriginColumn()
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };

        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        scroll.AddChild(column);

        column.AddChild(new Label { Text = "Origins", ThemeTypeVariation = "SectionHeader" });

        var group = new ButtonGroup();
        for (var index = 0; index < _origins.Length; index++)
        {
            var card = BuildOriginCard(_origins[index], index, group);
            _originCards.Add(card);
            column.AddChild(card);
        }

        var random = new Button
        {
            Text = "Random wild mage",
            CustomMinimumSize = new Vector2(0, 38),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        random.Pressed += BeginRandom;
        column.AddChild(random);

        return scroll;
    }

    private Button BuildOriginCard(OriginDefinition origin, int index, ButtonGroup group)
    {
        var card = new Button
        {
            ToggleMode = true,
            ButtonGroup = group,
            CustomMinimumSize = new Vector2(0, 72),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        card.Pressed += () => SelectOrigin(index);

        var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", UiTheme.SpaceSm);
        margin.AddThemeConstantOverride("margin_right", UiTheme.SpaceSm);
        margin.AddThemeConstantOverride("margin_top", UiTheme.SpaceXs);
        margin.AddThemeConstantOverride("margin_bottom", UiTheme.SpaceXs);
        card.AddChild(margin);

        var lines = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
        lines.AddThemeConstantOverride("separation", 1);
        margin.AddChild(lines);

        var name = new Label
        {
            Text = origin.DisplayName,
            MouseFilter = MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        lines.AddChild(name);

        var tradition = new Label
        {
            Text = origin.Tradition,
            MouseFilter = MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        tradition.AddThemeFontSizeOverride("font_size", 11);
        tradition.AddThemeColorOverride("font_color", UiTheme.Muted);
        lines.AddChild(tradition);

        var derived = new Label
        {
            Text = DerivedLine(origin),
            MouseFilter = MouseFilterEnum.Ignore,
            TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis,
        };
        derived.AddThemeFontSizeOverride("font_size", 11);
        derived.AddThemeColorOverride("font_color", UiTheme.Muted);
        lines.AddChild(derived);

        return card;
    }

    private static string DerivedLine(OriginDefinition origin)
    {
        var line = $"HP {CharacterMath.MaxHitPointsFromVigor(origin.BodyVigor)}"
            + $" · Mana {CharacterMath.MaxManaFromAttunement(origin.SoulAttunement)}";
        var charterCount = origin.StartingCharterSpells?.Count ?? 0;
        return charterCount > 0 ? $"{line} · charter ×{charterCount}" : line;
    }

    private Control BuildBuildColumn()
    {
        var scroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.4f,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
        };

        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        column.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        scroll.AddChild(column);

        _blurb = new RichTextLabel
        {
            BbcodeEnabled = true,
            FitContent = true,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 84),
        };
        column.AddChild(_blurb);

        _pointsLabel = new Label { ThemeTypeVariation = "SectionHeader" };
        column.AddChild(_pointsLabel);

        for (var stat = 0; stat < StatCount; stat++)
        {
            column.AddChild(BuildStatRow(stat));

            var hint = new Label { Text = StatHints[stat] };
            hint.AddThemeFontSizeOverride("font_size", 11);
            hint.AddThemeColorOverride("font_color", UiTheme.Muted);
            column.AddChild(hint);
        }

        column.AddChild(BuildPreviewRow());

        column.AddChild(new Label { Text = "Identity", ThemeTypeVariation = "SectionHeader" });

        _name = new LineEdit { CustomMinimumSize = new Vector2(0, 34) };
        column.AddChild(Labeled("Name", _name));

        _appearance = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 56),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ScrollFitContentHeight = true,
        };
        column.AddChild(Labeled("Appearance — what the world (and the resolver) sees", _appearance));

        _backstory = new TextEdit
        {
            CustomMinimumSize = new Vector2(0, 72),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
            ScrollFitContentHeight = true,
        };
        column.AddChild(Labeled("Backstory", _backstory));

        _signature = new LineEdit { CustomMinimumSize = new Vector2(0, 34) };
        column.AddChild(Labeled("Magical signature — the idiom your casts arrive in", _signature));

        column.AddChild(new Label { Text = "Charter form (bonus)", ThemeTypeVariation = "SectionHeader" });

        _charterPick = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _charterPick.ItemSelected += _ => UpdateCharterSummary();
        column.AddChild(_charterPick);

        _charterSummary = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _charterSummary.AddThemeFontSizeOverride("font_size", 11);
        _charterSummary.AddThemeColorOverride("font_color", UiTheme.Muted);
        column.AddChild(_charterSummary);

        return scroll;
    }

    private Control BuildStatRow(int stat)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", UiTheme.SpaceSm);

        var name = new Label { Text = StatNames[stat], CustomMinimumSize = new Vector2(92, 0) };
        row.AddChild(name);

        var minus = new Button { Text = "−", CustomMinimumSize = new Vector2(34, 34) };
        minus.Pressed += () => AdjustStat(stat, -1);
        row.AddChild(minus);
        _minusButtons[stat] = minus;

        var bar = new Label { HorizontalAlignment = HorizontalAlignment.Center };
        bar.AddThemeFontOverride("font", UiTheme.MonoFont);
        row.AddChild(bar);
        _barLabels[stat] = bar;

        var value = new Label
        {
            CustomMinimumSize = new Vector2(24, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        row.AddChild(value);
        _valueLabels[stat] = value;

        var plus = new Button { Text = "+", CustomMinimumSize = new Vector2(34, 34) };
        plus.Pressed += () => AdjustStat(stat, +1);
        row.AddChild(plus);
        _plusButtons[stat] = plus;

        return row;
    }

    private Control BuildPreviewRow()
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        row.AddThemeConstantOverride("separation", UiTheme.SpaceSm);

        var accents = new[] { UiTheme.Wild, UiTheme.Focus, UiTheme.Warning, UiTheme.Empire };
        for (var chip = 0; chip < _previewValues.Length; chip++)
        {
            var pill = new PanelContainer { ThemeTypeVariation = "Pill" };
            pill.AddThemeStyleboxOverride("panel", UiTheme.PillBox(UiTheme.PanelAlt));
            var label = new Label { VerticalAlignment = VerticalAlignment.Center };
            label.AddThemeColorOverride("font_color", accents[chip]);
            pill.AddChild(label);
            row.AddChild(pill);
            _previewValues[chip] = label;
        }

        return row;
    }

    private Control BuildPortraitColumn()
    {
        var column = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.0f,
        };
        column.AddThemeConstantOverride("separation", UiTheme.SpaceSm);

        if (_portraits?.Available == true)
        {
            column.AddChild(new Label { Text = "Portrait", ThemeTypeVariation = "SectionHeader" });

            var frame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            frame.AddThemeStyleboxOverride("panel", UiTheme.WellBox(UiTheme.OffMap, UiTheme.Border));
            _portraitRect = new TextureRect
            {
                CustomMinimumSize = new Vector2(256, 256),
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            };
            frame.AddChild(_portraitRect);
            column.AddChild(frame);

            _portraitStatus = new Label
            {
                AutowrapMode = TextServer.AutowrapMode.WordSmart,
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            _portraitStatus.AddThemeFontSizeOverride("font_size", 11);
            _portraitStatus.AddThemeColorOverride("font_color", UiTheme.Muted);
            column.AddChild(_portraitStatus);

            _portraitButton = new Button
            {
                Text = "Generate portrait",
                CustomMinimumSize = new Vector2(0, 38),
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
            };
            _portraitButton.Pressed += GeneratePortrait;
            column.AddChild(_portraitButton);
        }

        column.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill });

        var begin = new Button
        {
            Text = "Begin ▸",
            CustomMinimumSize = new Vector2(0, 48),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        begin.AddThemeFontSizeOverride("font_size", 16);
        begin.Pressed += Begin;
        column.AddChild(begin);

        var hint = new Label
        {
            Text = SessionHost.Session is null
                ? "Enter — begin · Ctrl+Enter — begin from a text box"
                : "Enter — begin · Ctrl+Enter — from a text box · Esc — back",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        hint.AddThemeFontSizeOverride("font_size", 11);
        hint.AddThemeColorOverride("font_color", UiTheme.Muted);
        column.AddChild(hint);

        return column;
    }

    private static Control Labeled(string caption, Control field)
    {
        var box = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        box.AddThemeConstantOverride("separation", 2);
        var label = new Label { Text = caption };
        label.AddThemeFontSizeOverride("font_size", 11);
        label.AddThemeColorOverride("font_color", UiTheme.Muted);
        box.AddChild(label);
        box.AddChild(field);
        return box;
    }

    private void SelectOrigin(int index)
    {
        _selectedOrigin = Math.Clamp(index, 0, _origins.Length - 1);
        var origin = _origins[_selectedOrigin];

        // Spend resets when the baseline changes; typed identity text intentionally survives
        // card switches (WildMagic semantics) — only the placeholders underneath change.
        _stats[0] = origin.BodyVigor;
        _stats[1] = origin.SoulAttunement;
        _stats[2] = origin.SoulComposure;

        for (var card = 0; card < _originCards.Count; card++)
        {
            var selected = card == _selectedOrigin;
            _originCards[card].SetPressedNoSignal(selected);
            if (selected)
            {
                _originCards[card].AddThemeStyleboxOverride(
                    "normal", UiTheme.ButtonBox(UiTheme.PanelAlt, UiTheme.Focus));
                _originCards[card].AddThemeStyleboxOverride(
                    "pressed", UiTheme.ButtonBox(UiTheme.PanelAlt, UiTheme.Focus));
                _originCards[card].AddThemeStyleboxOverride(
                    "hover", UiTheme.ButtonBox(UiTheme.PanelAlt, UiTheme.Focus));
            }
            else
            {
                _originCards[card].RemoveThemeStyleboxOverride("normal");
                _originCards[card].RemoveThemeStyleboxOverride("pressed");
                _originCards[card].RemoveThemeStyleboxOverride("hover");
            }
        }

        _blurb.Text = $"{UiTheme.Colorize(origin.Tradition, UiTheme.Arcane)}\n{UiTheme.Escape(origin.Backstory)}";

        _name.PlaceholderText = origin.PublicName;
        _appearance.PlaceholderText = origin.Appearance;
        _backstory.PlaceholderText = origin.Backstory;
        _signature.PlaceholderText = origin.MagicalSignature;

        RebuildCharterOptions(origin);
        RefreshBuildReadouts();
    }

    private void RebuildCharterOptions(OriginDefinition origin)
    {
        var previous = SelectedCharterId();
        _charterPick.Clear();
        _charterPick.AddItem("None — wild only");
        foreach (var spell in _spellbook.Spells)
        {
            if (origin.StartingCharterSpells?.Contains(spell.Id, StringComparer.OrdinalIgnoreCase) == true)
            {
                continue;
            }

            _charterPick.AddItem($"{spell.Name} — {spell.CostText}");
            _charterPick.SetItemMetadata(_charterPick.ItemCount - 1, spell.Id);
        }

        if (previous is not null)
        {
            for (var item = 1; item < _charterPick.ItemCount; item++)
            {
                if (string.Equals(_charterPick.GetItemMetadata(item).AsString(), previous, StringComparison.OrdinalIgnoreCase))
                {
                    _charterPick.Selected = item;
                    break;
                }
            }
        }

        UpdateCharterSummary();
    }

    private string? SelectedCharterId()
    {
        if (_charterPick is null || _charterPick.Selected <= 0)
        {
            return null;
        }

        var metadata = _charterPick.GetItemMetadata(_charterPick.Selected).AsString();
        return string.IsNullOrWhiteSpace(metadata) ? null : metadata;
    }

    private void UpdateCharterSummary()
    {
        var spell = _spellbook.Find(SelectedCharterId());
        _charterSummary.Text = spell?.Summary
            ?? "Charter forms are the Empire's licensed instant spells — no model, no gamble.";
    }

    private void AdjustStat(int stat, int delta)
    {
        var origin = _origins[_selectedOrigin];
        var baselines = OriginBaselines(origin);
        var next = _stats[stat] + delta;
        if (next < baselines[stat] || next > CreationRules.StatCap)
        {
            return;
        }

        if (delta > 0 && PointsRemaining() <= 0)
        {
            return;
        }

        _stats[stat] = next;
        RefreshBuildReadouts();
    }

    private static int[] OriginBaselines(OriginDefinition origin) =>
        new[] { origin.BodyVigor, origin.SoulAttunement, origin.SoulComposure };

    private int PointsRemaining()
    {
        var origin = _origins[_selectedOrigin];
        return CreationRules.PointPool - CreationRules.PointsSpent(
            origin, new CharacterBuild(origin.Id, _stats[0], _stats[1], _stats[2]));
    }

    private void RefreshBuildReadouts()
    {
        var origin = _origins[_selectedOrigin];
        var baselines = OriginBaselines(origin);
        var remaining = PointsRemaining();

        _pointsLabel.Text = remaining == 1
            ? "Stats — 1 point to spend"
            : $"Stats — {remaining} points to spend";

        for (var stat = 0; stat < StatCount; stat++)
        {
            _barLabels[stat].Text =
                $"[{new string('#', _stats[stat])}{new string('-', CreationRules.StatCap - _stats[stat])}]";
            _valueLabels[stat].Text = _stats[stat].ToString();
            _minusButtons[stat].Disabled = _stats[stat] <= baselines[stat];
            _plusButtons[stat].Disabled = _stats[stat] >= CreationRules.StatCap || remaining <= 0;
        }

        _previewValues[0].Text = $"HP {CharacterMath.MaxHitPointsFromVigor(_stats[0])}";
        _previewValues[1].Text = $"Mana {CharacterMath.MaxManaFromAttunement(_stats[1])}";
        _previewValues[2].Text = $"Atk {CharacterMath.AttackFromVigor(_stats[0])}";
        _previewValues[3].Text = $"Def {CharacterMath.DefenseFromVigor(_stats[0])}";
    }

    private void GeneratePortrait()
    {
        if (_portraits is null)
        {
            return;
        }

        var description = string.IsNullOrWhiteSpace(_appearance.Text)
            ? _origins[_selectedOrigin].Appearance
            : _appearance.Text;
        _portraitRequestId = _portraits.Request(description, Random.Shared.Next());
        if (_portraitRequestId is null)
        {
            _portraitStatus.Text = "portrait generator unavailable";
            _portraitStatus.AddThemeColorOverride("font_color", UiTheme.Danger);
            return;
        }

        _portraitStatus.Text = _portraits.Warming
            ? "model warming — the first portrait is slow"
            : "painting…";
        _portraitStatus.AddThemeColorOverride("font_color", UiTheme.Muted);
        _portraitButton.Disabled = true;
    }

    private CharacterBuild CurrentBuild()
    {
        var origin = _origins[_selectedOrigin];
        return new CharacterBuild(
            origin.Id,
            _stats[0],
            _stats[1],
            _stats[2],
            _name.Text,
            _appearance.Text,
            _backstory.Text,
            _signature.Text,
            SelectedCharterId(),
            _portraitPath);
    }

    private void Begin() => LaunchRun(CreationRules.Sanitize(CurrentBuild(), _catalog, _spellbook));

    private void BeginRandom() => LaunchRun(CreationRules.RandomBuild(_catalog, new Random()));

    private void LaunchRun(CharacterBuild build)
    {
        SessionHost.PendingBuild = build;
        SessionHost.Session = null;
        GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");
    }

    private void GoBack() => GetTree().ChangeSceneToFile("res://Scenes/Main.tscn");

    /// <summary>Which Ollama the portrait worker should evict models from before it takes the
    /// GPU. The Esc-menu host override applies when the run itself targets Ollama; otherwise
    /// the usual env fallback chain (mirrors Main.DefaultProviderHost's ollama branch).</summary>
    private static string ResolveOllamaHost()
    {
        var provider = EffectiveProviderName();
        if (provider.Trim().ToLowerInvariant() == "ollama"
            && !string.IsNullOrWhiteSpace(SessionHost.HostOverride))
        {
            return SessionHost.HostOverride;
        }

        return System.Environment.GetEnvironmentVariable("SORCERER_OLLAMA_HOST")
            ?? System.Environment.GetEnvironmentVariable("WILDMAGIC_OLLAMA_HOST")
            ?? System.Environment.GetEnvironmentVariable("OLLAMA_HOST")
            ?? "http://127.0.0.1:11434";
    }

    private static string EffectiveProviderName() =>
        SessionHost.ProviderOverride
        ?? System.Environment.GetEnvironmentVariable("SORCERER_PROVIDER")
        ?? "ollama";

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(LayoutPreset.FullRect);
        return control;
    }
}
