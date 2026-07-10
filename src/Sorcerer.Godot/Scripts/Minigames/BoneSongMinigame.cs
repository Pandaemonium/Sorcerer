using Godot;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// Bone-Song is a carved-drum response played in a night hall. Three strike marks sit at the
/// drum's rim, center, and edge; incoming ivory notes follow readable lane-specific swoops and
/// land on the corresponding mark. Gold flourishes are optional boasts that add power without
/// punishing restraint. A/S/D answer the marks in left-to-right screen order (Space also strikes
/// the center), or the marks can be clicked directly. Motion is the timing authority and
/// synthesized audio only reinforces it, preserving muted play and avoiding latency calibration.
///
/// A circle of bone-singer silhouettes rings the drum; each streak note lights another ember
/// until the whole hall answers, and a broken groove dims them again. Aurora ribbons above the
/// hall breathe with the streak. Strikes are timed sub-frame, early presses during the count-in
/// are honored near the first beat, and a wrong drum only consumes the neighboring note when the
/// press was clearly meant for it.
///
/// This node owns presentation and input reduction. <see cref="BoneSongPattern"/> owns the
/// deterministic phrase book and <see cref="BoneSongScoring"/> maps renderer-free totals to
/// <see cref="CastPerformance"/>.
/// </summary>
public partial class BoneSongMinigame : Control
{
    private enum Phase
    {
        Idle,
        FadeIn,
        CountIn,
        Play,
        Finale,
    }

    private struct LiveNote
    {
        public BoneSongNote Note;
        public bool Resolved;
        public bool Hit;
        public float FeedbackAge;
    }

    private struct Spark
    {
        public Vector2 Position;
        public Vector2 Velocity;
        public float Life;
        public float MaxLife;
        public float Size;
        public Color Color;
    }

    private struct Ring
    {
        public Vector2 Center;
        public float StartRadius;
        public float EndRadius;
        public float Age;
        public float Life;
        public float Width;
        public Color Color;
    }

    private struct Mote
    {
        public Vector2 Anchor;
        public float Fall;
        public float Sway;
        public float Phase;
        public float Size;
        public float Alpha;
    }

    private const float FadeInSeconds = 0.16f;
    private const float FinaleSeconds = 0.62f;
    private const float GraceSeconds = 2.0f;
    private const float ApproachSeconds = 3.8f;
    private const int MaximumSparks = 520;
    private const int MoteCount = 56;
    private const int SingerCount = 9;

    /// <summary>A wrong-lane press consumes the note only inside this window; farther out the
    /// note survives and the press alone is penalized, so one slip cannot cost two notes.</summary>
    private const double WrongDrumConsumeSeconds = 0.08;

    /// <summary>Streak needed to earn an extra gold boast in an upcoming phrase, and to arm the
    /// skip confirmation that protects a hot groove from a reflexive Escape.</summary>
    private const int HotStreak = 6;

    private const string SkipIdleText = "Cast Unshaped  (Esc)";
    private const string SkipArmedText = "Esc Again — Let Go";

    private static readonly Color Night = new(0.008f, 0.014f, 0.026f);
    private static readonly Color Bone = new(0.93f, 0.90f, 0.80f);
    private static readonly Color BoneDim = new(0.47f, 0.45f, 0.40f);
    private static readonly Color DeepBlue = new(0.25f, 0.52f, 0.65f);
    private static readonly Color HeartIvory = new(0.94f, 0.82f, 0.63f);
    private static readonly Color BrightJade = UiTheme.Wild;
    private static readonly Color Gold = new(1.0f, 0.76f, 0.30f);
    private static readonly Color WhiteHot = new(1.0f, 0.97f, 0.88f);
    private static readonly Color MissRed = new(1.0f, 0.31f, 0.30f);
    private static readonly Color SeaInk = new(0.27f, 0.48f, 0.58f);
    private static readonly Color Silhouette = new(0.030f, 0.044f, 0.066f);

    private TaskCompletionSource<CastPerformance>? _completion;
    private Func<bool>? _providerSettled;
    private Phase _phase = Phase.Idle;
    private string _spellText = "";
    private double _phaseClock;
    private double _time;
    private double _activeSeconds;
    private double _graceClock;
    private bool _providerReady;
    private bool _everPlayed;
    private bool _skipped;
    private ulong _lastTickUsec;

    private BoneSongPhrase _phrase = BoneSongPattern.Create("", 0);
    private BoneSongPhrase _nextPhrase = BoneSongPattern.Create("", 1);
    private LiveNote[] _notes = Array.Empty<LiveNote>();
    private int _phraseIndex;
    private int _lastCountBeat = -1;
    private int _lastMetronomeBeat = -1;

    private int _requiredResolved;
    private int _requiredHits;
    private double _timingQualityTotal;
    private int _boastsOffered;
    private int _boastsHit;
    private int _mistimedStrikes;
    private int _completedPhrases;
    private int _streak;
    private int _longestStreak;

    private readonly float[] _laneFlash = new float[3];
    private readonly float[] _laneError = new float[3];
    private readonly List<Spark> _sparks = new(MaximumSparks);
    private int _sparkCursor;
    private readonly List<Ring> _rings = new();
    private Mote[] _motes = Array.Empty<Mote>();
    private readonly float[] _singerGlow = new float[SingerCount];
    private readonly Random _vfxRng = new();
    private float _shake;
    private Vector2 _shakeOffset;
    private string _judgement = "";
    private string _drift = "";
    private float _judgementLife;
    private float _judgementPop;
    private float _auroraRamp;
    private float _fireRamp;
    private float _emberRamp;
    private float _skipArm;

    private DrawLayer _ink = null!;
    private DrawLayer _glow = null!;
    private BoneSongVoice _voice = null!;
    private Label _title = null!;
    private Label _subtitle = null!;
    private Label _hint = null!;
    private Label _judgementLabel = null!;
    private Label _driftLabel = null!;
    private Label _tally = null!;
    private readonly Label[] _laneLabels = new Label[3];
    private ProgressBar _powerBar = null!;
    private ProgressBar _controlBar = null!;
    private Button _skip = null!;

    public bool Active => _completion is not null;

    /// <summary>
    /// Strikes are compared against the visual clock advanced by the real time elapsed since the
    /// last process tick, so timing is not quantized to the frame rate (a full frame at 30 fps
    /// would otherwise eat half of the 75 ms "TRUE" window as a constant late bias).
    /// </summary>
    private double StrikeChartTime()
    {
        var sinceTick = (Time.GetTicksUsec() - _lastTickUsec) / 1_000_000.0;
        return ChartTime() + Math.Clamp(sinceTick, 0.0, 0.05);
    }

