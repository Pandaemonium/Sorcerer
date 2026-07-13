using Godot;
using Sorcerer.Core;
using Sorcerer.Core.Characters;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Persistence;
using Sorcerer.Core.Primitives;
using Sorcerer.Core.Results;
using Sorcerer.Core.Views;
using Sorcerer.Godot.Minigames;
using Sorcerer.Llm;
using Sorcerer.Llm.Auditing;
using Sorcerer.Llm.Configuration;
using Sorcerer.Magic;

namespace Sorcerer.Godot;

public partial class Main : Control
{
    private const int MapCellSeparation = 0;
    private const int MinMapCellSize = 14;
    private const int MaxMapCellSize = 46;
    private const int MinMapFontSize = 11;
    private const int MaxMapFontSize = 34;
    private const float MapFontSizeRatio = 0.72f;

    private readonly List<Control> _busyControls = new();
    private Button[,] _cells = new Button[0, 0];

    private GameSession _session = null!;
    private PanelContainer _mapFrame = null!;
    private GridContainer _mapGrid = null!;
    private Control _escMenu = null!;
    private PanelContainer _providerStatusPanel = null!;
    private Label _providerStatus = null!;
    private Label _statusLine = null!;
    private Label _objectiveLine = null!;
    private ProgressBar _hpBar = null!;
    private Label _hpLabel = null!;
    private ProgressBar _manaBar = null!;
    private Label _manaLabel = null!;
    private HFlowContainer _statusChips = null!;
    private RichTextLabel _entities = null!;
    private RichTextLabel _inventory = null!;
    private VBoxContainer _repertoireBox = null!;
    private readonly List<Button> _repertoireChips = new();
    private RichTextLabel _log = null!;
    private LineEdit _spellLine = null!;
    private LineEdit _commandLine = null!;
    private LineEdit _provider = null!;
    private LineEdit _host = null!;
    private LineEdit _model = null!;
    private LineEdit _effort = null!;
    private Button _cast = null!;
    private PanelContainer _contextMenu = null!;
    private VBoxContainer _contextMenuItems = null!;
    private RuneTraceMinigame _minigame = null!;
    private ThreadKnotMinigame _threadKnot = null!;
    private TrueSigilMinigame _trueSigil = null!;
    private BoneSongMinigame _boneSong = null!;
    private int _lastMinigame = -1;
    private readonly Random _minigameRng = new();
    private LlmDebugPanel _llmDebug = null!;

    // Cells whose glyph and floor pulse each frame: living magic terrain, the player's own
    // glyph, and the targeting ring. Rebuilt on every RenderMap.
    private sealed record ShimmerCell(
        Button Cell,
        StyleBoxFlat Box,
        Color BaseBackground,
        Color BaseGlyph,
        float Amplitude,
        float Phase);

    private readonly List<ShimmerCell> _shimmerCells = new();
    private double _shimmerClock;
    private double _shimmerLastUpdate;

    private ActionResult? _lastResult;
    private string? _lastError;
    private string? _lastPendingCastKey;
    private string _activeProviderName = "ollama";
    private string? _busyStatusText;
    private bool _busy;

    // --- Autoplay: an AI agent drives the live session so a spectator can watch it play. ---
    private bool _autoplay;
    private double _autoplayTimer;
    private int _apStep;
    private int _apSpellIdx;
    private int _apLineIdx;
    private int _apTravelIdx;
    private bool _apLastTravel;
    private const double AutoplayStepDelay = 1.1;

    private static readonly string[] AutoplaySpells =
    {
        "raise a wall of ice between me and the nearest enemy",
        "strike the nearest enemy with blue fire",
        "bind the nearest enemy in sticky blue webbing",
        "summon a friendly brass moth to fight beside me",
        "turn the floor beneath the nearest enemy into slick ice",
        "heal my wounds with warm green light",
        "push the nearest enemy away with a rude wind",
        "conjure a shard of singing glass from nothing",
        "grow thorned vines across the floor around me",
        "curse me with an echoing wild debt",
        "make the nearest enemy vulnerable to fire",
        "reveal the nearest hidden thing by making its shadow glow blue",
        "promise that this door will remember my name",
        "wreath my hand in lightning and strike the nearest foe",
        "turn the nearest soldier's teeth to glass and make him regret it",
        "wither the nearest enemy to grey dust with a fistful of grave salt",
        "call down a small crackling storm on all nearby enemies",
        "teleport one step sideways through a folded room",
    };

    private static readonly string[] AutoplayLines =
    {
        "what do you know about this place and the people who live here?",
        "is there a road, town, or hidden way somewhere near here?",
        "who rules here, and do they fear the empire's censors?",
        "have you anything to sell, or a service you can offer me?",
        "tell me a rumor worth knowing about these parts.",
        "what do you know of the old crystal tradition of Stalnaz?",
        "have you ever heard of Hollowmere and its reed memory?",
        "will you remember me kindly if I do right by you?",
    };

    private static readonly Direction[] AutoplayTravel =
    {
        Direction.East, Direction.South, Direction.West, Direction.North,
    };

