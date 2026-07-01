using Godot;
using Sorcerer.Core;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Views;
using Sorcerer.Llm;
using Sorcerer.Llm.Auditing;
using Sorcerer.Magic;

namespace Sorcerer.Godot;

public partial class Main : Control
{
    private static readonly Color Background = new("101216");
    private static readonly Color Panel = new("171b22");
    private static readonly Color PanelAlt = new("1e242d");
    private static readonly Color Border = new("33404d");
    private static readonly Color Text = new("dce6ec");
    private static readonly Color Muted = new("87929d");
    private static readonly Color Wild = new("85f0c5");
    private static readonly Color Empire = new("e6d8c5");
    private static readonly Color Warning = new("ffb86b");
    private static readonly Color Danger = new("ff6b6b");
    private static readonly Color Focus = new("8ab4ff");

    private readonly List<Control> _busyControls = new();
    private Button[,] _cells = new Button[0, 0];

    private GameSession _session = null!;
    private GridContainer _mapGrid = null!;
    private Label _title = null!;
    private Label _providerStatus = null!;
    private RichTextLabel _status = null!;
    private RichTextLabel _world = null!;
    private RichTextLabel _entities = null!;
    private RichTextLabel _inventory = null!;
    private RichTextLabel _promises = null!;
    private RichTextLabel _log = null!;
    private LineEdit _spellLine = null!;
    private LineEdit _commandLine = null!;
    private LineEdit _model = null!;
    private Button _cast = null!;
    private Button _beginCast = null!;
    private Button _awaitCast = null!;
    private Button _cancelCast = null!;

    private ActionResult? _lastResult;
    private string? _lastError;
    private bool _busy;

    public override void _Ready()
    {
        BuildUi();
        StartNewRun();
        RefreshView();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo || _busy)
        {
            return;
        }

        if (GetViewport().GuiGetFocusOwner() is LineEdit)
        {
            return;
        }

        if (key.Keycode == Key.C)
        {
            _spellLine.GrabFocus();
            GetViewport().SetInputAsHandled();
            return;
        }

        var command = CommandForKey(key.Keycode);
        if (command is null)
        {
            return;
        }