    /// <summary>Hit window scaled so faster phrases never let one window swallow a neighbor.</summary>
    private double HitWindow => Math.Min(0.22, _phrase.BeatSeconds * 0.45);

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 60;
        BuildLayers();
        BuildHud();
        _voice = new BoneSongVoice();
        AddChild(_voice);
        SetProcess(false);
    }

    public Task<CastPerformance> PlayAsync(string spellText, Func<bool> providerSettled)
    {
        if (_completion is not null)
        {
            return Task.FromResult(CastPerformance.Neutral);
        }

        _completion = new TaskCompletionSource<CastPerformance>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _providerSettled = providerSettled;
        _spellText = spellText;
        _phase = Phase.FadeIn;
        _phaseClock = 0;
        _time = 0;
        _activeSeconds = 0;
        _graceClock = 0;
        _providerReady = false;
        _everPlayed = false;
        _skipped = false;
        _requiredResolved = 0;
        _requiredHits = 0;
        _timingQualityTotal = 0;
        _boastsOffered = 0;
        _boastsHit = 0;
        _mistimedStrikes = 0;
        _completedPhrases = 0;
        _streak = 0;
        _longestStreak = 0;
        _lastCountBeat = -1;
        _lastTickUsec = Time.GetTicksUsec();
        Array.Clear(_laneFlash);
        Array.Clear(_laneError);
        Array.Clear(_singerGlow);
        _sparks.Clear();
        _sparkCursor = 0;
        _rings.Clear();
        _shake = 0;
        _shakeOffset = Vector2.Zero;
        _judgement = "";
        _drift = "";
        _judgementLife = 0;
        _judgementPop = 0;
        _auroraRamp = 0;
        _fireRamp = 0;
        _emberRamp = 0;
        _skipArm = 0;
        _skip.Text = SkipIdleText;
        BeginPhrases();
        SeedMotes();
        Modulate = new Color(1f, 1f, 1f, 0f);
        Visible = true;
        SetProcess(true);
        _voice.Begin();
        UpdateHud();
        return _completion.Task;
    }

    public override void _Process(double delta)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        _time += delta;
        _phaseClock += delta;
        _providerReady = _providerReady || (_providerSettled?.Invoke() ?? false);

        switch (_phase)
        {
            case Phase.FadeIn:
                Modulate = new Color(1f, 1f, 1f, Mathf.Clamp((float)(_phaseClock / FadeInSeconds), 0f, 1f));
                if (_providerReady)
                {
                    Complete();
                    return;
                }

                if (_phaseClock >= FadeInSeconds)
                {
                    _phase = Phase.CountIn;
                    _phaseClock = 0;
                }

                break;

            case Phase.CountIn:
                StepCountIn();
                if (_providerReady)
                {
                    Complete();
                    return;
                }

                if (_phaseClock >= CountInSeconds())
                {
                    _phase = Phase.Play;
                    _phaseClock = 0;
                    _lastMetronomeBeat = -1;
                }

                break;

            case Phase.Play:
                StepPlay(delta);
                break;

            case Phase.Finale:
                Modulate = new Color(
                    1f,
                    1f,
                    1f,
                    Mathf.Clamp(1f - (float)((_phaseClock - 0.18f) / (FinaleSeconds - 0.18f)), 0f, 1f));
                if (_phaseClock >= FinaleSeconds)
                {
                    Complete();
                    return;
                }

                break;
        }

        StepFeedback((float)delta);
        _voice.SetDrone(Mathf.Clamp(_streak / 12f, 0f, 1f));
        UpdateHud();
        _ink.QueueRedraw();
        _glow.QueueRedraw();
        _lastTickUsec = Time.GetTicksUsec();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_phase is not (Phase.CountIn or Phase.Play)
            || @event is not InputEventMouseButton button
            || button.ButtonIndex != MouseButton.Left
            || !button.Pressed)
        {
            return;
        }

        for (var lane = 0; lane < 3; lane++)
        {
            if (StrikeRect((BoneSongLane)lane).HasPoint(button.Position))
            {
                Strike((BoneSongLane)lane);
                AcceptEvent();
                return;
            }
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_phase == Phase.Idle || @event is not InputEventKey key || !key.Pressed || key.Echo)
        {
            return;
        }

        if (key.Keycode == Key.Escape)
        {
            Skip();
            GetViewport().SetInputAsHandled();
            return;
        }

        // Keys mirror the marks left-to-right on screen: A = rim (lower left), S = center,
        // D = edge (right). Space doubles the big center strike.
        var lane = key.Keycode switch
        {
            Key.A or Key.Left => BoneSongLane.Bright,
            Key.S or Key.Space or Key.Down => BoneSongLane.Deep,
            Key.D or Key.Right => BoneSongLane.Heart,
            _ => (BoneSongLane?)null,
        };
        if (lane is not null && _phase is Phase.CountIn or Phase.Play)
        {
            Strike(lane.Value);
            GetViewport().SetInputAsHandled();
        }
    }

    private void Skip()
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        // A reflexive Escape should not throw away a strong groove: once the player is deep in
        // a good run, the first press only arms the skip and asks again.
        var grooveWorthGuarding = _phase == Phase.Play
            && _everPlayed
            && (_streak >= HotStreak || _requiredHits >= HotStreak);
        if (grooveWorthGuarding && _skipArm <= 0f)
        {
            _skipArm = 1.4f;
            _skip.Text = SkipArmedText;
            SetJudgement("THE SONG HOLDS YOU — AGAIN TO LET GO", BoneDim);
            return;
        }

        _skipped = true;
        Complete();
    }

    private void Complete()
    {
        var performance = _skipped
            ? CastPerformance.Neutral
            : BoneSongScoring.ToPerformance(CurrentMetrics());
        _phase = Phase.Idle;
        Visible = false;
        SetProcess(false);
        _voice.End();
        _providerSettled = null;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(performance);
    }

    private BoneSongMetrics CurrentMetrics() =>
        new(
            _everPlayed,
            _activeSeconds,
            _requiredResolved,
            _requiredHits,
            _timingQualityTotal,
            _boastsOffered,
            _boastsHit,
            _mistimedStrikes,
            _completedPhrases,
            _longestStreak);

    private void BeginPhrases()
    {
        _phraseIndex = 0;
        _phrase = BoneSongPattern.Create(_spellText, 0);
        _nextPhrase = BoneSongPattern.Create(_spellText, 1);
        _notes = _phrase.Notes.Select(note => new LiveNote { Note = note }).ToArray();
        _phaseClock = 0;
        _lastMetronomeBeat = -1;
    }

    private void AdvancePhrase()
    {
        // The phrase after next is decided now, so a streak-earned extra boast still enters with
        // a full visible approach instead of materializing on its X.
        _phraseIndex++;
        _phrase = _nextPhrase;
        _notes = _phrase.Notes.Select(note => new LiveNote { Note = note }).ToArray();
        _nextPhrase = BoneSongPattern.Create(
            _spellText,
            _phraseIndex + 1,
            _streak >= HotStreak ? 1 : 0);
        _phaseClock = 0;
        _lastMetronomeBeat = -1;
    }

    private double CountInSeconds() => _phrase.BeatSeconds * 2.0;

    private void StepCountIn()
    {
        var beat = Math.Min(1, (int)(_phaseClock / _phrase.BeatSeconds));
        if (beat > _lastCountBeat)
        {
            _lastCountBeat = beat;
            _voice.Strike(BoneSongVoice.Hit.Count);
            SpawnRing(DrumCenter(), 18f, DrumRadius() * 0.72f, 0.32f, HeartIvory, 2f);
        }
    }

    private void StepPlay(double delta)
    {
        if (_everPlayed)
        {
            _activeSeconds += delta;
        }

        ResolvePassedNotes();

        var metronomeBeat = (int)(_phaseClock / _phrase.BeatSeconds);
        if (metronomeBeat > _lastMetronomeBeat)
        {
            _lastMetronomeBeat = metronomeBeat;
            SpawnRing(
                DrumCenter(),
                DrumRadius() * 0.18f,
                DrumRadius() * 0.30f,
                0.20f,
                BoneDim,
                1.2f);
        }

        if (_phaseClock >= _phrase.DurationSeconds)
        {
            ResolvePassedNotes(force: true);
            _completedPhrases++;
            if (_providerReady)
            {
                EnterFinale();
                return;
            }

            AdvancePhrase();
            return;
        }

        if (_providerReady)
        {
            if (!_everPlayed)
            {
                Complete();
                return;
            }

            _graceClock += delta;
            if (_graceClock >= GraceSeconds)
            {
                EnterFinale();
            }
        }
    }

    private void ResolvePassedNotes(bool force = false)
    {
        for (var index = 0; index < _notes.Length; index++)
        {
            if (_notes[index].Resolved)
            {
                continue;
            }

            var noteTime = _notes[index].Note.Beat * _phrase.BeatSeconds;
            if (!force && _phaseClock <= noteTime + HitWindow)
            {
                continue;
            }

            var live = _notes[index];
            live.Resolved = true;
            live.Hit = false;
            _notes[index] = live;
            if (live.Note.Kind == BoneSongNoteKind.Required)
            {
                if (!_everPlayed)
                {
                    continue;
                }

                _requiredResolved++;
                _streak = 0;
                _laneError[(int)live.Note.Lane] = 1f;
                _voice.Strike(BoneSongVoice.Hit.Miss);
                SpawnMiss(live.Note.Lane);
            }
            else
            {
                if (_everPlayed)
                {
                    _boastsOffered++;
                }
            }
        }
    }

    private void Strike(BoneSongLane lane)
    {
        var strikeTime = StrikeChartTime();
        var window = HitWindow;
        var bestSameLane = -1;
        var bestSameError = double.MaxValue;
        var bestAny = -1;
        var bestAnyError = double.MaxValue;
        for (var index = 0; index < _notes.Length; index++)
        {
            if (_notes[index].Resolved)
            {
                continue;
            }

            var noteTime = _notes[index].Note.Beat * _phrase.BeatSeconds;
            var signed = strikeTime - noteTime;
            if (Math.Abs(signed) < Math.Abs(bestAnyError))
            {
                bestAnyError = signed;
                bestAny = index;
            }

            if (_notes[index].Note.Lane == lane && Math.Abs(signed) < Math.Abs(bestSameError))
            {
                bestSameError = signed;
                bestSameLane = index;
            }
        }

        if (bestSameLane >= 0 && Math.Abs(bestSameError) <= window)
        {
            _everPlayed = true;
            ResolveHit(bestSameLane, bestSameError);
            return;
        }

        if (bestAny >= 0 && Math.Abs(bestAnyError) <= window)
        {
            _everPlayed = true;
            if (Math.Abs(bestAnyError) <= WrongDrumConsumeSeconds)
            {
                ResolveWrongDrum(bestAny, lane);
            }
            else
            {
                GlanceWrongDrum(bestAny, lane);
            }

            return;
        }

        if (_phase == Phase.CountIn)
        {
            // Taps along with the count-in knocks are free practice, never punished; only a
            // press that clearly reaches for the first note is judged.
            return;
        }

        _everPlayed = true;
        _mistimedStrikes++;
        _streak = 0;
        _laneError[(int)lane] = 1f;
        _voice.Strike(BoneSongVoice.Hit.Miss);
        SetJudgement("EMPTY WATER", MissRed);
        SpawnMiss(lane);
    }

    private void ResolveHit(int index, double signedError)
    {
        var live = _notes[index];
        live.Resolved = true;
        live.Hit = true;
        _notes[index] = live;
        var quality = BoneSongScoring.TimingQuality(signedError);
        _laneFlash[(int)live.Note.Lane] = 1f;

        if (live.Note.Kind == BoneSongNoteKind.Boast)
        {
            _boastsOffered++;
            _boastsHit++;
            _voice.Strike(BoneSongVoice.Hit.Boast);
            SetJudgement("BOAST!", Gold);
            SpawnHit(live.Note.Lane, Gold, large: true);
            return;
        }

        _requiredResolved++;
        _requiredHits++;
        _timingQualityTotal += quality;
        _streak++;
        _longestStreak = Math.Max(_longestStreak, _streak);
        _voice.Strike(live.Note.Lane switch
        {
            BoneSongLane.Deep => BoneSongVoice.Hit.Deep,
            BoneSongLane.Heart => BoneSongVoice.Hit.Heart,
            _ => BoneSongVoice.Hit.Bright,
        });
        var (text, color) = quality switch
        {
            >= 0.99 => ("TRUE", WhiteHot),
            >= 0.64 => ("HELD", BrightJade),
            _ => ("CAUGHT", HeartIvory),
        };
        SetJudgement(text, color);

        // Direction turns every imperfect hit into calibration data: a player who is
        // consistently 100 ms early can finally see it.
        if (quality < 0.99 && Math.Abs(signedError) > 0.03)
        {
            _drift = signedError < 0 ? "‹ EARLY" : "LATE ›";
        }

        SpawnHit(live.Note.Lane, LaneColor(live.Note.Lane), large: quality >= 0.99);
    }

    private void ResolveWrongDrum(int index, BoneSongLane struckLane)
    {
        var live = _notes[index];
        live.Resolved = true;
        live.Hit = false;
        _notes[index] = live;
        if (live.Note.Kind == BoneSongNoteKind.Required)
        {
            _requiredResolved++;
        }
        else
        {
            _boastsOffered++;
            _mistimedStrikes++;
        }

        _streak = 0;
        _laneError[(int)struckLane] = 1f;
        _laneError[(int)live.Note.Lane] = 0.65f;
        _voice.Strike(BoneSongVoice.Hit.Miss);
        SetJudgement("WRONG DRUM", MissRed);
        SpawnMiss(struckLane);
    }

    /// <summary>
    /// A wrong-lane press outside the consume window: the press is penalized and named, but the
    /// neighboring note survives to be answered properly, avoiding a double punishment.
    /// </summary>
    private void GlanceWrongDrum(int index, BoneSongLane struckLane)
    {
        _mistimedStrikes++;
        _streak = 0;
        _laneError[(int)struckLane] = 1f;
        _laneError[(int)_notes[index].Note.Lane] =
            Mathf.Max(_laneError[(int)_notes[index].Note.Lane], 0.5f);
        _voice.Strike(BoneSongVoice.Hit.Miss);
        SetJudgement("WRONG DRUM", MissRed);
        SpawnMiss(struckLane);
    }

    private void EnterFinale()
    {
        if (!_everPlayed)
        {
            Complete();
            return;
        }

        for (var lane = 0; lane < 3; lane++)
        {
            SpawnHit((BoneSongLane)lane, lane == 0 ? Gold : LaneColor((BoneSongLane)lane), large: true);
        }

        // The whole hall answers at once: a white shockwave rolls off the drum, a gold one
        // follows, and every singer flares while the song fades.
        SpawnRing(DrumCenter(), DrumRadius() * 0.2f, DrumRadius() * 2.4f, 0.72f, WhiteHot, 5f);
        SpawnRing(DrumCenter(), DrumRadius() * 0.1f, DrumRadius() * 1.7f, 0.58f, Gold, 3f);
        _phase = Phase.Finale;
        _phaseClock = 0;
        _shake = 5f;
    }

    private void SetJudgement(string text, Color color)
    {
        _judgement = text;
        _drift = "";
        _judgementLife = 0.55f;
        _judgementPop = 1f;
        _judgementLabel.AddThemeColorOverride("font_color", color);
    }

    private void SpawnHit(BoneSongLane lane, Color color, bool large)
    {
        var center = StrikePoint(lane);
        SpawnRing(center, 10f, large ? 82f : 54f, large ? 0.48f : 0.34f, color, large ? 3f : 2f);
        var count = large ? 26 : 14;
        for (var index = 0; index < count; index++)
        {
            var direction = Vector2.FromAngle((float)(_vfxRng.NextDouble() * Math.Tau));
            SpawnSpark(
                center,
                direction * (45f + ((float)_vfxRng.NextDouble() * (large ? 150f : 90f))),
                0.28f + ((float)_vfxRng.NextDouble() * 0.44f),
                1.5f + ((float)_vfxRng.NextDouble() * 2f),
                color);
        }
    }

    private void SpawnMiss(BoneSongLane lane)
    {
        // A missed arrival is deliberately a dud: the note darkens on its X and the drum does
        // not answer with a shockwave. The contrast makes a successful impact feel physical.
        _laneError[(int)lane] = Mathf.Max(_laneError[(int)lane], 0.45f);
        _shake = Mathf.Max(_shake, 0.8f);
    }

    private void SpawnRing(Vector2 center, float start, float end, float life, Color color, float width) =>
        _rings.Add(new Ring
        {
            Center = center,
            StartRadius = start,
            EndRadius = end,
            Life = life,
            Color = color,
            Width = width,
        });

    private void SpawnSpark(Vector2 position, Vector2 velocity, float life, float size, Color color)
    {
        var spark = new Spark
        {
            Position = position,
            Velocity = velocity,
            Life = life,
            MaxLife = life,
            Size = size,
            Color = color,
        };
        if (_sparks.Count >= MaximumSparks)
        {
            _sparkCursor %= _sparks.Count;
            _sparks[_sparkCursor] = spark;
            _sparkCursor++;
            return;
        }

        _sparks.Add(spark);
    }

    private void SeedMotes()
    {
        _motes = new Mote[MoteCount];
        for (var index = 0; index < _motes.Length; index++)
        {
            _motes[index] = new Mote
            {
                Anchor = new Vector2((float)_vfxRng.NextDouble(), (float)_vfxRng.NextDouble()),
                Fall = 0.010f + ((float)_vfxRng.NextDouble() * 0.024f),
                Sway = 0.5f + ((float)_vfxRng.NextDouble() * 1.1f),
                Phase = (float)(_vfxRng.NextDouble() * Math.Tau),
                Size = 1.0f + ((float)_vfxRng.NextDouble() * 1.7f),
                Alpha = 0.04f + ((float)_vfxRng.NextDouble() * 0.09f),
            };
        }
    }

    private void StepFeedback(float delta)
    {
        _shake *= Mathf.Exp(-7f * delta);
        _shakeOffset = _shake <= 0.04f
            ? Vector2.Zero
            : new Vector2(NextSigned() * _shake, NextSigned() * _shake);
        _judgementLife = Mathf.Max(0f, _judgementLife - delta);
        _judgementPop *= Mathf.Exp(-9f * delta);
        for (var lane = 0; lane < 3; lane++)
        {
            _laneFlash[lane] = Mathf.Max(0f, _laneFlash[lane] - (delta * 3.8f));
            _laneError[lane] = Mathf.Max(0f, _laneError[lane] - (delta * 3.0f));
        }

        if (_skipArm > 0f)
        {
            _skipArm -= delta;
            if (_skipArm <= 0f)
            {
                _skip.Text = SkipIdleText;
            }
        }

        // Atmosphere ramps: aurora and hall embers breathe up with the streak, surge in the
        // finale, and sag when the groove breaks.
        var streakNorm = Mathf.Clamp(_streak / 12f, 0f, 1f);
        var auroraTarget = _phase == Phase.Finale ? 1f : 0.30f + (0.70f * streakNorm);
        _auroraRamp += (auroraTarget - _auroraRamp) * Mathf.Min(1f, delta * 2.2f);
        var fireTarget = _phase == Phase.Finale || _streak >= HotStreak ? 1f : 0f;
        _fireRamp += (fireTarget - _fireRamp) * Mathf.Min(1f, delta * 2.8f);
        var emberTarget = _phase == Phase.Finale || _streak >= 12 ? 1f : 0f;
        _emberRamp += (emberTarget - _emberRamp) * Mathf.Min(1f, delta * 2.8f);
        for (var index = 0; index < SingerCount; index++)
        {
            var lit = _phase == Phase.Finale || index < _streak;
            var rate = lit ? 3.4f : 1.6f;
            _singerGlow[index] += ((lit ? 1f : 0f) - _singerGlow[index]) * Mathf.Min(1f, delta * rate);
        }

        for (var index = 0; index < _motes.Length; index++)
        {
            var mote = _motes[index];
            mote.Anchor.Y += mote.Fall * delta;
            if (mote.Anchor.Y > 1.04f)
            {
                mote.Anchor.Y = -0.04f;
                mote.Anchor.X = (float)_vfxRng.NextDouble();
            }

            _motes[index] = mote;
        }

        for (var index = _sparks.Count - 1; index >= 0; index--)
        {
            var spark = _sparks[index];
            spark.Life -= delta;
            if (spark.Life <= 0)
            {
                _sparks[index] = _sparks[^1];
                _sparks.RemoveAt(_sparks.Count - 1);
                continue;
            }

            spark.Velocity *= Mathf.Exp(-2.2f * delta);
            spark.Velocity += new Vector2(0f, 28f * delta);
            spark.Position += spark.Velocity * delta;
            _sparks[index] = spark;
        }

        for (var index = _rings.Count - 1; index >= 0; index--)
        {
            var ring = _rings[index];
            ring.Age += delta;
            if (ring.Age >= ring.Life)
            {
                _rings.RemoveAt(index);
                continue;
            }

            _rings[index] = ring;
        }

        for (var index = 0; index < _notes.Length; index++)
        {
            if (_notes[index].Resolved)
            {
                var live = _notes[index];
                live.FeedbackAge += delta;
                _notes[index] = live;
            }
        }
    }

    private float NextSigned() => ((float)_vfxRng.NextDouble() * 2f) - 1f;

    // ---------------------------------------------------------------- drawing

    private void BuildLayers()
    {
        _ink = new DrawLayer { MouseFilter = MouseFilterEnum.Ignore };
        _ink.SetAnchorsPreset(LayoutPreset.FullRect);
        _ink.Painted += DrawInk;
        AddChild(_ink);

        _glow = new DrawLayer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Material = new CanvasItemMaterial { BlendMode = CanvasItemMaterial.BlendModeEnum.Add },
        };
        _glow.SetAnchorsPreset(LayoutPreset.FullRect);
        _glow.Painted += DrawGlow;
        AddChild(_glow);
    }

    private void DrawInk(DrawLayer layer)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        layer.DrawRect(new Rect2(Vector2.Zero, Size), new Color(Night, 0.95f));
        DrawAurora(layer);
        DrawSingers(layer);

        var center = DrumCenter() + _shakeOffset;
        var radius = DrumRadius();

        // A pool of hearth light gathers behind the drum and swells softly on each beat.
        var beatFraction = 1f - (float)((_phaseClock / _phrase.BeatSeconds) % 1.0);
        var pulse = 0.72f + (0.28f * beatFraction * beatFraction);
        layer.DrawCircle(center, radius * 2.0f, new Color(HeartIvory, 0.009f * pulse));
        layer.DrawCircle(center, radius * 1.6f, new Color(HeartIvory, 0.016f * pulse));
        layer.DrawCircle(center, radius * 1.25f, new Color(HeartIvory, 0.028f * pulse));
        layer.DrawCircle(center, radius * 0.9f, new Color(HeartIvory, 0.05f * pulse));

        // A single physical object owns the composition: aged bone head, growth rings, heavy
        // whalebone rim carved with notches, and a hunt gradually inked in by completed phrases.
        layer.DrawCircle(center, radius, new Color(0.10f, 0.095f, 0.078f, 0.97f));
        layer.DrawCircle(center, radius * 0.93f, new Color(0.055f, 0.060f, 0.058f, 0.72f));
        for (var ring = 1; ring <= 5; ring++)
        {
            layer.DrawArc(
                center + new Vector2(5f, 3f),
                radius * ring / 6f,
                0,
                Mathf.Tau,
                96,
                new Color(Bone, 0.025f + (ring * 0.006f)),
                1.1f,
                antialiased: true);
        }

        layer.DrawArc(center, radius, 0, Mathf.Tau, 160, new Color(Bone, 0.68f), 8f, antialiased: true);
        layer.DrawArc(center, radius - 13f, 0, Mathf.Tau, 160, new Color(Bone, 0.20f), 2f, antialiased: true);
        layer.DrawArc(center, radius + 13f, 0, Mathf.Tau, 160, new Color(Bone, 0.16f), 2f, antialiased: true);
        for (var notch = 0; notch < 28; notch++)
        {
            var angle = (Mathf.Tau * notch / 28f) + 0.06f;
            var direction = Vector2.FromAngle(angle);
            layer.DrawLine(
                center + (direction * (radius - 5f)),
                center + (direction * (radius + 7f)),
                new Color(Bone, 0.15f),
                1.7f,
                antialiased: true);
        }

        DrawScrimshaw(layer, center, radius);
        DrawStrikeMarks(layer);
    }

    private void DrawAurora(DrawLayer layer)
    {
        if (_auroraRamp <= 0.02f)
        {
            return;
        }

        // Three slow ribbons over the hall; amplitude and light follow the streak ramp.
        var t = (float)_time;
        for (var band = 0; band < 3; band++)
        {
            const int Count = 40;
            var baseY = Size.Y * (0.08f + (band * 0.065f));
            var speed = 0.35f + (band * 0.17f);
            var amp = 15f + (band * 9f);
            var points = new Vector2[Count];
            for (var index = 0; index < Count; index++)
            {
                var x = Size.X * index / (Count - 1);
                points[index] = new Vector2(
                    x,
                    baseY
                        + (Mathf.Sin((x * 0.0045f) + (t * speed) + (band * 2.1f)) * amp)
                        + (Mathf.Sin((x * 0.0017f) - (t * 0.53f) + band) * amp * 1.6f * _auroraRamp));
            }

            var color = band switch
            {
                0 => BrightJade,
                1 => DeepBlue,
                _ => SeaInk,
            };
            layer.DrawPolyline(
                points,
                new Color(color, 0.024f + (0.045f * _auroraRamp)),
                30f - (band * 6f),
                antialiased: true);
            layer.DrawPolyline(
                points,
                new Color(color, 0.045f + (0.065f * _auroraRamp)),
                6f,
                antialiased: true);
        }
    }

    private void DrawSingers(DrawLayer layer)
    {
        // Nine silhouettes ring the upper hall. Every streak note lights another singer's ember;
        // a broken groove lets them fade back into the dark. At a full hall they burn gold.
        var center = DrumCenter();
        var radius = DrumRadius();
        for (var index = 0; index < SingerCount; index++)
        {
            var angle = Mathf.Pi * (1.06f + (0.88f * index / (SingerCount - 1)));
            var at = center
                + new Vector2(Mathf.Cos(angle) * radius * 1.55f, Mathf.Sin(angle) * radius * 1.22f)
                + new Vector2(0f, Mathf.Sin(((float)_time * 1.6f) + (index * 1.3f)) * 2.2f);
            var glow = _singerGlow[index];
            if (glow > 0.02f)
            {
                var flicker = 0.8f + (0.2f * Mathf.Sin(((float)_time * 7f) + (index * 2.7f)));
                var ember = HeartIvory.Lerp(Gold, _emberRamp);
                layer.DrawCircle(at + new Vector2(0f, 6f), 26f, new Color(ember, 0.05f * glow * flicker));
                layer.DrawCircle(at + new Vector2(0f, 6f), 15f, new Color(ember, 0.09f * glow * flicker));
            }

            layer.DrawCircle(at + new Vector2(0f, 15f), 11f, Silhouette);
            layer.DrawCircle(at + new Vector2(0f, 11f), 9f, Silhouette);
            layer.DrawCircle(at, 5.6f, Silhouette);
            if (glow > 0.02f)
            {
                layer.DrawArc(
                    at,
                    6.4f,
                    Mathf.Pi * 1.08f,
                    Mathf.Pi * 1.92f,
                    10,
                    new Color(HeartIvory, 0.35f * glow),
                    1.4f,
                    antialiased: true);
            }
        }
    }

    private void DrawGlow(DrawLayer layer)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        var center = DrumCenter() + _shakeOffset;
        var radius = DrumRadius();

        // A slow shimmer sweeps the whalebone rim; a hot streak wreaths it in orbiting fire.
        var streakNorm = Mathf.Clamp(_streak / 12f, 0f, 1f);
        var shimmer = (float)(_time * 0.8 % Math.Tau);
        layer.DrawArc(
            center,
            radius + 2f,
            shimmer,
            shimmer + 0.85f,
            26,
            new Color(Gold, 0.04f + (0.08f * streakNorm)),
            6f,
            antialiased: true);
        if (_fireRamp > 0.02f)
        {
            for (var arm = 0; arm < 3; arm++)
            {
                var angle = ((float)_time * 1.35f) + (arm * Mathf.Tau / 3f);
                layer.DrawArc(
                    center,
                    radius + 17f,
                    angle,
                    angle + 0.55f,
                    20,
                    new Color(Gold, 0.30f * _fireRamp),
                    3.2f,
                    antialiased: true);
            }
        }

        if (_emberRamp > 0.02f)
        {
            for (var arm = 0; arm < 4; arm++)
            {
                var angle = (-(float)_time * 1.05f) + (arm * Mathf.Tau / 4f);
                layer.DrawArc(
                    center,
                    radius + 28f,
                    angle,
                    angle + 0.42f,
                    18,
                    new Color(WhiteHot, 0.22f * _emberRamp),
                    2.4f,
                    antialiased: true);
            }
        }

        DrawNotes(layer);
        foreach (var ring in _rings)
        {
            var progress = ring.Age / ring.Life;
            layer.DrawArc(
                ring.Center + _shakeOffset,
                Mathf.Lerp(ring.StartRadius, ring.EndRadius, progress),
                0,
                Mathf.Tau,
                64,
                new Color(ring.Color, 0.55f * (1f - progress)),
                ring.Width,
                antialiased: true);
        }

        // Sparks are drawn as embers with short comet tails along their velocity.
        foreach (var spark in _sparks)
        {
            var life = spark.Life / spark.MaxLife;
            var head = spark.Position + _shakeOffset;
            layer.DrawLine(
                head - (spark.Velocity * 0.035f),
                head,
                new Color(spark.Color, 0.55f * life),
                Mathf.Max(1f, spark.Size * life),
                antialiased: true);
            layer.DrawCircle(head, spark.Size * life * 0.9f, new Color(spark.Color, 0.8f * life));
        }

        foreach (var mote in _motes)
        {
            var at = new Vector2(
                (mote.Anchor.X + (Mathf.Sin(((float)_time * mote.Sway) + mote.Phase) * 0.012f)) * Size.X,
                mote.Anchor.Y * Size.Y);
            layer.DrawCircle(at, mote.Size, new Color(Bone, mote.Alpha));
        }
    }

    private void DrawNotes(DrawLayer layer)
    {
        if (_phase is not (Phase.CountIn or Phase.Play))
        {
            return;
        }

        for (var index = 0; index < _notes.Length; index++)
        {
            DrawNote(
                layer,
                _notes[index],
                (_notes[index].Note.Beat * _phrase.BeatSeconds) - ChartTime(),
                _phraseIndex,
                index);
        }

        // The next phrase begins entering before the current one ends. Nothing materializes on an
        // X without the player first seeing its lane-specific approach.
        if (_phase == Phase.Play && !_providerReady)
        {
            for (var index = 0; index < _nextPhrase.Notes.Count; index++)
            {
                var note = _nextPhrase.Notes[index];
                var secondsAway = (_phrase.DurationSeconds - _phaseClock) + (note.Beat * _nextPhrase.BeatSeconds);
                DrawNote(
                    layer,
                    new LiveNote { Note = note },
                    secondsAway,
                    _phraseIndex + 1,
                    index,
                    preview: true);
            }
        }
    }

    private void DrawNote(
        DrawLayer layer,
        LiveNote live,
        double secondsAway,
        int phraseIndex,
        int noteIndex,
        bool preview = false)
    {
        if (secondsAway > ApproachSeconds || secondsAway < -0.65)
        {
            return;
        }

        var baseColor = live.Note.Kind == BoneSongNoteKind.Boast ? Gold : Bone;
        var trailColor = live.Note.Kind == BoneSongNoteKind.Boast ? Gold : LaneColor(live.Note.Lane);
        var alpha = preview ? 0.48f : live.Resolved ? Mathf.Max(0.05f, 0.55f - live.FeedbackAge) : 0.92f;
        var color = !live.Resolved || live.Hit ? baseColor : BoneDim;
        var progress = Mathf.Clamp(1f - ((float)Math.Max(0, secondsAway) / ApproachSeconds), 0f, 1f);
        var at = ApproachPoint(live.Note.Lane, progress, phraseIndex, noteIndex) + _shakeOffset;

        // A successful arrival rebounds out of the skin after making its shockwave. An unanswered
        // arrival simply loses its light on the X: impact without response, a visual dud.
        if (live.Resolved)
        {
            at = StrikePoint(live.Note.Lane) + _shakeOffset;
            if (live.Hit)
            {
                at += BounceDirection(live.Note.Lane) * (live.FeedbackAge * 150f);
                alpha *= Mathf.Clamp(1f - (live.FeedbackAge * 1.8f), 0f, 1f);
            }
            else
            {
                alpha *= Mathf.Clamp(1f - (live.FeedbackAge * 2.1f), 0f, 1f);
            }
        }

        if (!live.Resolved && progress > 0.04f)
        {
            // Comet trail: one wide soft pass under a thin bright core.
            var trail = new Vector2[7];
            for (var index = 0; index < trail.Length; index++)
            {
                var trailProgress = Mathf.Max(0f, progress - ((trail.Length - 1 - index) * 0.025f));
                trail[index] = ApproachPoint(live.Note.Lane, trailProgress, phraseIndex, noteIndex) + _shakeOffset;
            }

            layer.DrawPolyline(trail, new Color(trailColor, 0.04f + (progress * 0.08f)), 7f, antialiased: true);
            layer.DrawPolyline(trail, new Color(trailColor, 0.08f + (progress * 0.16f)), 2f, antialiased: true);
        }

        var near = Mathf.Clamp(1f - ((float)Math.Abs(secondsAway) / 0.42f), 0f, 1f);

        if (!live.Resolved && !preview)
        {
            layer.DrawCircle(at, 15f + (near * 6f), new Color(trailColor, alpha * (0.06f + (0.08f * near))));
        }

        if (live.Note.Kind == BoneSongNoteKind.Boast)
        {
            var radius = 9f + (near * 3f);
            var spin = preview || live.Resolved ? 0f : (float)(_time * 2.4 % Math.Tau);
            var diamond = new Vector2[5];
            for (var corner = 0; corner < 4; corner++)
            {
                diamond[corner] = at + (Vector2.FromAngle(spin + (corner * Mathf.Tau / 4f)) * radius);
            }

            diamond[4] = diamond[0];
            layer.DrawPolyline(diamond, new Color(color, alpha), 3f, antialiased: true);
            layer.DrawCircle(at, 3f, new Color(WhiteHot, alpha));
        }
        else
        {
            layer.DrawCircle(at, 8f + (near * 3f), new Color(color, alpha * 0.22f));
            layer.DrawArc(at, 7f + (near * 2f), 0, Mathf.Tau, 28, new Color(color, alpha), 3f, antialiased: true);
        }

        if (!live.Resolved && near > 0)
        {
            layer.DrawArc(at, 16f + (near * 7f), 0, Mathf.Tau, 32, new Color(color, 0.28f * near), 2f);
        }
    }

    private double ChartTime() =>
        _phase == Phase.CountIn ? _phaseClock - CountInSeconds() : _phaseClock;

    private void DrawScrimshaw(DrawLayer layer, Vector2 center, float radius)
    {
        var whale = new[]
        {
            new Vector2(-0.72f, 0.10f), new Vector2(-0.56f, -0.04f), new Vector2(-0.25f, -0.15f),
            new Vector2(0.12f, -0.12f), new Vector2(0.47f, 0.00f), new Vector2(0.67f, 0.15f),
            new Vector2(0.45f, 0.22f), new Vector2(0.08f, 0.25f), new Vector2(-0.30f, 0.22f),
            new Vector2(-0.58f, 0.16f), new Vector2(-0.72f, 0.10f),
        };
        var points = whale.Select(point => center + (point * radius * 0.72f)).ToArray();
        layer.DrawPolyline(points, new Color(SeaInk, 0.075f), 1.2f, antialiased: true);
        var revealed = Mathf.Clamp((int)Math.Ceiling(points.Length * Mathf.Clamp(_completedPhrases / 8f, 0f, 1f)), 0, points.Length);
        if (revealed >= 2)
        {
            var inked = points.Take(revealed).ToArray();
            layer.DrawPolyline(inked, new Color(SeaInk, 0.16f), 5f, antialiased: true);
            layer.DrawPolyline(inked, new Color(SeaInk, 0.62f), 1.6f, antialiased: true);
        }
    }

    private void DrawStrikeMarks(DrawLayer layer)
    {
        for (var lane = 0; lane < 3; lane++)
        {
            var songLane = (BoneSongLane)lane;
            var at = StrikePoint(songLane) + _shakeOffset;
            var size = songLane == BoneSongLane.Deep ? 13f : songLane == BoneSongLane.Heart ? 12f : 11f;
            var color = _laneError[lane] > 0.01f
                ? BoneDim
                : LaneColor(songLane).Lerp(WhiteHot, _laneFlash[lane] * 0.55f);
            layer.DrawLine(at + new Vector2(-size, -size), at + new Vector2(size, size), new Color(color, 0.86f), 3.2f, antialiased: true);
            layer.DrawLine(at + new Vector2(size, -size), at + new Vector2(-size, size), new Color(color, 0.86f), 3.2f, antialiased: true);
            layer.DrawCircle(at, 3f, new Color(color, 0.42f));
        }
    }

    private Vector2 DrumCenter() => new(Size.X * 0.52f, (Size.Y * 0.49f) - 8f);

    private float DrumRadius() => Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * 0.285f, 175f, 285f);

    private Vector2 StrikePoint(BoneSongLane lane)
    {
        var center = DrumCenter();
        var radius = DrumRadius();
        return lane switch
        {
            BoneSongLane.Deep => center,
            BoneSongLane.Heart => center + new Vector2(radius * 0.52f, -radius * 0.18f),
            _ => center + new Vector2(-radius * 0.58f, radius * 0.78f),
        };
    }

    private Rect2 StrikeRect(BoneSongLane lane)
    {
        var size = Mathf.Clamp(DrumRadius() * 0.34f, 70f, 104f);
        return new Rect2(StrikePoint(lane) - new Vector2(size / 2f, size / 2f), new Vector2(size, size));
    }

    private Vector2 ApproachPoint(BoneSongLane lane, float progress, int phraseIndex, int noteIndex)
    {
        var center = DrumCenter();
        var radius = DrumRadius();
        var target = StrikePoint(lane);
        var variation = Mathf.Sin(((phraseIndex + 1) * 7.13f) + ((noteIndex + 1) * 3.71f));
        Vector2 start;
        Vector2 controlA;
        Vector2 controlB;
        switch (lane)
        {
            case BoneSongLane.Deep:
                // Horizontal left-to-right travel, then a low hook into the central X.
                start = center + new Vector2(-radius * 1.72f, radius * (-0.52f + (variation * 0.10f)));
                controlA = center + new Vector2(-radius * 1.05f, radius * (-0.52f + (variation * 0.10f)));
                controlB = target + new Vector2(-radius * 0.52f, radius * (0.15f + (variation * 0.06f)));
                break;
            case BoneSongLane.Heart:
                // A falling diagonal crosses the room before curling back to the edge X.
                start = center + new Vector2(radius * 1.50f, radius * (-1.18f + (variation * 0.08f)));
                controlA = center + new Vector2(radius * 1.08f, radius * -0.82f);
                controlB = target + new Vector2(radius * 0.38f, radius * (-0.44f + (variation * 0.07f)));
                break;
            default:
                // The rim note descends almost vertically before its final inward swoop.
                start = center + new Vector2(radius * (-0.64f + (variation * 0.08f)), radius * -1.45f);
                controlA = center + new Vector2(radius * (-0.64f + (variation * 0.08f)), radius * -0.62f);
                controlB = target + new Vector2(radius * -0.24f, radius * -0.42f);
                break;
        }

        return CubicBezier(start, controlA, controlB, target, EaseSwoop(progress));
    }

    private static float EaseSwoop(float progress) =>
        progress < 0.62f
            ? progress * 0.82f
            : 0.5084f + (0.4916f * (1f - Mathf.Pow(1f - ((progress - 0.62f) / 0.38f), 2.4f)));

    private static Vector2 CubicBezier(Vector2 a, Vector2 b, Vector2 c, Vector2 d, float amount)
    {
        var inverse = 1f - amount;
        return (a * inverse * inverse * inverse)
            + (b * 3f * inverse * inverse * amount)
            + (c * 3f * inverse * amount * amount)
            + (d * amount * amount * amount);
    }

    private Vector2 BounceDirection(BoneSongLane lane)
    {
        var outward = StrikePoint(lane) - DrumCenter();
        return outward.LengthSquared() < 0.01f ? Vector2.Up : outward.Normalized();
    }

    private static Color LaneColor(BoneSongLane lane) => lane switch
    {
        BoneSongLane.Deep => DeepBlue,
        BoneSongLane.Heart => HeartIvory,
        _ => BrightJade,
    };

    // ---------------------------------------------------------------- hud

    private void BuildHud()
    {
        var top = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        top.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        top.OffsetTop = 28;
        top.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        AddChild(top);

        _title = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", Gold);
        top.AddChild(_title);

        _subtitle = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 13);
        _subtitle.AddThemeColorOverride("font_color", UiTheme.Muted);
        top.AddChild(_subtitle);

        _judgementLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _judgementLabel.AddThemeFontSizeOverride("font_size", 16);
        top.AddChild(_judgementLabel);

        _driftLabel = new Label
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _driftLabel.AddThemeFontSizeOverride("font_size", 11);
        _driftLabel.AddThemeColorOverride("font_color", UiTheme.Muted);
        top.AddChild(_driftLabel);

        var bottom = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        bottom.OffsetTop = -142;
        bottom.OffsetBottom = -22;
        bottom.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        AddChild(bottom);

        _hint = new Label
        {
            Text = "A rim · S center · D edge (Space also strikes center), or click an X. Strike as the note swoops onto its mark; gold flourishes are optional power.",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _hint.AddThemeFontSizeOverride("font_size", 12);
        _hint.AddThemeColorOverride("font_color", UiTheme.Muted);
        bottom.AddChild(_hint);

        var row = new HBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        row.AddThemeConstantOverride("separation", UiTheme.SpaceLg);
        bottom.AddChild(row);
        row.AddChild(Meter("POWER", UiTheme.Warning, out _powerBar));
        row.AddChild(Meter("CONTROL", UiTheme.Wild, out _controlBar));

        _tally = new Label
        {
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _tally.AddThemeFontSizeOverride("font_size", 12);
        _tally.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(_tally);

        _skip = new Button
        {
            Text = SkipIdleText,
            CustomMinimumSize = new Vector2(150, 34),
        };
        _skip.Pressed += Skip;
        row.AddChild(_skip);

        for (var lane = 0; lane < _laneLabels.Length; lane++)
        {
            var songLane = (BoneSongLane)lane;
            _laneLabels[lane] = new Label
            {
                Text = LaneKeyLabel(songLane),
                MouseFilter = MouseFilterEnum.Ignore,
                HorizontalAlignment = HorizontalAlignment.Right,
                Size = new Vector2(150, 24),
            };
            _laneLabels[lane].AddThemeFontSizeOverride("font_size", 11);
            _laneLabels[lane].AddThemeColorOverride("font_color", LaneColor(songLane));
            AddChild(_laneLabels[lane]);
        }
    }

    private static VBoxContainer Meter(string caption, Color color, out ProgressBar bar)
    {
        var box = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        box.AddThemeConstantOverride("separation", 2);
        var label = new Label
        {
            Text = caption,
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        label.AddThemeFontSizeOverride("font_size", 10);
        label.AddThemeColorOverride("font_color", color);
        box.AddChild(label);

        bar = new ProgressBar
        {
            MinValue = 0,
            MaxValue = 1,
            Step = 0.001,
            Value = 0.5,
            ShowPercentage = false,
            CustomMinimumSize = new Vector2(170, 10),
            MouseFilter = MouseFilterEnum.Ignore,
        };
        bar.AddThemeStyleboxOverride(
            "fill",
            UiTheme.Box(color, color, borderWidth: 0, radius: 4, shadow: false, marginX: 0, marginY: 0));
        box.AddChild(bar);
        return box;
    }

    private void UpdateHud()
    {
        for (var lane = 0; lane < _laneLabels.Length; lane++)
        {
            var songLane = (BoneSongLane)lane;
            var offset = songLane switch
            {
                BoneSongLane.Deep => new Vector2(-75f, 24f),
                BoneSongLane.Heart => new Vector2(20f, -34f),
                _ => new Vector2(-172f, 22f),
            };
            _laneLabels[lane].Position = StrikePoint(songLane) + offset;
        }

        _title.Text = _phase switch
        {
            Phase.CountIn => $"THE HALL CALLS — {Math.Max(1, 2 - _lastCountBeat)}",
            Phase.Play when _streak >= 12 => "THE WHOLE HALL ANSWERS",
            Phase.Play when _streak >= HotStreak => "THE BONE-SINGERS ANSWER",
            Phase.Play => "ANSWER THE BONE-SONG",
            Phase.Finale => "THE ANSWER FEEDS THE SPELL",
            _ => "BONE-SONG",
        };
        _subtitle.Text = $"“{_spellText}” · phrase {_phraseIndex + 1} · {_phrase.BeatsPerMinute:0} bpm";
        _judgementLabel.Text = _judgementLife > 0 ? _judgement : "";
        _judgementLabel.PivotOffset = _judgementLabel.Size / 2f;
        _judgementLabel.Scale = Vector2.One * (1f + (0.38f * _judgementPop));
        _driftLabel.Text = _judgementLife > 0 ? _drift : "";
        _tally.Text = $"{_requiredHits}/{_requiredResolved} held · {_boastsHit} gold · streak {_streak}";

        if (!_everPlayed || _requiredResolved <= 0)
        {
            _powerBar.Value = 0.5;
            _controlBar.Value = 0.5;
            return;
        }

        var performance = BoneSongScoring.ToPerformance(CurrentMetrics() with
        {
            ActiveSeconds = Math.Max(_activeSeconds, BoneSongScoring.MinimumScoringWindowSeconds),
        });
        _powerBar.Value = Mathf.Clamp(
            (performance.PowerModifier - (1f - (float)BoneSongScoring.PowerSwing))
                / (2f * (float)BoneSongScoring.PowerSwing),
            0f,
            1f);
        _controlBar.Value = Mathf.Clamp(
            (performance.ControlModifier - (1f - (float)BoneSongScoring.ControlSwing))
                / (2f * (float)BoneSongScoring.ControlSwing),
            0f,
            1f);
    }

    private static string LaneKeyLabel(BoneSongLane lane) => lane switch
    {
        BoneSongLane.Deep => "S — CENTER",
        BoneSongLane.Heart => "D — EDGE",
        _ => "A — RIM",
    };
}