    public override void _Ready()
    {
        DotEnv.Load();
        Theme = UiTheme.Build();
        BuildUi();
        if (SessionHost.Session is not null)
        {
            _session = SessionHost.Session;
        }
        else if (SessionHost.PendingBuild is not null || QuickStartRequested())
        {
            var build = SessionHost.PendingBuild;
            SessionHost.PendingBuild = null;
            StartNewRun(build);
        }
        else
        {
            // Fresh boot with no character chosen: hand off to the creation screen. Deferred
            // because swapping scenes from inside _Ready is unsupported; early return because
            // there is no session for the rest of _Ready to refresh.
            CallDeferred(nameof(OpenCharacterCreation));
            return;
        }

        // Opt-in autoplay-on-launch (does not change normal play): set SORCERER_AUTOPLAY=1.
        if (System.Environment.GetEnvironmentVariable("SORCERER_AUTOPLAY") == "1")
        {
            _autoplay = true;
        }

        RefreshView();
        if (System.Environment.GetEnvironmentVariable("SORCERER_MINIGAME_PREVIEW")
            ?.Equals("bone_song", StringComparison.OrdinalIgnoreCase) == true)
        {
            _ = _boneSong.PlayAsync(
                "ask the whale-road to carry my impossible name",
                providerSettled: () => false);
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Handled in _Input (before the GUI) so these work even while a text box has focus.
        if (TryConsumeCtrlCommand(@event))
        {
            return;
        }

        TryConsumeNumpadMovement(@event);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        if (key.Keycode == Key.Escape)
        {
            ToggleEscMenu();
            GetViewport().SetInputAsHandled();
            return;
        }

        var action = Keybindings.ActionForKey(key.Keycode);

        // The LLM debug toggle is handled before the busy/menu guards on purpose: the whole
        // point is to read prompts while a call is in flight (which is exactly when _busy is true).
        if (action == BindableAction.ToggleLlmDebug)
        {
            _llmDebug.Toggle();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Autoplay: an AI agent drives the live session so you can watch it play.
        if (action == BindableAction.ToggleAutoplay)
        {
            _autoplay = !_autoplay;
            _autoplayTimer = 0;
            RefreshView();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Controls screen: allowed while the Esc menu is open (it lives on that menu too),
        // blocked only mid-cast because the scene swap would abandon the busy state.
        // Handled-flag first: the scene swap detaches this node, after which GetViewport()
        // returns null.
        if (action == BindableAction.OpenControls && !_busy)
        {
            GetViewport().SetInputAsHandled();
            OpenControlsScene();
            return;
        }

        if (_escMenu.Visible || _busy)
        {
            return;
        }

        if (TryConsumeNumpadMovement(@event))
        {
            return;
        }

        if (GetViewport().GuiGetFocusOwner() is LineEdit)
        {
            return;
        }

        if (action == BindableAction.FocusSpell)
        {
            _spellLine.GrabFocus();
            GetViewport().SetInputAsHandled();
            return;
        }

        if (action == BindableAction.FocusTalk)
        {
            FocusCommandLine("talk ");
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

    private void OpenControlsScene()
    {
        SessionHost.Session = _session;
        StashProviderOverrides();
        GetTree().ChangeSceneToFile("res://Scenes/Controls.tscn");
    }

    public override void _Process(double delta)
    {
        // The shimmer runs before the busy guard so magic keeps breathing while a cast resolves.
        _shimmerClock += delta;
        UpdateShimmer();

        if (_busy || _session is null)
        {
            return;
        }

        if (_autoplay)
        {
            if (_session.Observation().PendingCast is not null)
            {
                _ = ExecuteAsync(new AwaitCastCommand());
                return;
            }

            _autoplayTimer += delta;
            if (_autoplayTimer >= AutoplayStepDelay)
            {
                _autoplayTimer = 0;
                _ = ExecuteAsync(NextAutoplayCommand());
                return;
            }
        }

        var pending = _session.Observation().PendingCast;
        var key = PendingCastKey(pending);
        if (key != _lastPendingCastKey)
        {
            RefreshView();
        }
    }

    /// <summary>
    /// Picks the next command for autoplay: mostly varied wild spells, with dialogue when a friendly
    /// NPC is adjacent and occasional travel (then a look-around) to meet new people. Deliberately
    /// simple and lively so a spectator sees a bit of everything.
    /// </summary>
    private GameCommand NextAutoplayCommand()
    {
        var view = _session.View();
        var player = view.Entities.FirstOrDefault(entity => entity.Id == view.ControlledEntityId);
        if (player is null)
        {
            return new InspectCommand();
        }

        _apStep++;

        if (_apLastTravel)
        {
            // A resident spawns on travel but is not perceived until we look.
            _apLastTravel = false;
            return new InspectCommand();
        }

        var adjacent = AutoplayTalkers(view, player)
            .Where(entity => AutoplayDistance(player, entity) <= 1)
            .OrderBy(entity => AutoplayDistance(player, entity))
            .FirstOrDefault();
        if (adjacent is not null && _apStep % 3 != 0)
        {
            return new TalkCommand($"{adjacent.Name}, {AutoplayLines[_apLineIdx++ % AutoplayLines.Length]}");
        }

        if (_apStep % 6 == 0)
        {
            _apLastTravel = true;
            return new TravelCommand(AutoplayTravel[_apTravelIdx++ % AutoplayTravel.Length]);
        }

        var visible = AutoplayTalkers(view, player)
            .Where(entity => AutoplayDistance(player, entity) > 1)
            .OrderBy(entity => AutoplayDistance(player, entity))
            .FirstOrDefault();
        if (visible is not null && _apStep % 4 == 0)
        {
            return new MoveCommand(AutoplayDirection(player, visible));
        }

        return new CastCommand(AutoplaySpells[_apSpellIdx++ % AutoplaySpells.Length], CastPerformance.Neutral);
    }

    private static IEnumerable<EntityCard> AutoplayTalkers(GameView view, EntityCard player) =>
        view.Entities
            .Where(entity => entity.Id != view.ControlledEntityId)
            .Where(entity => entity.HitPoints is null or > 0)
            .Where(entity => entity.Tags.Any(tag =>
                tag.Equals("npc", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("resident", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("prisoner", StringComparison.OrdinalIgnoreCase)
                || tag.Equals("merchant", StringComparison.OrdinalIgnoreCase)))
            .Where(entity => !string.Equals(entity.Faction, "empire", StringComparison.OrdinalIgnoreCase));

    private static int AutoplayDistance(EntityCard a, EntityCard b) => Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);

    private static Direction AutoplayDirection(EntityCard from, EntityCard to)
    {
        var dx = Math.Sign(to.X - from.X);
        var dy = Math.Sign(to.Y - from.Y);
        return (dx, dy) switch
        {
            (0, -1) => Direction.North,
            (0, 1) => Direction.South,
            (1, 0) => Direction.East,
            (-1, 0) => Direction.West,
            (1, -1) => Direction.NorthEast,
            (-1, -1) => Direction.NorthWest,
            (1, 1) => Direction.SouthEast,
            (-1, 1) => Direction.SouthWest,
            _ => Direction.East,
        };
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

        var root = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        root.AddThemeConstantOverride("separation", UiTheme.SpaceMd);
        outer.AddChild(root);

        var body = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        body.AddThemeConstantOverride("separation", UiTheme.SpaceMd);
        root.AddChild(body);

        body.AddChild(BuildMapPanel());
        body.AddChild(BuildSidePanel());
        root.AddChild(BuildCommandPanel());

        AddChild(BuildEscMenu());
        AddChild(BuildContextMenu());

        _minigame = new RuneTraceMinigame();
        AddChild(FullRect(_minigame));

        _threadKnot = new ThreadKnotMinigame();
        AddChild(FullRect(_threadKnot));

        _trueSigil = new TrueSigilMinigame();
        AddChild(FullRect(_trueSigil));

        _boneSong = new BoneSongMinigame();
        AddChild(FullRect(_boneSong));

        _llmDebug = new LlmDebugPanel { ZIndex = 90 };
        AddChild(FullRect(_llmDebug));
    }

    private Control BuildContextMenu()
    {
        _contextMenu = new PanelContainer
        {
            Visible = false,
            MouseFilter = MouseFilterEnum.Stop,
            ZIndex = 40,
            CustomMinimumSize = new Vector2(160, 0),
        };
        _contextMenu.AddThemeStyleboxOverride(
            "panel",
            UiTheme.Box(
                UiTheme.Panel,
                UiTheme.Empire,
                borderWidth: 1,
                radius: 4,
                shadow: true,
                marginX: UiTheme.SpaceXs,
                marginY: UiTheme.SpaceXs));

        _contextMenuItems = new VBoxContainer();
        _contextMenuItems.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        _contextMenu.AddChild(_contextMenuItems);
        return _contextMenu;
    }

    private Control BuildEscMenu()
    {
        var overlay = FullRect(new ColorRect
        {
            Color = new Color(0f, 0f, 0f, 0.55f),
            Visible = false,
        });
        _escMenu = overlay;

        var center = FullRect(new CenterContainer());
        overlay.AddChild(center);

        var card = PanelBox();
        center.AddChild(card);

        var stack = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0) };
        stack.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        card.AddChild(stack);

        var title = new Label { Text = "Sorcerer" };
        title.AddThemeFontSizeOverride("font_size", 20);
        title.AddThemeColorOverride("font_color", UiTheme.Wild);
        stack.AddChild(title);

        var providerRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        providerRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(providerRow);

        _providerStatusPanel = new PanelContainer { ThemeTypeVariation = "Pill" };
        _providerStatus = new Label
        {
            Text = "",
            CustomMinimumSize = new Vector2(120, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _providerStatusPanel.AddChild(_providerStatus);
        providerRow.AddChild(_providerStatusPanel);

        var providerLabel = SmallLabel("Provider");
        providerLabel.CustomMinimumSize = new Vector2(70, 34);
        providerRow.AddChild(providerLabel);

        var defaultProvider = SessionHost.ProviderOverride
            ?? System.Environment.GetEnvironmentVariable("SORCERER_PROVIDER")
            ?? "ollama";
        _provider = new LineEdit
        {
            Text = defaultProvider,
            PlaceholderText = "provider",
            CustomMinimumSize = new Vector2(116, 34),
        };
        providerRow.AddChild(_provider);
        _busyControls.Add(_provider);

        _host = new LineEdit
        {
            Text = SessionHost.HostOverride
                ?? DefaultProviderHost(defaultProvider)
                ?? "",
            PlaceholderText = "host",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34),
        };
        providerRow.AddChild(_host);
        _busyControls.Add(_host);

        _model = new LineEdit
        {
            Text = SessionHost.ModelOverride
                ?? System.Environment.GetEnvironmentVariable("SORCERER_MODEL")
                ?? System.Environment.GetEnvironmentVariable("WILDMAGIC_MODEL")
                ?? DefaultProviderModel(defaultProvider),
            PlaceholderText = "model",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 34),
        };
        providerRow.AddChild(_model);
        _busyControls.Add(_model);

        _effort = new LineEdit
        {
            Text = SessionHost.EffortOverride
                ?? System.Environment.GetEnvironmentVariable("SORCERER_EFFORT")
                ?? (UsesEffort(defaultProvider) ? "medium" : ""),
            PlaceholderText = "effort",
            TooltipText = "Claude: low–max. Gemini thinking: minimal, low, medium, or high.",
            CustomMinimumSize = new Vector2(76, 34),
        };
        providerRow.AddChild(_effort);
        _busyControls.Add(_effort);

        var runRow = new HBoxContainer();
        runRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(runRow);

        var newRun = SmallButton("New Run");
        newRun.TooltipText = "Create a new character and start over (this run keeps running until you Begin).";
        newRun.Pressed += () =>
        {
            // Keep the current session referenced so Esc on the creation screen resumes it
            // untouched; Begin over there clears it and stashes a PendingBuild instead.
            SessionHost.Session = _session;
            StashProviderOverrides();
            GetTree().ChangeSceneToFile("res://Scenes/CharacterCreation.tscn");
        };
        runRow.AddChild(newRun);
        _busyControls.Add(newRun);

        var save = SmallButton("Save");
        save.TooltipText = "Save to runs/quicksave.json — typed: save";
        save.Pressed += () => _ = ExecuteAsync(new SaveCommand(Path.Combine("runs", "quicksave.json")));
        runRow.AddChild(save);
        _busyControls.Add(save);

        var load = SmallButton("Load");
        load.TooltipText = "Load runs/quicksave.json — typed: load";
        load.Pressed += () => _ = ExecuteAsync(new LoadCommand(Path.Combine("runs", "quicksave.json")));
        runRow.AddChild(load);
        _busyControls.Add(load);

        var recordsRow = new HBoxContainer();
        recordsRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(recordsRow);

        AddSceneButton(recordsRow, "Journal", "res://Scenes/Journal.tscn", "Leads, claims, and promises — typed: journal");
        AddSceneButton(recordsRow, "Promises", "res://Scenes/Promises.tscn", "Magic that is still owed — typed: promises");
        AddSceneButton(recordsRow, "Rumors", "res://Scenes/Rumors.tscn", "What people are saying — typed: rumors");

        var systemsRow = new HBoxContainer();
        systemsRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(systemsRow);

        AddMenuCommandButton(systemsRow, "Character", new CharacterCommand(), "Your sheet: stats, signature, charter forms — typed: character");
        AddMenuCommandButton(systemsRow, "Standing", new StandingCommand(), "Faction standing, pressure, and legend — typed: standing");
        AddMenuCommandButton(systemsRow, "Followers", new FollowersCommand(), "Who travels with you — typed: followers");

        var affordanceRow = new HBoxContainer();
        affordanceRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(affordanceRow);

        AddMenuCommandButton(affordanceRow, "Services", new ServicesCommand(), "What nearby people offer — typed: services");
        AddMenuCommandButton(affordanceRow, "Jobs", new JobsCommand(), "Background work in progress — typed: jobs");
        AddMenuCommandButton(affordanceRow, "Atlas", new AtlasCommand(), "Where you are in the world — typed: atlas");

        var controlsRow = new HBoxContainer();
        controlsRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(controlsRow);
        AddSceneButton(
            controlsRow,
            "Controls",
            "res://Scenes/Controls.tscn",
            $"Keys, rebinding, and the typed command list ({KeyName(BindableAction.OpenControls)})");

        var resume = SmallButton("Resume");
        resume.TooltipText = "Back to the game (Esc)";
        resume.Pressed += CloseEscMenu;
        stack.AddChild(resume);

        return overlay;
    }

    private void AddSceneButton(BoxContainer parent, string label, string scenePath, string? tooltip = null)
    {
        var button = SmallButton(label);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (tooltip is not null)
        {
            button.TooltipText = tooltip;
        }

        button.Pressed += () =>
        {
            SessionHost.Session = _session;
            StashProviderOverrides();
            GetTree().ChangeSceneToFile(scenePath);
        };
        parent.AddChild(button);
        _busyControls.Add(button);
    }

    /// <summary>Provider fields live in this scene's LineEdits; park them in SessionHost so
    /// they survive the scene swap (Main is rebuilt from scratch on the way back).</summary>
    private void StashProviderOverrides()
    {
        SessionHost.ProviderOverride = _provider?.Text;
        SessionHost.HostOverride = _host?.Text;
        SessionHost.ModelOverride = _model?.Text;
        SessionHost.EffortOverride = _effort?.Text;
    }

    private void OpenCharacterCreation()
    {
        GetTree().ChangeSceneToFile("res://Scenes/CharacterCreation.tscn");
    }

    /// <summary>Scripted, agent, and CLI-parity launches must never block on a menu
    /// (docs/CLI_AND_AGENT_PLAYTESTING.md): any of these env vars boots straight into play.</summary>
    private static bool QuickStartRequested() =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SORCERER_ORIGIN"))
        || System.Environment.GetEnvironmentVariable("SORCERER_QUICKSTART") == "1"
        || System.Environment.GetEnvironmentVariable("SORCERER_AUTOPLAY") == "1"
        || !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable("SORCERER_MINIGAME_PREVIEW"));

    private void AddMenuCommandButton(BoxContainer parent, string label, GameCommand command, string? tooltip = null)
    {
        var button = SmallButton(label);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        if (tooltip is not null)
        {
            button.TooltipText = tooltip;
        }

        button.Pressed += () =>
        {
            CloseEscMenu();
            _ = ExecuteAsync(command);
        };
        parent.AddChild(button);
        _busyControls.Add(button);
    }

    private void ToggleEscMenu()
    {
        // _session is null for the one frame between _Ready and the deferred hand-off to
        // character creation on a fresh boot; a menu over a session-less Main has nothing
        // to show or resume into.
        if (_session is null)
        {
            return;
        }

        HideContextMenu();
        _escMenu.Visible = !_escMenu.Visible;
        if (_escMenu.Visible)
        {
            var observation = _session.Observation();
            RenderSidebars(observation.View, observation.PendingCast);
        }
    }

    private void CloseEscMenu() => _escMenu.Visible = false;

    private Control BuildMapPanel()
    {
        var panel = PanelBox();
        panel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        panel.SizeFlagsStretchRatio = 2.4f;

        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        panel.AddChild(stack);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(header);
        var mapTitle = new Label
        {
            Text = "Encounter",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            ThemeTypeVariation = "SectionHeader",
        };
        header.AddChild(mapTitle);

        var wait = SmallButton("Wait");
        wait.TooltipText = $"Pass a turn ({KeyName(BindableAction.Wait)}) — typed: wait";
        wait.Pressed += () => _ = ExecuteAsync(new WaitCommand());
        header.AddChild(wait);
        _busyControls.Add(wait);

        var inspect = SmallButton("Inspect");
        inspect.TooltipText = $"Describe your surroundings ({KeyName(BindableAction.Inspect)}) — typed: inspect";
        inspect.Pressed += () => _ = ExecuteAsync(new InspectCommand());
        header.AddChild(inspect);
        _busyControls.Add(inspect);

        var pickup = SmallButton("Pickup");
        pickup.TooltipText = $"Pick up what is underfoot ({KeyName(BindableAction.Pickup)}) — typed: pickup";
        pickup.Pressed += () => _ = ExecuteAsync(new PickupCommand());
        header.AddChild(pickup);
        _busyControls.Add(pickup);

        var talk = SmallButton("Talk");
        talk.TooltipText = $"Speak to someone nearby ({KeyName(BindableAction.FocusTalk)}) — fills 'talk ' in the command box";
        talk.Pressed += () => FocusCommandLine("talk ");
        header.AddChild(talk);
        _busyControls.Add(talk);

        var mapFrame = new PanelContainer
        {
            ThemeTypeVariation = "MapFrame",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        _mapFrame = mapFrame;
        _mapFrame.Resized += UpdateMapCellSizing;
        stack.AddChild(mapFrame);

        var mapCenter = new CenterContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        mapFrame.AddChild(mapCenter);

        _mapGrid = new GridContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
            SizeFlagsVertical = SizeFlags.ShrinkCenter,
        };
        _mapGrid.AddThemeConstantOverride("h_separation", MapCellSeparation);
        _mapGrid.AddThemeConstantOverride("v_separation", MapCellSeparation);
        mapCenter.AddChild(_mapGrid);

        var dpad = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ShrinkCenter,
        };
        dpad.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        stack.AddChild(dpad);

        AddMoveButton(dpad, "NW", Direction.NorthWest, BindableAction.MoveNorthWest, "numpad 7");
        AddMoveButton(dpad, "N", Direction.North, BindableAction.MoveNorth, "numpad 8");
        AddMoveButton(dpad, "NE", Direction.NorthEast, BindableAction.MoveNorthEast, "numpad 9");
        AddMoveButton(dpad, "W", Direction.West, BindableAction.MoveWest, "numpad 4");
        AddMoveButton(dpad, "E", Direction.East, BindableAction.MoveEast, "numpad 6");
        AddMoveButton(dpad, "SW", Direction.SouthWest, BindableAction.MoveSouthWest, "numpad 1");
        AddMoveButton(dpad, "S", Direction.South, BindableAction.MoveSouth, "numpad 2");
        AddMoveButton(dpad, "SE", Direction.SouthEast, BindableAction.MoveSouthEast, "numpad 3");

        return panel;
    }

    private Control BuildSidePanel()
    {
        var panel = PanelBox();
        panel.CustomMinimumSize = new Vector2(300, 0);
        panel.SizeFlagsVertical = SizeFlags.ExpandFill;
        panel.SizeFlagsStretchRatio = 1f;

        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        panel.AddChild(stack);

        stack.AddChild(Section("State", BuildStateHud(), UiTheme.Wild));

        _entities = Readout(150);
        stack.AddChild(Section("Visible", _entities, UiTheme.Danger));

        _inventory = Readout(50);
        stack.AddChild(Section("Inventory", _inventory, UiTheme.Focus));

        _repertoireBox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _repertoireBox.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        stack.AddChild(Section("Repertoire", _repertoireBox, UiTheme.Arcane));

        _log = Readout(200);
        _log.ScrollFollowing = true;
        _log.SizeFlagsVertical = SizeFlags.ExpandFill;
        stack.AddChild(Section("Log", _log, UiTheme.Muted, expand: true));

        return panel;
    }

    private Control BuildStateHud()
    {
        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", UiTheme.SpaceXs);

        _statusLine = new Label();
        _statusLine.AddThemeColorOverride("font_color", UiTheme.Text);
        box.AddChild(_statusLine);

        _objectiveLine = new Label
        {
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
            TooltipText = "Current objective — open Journal for the full thread.",
        };
        _objectiveLine.AddThemeFontSizeOverride("font_size", 12);
        _objectiveLine.AddThemeColorOverride("font_color", UiTheme.Wild);
        box.AddChild(_objectiveLine);

        var hpRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        hpRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        box.AddChild(hpRow);

        _hpBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.01,
            ShowPercentage = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 16),
        };
        hpRow.AddChild(_hpBar);

        _hpLabel = SmallLabel("");
        _hpLabel.CustomMinimumSize = new Vector2(60, 0);
        hpRow.AddChild(_hpLabel);

        var manaRow = new HBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        manaRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        box.AddChild(manaRow);

        _manaBar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.01,
            ShowPercentage = false,
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 16),
        };
        manaRow.AddChild(_manaBar);

        _manaLabel = SmallLabel("");
        _manaLabel.CustomMinimumSize = new Vector2(60, 0);
        manaRow.AddChild(_manaLabel);

        _statusChips = new HFlowContainer();
        _statusChips.AddThemeConstantOverride("h_separation", UiTheme.SpaceXs);
        _statusChips.AddThemeConstantOverride("v_separation", UiTheme.SpaceXs);
        box.AddChild(_statusChips);

        return box;
    }

    private Control BuildCommandPanel()
    {
        var panel = PanelBox();
        var stack = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        stack.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        panel.AddChild(stack);

        var spellRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        spellRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(spellRow);

        spellRow.AddChild(SmallLabel("Spell"));
        _spellLine = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "speak your spell...",
            TooltipText = "Describe any spell in plain words — wild magic is a gamble.\n"
                + "A known charter form's name casts it instantly instead.",
        };
        _spellLine.TextSubmitted += text => _ = CastSpellAsync(text);
        spellRow.AddChild(_spellLine);

        _cast = SmallButton("Cast");
        _cast.TooltipText = "Cast the spell text (Enter in the spell box).";
        _cast.Pressed += () => _ = CastSpellAsync(_spellLine.Text);
        spellRow.AddChild(_cast);

        _busyControls.AddRange(new Control[] { _spellLine, _cast });

        var commandRow = new HBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        commandRow.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        stack.AddChild(commandRow);
        commandRow.AddChild(SmallLabel("Command"));

        _commandLine = new LineEdit
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            PlaceholderText = "command — try 'help'",
            TooltipText = "Typed verbs: talk, examine, read, travel, charter, echoes, wares, possess…\n"
                + "Type 'help' for the full list, or open Controls (F1).",
        };
        _commandLine.TextSubmitted += text => _ = SubmitCommandAsync(text);
        commandRow.AddChild(_commandLine);
        _busyControls.Add(_commandLine);

        var run = SmallButton("Run");
        run.TooltipText = "Run the typed command — try 'help'.";
        run.Pressed += () => _ = SubmitCommandAsync(_commandLine.Text);
        commandRow.AddChild(run);
        _busyControls.Add(run);

        var hints = SmallLabel(
            $"Esc — menu · right-click — actions · {KeyName(BindableAction.FocusSpell)} — spell · "
            + $"{KeyName(BindableAction.FocusTalk)} — talk · {KeyName(BindableAction.OpenControls)} — controls");
        hints.AddThemeFontSizeOverride("font_size", 11);
        stack.AddChild(hints);

        return panel;
    }

    private void StartNewRun(CharacterBuild? build = null)
    {
        var providerName = string.IsNullOrWhiteSpace(_provider?.Text)
            ? System.Environment.GetEnvironmentVariable("SORCERER_PROVIDER") ?? "ollama"
            : _provider.Text.Trim();
        var host = string.IsNullOrWhiteSpace(_host?.Text)
            ? DefaultProviderHost(providerName)
            : _host.Text.Trim();
        var model = string.IsNullOrWhiteSpace(_model?.Text) ? null : _model.Text.Trim();
        var effort = string.IsNullOrWhiteSpace(_effort?.Text) ? null : _effort.Text.Trim();
        var provider = SpellProviderFactory.Create(providerName, host, model, effort: effort);
        var router = SpellRouterFactory.Create(providerName, host, model, effort: effort);
        var dialogueProvider = DialogueProviderFactory.Create(new Sorcerer.Llm.Configuration.LlmPurposeSettings(
            providerName,
            host,
            model,
            TimeoutSeconds: 180,
            Effort: effort));
        var dialogueRouterSettings = LlmConfiguration
            .FromEnvironment()
            .WithPurposeOverride(
                LlmPurpose.DialogueRouter,
                DialogueRouterEnvironmentOverride("PROVIDER") ? null : providerName,
                DialogueRouterEnvironmentOverride("HOST") ? null : host,
                DialogueRouterEnvironmentOverride("MODEL") ? null : model,
                effort: DialogueRouterEnvironmentOverride("EFFORT") ? null : effort)
            .SettingsFor(LlmPurpose.DialogueRouter);
        var dialogueRouter = DialogueRouterFactory.Create(dialogueRouterSettings);
        var dialogueParserSettings = LlmConfiguration
            .FromEnvironment()
            .WithPurposeOverride(
                LlmPurpose.DialogueParser,
                ParserEnvironmentOverride("PROVIDER") ? null : providerName,
                ParserEnvironmentOverride("HOST") ? null : host,
                ParserEnvironmentOverride("MODEL") ? null : model,
                effort: ParserEnvironmentOverride("EFFORT") ? null : effort)
            .SettingsFor(LlmPurpose.DialogueParser);
        var dialogueParserRouterSettings = LlmConfiguration
            .FromEnvironment()
            .WithPurposeOverride(
                LlmPurpose.DialogueParserRouter,
                ParserRouterEnvironmentOverride("PROVIDER") ? null : providerName,
                ParserRouterEnvironmentOverride("HOST") ? null : host,
                ParserRouterEnvironmentOverride("MODEL") ? null : model,
                effort: ParserRouterEnvironmentOverride("EFFORT") ? null : effort)
            .SettingsFor(LlmPurpose.DialogueParserRouter);
        var dialogueParserRouter = DialogueParserRouterFactory.Create(dialogueParserRouterSettings);
        var dialogueParser = DialogueParserFactory.Create(dialogueParserSettings);
        var backgroundAudit = new JsonlBackgroundTextAuditSink(Path.Combine("logs", "background_audit.jsonl"));
        var backgroundTextGenerator = BackgroundTextGeneratorFactory.Create(
            LlmConfiguration.FromEnvironment(),
            LlmPurpose.Background,
            backgroundAudit);
        _activeProviderName = provider.Name;
        var audit = new JsonlSpellAuditSink(Path.Combine("logs", "wild_magic_audit.jsonl"));
        var dialogueAudit = new JsonlDialogueAuditSink(Path.Combine("logs", "dialogue_audit.jsonl"));
        var origin = build is null
            ? System.Environment.GetEnvironmentVariable("SORCERER_ORIGIN")
            : null;
        var seed = int.TryParse(System.Environment.GetEnvironmentVariable("SORCERER_SEED"), out var parsedSeed)
            ? Math.Max(1, parsedSeed)
            : 7;
        _session = GameSession.CreateImperialEncounter(
            new WildMagicController(provider, audit: audit, router: router),
            origin,
            seed,
            CrossRunMemorialStore.LoadDefault(),
            dialogueProvider: dialogueProvider,
            dialogueRouter: dialogueRouter,
            dialogueAudit: dialogueAudit,
            backgroundTextGenerator: backgroundTextGenerator,
            dialogueParser: dialogueParser,
            dialogueParserRouter: dialogueParserRouter,
            build: build);
        _lastResult = null;
        _lastError = null;
        EnsureMapCells(_session.View());
    }

    private static string? DefaultProviderHost(string provider)
    {
        var shared = System.Environment.GetEnvironmentVariable("SORCERER_HOST");
        if (!string.IsNullOrWhiteSpace(shared))
        {
            return shared;
        }

        var normalized = provider.Trim().ToLowerInvariant();
        if (IsGeminiProvider(provider))
        {
            return System.Environment.GetEnvironmentVariable("SORCERER_GEMINI_HOST")
                ?? System.Environment.GetEnvironmentVariable("GEMINI_BASE_URL")
                ?? "https://generativelanguage.googleapis.com/v1beta";
        }

        if (IsAnthropicProvider(provider))
        {
            return System.Environment.GetEnvironmentVariable("SORCERER_ANTHROPIC_HOST")
                ?? System.Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
                ?? "https://api.anthropic.com/v1";
        }

        if (normalized is "api" or "openai" or "openai-compatible")
        {
            return System.Environment.GetEnvironmentVariable("SORCERER_OPENAI_HOST")
                ?? System.Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
                ?? "https://api.openai.com/v1";
        }

        return System.Environment.GetEnvironmentVariable("SORCERER_OLLAMA_HOST")
            ?? System.Environment.GetEnvironmentVariable("WILDMAGIC_OLLAMA_HOST")
            ?? System.Environment.GetEnvironmentVariable("OLLAMA_HOST")
            ?? "http://127.0.0.1:11434";
    }

    private static string DefaultProviderModel(string provider) =>
        IsGeminiProvider(provider)
            ? "gemini-3.5-flash"
            : IsAnthropicProvider(provider) ? "claude-sonnet-5" : "qwen3.5:9b-cpu";

    private static bool IsAnthropicProvider(string provider) =>
        provider.Trim().ToLowerInvariant() is "anthropic" or "claude";

    private static bool IsGeminiProvider(string provider) =>
        provider.Trim().ToLowerInvariant() is "gemini" or "google";

    private static bool UsesEffort(string provider) =>
        IsAnthropicProvider(provider) || IsGeminiProvider(provider);

    private static bool ParserEnvironmentOverride(string suffix) =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable($"SORCERER_DIALOGUE_PARSER_{suffix}"));

    private static bool ParserRouterEnvironmentOverride(string suffix) =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable($"SORCERER_DIALOGUE_PARSER_ROUTER_{suffix}"));

    private static bool DialogueRouterEnvironmentOverride(string suffix) =>
        !string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable($"SORCERER_DIALOGUE_ROUTER_{suffix}"));

    private string ProviderStatusText(PendingCastView? pendingCast, bool errored)
    {
        if (_busy)
        {
            return _busyStatusText ?? "resolving...";
        }

        if (pendingCast is not null)
        {
            return pendingCast.State switch
            {
                "ready" => "spell ready",
                "failed" => "spell failed",
                "resolving" => "spell resolving",
                _ => $"spell {pendingCast.State}",
            };
        }

        return errored ? "error" : $"{_activeProviderName} ready";
    }

    private Color ProviderStatusColor(PendingCastView? pendingCast, bool errored)
    {
        if (_busy)
        {
            return UiTheme.Warning;
        }

        if (pendingCast is not null)
        {
            return pendingCast.State switch
            {
                "ready" => UiTheme.Wild,
                "failed" => UiTheme.Danger,
                _ => UiTheme.Warning,
            };
        }

        return errored ? UiTheme.Danger : UiTheme.Muted;
    }

    private static string? PendingCastKey(PendingCastView? pendingCast) =>
        pendingCast is null
            ? null
            : string.Join(
                "|",
                pendingCast.Id,
                pendingCast.State,
                pendingCast.Provider ?? "",
                pendingCast.Accepted?.ToString() ?? "",
                pendingCast.TechnicalFailure?.ToString() ?? "",
                pendingCast.Error ?? "",
                pendingCast.EffectTypes is null ? "" : string.Join(",", pendingCast.EffectTypes));

    private async Task CastSpellAsync(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || _busy)
        {
            return;
        }

        var trimmed = text.Trim();
        _spellLine.Text = "";
        HideContextMenu();
        _busy = true;
        _busyStatusText = "spell resolving";
        SetBusy(true);
        SetProviderBusyStatus();
        _lastError = null;

        try
        {
            // Submit the cast, then play a casting minigame while the provider thinks.
            // The score arrives with await_cast because it does not exist at submit time.
            var begin = await _session.ExecuteAsync(new BeginCastCommand(trimmed));
            _lastResult = begin;
            if (begin.Action != "begin_cast")
            {
                // Instant charter lane: a known form's name resolved the cast inside
                // begin_cast itself — nothing is pending, so no minigame and no await.
                RecordChronicleIfComplete(begin);
            }
            else if (begin.Success)
            {
                var performance = await PlayCastMinigameAsync(trimmed);
                _lastResult = await _session.ExecuteAsync(new AwaitCastCommand(performance));
                RecordChronicleIfComplete(_lastResult);
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            _busy = false;
            _busyStatusText = null;
            RefreshView();
        }
    }

    /// <summary>
    /// The repertoire draw (docs/CASTING_AND_MINIGAMES.md): the GUI pulls a random casting
    /// game, never the same one twice in a row so every game stays in circulation.
    /// </summary>
    private Task<CastPerformance> PlayCastMinigameAsync(string spellText)
    {
        const int repertoire = 4;
        var pick = _minigameRng.Next(repertoire);
        if (pick == _lastMinigame)
        {
            pick = (pick + 1 + _minigameRng.Next(repertoire - 1)) % repertoire;
        }

        _lastMinigame = pick;
        return pick switch
        {
            0 => _minigame.PlayAsync(spellText, PendingCastSettled),
            1 => _threadKnot.PlayAsync(spellText, PendingCastSettled),
            2 => _trueSigil.PlayAsync(spellText, PendingCastSettled),
            _ => _boneSong.PlayAsync(spellText, PendingCastSettled),
        };
    }

    private bool PendingCastSettled()
    {
        var pending = _session.Observation().PendingCast;
        return pending is null || pending.State is not "resolving";
    }

    private void RecordChronicleIfComplete(ActionResult result)
    {
        if (result.Deltas.Any(delta => delta.Operation.Equals("runComplete", StringComparison.OrdinalIgnoreCase)))
        {
            CrossRunMemorialStore.AppendLatestChronicle(_session.Engine.State);
        }
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

    private void FocusCommandLine(string prefix, int? caretColumn = null)
    {
        HideContextMenu();
        _commandLine.Text = prefix;
        _commandLine.CaretColumn = Math.Clamp(caretColumn ?? prefix.Length, 0, prefix.Length);
        _commandLine.GrabFocus();
    }

    private async Task ExecuteAsync(GameCommand command)
    {
        if (_busy)
        {
            return;
        }

        HideContextMenu();
        _busy = true;
        _busyStatusText = BusyStatusFor(command);
        SetBusy(true);
        SetProviderBusyStatus();
        _lastError = null;

        try
        {
            _lastResult = await _session.ExecuteAsync(command);
            RecordChronicleIfComplete(_lastResult);
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
        }
        finally
        {
            _busy = false;
            _busyStatusText = null;
            RefreshView();
        }
    }

    private string BusyStatusFor(GameCommand command) =>
        command switch
        {
            TalkCommand => "dialogue resolving",
            CastCommand => "spell resolving",
            BeginCastCommand => "spell resolving",
            AwaitCastCommand => "spell resolving",
            SaveCommand => "saving",
            LoadCommand => "loading",
            _ => "resolving...",
        };

    private void SetProviderBusyStatus()
    {
        if (_providerStatus is null || _providerStatusPanel is null)
        {
            return;
        }

        _providerStatus.Text = _busyStatusText ?? "resolving...";
        _providerStatus.AddThemeColorOverride("font_color", UiTheme.Background);
        _providerStatusPanel.AddThemeStyleboxOverride("panel", UiTheme.PillBox(UiTheme.Warning));
    }

    private void RefreshView()
    {
        var observation = _session.Observation();
        var view = observation.View;
        EnsureMapCells(view);
        UpdateMapCellSizing();
        RenderMap(view);
        RenderSidebars(view, observation.PendingCast);
        _lastPendingCastKey = PendingCastKey(observation.PendingCast);
        SetBusy(_busy);
        SessionHost.Session = _session;
    }

    // Ctrl + a bound game key (Ctrl+G to pick up, Ctrl+H/J/K/L to move, ...) runs that command even
    // when a LineEdit has focus: Ctrl is the "bypass the text box" modifier. Consumed here so the
    // letter is never typed into the focused field.
    private bool TryConsumeCtrlCommand(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo || !key.CtrlPressed)
        {
            return false;
        }

        if (_session is null || _escMenu is null || _escMenu.Visible || _busy)
        {
            return false;
        }

        var command = CommandForKey(key.Keycode);
        if (command is null)
        {
            return false;
        }

        _ = ExecuteAsync(command);
        GetViewport().SetInputAsHandled();
        return true;
    }

    private bool TryConsumeNumpadMovement(InputEvent @event)
    {
        if (@event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return false;
        }

        if (_session is null || _escMenu is null || _escMenu.Visible || _busy)
        {
            return false;
        }

        var command = NumpadCommandForKey(key.PhysicalKeycode) ?? NumpadCommandForKey(key.Keycode);
        if (command is null)
        {
            return false;
        }

        _ = ExecuteAsync(command);
        GetViewport().SetInputAsHandled();
        return true;
    }

    private void RenderMap(GameView view)
    {
        _shimmerCells.Clear();
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

                var background = TerrainBackground(tile, x, y);
                var glyphColor = GlyphColor(entity, tile, selected, x, y, view.ControlledEntityId);
                cell.Text = CellGlyph(tile, entity, selected, x, y);
                cell.TooltipText = CellTooltip(point, tile, entity, selected);
                cell.AddThemeColorOverride("font_color", glyphColor);
                cell.AddThemeColorOverride("font_hover_color", glyphColor);

                var normal = CellStyle(background, selected);
                cell.AddThemeStyleboxOverride("normal", normal);
                cell.AddThemeStyleboxOverride("hover", CellStyle(background.Lightened(0.08f), selected));
                cell.AddThemeStyleboxOverride("pressed", CellStyle(background.Darkened(0.12f), selected));
                cell.AddThemeStyleboxOverride("focus", normal);

                RegisterShimmer(cell, normal, background, glyphColor, tile, entity, selected, view, x, y);
            }
        }
    }

    /// <summary>
    /// Living cells pulse: magical terrain thrums at its style's amplitude, the controlled body's
    /// glyph breathes, and the targeting ring glows. Amplitudes stay gentle — the map should
    /// shimmer, not strobe.
    /// </summary>
    private void RegisterShimmer(
        Button cell,
        StyleBoxFlat box,
        Color background,
        Color glyphColor,
        MapTileCard? tile,
        EntityCard? entity,
        bool selected,
        GameView view,
        int x,
        int y)
    {
        var lit = tile is { Explored: true, Visible: true };
        if (entity is not null && entity.Id == view.ControlledEntityId && lit)
        {
            _shimmerCells.Add(new ShimmerCell(cell, box, background, glyphColor, 0.35f, 0f));
            return;
        }

        if (selected)
        {
            _shimmerCells.Add(new ShimmerCell(cell, box, background, glyphColor, 0.3f, 1.6f));
            return;
        }

        if (entity is null && lit)
        {
            var style = TerrainStyles.Resolve(tile!.Terrain, IsWallLike(tile));
            if (style.Shimmer > 0f)
            {
                _shimmerCells.Add(new ShimmerCell(
                    cell, box, background, glyphColor, style.Shimmer, TerrainStyles.PhaseFor(x, y)));
            }
        }
    }

    private void UpdateShimmer()
    {
        // ~20 Hz is plenty for a slow pulse and keeps per-frame work negligible.
        if (_shimmerCells.Count == 0 || _shimmerClock - _shimmerLastUpdate < 0.05)
        {
            return;
        }

        _shimmerLastUpdate = _shimmerClock;
        foreach (var shimmer in _shimmerCells)
        {
            var wave = 0.5f + (0.5f * MathF.Sin(((float)_shimmerClock * 2.1f) + shimmer.Phase));
            shimmer.Box.BgColor = shimmer.BaseBackground.Lightened(shimmer.Amplitude * wave * 0.45f);
            shimmer.Cell.AddThemeColorOverride(
                "font_color",
                shimmer.BaseGlyph.Lightened(shimmer.Amplitude * wave * 0.8f));
        }
    }

    private void RenderSidebars(GameView view, PendingCastView? pendingCast)
    {
        var player = view.Entities.FirstOrDefault(entity => entity.Id == view.ControlledEntityId);
        Color hpColor;
        double hpFraction = 0;
        if (player?.HitPoints is null || player.MaxHitPoints is null || player.MaxHitPoints == 0)
        {
            hpColor = UiTheme.Muted;
        }
        else
        {
            hpFraction = (double)player.HitPoints.Value / player.MaxHitPoints.Value;
            hpColor = hpFraction switch
            {
                >= 0.6 => UiTheme.Wild,
                >= 0.3 => UiTheme.Warning,
                _ => UiTheme.Danger,
            };
        }

        _hpBar.Value = hpFraction;
        _hpBar.AddThemeStyleboxOverride(
            "fill",
            UiTheme.Box(hpColor, hpColor, borderWidth: 0, radius: 6, shadow: false, marginX: 0, marginY: 0));
        _hpLabel.Text = player?.HitPoints is null || player.MaxHitPoints is null
            ? "HP ?"
            : $"{player.HitPoints}/{player.MaxHitPoints}";
        _hpLabel.AddThemeColorOverride("font_color", hpColor);

        var mana = view.Character?.Mana;
        var maxMana = view.Character?.MaxMana;
        Color manaColor;
        double manaFraction = 0;
        if (mana is null || maxMana is null || maxMana == 0)
        {
            manaColor = UiTheme.Muted;
        }
        else
        {
            manaFraction = (double)mana.Value / maxMana.Value;
            manaColor = UiTheme.Focus;
        }

        _manaBar.Value = manaFraction;
        _manaBar.AddThemeStyleboxOverride(
            "fill",
            UiTheme.Box(manaColor, manaColor, borderWidth: 0, radius: 6, shadow: false, marginX: 0, marginY: 0));
        _manaLabel.Text = mana is null || maxMana is null ? "MP ?" : $"{mana}/{maxMana}";
        _manaLabel.AddThemeColorOverride("font_color", manaColor);

        var origin = view.Character?.OriginName;
        var pendingSuffix = pendingCast is null ? "" : $" | Cast {pendingCast.State}";
        var autoplayPrefix = _autoplay ? "[AUTOPLAY ▶ press P to stop] " : "";
        _statusLine.Text = view.Character is null
            ? $"{autoplayPrefix}Turn {view.Turn}{pendingSuffix}"
            : $"{autoplayPrefix}Turn {view.Turn} — {origin} (VIG {view.Character.Vigor} ATT {view.Character.Attunement} COM {view.Character.Composure}){pendingSuffix}";
        _objectiveLine.Text = view.CurrentObjective is null
            ? "No immediate objective — explore, talk, read, or work wild magic."
            : $"Next: {view.CurrentObjective.NextStep}";
        _objectiveLine.AddThemeColorOverride(
            "font_color",
            view.CurrentObjective?.ReadyToReturn == true ? UiTheme.Warning : UiTheme.Wild);

        foreach (var child in _statusChips.GetChildren().ToArray())
        {
            child.QueueFree();
        }

        foreach (var status in view.Statuses ?? Array.Empty<StatusCard>())
        {
            var chip = new PanelContainer();
            chip.AddThemeStyleboxOverride("panel", UiTheme.PillBox(UiTheme.Focus.Darkened(0.3f)));
            var label = new Label { Text = status.DisplayName };
            label.AddThemeFontSizeOverride("font_size", 11);
            label.AddThemeColorOverride("font_color", UiTheme.Text);
            chip.AddChild(label);
            _statusChips.AddChild(chip);
        }

        var errored = !_busy && _lastError is not null;
        _providerStatus.Text = ProviderStatusText(pendingCast, errored);
        var pillColor = ProviderStatusColor(pendingCast, errored);
        _providerStatus.AddThemeColorOverride("font_color", UiTheme.Background);
        _providerStatusPanel.AddThemeStyleboxOverride("panel", UiTheme.PillBox(pillColor));

        _entities.Text = string.Join(
            "\n",
            view.Entities
                .Where(entity => entity.Id != view.ControlledEntityId)
                .OrderBy(entity => entity.Faction == "empire" ? 0 : 1)
                .ThenBy(entity => entity.Id)
                .Select(FormatEntity));

        _inventory.Text = string.Join(
            "  ·  ",
            (view.Inventory ?? Array.Empty<ItemCard>())
                .OrderBy(item => item.Name)
                .Select(item => $"{UiTheme.Escape(item.Name)} x{item.Quantity}{(item.Protected ? UiTheme.Colorize(" protected", UiTheme.Warning) : "")}"));

        RenderRepertoire(view.Repertoire, pendingCast);

        var logLines = new List<string>();
        if (_lastError is not null)
        {
            logLines.Add(UiTheme.Colorize($"Error: {_lastError}", UiTheme.Danger));
        }

        if (_lastResult is not null)
        {
            var resultLabel = _lastResult.Success ? "ok" : _lastResult.TechnicalFailure ? "technical failure" : "rejected";
            var resultColor = _lastResult.Success ? UiTheme.Wild : _lastResult.TechnicalFailure ? UiTheme.Danger : UiTheme.Warning;
            logLines.Add($"{UiTheme.Escape(_lastResult.Action)}: {UiTheme.Colorize(resultLabel, resultColor)}");
        }

        if (pendingCast is not null)
        {
            var pendingColor = pendingCast.State switch
            {
                "ready" => UiTheme.Wild,
                "failed" => UiTheme.Danger,
                _ => UiTheme.Warning,
            };
            var effectText = pendingCast.EffectTypes is { Count: > 0 }
                ? $" [{string.Join(", ", pendingCast.EffectTypes)}]"
                : "";
            // Show the spell the player reached for, not the internal cast id.
            var spellLabel = string.IsNullOrWhiteSpace(pendingCast.Text) ? pendingCast.State : pendingCast.Text.Trim();
            logLines.Add(UiTheme.Colorize(
                $"Wild spell: {spellLabel} ({pendingCast.State}){effectText}",
                pendingColor));
            if (!string.IsNullOrWhiteSpace(pendingCast.Error))
            {
                logLines.Add(UiTheme.Colorize(UiTheme.Escape(pendingCast.Error), UiTheme.Danger));
            }
        }

        var cards = view.MessageCards is { Count: > 0 }
            ? view.MessageCards.TakeLast(14).Select(RenderMessageCard)
            : view.Messages.TakeLast(14).Select(UiTheme.Escape);
        logLines.AddRange(cards);

        // A faint hairline between messages so colored lines read as distinct entries rather than
        // one blurred block (message-log immersion pass).
        _log.Text = string.Join($"\n[color=#2b2348]{new string('─', 44)}[/color]\n", logLines);
    }

    /// <summary>
    /// Renders one curated message as BBCode: each colored segment (e.g. a tinted damage type) keeps
    /// its color, uncolored runs fall back to the line's accent, and the player's own speech is
    /// italicized. Colors come from Core's renderer-agnostic MessageLog palette.
    /// </summary>
    private static string RenderMessageCard(MessageCard card)
    {
        var body = new System.Text.StringBuilder();
        foreach (var segment in card.Segments)
        {
            var text = UiTheme.Escape(segment.Text);
            var color = segment.Color ?? card.AccentColor;
            body.Append(string.IsNullOrEmpty(color) ? text : $"[color=#{color}]{text}[/color]");
        }

        var rendered = body.ToString();
        return card.Kind == MessageKind.PlayerSpeech ? $"[i]{rendered}[/i]" : rendered;
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
                    FocusMode = FocusModeEnum.None,
                    ThemeTypeVariation = "MapCell",
                };
                cell.Pressed += () => _ = ExecuteAsync(new TargetCommand(point));
                cell.GuiInput += @event => OnMapCellGuiInput(@event, point);
                _mapGrid.AddChild(cell);
                _cells[y, x] = cell;
            }
        }

        UpdateMapCellSizing();
        CallDeferred(nameof(UpdateMapCellSizing));
    }

    private void OnMapCellGuiInput(InputEvent @event, GridPoint point)
    {
        if (@event is not InputEventMouseButton mouse
            || !mouse.Pressed
            || mouse.ButtonIndex != MouseButton.Right)
        {
            return;
        }

        if (_busy || _escMenu.Visible)
        {
            return;
        }

        ShowContextMenu(point, GetGlobalMousePosition());
        GetViewport().SetInputAsHandled();
    }

    private void ShowContextMenu(GridPoint point, Vector2 globalPosition)
    {
        var view = _session.Observation().View;
        var entity = EntityAtPoint(view, point);
        if (entity is null)
        {
            HideContextMenu();
            return;
        }

        var actions = (entity.Actions ?? Array.Empty<ContextActionCard>())
            .Where(action => action.Enabled || !string.IsNullOrWhiteSpace(action.DisabledReason))
            .ToArray();
        if (actions.Length == 0)
        {
            HideContextMenu();
            return;
        }

        foreach (var child in _contextMenuItems.GetChildren())
        {
            _contextMenuItems.RemoveChild(child);
            child.QueueFree();
        }

        var title = new Label
        {
            Text = entity.Name,
            HorizontalAlignment = HorizontalAlignment.Left,
            TooltipText = $"{entity.Id} at {entity.X},{entity.Y}",
        };
        title.AddThemeColorOverride("font_color", UiTheme.Empire);
        title.AddThemeFontSizeOverride("font_size", 12);
        _contextMenuItems.AddChild(title);

        foreach (var action in actions)
        {
            var button = SmallButton(action.Label);
            button.CustomMinimumSize = new Vector2(150, 30);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.Disabled = !action.Enabled;
            button.TooltipText = action.Enabled
                ? action.Command
                : action.DisabledReason ?? action.Command;
            button.Pressed += () => InvokeContextAction(action);
            _contextMenuItems.AddChild(button);
        }

        _contextMenu.Position = ContextMenuPosition(globalPosition);
        _contextMenu.Visible = true;
        _contextMenu.MoveToFront();
    }

    private void InvokeContextAction(ContextActionCard action)
    {
        HideContextMenu();
        if (action.Presentation.Equals("compose", StringComparison.OrdinalIgnoreCase))
        {
            FocusCommandLine(action.Command, ComposeCaretColumn(action));
            return;
        }

        _ = ExecuteAsync(TextCommandParser.Parse(action.Command));
    }

    private void HideContextMenu()
    {
        if (_contextMenu is not null)
        {
            _contextMenu.Visible = false;
        }
    }

    private Vector2 ContextMenuPosition(Vector2 requested)
    {
        var viewport = GetViewportRect().Size;
        const float approximateWidth = 180f;
        const float approximateHeight = 240f;
        return new Vector2(
            Mathf.Clamp(requested.X + 6f, 0f, Math.Max(0f, viewport.X - approximateWidth)),
            Mathf.Clamp(requested.Y + 6f, 0f, Math.Max(0f, viewport.Y - approximateHeight)));
    }

    private static int ComposeCaretColumn(ContextActionCard action)
    {
        if (action.Id.Equals("give", StringComparison.OrdinalIgnoreCase))
        {
            var marker = action.Command.IndexOf("  to ", StringComparison.Ordinal);
            if (marker >= 0)
            {
                return marker + 1;
            }
        }

        return action.Command.Length;
    }

    private static EntityCard? EntityAtPoint(GameView view, GridPoint point) =>
        view.Entities
            .Where(entity => entity.X == point.X && entity.Y == point.Y)
            .OrderBy(entity => entity.Id == view.ControlledEntityId ? 1 : 0)
            .LastOrDefault();

    private void UpdateMapCellSizing()
    {
        if (_mapFrame is null || _cells.Length == 0)
        {
            return;
        }

        var rows = _cells.GetLength(0);
        var columns = _cells.GetLength(1);
        if (rows == 0 || columns == 0)
        {
            return;
        }

        var availableWidth = _mapFrame.Size.X
            - (UiTheme.SpaceMd * 2)
            - (MapCellSeparation * Math.Max(0, columns - 1));
        var availableHeight = _mapFrame.Size.Y
            - (UiTheme.SpaceSm * 2)
            - (MapCellSeparation * Math.Max(0, rows - 1));
        if (availableWidth <= 0 || availableHeight <= 0)
        {
            return;
        }

        var cellSize = Math.Clamp(
            (int)MathF.Floor(MathF.Min(availableWidth / columns, availableHeight / rows)),
            MinMapCellSize,
            MaxMapCellSize);
        var fontSize = Math.Clamp(
            (int)MathF.Round(cellSize * MapFontSizeRatio),
            MinMapFontSize,
            MaxMapFontSize);
        var size = new Vector2(cellSize, cellSize);
        for (var y = 0; y < rows; y++)
        {
            for (var x = 0; x < columns; x++)
            {
                var cell = _cells[y, x];
                cell.CustomMinimumSize = size;
                cell.AddThemeFontSizeOverride("font_size", fontSize);
            }
        }
    }

    private static Control FullRect(Control control)
    {
        control.SetAnchorsPreset(LayoutPreset.FullRect);
        return control;
    }

    private static PanelContainer PanelBox() =>
        new() { SizeFlagsHorizontal = SizeFlags.ExpandFill };

    private static VBoxContainer Section(string title, Control content, Color accent, bool expand = false)
    {
        var box = new VBoxContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = expand ? SizeFlags.ExpandFill : SizeFlags.ShrinkBegin,
        };
        box.AddThemeConstantOverride("separation", UiTheme.SpaceXs);

        var header = new HBoxContainer();
        header.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        header.AddChild(new ColorRect
        {
            Color = accent,
            CustomMinimumSize = new Vector2(3, 15),
        });
        header.AddChild(new Label
        {
            Text = title,
            ThemeTypeVariation = "SectionHeader",
            VerticalAlignment = VerticalAlignment.Center,
        });
        box.AddChild(header);
        box.AddChild(content);
        return box;
    }

    private static RichTextLabel Readout(float minHeight) =>
        new()
        {
            BbcodeEnabled = true,
            ContextMenuEnabled = true,
            FitContent = false,
            ScrollActive = true,
            SelectionEnabled = true,
            CustomMinimumSize = new Vector2(0, minHeight),
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };

    private static Label SmallLabel(string text)
    {
        var label = new Label
        {
            Text = text,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.AddThemeColorOverride("font_color", UiTheme.Muted);
        return label;
    }

    private static Button SmallButton(string text) =>
        new()
        {
            Text = text,
            CustomMinimumSize = new Vector2(74, 34),
        };

    private void AddMoveButton(BoxContainer parent, string label, Direction direction, BindableAction action, string numpadHint)
    {
        var button = SmallButton(label);
        button.CustomMinimumSize = new Vector2(44, 34);
        button.TooltipText = $"Move {label} ({KeyName(action)} / {numpadHint})";
        button.Pressed += () => _ = ExecuteAsync(new MoveCommand(direction));
        parent.AddChild(button);
        _busyControls.Add(button);
    }

    private static string KeyName(BindableAction action) =>
        Keybindings.DisplayName(Keybindings.KeyFor(action));

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

        // Repertoire chips are rebuilt on every render, so they ride their own list rather
        // than accumulating stale entries in _busyControls.
        foreach (var chip in _repertoireChips)
        {
            chip.Disabled = busy;
        }
    }

    /// <summary>
    /// The Repertoire section: the player's reliable magic as one-click affordances — known
    /// charter forms (instant, fixed cost) and, when the experiment flag is on, spell echoes.
    /// Exists because these were previously reachable only by knowing the typed verbs.
    /// </summary>
    private void RenderRepertoire(RepertoireCard? repertoire, PendingCastView? pendingCast)
    {
        _repertoireChips.Clear();
        foreach (var child in _repertoireBox.GetChildren().ToArray())
        {
            child.QueueFree();
        }

        var charterSpells = repertoire?.CharterSpells ?? Array.Empty<CharterSpellCard>();
        var echoes = repertoire?.Echoes ?? Array.Empty<EchoCard>();
        if (charterSpells.Count == 0 && echoes.Count == 0)
        {
            var empty = SmallLabel("No charter forms known — they are learned from manuals, warrants, and notices.");
            empty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            empty.AddThemeFontSizeOverride("font_size", 11);
            _repertoireBox.AddChild(empty);
            return;
        }

        // Charter (and echo) casts are blocked while a wild cast is pending, so the chips
        // grey out instead of failing with a log message.
        var blocked = pendingCast is not null;

        if (charterSpells.Count > 0)
        {
            var charterFlow = new HFlowContainer();
            charterFlow.AddThemeConstantOverride("h_separation", UiTheme.SpaceXs);
            charterFlow.AddThemeConstantOverride("v_separation", UiTheme.SpaceXs);
            _repertoireBox.AddChild(charterFlow);

            foreach (var spell in charterSpells)
            {
                var chip = SmallButton($"{spell.Name} · {spell.CostText}");
                chip.AddThemeFontSizeOverride("font_size", 12);
                chip.TooltipText = $"{spell.Summary}\nCost: {spell.CostText} · Targeting: {spell.Targeting}\n"
                    + "Instant charter magic — no gamble, no waiting.\n"
                    + $"Typed: charter {spell.Id}";
                var spellId = spell.Id;
                chip.Pressed += () => _ = ExecuteAsync(new CharterCommand(spellId));
                chip.Disabled = blocked;
                charterFlow.AddChild(chip);
                _repertoireChips.Add(chip);
            }
        }

        if (echoes.Count > 0)
        {
            var echoFlow = new HFlowContainer();
            echoFlow.AddThemeConstantOverride("h_separation", UiTheme.SpaceXs);
            echoFlow.AddThemeConstantOverride("v_separation", UiTheme.SpaceXs);
            _repertoireBox.AddChild(echoFlow);

            foreach (var echo in echoes)
            {
                var chip = SmallButton($"«{echo.Name}» ×{echo.TimesCast}");
                chip.AddThemeFontSizeOverride("font_size", 12);
                chip.TooltipText = "Re-cast this recorded spell instantly.\n"
                    + $"Repetition fatigue: +{echo.NextCastFatigue} mana on this cast.\n"
                    + $"Typed: echo {echo.Index}";
                var reference = echo.Index.ToString();
                chip.Pressed += () => _ = ExecuteAsync(new EchoCommand(reference));
                chip.Disabled = blocked;
                echoFlow.AddChild(chip);
                _repertoireChips.Add(chip);
            }
        }
    }

    private static string CellGlyph(MapTileCard? tile, EntityCard? entity, bool selected, int x, int y)
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

        // Keep open, unoccupied ground visually quiet. Terrain identity still comes through its
        // background color, shimmer, and tooltip; punctuation texture is reserved for blocking
        // features so a clear route reads immediately at a glance.
        if (!tile.BlocksMovement && !tile.BlocksSight)
        {
            return ".";
        }

        return TerrainStyles.GlyphFor(TerrainStyles.Resolve(tile.Terrain, IsWallLike(tile)), x, y);
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

            lines.Add("right-click for actions");
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
        var factionColor = faction switch
        {
            "empire" => UiTheme.Danger,
            "player" => UiTheme.Focus,
            _ => UiTheme.Gold,
        };
        var name = UiTheme.Colorize(entity.Name, factionColor);
        return $"{UiTheme.Escape(entity.Glyph.ToString())} {name} ({entity.X},{entity.Y}) {UiTheme.Escape(faction)}{UiTheme.Escape(hp)}";
    }

    private static Color GlyphColor(
        EntityCard? entity,
        MapTileCard? tile,
        bool selected,
        int x,
        int y,
        string? controlledEntityId)
    {
        if (tile is null || !tile.Explored)
        {
            return selected ? UiTheme.Gold : UiTheme.Muted.Darkened(0.35f);
        }

        var dim = tile.Visible ? 0f : 0.35f;
        if (entity is not null)
        {
            // The controlled body glows wild-green even after a body swap.
            if (entity.Id == controlledEntityId)
            {
                return UiTheme.Wild.Darkened(dim);
            }

            var color = entity.Faction == "empire"
                ? UiTheme.Danger
                : entity.Faction == "player"
                    ? UiTheme.Focus
                    : UiTheme.Gold;
            return color.Darkened(dim);
        }

        if (selected)
        {
            return UiTheme.Gold;
        }

        var style = TerrainStyles.Resolve(tile.Terrain, IsWallLike(tile));
        return TerrainStyles.GlyphColorFor(style, x, y).Darkened(dim);
    }

    private static Color TerrainBackground(MapTileCard? tile, int x, int y)
    {
        if (tile is null)
        {
            return UiTheme.OffMap;
        }

        if (!tile.Explored)
        {
            return UiTheme.UnknownTile;
        }

        var style = TerrainStyles.Resolve(tile.Terrain, IsWallLike(tile));
        var color = TerrainStyles.BackgroundFor(style, x, y);
        return tile.Visible ? color : color.Darkened(0.28f);
    }

    private static bool IsWallLike(MapTileCard tile) =>
        tile.BlocksSight
        || (tile.BlocksMovement
            && !tile.Terrain.Contains("rubble", StringComparison.OrdinalIgnoreCase)
            && !tile.Terrain.Contains("vine", StringComparison.OrdinalIgnoreCase));

    private static StyleBoxFlat CellStyle(Color background, bool selected)
    {
        var fill = selected ? background.Lightened(0.1f) : background;
        return UiTheme.Box(
            fill,
            selected ? UiTheme.Gold : fill,
            borderWidth: selected ? 1 : 0,
            radius: 0,
            shadow: false,
            marginX: 0,
            marginY: 0);
    }

    // Routes through the rebindable Keybindings table (arrows ride its fixed fallback).
    // Interface actions (FocusSpell etc.) return null here on purpose: they are not game
    // commands, and returning null keeps Ctrl+<their key> free for normal editing shortcuts.
    private static GameCommand? CommandForKey(Key key) =>
        Keybindings.ActionForKey(key) switch
        {
            BindableAction.MoveNorth => new MoveCommand(Direction.North),
            BindableAction.MoveSouth => new MoveCommand(Direction.South),
            BindableAction.MoveWest => new MoveCommand(Direction.West),
            BindableAction.MoveEast => new MoveCommand(Direction.East),
            BindableAction.MoveNorthWest => new MoveCommand(Direction.NorthWest),
            BindableAction.MoveNorthEast => new MoveCommand(Direction.NorthEast),
            BindableAction.MoveSouthWest => new MoveCommand(Direction.SouthWest),
            BindableAction.MoveSouthEast => new MoveCommand(Direction.SouthEast),
            BindableAction.Pickup => new PickupCommand(),
            BindableAction.Wait => new WaitCommand(),
            BindableAction.Inspect => new InspectCommand(),
            _ => null,
        };

    private static GameCommand? NumpadCommandForKey(Key key) =>
        key switch
        {
            Key.Kp8 => new MoveCommand(Direction.North),
            Key.Kp2 => new MoveCommand(Direction.South),
            Key.Kp4 => new MoveCommand(Direction.West),
            Key.Kp6 => new MoveCommand(Direction.East),
            Key.Kp7 => new MoveCommand(Direction.NorthWest),
            Key.Kp9 => new MoveCommand(Direction.NorthEast),
            Key.Kp1 => new MoveCommand(Direction.SouthWest),
            Key.Kp3 => new MoveCommand(Direction.SouthEast),
            Key.Kp5 => new WaitCommand(),
            _ => null,
        };
}