        _ = ExecuteAsync(command);
        GetViewport().SetInputAsHandled();
    }

    private void BuildUi()
    {
        AddChild(FullRect(new ColorRect { Color = Background }));

        var outer = FullRect(new MarginContainer());
        outer.AddThemeConstantOverride("margin_left", 14);
        outer.AddThemeConstantOverride("margin_top", 14);
        outer.AddThemeConstantOverride("margin_right", 14);
        outer.AddThemeConstantOverride("margin_bottom", 14);
        AddChild(outer);

        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", 10);
        outer.AddChild(root);

        root.AddChild(BuildToolbar());

        var body = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        body.AddThemeConstantOverride("separation", 10);
        root.AddChild(body);

        body.AddChild(BuildMapPanel());
        body.AddChild(BuildSidePanel());
        root.AddChild(BuildCommandPanel());
    }

    private Control BuildToolbar()
    {
        var bar = PanelBox();
        var row = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        row.AddThemeConstantOverride("separation", 10);
        bar.AddChild(row);

        _title = new Label
        {
            Text = "Sorcerer",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _title.AddThemeFontSizeOverride("font_size", 22);
        _title.AddThemeColorOverride("font_color", Wild);
        row.AddChild(_title);

        _providerStatus = SmallLabel("");
        _providerStatus.CustomMinimumSize = new Vector2(170, 0);
        row.AddChild(_providerStatus);

        var providerLabel = SmallLabel("Ollama");
        providerLabel.CustomMinimumSize = new Vector2(78, 34);
        row.AddChild(providerLabel);

        _model = new LineEdit
        {
            Text = System.Environment.GetEnvironmentVariable("SORCERER_MODEL")
                ?? System.Environment.GetEnvironmentVariable("WILDMAGIC_MODEL")
                ?? "qwen3.5:9b-cpu",
            PlaceholderText = "model",
            CustomMinimumSize = new Vector2(180, 34),
        };
        row.AddChild(_model);
        _busyControls.Add(_model);

        var newRun = SmallButton("New Run");
        newRun.Pressed += () =>
        {
            StartNewRun();
            RefreshView();
        };
        row.AddChild(newRun);
        _busyControls.Add(newRun);

        var save = SmallButton("Save");
        save.Pressed += () => _ = ExecuteAsync(new SaveCommand(Path.Combine("runs", "quicksave.json")));
        row.AddChild(save);
        _busyControls.Add(save);

        var load = SmallButton("Load");
        load.Pressed += () => _ = ExecuteAsync(new LoadCommand(Path.Combine("runs", "quicksave.json")));
        row.AddChild(load);
        _busyControls.Add(load);

        return bar;
    }

    private Control BuildMapPanel()
    {
        var panel = PanelBox();
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;

        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", 8);
        panel.AddChild(stack);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", 8);
        stack.AddChild(header);
        var mapTitle = new Label
        {
            Text = "Encounter",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        mapTitle.AddThemeColorOverride("font_color", Text);
        mapTitle.AddThemeFontSizeOverride("font_size", 17);
        header.AddChild(mapTitle);

        var wait = SmallButton("Wait");
        wait.Pressed += () => _ = ExecuteAsync(new WaitCommand());
        header.AddChild(wait);
        _busyControls.Add(wait);

        var inspect = SmallButton("Inspect");
        inspect.Pressed += () => _ = ExecuteAsync(new InspectCommand());
        header.AddChild(inspect);
        _busyControls.Add(inspect);

        _mapGrid = new GridContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _mapGrid.AddThemeConstantOverride("h_separation", 2);
        _mapGrid.AddThemeConstantOverride("v_separation", 2);
        stack.AddChild(_mapGrid);

        var dpad = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        dpad.AddThemeConstantOverride("separation", 6);
        stack.AddChild(dpad);

        AddMoveButton(dpad, "NW", Direction.NorthWest);
        AddMoveButton(dpad, "N", Direction.North);
        AddMoveButton(dpad, "NE", Direction.NorthEast);
        AddMoveButton(dpad, "W", Direction.West);
        AddMoveButton(dpad, "E", Direction.East);
        AddMoveButton(dpad, "SW", Direction.SouthWest);
        AddMoveButton(dpad, "S", Direction.South);
        AddMoveButton(dpad, "SE", Direction.SouthEast);

        return panel;
    }

    private Control BuildSidePanel()
    {
        var panel = PanelBox();
        panel.CustomMinimumSize = new Vector2(380, 0);
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;

        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", 8);
        panel.AddChild(stack);

        _status = Readout(120);
        _world = Readout(90);
        _entities = Readout(150);
        _inventory = Readout(145);
        _promises = Readout(120);
        _log = Readout(200);
        _log.ScrollFollowing = true;
        _log.SizeFlagsVertical = SizeFlags.ExpandFill;

        stack.AddChild(Section("State", _status));
        stack.AddChild(Section("World", _world));
        stack.AddChild(Section("Visible", _entities));
        stack.AddChild(Section("Inventory", _inventory));
        stack.AddChild(Section("Promises", _promises));
        stack.AddChild(Section("Log", _log, expand: true));

        return panel;
    }

    private Control BuildCommandPanel()
    {
        var panel = PanelBox();
        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", 8);
        panel.AddChild(stack);

        var spellRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        spellRow.AddThemeConstantOverride("separation", 8);
        stack.AddChild(spellRow);

        spellRow.AddChild(SmallLabel("Spell"));
        _spellLine = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "bind the nearest enemy in sticky blue webbing",
        };
        _spellLine.TextSubmitted += text => _ = CastSpellAsync(text);
        spellRow.AddChild(_spellLine);

        _cast = SmallButton("Cast");
        _cast.Pressed += () => _ = CastSpellAsync(_spellLine.Text);
        spellRow.AddChild(_cast);

        _beginCast = SmallButton("Begin");
        _beginCast.Pressed += () => _ = BeginCastAsync(_spellLine.Text);
        spellRow.AddChild(_beginCast);

        _awaitCast = SmallButton("Await");
        _awaitCast.Pressed += () => _ = ExecuteAsync(new AwaitCastCommand());
        spellRow.AddChild(_awaitCast);

        _cancelCast = SmallButton("Cancel");
        _cancelCast.Pressed += () => _ = ExecuteAsync(new CancelCastCommand());
        spellRow.AddChild(_cancelCast);

        _busyControls.AddRange(new Control[] { _spellLine, _cast, _beginCast });

        var quickRow = new HBoxContainer();
        quickRow.AddThemeConstantOverride("separation", 8);
        stack.AddChild(quickRow);
        AddQuickSpell(quickRow, "Web", "bind the nearest enemy in sticky blue webbing");
        AddQuickSpell(quickRow, "Moth", "summon a friendly brass moth that bites enemies");
        AddQuickSpell(quickRow, "Ice", "turn the floor between me and the enemy into slick ice");
        AddQuickSpell(quickRow, "Prophecy", "promise that the room remembers my name");

        var travelRow = new HBoxContainer();
        travelRow.AddThemeConstantOverride("separation", 8);
        stack.AddChild(travelRow);
        travelRow.AddChild(SmallLabel("Travel"));
        AddTravelButton(travelRow, "N", Direction.North);
        AddTravelButton(travelRow, "E", Direction.East);
        AddTravelButton(travelRow, "S", Direction.South);
        AddTravelButton(travelRow, "W", Direction.West);

        var commandRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        commandRow.AddThemeConstantOverride("separation", 8);
        stack.AddChild(commandRow);
        commandRow.AddChild(SmallLabel("Command"));

        _commandLine = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "inspect",
        };
        _commandLine.TextSubmitted += text => _ = SubmitCommandAsync(text);
        commandRow.AddChild(_commandLine);
        _busyControls.Add(_commandLine);

        var run = SmallButton("Run");
        run.Pressed += () => _ = SubmitCommandAsync(_commandLine.Text);
        commandRow.AddChild(run);
        _busyControls.Add(run);

        return panel;
    }

    private void StartNewRun()
    {
        var model = string.IsNullOrWhiteSpace(_model?.Text) ? null : _model.Text.Trim();
        var host = System.Environment.GetEnvironmentVariable("SORCERER_OLLAMA_HOST")
            ?? System.Environment.GetEnvironmentVariable("WILDMAGIC_OLLAMA_HOST");
        var provider = SpellProviderFactory.Create("ollama", host, model);
        var audit = new JsonlSpellAuditSink(Path.Combine("logs", "wild_magic_audit.jsonl"));
        var origin = System.Environment.GetEnvironmentVariable("SORCERER_ORIGIN");
        var seed = int.TryParse(System.Environment.GetEnvironmentVariable("SORCERER_SEED"), out var parsedSeed)
            ? Math.Max(1, parsedSeed)
            : 7;
        _session = GameSession.CreateImperialEncounter(
            new WildMagicController(provider, audit: audit),
            origin,
            seed,
            CrossRunMemorialStore.LoadDefault());
        _lastResult = null;
        _lastError = null;
        EnsureMapCells(_session.View());
    }

    private async Task CastSpellAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await ExecuteAsync(new CastCommand(text.Trim(), CastPerformance.Neutral));
    }

    private async Task BeginCastAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await ExecuteAsync(new BeginCastCommand(text.Trim(), CastPerformance.Neutral));
    }

    private async Task SubmitCommandAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        _commandLine.Text = "";
        await ExecuteAsync(TextCommandParser.Parse(text.Trim()));
    }

    private async Task ExecuteAsync(GameCommand command)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetBusy(true);
        _lastError = null;

        try
        {
            _lastResult = await _session.ExecuteAsync(command);
            if (_lastResult.Deltas.Any(delta => delta.Operation.Equals("runComplete", StringComparison.OrdinalIgnoreCase)))
            {
                CrossRunMemorialStore.AppendLatestChronicle(_session.Engine.State);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            _busy = false;
            RefreshView();
        }
    }

    private void RefreshView()
    {
        var view = _session.View();
        var observation = _session.Observation(debug: false);
        EnsureMapCells(view);
        RenderMap(view);
        RenderSidebars(view, observation.PendingCast);
        SetBusy(_busy);
    }

    private void RenderMap(GameView view)
    {
        var tiles = (view.Tiles ?? Array.Empty<MapTileCard>())
            .ToDictionary(tile => new GridPoint(tile.X, tile.Y));
        var entitiesByPoint = view.Entities
            .Where(entity => entity.X >= 0 && entity.Y >= 0)
            .OrderBy(entity => entity.Id == view.ControlledEntityId ? 1 : 0)
            .GroupBy(entity => new GridPoint(entity.X, entity.Y))
            .ToDictionary(group => group.Key, group => group.Last());

        for (var y = 0; y < view.Height; y++)
        {
            for (var x = 0; x < view.Width; x++)
            {
                var point = new GridPoint(x, y);
                tiles.TryGetValue(point, out var tile);
                entitiesByPoint.TryGetValue(point, out var entity);
                var selected = view.SelectedTarget == point;
                var cell = _cells[y, x];
                cell.Text = CellGlyph(tile, entity, selected);
                cell.TooltipText = CellTooltip(point, tile, entity, selected);
                cell.AddThemeColorOverride("font_color", GlyphColor(entity, tile, selected));
                cell.AddThemeColorOverride("font_hover_color", GlyphColor(entity, tile, selected));

                var style = CellStyle(TerrainColor(tile), selected);
                cell.AddThemeStyleboxOverride("normal", style);
                cell.AddThemeStyleboxOverride("hover", CellStyle(TerrainColor(tile).Lightened(0.08f), selected));
                cell.AddThemeStyleboxOverride("pressed", CellStyle(TerrainColor(tile).Darkened(0.12f), selected));
                cell.AddThemeStyleboxOverride("focus", style);
            }
        }
    }

    private void RenderSidebars(GameView view, PendingCastView? pendingCast)
    {
        var player = view.Entities.FirstOrDefault(entity => entity.Id == view.ControlledEntityId);
        var hp = player?.HitPoints is null || player.MaxHitPoints is null
            ? "HP ?"
            : $"HP {player.HitPoints}/{player.MaxHitPoints}";
        var statuses = view.Statuses is { Count: > 0 }
            ? string.Join(", ", view.Statuses.Select(status => status.DisplayName))
            : "none";
        var character = view.Character is null
            ? "Stats ?"
            : $"{view.Character.OriginName}\nVIG {view.Character.Vigor} ATT {view.Character.Attunement} COM {view.Character.Composure}";
        var selected = view.SelectedTarget is null
            ? "none"
            : $"{view.SelectedTarget.Value.X},{view.SelectedTarget.Value.Y}";
        var pending = pendingCast is null
            ? "none"
            : $"{pendingCast.Id}: {pendingCast.Text}";
        _status.Text = $"Turn {view.Turn}\n{character}\n{hp}\nTarget {selected}\nPending {pending}\nStatuses {statuses}";
        _world.Text = view.World is null
            ? "unknown"
            : string.Join(
                "\n",
                new[]
                {
                    $"{view.World.RegionName} ({view.World.CurrentZoneId})",
                    $"{view.World.RealmStatus}; {view.World.RealmRuler}",
                    $"tradition {view.World.TraditionId}",
                    $"imperial {view.World.ImperialPresence} wild {view.World.Wildness}",
                }.Concat(view.World.Affordances.Select(affordance => affordance.Id)));
        _awaitCast.Disabled = _busy || pendingCast is null;
        _cancelCast.Disabled = _busy || pendingCast is null;

        _providerStatus.Text = _busy
            ? "resolving..."
            : "ollama ready";
        _providerStatus.AddThemeColorOverride("font_color", _busy ? Warning : Muted);

        _entities.Text = string.Join(
            "\n",
            view.Entities
                .Where(entity => entity.Id != view.ControlledEntityId)
                .OrderBy(entity => entity.Faction == "empire" ? 0 : 1)
                .ThenBy(entity => entity.Id)
                .Select(FormatEntity));

        _inventory.Text = string.Join(
            "\n",
            (view.Inventory ?? Array.Empty<ItemCard>())
                .OrderBy(item => item.Name)
                .Select(item => $"{item.Name} x{item.Quantity}{(item.Protected ? " protected" : "")}"));

        _promises.Text = view.Promises.Count == 0
            ? "none"
            : string.Join("\n", view.Promises.Select(promise => $"{promise.Kind}: {promise.Text}"));

        var logLines = new List<string>();
        if (_lastError is not null)
        {
            logLines.Add($"Error: {_lastError}");
        }

        if (_lastResult is not null)
        {
            var result = _lastResult.Success ? "ok" : _lastResult.TechnicalFailure ? "technical failure" : "rejected";
            logLines.Add($"{_lastResult.Action}: {result}");
        }

        logLines.AddRange(view.Messages.TakeLast(14));
        _log.Text = string.Join("\n", logLines);
    }

    private void EnsureMapCells(GameView view)
    {
        if (_cells.GetLength(0) == view.Height && _cells.GetLength(1) == view.Width)
        {
            return;
        }

        foreach (var child in _mapGrid.GetChildren())
        {
            child.QueueFree();
        }

        _mapGrid.Columns = view.Width;
        _cells = new Button[view.Height, view.Width];
        for (var y = 0; y < view.Height; y++)
        {
            for (var x = 0; x < view.Width; x++)
            {
                var point = new GridPoint(x, y);
                var cell = new Button
                {
                    Text = ".",
                    CustomMinimumSize = new Vector2(34, 34),
                    FocusMode = FocusModeEnum.None,
                };
                cell.AddThemeFontSizeOverride("font_size", 18);
                cell.Pressed += () => _ = ExecuteAsync(new TargetCommand(point));
                _mapGrid.AddChild(cell);
                _cells[y, x] = cell;
            }
        }
    }

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(LayoutPreset.FullRect);
        return control;
    }

    private static PanelContainer PanelBox()
    {
        var panel = new PanelContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        panel.AddThemeStyleboxOverride("panel", Box(Panel, Border));
        return panel;
    }

    private static VBoxContainer Section(string title, Control content, bool expand = false)
    {
        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = expand ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin,
        };
        box.AddThemeConstantOverride("separation", 4);
        var label = SmallLabel(title);
        label.AddThemeColorOverride("font_color", Wild);
        box.AddChild(label);
        box.AddChild(content);
        return box;
    }

    private static RichTextLabel Readout(float minHeight)
    {
        var readout = new RichTextLabel
        {
            BbcodeEnabled = false,
            ContextMenuEnabled = true,
            FitContent = false,
            ScrollActive = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(0, minHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        readout.AddThemeColorOverride("default_color", Text);
        readout.AddThemeStyleboxOverride("normal", Box(PanelAlt, new Color("26313d")));
        return readout;
    }

    private static Label SmallLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeColorOverride("font_color", Muted);
        return label;
    }

    private static Button SmallButton(string text)
    {
        var button = new Button
        {
            Text = text,
            CustomMinimumSize = new Vector2(74, 34),
        };
        return button;
    }

    private void AddMoveButton(BoxContainer parent, string label, Direction direction)
    {
        var button = SmallButton(label);
        button.CustomMinimumSize = new Vector2(44, 34);
        button.Pressed += () => _ = ExecuteAsync(new MoveCommand(direction));
        parent.AddChild(button);
        _busyControls.Add(button);
    }

    private void AddTravelButton(BoxContainer parent, string label, Direction direction)
    {
        var button = SmallButton(label);
        button.CustomMinimumSize = new Vector2(44, 34);
        button.Pressed += () => _ = ExecuteAsync(new TravelCommand(direction));
        parent.AddChild(button);
        _busyControls.Add(button);
    }

    private void AddQuickSpell(BoxContainer parent, string label, string spell)
    {
        var button = SmallButton(label);
        button.Pressed += () =>
        {
            _spellLine.Text = spell;
            _ = CastSpellAsync(spell);
        };
        parent.AddChild(button);
        _busyControls.Add(button);
    }

    private void SetBusy(bool busy)
    {
        foreach (var control in _busyControls)
        {
            switch (control)
            {
                case Button button:
                    button.Disabled = busy;
                    break;
                case LineEdit lineEdit:
                    lineEdit.Editable = !busy;
                    break;
            }
        }
    }

    private static string CellGlyph(MapTileCard? tile, EntityCard? entity, bool selected)
    {
        if (tile is null || !tile.Explored)
        {
            return selected ? "X" : " ";
        }

        if (entity is not null)
        {
            return entity.Glyph.ToString();
        }

        if (selected)
        {
            return "X";
        }

        return tile?.Terrain switch
        {
            "wall" => "#",
            "slick_ice" => "~",
            "shallow_water" => "~",
            "steam_mist" => "~",
            "vines" => "\"",
            "rubble" => "%",
            "wild_fire" => "^",
            "ice_wall" => "#",
            _ => ".",
        };
    }

    private static string CellTooltip(GridPoint point, MapTileCard? tile, EntityCard? entity, bool selected)
    {
        if (tile is null || !tile.Explored)
        {
            return selected
                ? $"{point.X},{point.Y} unknown\ntargeted"
                : $"{point.X},{point.Y} unknown";
        }

        var lines = new List<string>
        {
            $"{point.X},{point.Y} {tile.Terrain}{(tile.Visible ? "" : " (remembered)")}",
        };
        if (entity is not null)
        {
            var hp = entity.HitPoints is null ? "" : $" HP {entity.HitPoints}/{entity.MaxHitPoints}";
            lines.Add($"{entity.Name}{hp}");
            if (entity.Tags.Count > 0)
            {
                lines.Add(string.Join(", ", entity.Tags));
            }
        }

        if (selected)
        {
            lines.Add("targeted");
        }

        return string.Join("\n", lines);
    }

    private static string FormatEntity(EntityCard entity)
    {
        var hp = entity.HitPoints is null ? "" : $" {entity.HitPoints}/{entity.MaxHitPoints} HP";
        var faction = string.IsNullOrWhiteSpace(entity.Faction) ? "object" : entity.Faction;
        return $"{entity.Glyph} {entity.Name} ({entity.X},{entity.Y}) {faction}{hp}";
    }

    private static Color GlyphColor(EntityCard? entity, MapTileCard? tile, bool selected)
    {
        if (tile is null || !tile.Explored)
        {
            return selected ? Warning : Muted.Darkened(0.35f);
        }

        var dim = tile.Visible ? 0f : 0.35f;
        if (entity is not null)
        {
            if (entity.Id == "player")
            {
                return Wild.Darkened(dim);
            }

            var color = entity.Faction == "empire"
                ? Danger
                : entity.Faction == "player"
                    ? Focus
                    : Empire;
            return color.Darkened(dim);
        }

        if (selected)
        {
            return Warning;
        }

        return (tile.Terrain == "wall" ? Muted : Text).Darkened(dim);
    }

    private static Color TerrainColor(MapTileCard? tile)
    {
        if (tile is null || !tile.Explored)
        {
            return new Color("0c0e12");
        }

        var color = tile.Terrain switch
        {
            "wall" => new Color("252b33"),
            "slick_ice" => new Color("173647"),
            "shallow_water" => new Color("14324b"),
            "steam_mist" => new Color("2f3f44"),
            "vines" => new Color("1d372a"),
            "rubble" => new Color("3a332b"),
            "wild_fire" => new Color("462817"),
            "ice_wall" => new Color("263e50"),
            _ => new Color("15191f"),
        };
        return tile.Visible ? color : color.Darkened(0.28f);
    }

    private static StyleBoxFlat CellStyle(Color background, bool selected)
    {
        var style = Box(background, selected ? Warning : new Color("222933"));
        style.SetBorderWidthAll(selected ? 2 : 1);
        return style;
    }

    private static StyleBoxFlat Box(Color background, Color border)
    {
        var box = new StyleBoxFlat
        {
            BgColor = background,
            BorderColor = border,
        };
        box.SetBorderWidthAll(1);
        box.SetCornerRadiusAll(4);
        box.ContentMarginLeft = 8;
        box.ContentMarginRight = 8;
        box.ContentMarginTop = 6;
        box.ContentMarginBottom = 6;
        return box;
    }

    private static GameCommand? CommandForKey(Key key) =>
        key switch
        {
            Key.Up or Key.K => new MoveCommand(Direction.North),
            Key.Down or Key.J => new MoveCommand(Direction.South),
            Key.Left or Key.H => new MoveCommand(Direction.West),
            Key.Right or Key.L => new MoveCommand(Direction.East),
            Key.Y => new MoveCommand(Direction.NorthWest),
            Key.U => new MoveCommand(Direction.NorthEast),
            Key.B => new MoveCommand(Direction.SouthWest),
            Key.N => new MoveCommand(Direction.SouthEast),
            Key.Period => new WaitCommand(),
            Key.I => new InspectCommand(),
            _ => null,
        };
}
