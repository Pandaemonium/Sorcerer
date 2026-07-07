using Godot;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// The Thread &amp; Knot casting minigame: while the provider resolves a wild spell, the spell
/// spools out of the caster as a living thread of light. Hold to draw it - the longer the
/// pull, the faster power banks and the faster the thread frays. Release to whip it into a
/// knot and bank the draw; pull past the fray and it snaps, scattering the unbanked power as
/// sparks. Threads chain indefinitely until the provider returns, so unknown latency is the
/// game's natural clock. Push-your-luck judgment, no aiming: skipping, or never pulling, is
/// a neutral cast, never a punishment.
///
/// The overlay owns presentation and gesture reduction only; it reduces the session to
/// <see cref="ThreadKnotMetrics"/> and lets <see cref="ThreadKnotScoring"/> (core) decide the
/// <see cref="CastPerformance"/>, so calibration stays engine-owned and testable.
///
/// The thread itself is a small verlet rope: pinned at the spool, pinned at the draw head,
/// sagging in a catenary when slack and humming with a standing wave as strain rises. Gust
/// schedules are seeded from the spell text (like rune shapes), so recasting the same phrase
/// meets a thread with the same temperament.
/// </summary>
public partial class ThreadKnotMinigame : Control
{
    private enum Phase
    {
        Idle,
        FadeIn,
        Spool,
        Draw,
        Tie,
        Snap,
        Finale,
    }

    private const float FadeInSeconds = 0.18f;
    private const float SpoolSeconds = 0.38f;
    private const float TieSeconds = 0.5f;
    private const float SnapSeconds = 0.72f;
    private const float FinaleSeconds = 0.5f;
    private const float GraceSeconds = 2.0f;
    private const int MaxEmbers = 720;

    // Pull economy (presentation-side calibration; the core scoring sees only ratios).
    // A par player holds ~1.6s per pull and ties about 7 of every 10 threads.
    private const float BasePullUnitsPerSecond = 1.0f;
    private const float PullAcceleration = 0.55f;      // pull rate grows with hold time
    private const float ParBankUnitsPerSecond = 0.75f; // denominator for BankRateRatio

    // Fray. strain(t) = escalation * gust * (StrainBase*t + StrainRamp*t^2/2); snap at 1.
    private const float StrainBasePerSecond = 0.22f;
    private const float StrainRampPerSecondSquared = 0.16f;
    private const float EscalationPerKnot = 0.10f;
    private const int EscalationKnotCap = 8;
    private const float GustStrainFactor = 2.5f;

    // Verlet thread.
    private const int ThreadNodes = 30;
    private const int ConstraintPasses = 5;
    private const float SlackSag = 1.38f;   // rest-length multiplier when idle
    private const float TautSag = 1.012f;   // rest-length multiplier at full strain

    private static readonly Color DeepEmber = new(0.55f, 0.16f, 0.09f);
    private static readonly Color EmberOrange = new(1.0f, 0.47f, 0.16f);
    private static readonly Color EmberGold = new(1.0f, 0.78f, 0.40f);
    private static readonly Color WhiteHot = new(1.0f, 0.96f, 0.86f);
    private static readonly Color Jade = UiTheme.Wild;
    private static readonly Color WildViolet = new(0.69f, 0.48f, 1.0f);
    private static readonly Color SnapRed = new(1.0f, 0.32f, 0.28f);

    private struct Ember
    {
        public Vector2 Pos;
        public Vector2 Vel;
        public float Life;
        public float MaxLife;
        public float Size;
        public Color Color;
        public float Drag;
        public float Rise;
        public bool PullToCenter;
    }

    private struct Ring
    {
        public float Radius;
        public float Speed;
        public float Age;
        public float Life;
        public float Alpha;
        public float Width;
        public Color Color;
        public Vector2 Center;
    }

    /// <summary>A fray fiber: a small curling wisp sticking off the thread as a snap warning.</summary>
    private struct Fiber
    {
        public float Along01;
        public float Angle;
        public float Length;
        public float Age;
        public float Life;
        public float Curl;
    }

    /// <summary>A banked knot flying to, then pulsing on, the necklace chain.</summary>
    private struct Knot
    {
        public Vector2 Pos;
        public Vector2 From;
        public Vector2 Target;
        public float Size;
        public float Flight;      // 0..1 travel progress
        public float PulsePhase;
        public int Seed;
    }

    // Session state.
    private TaskCompletionSource<CastPerformance>? _completion;
    private Func<bool>? _providerSettled;
    private string _spellText = "";
    private Phase _phase = Phase.Idle;
    private double _phaseClock;
    private double _graceClock;
    private double _time;
    private bool _providerReady;
    private bool _everPulled;
    private bool _skipped;

    // Scoring totals (rate-based; see ThreadKnotScoring).
    private double _activeSeconds;
    private double _bankedUnits;
    private int _knotCount;
    private int _snapCount;

    // Current thread.
    private int _threadIndex;
    private bool _holding;
    private float _holdSeconds;
    private float _drawnUnits;
    private float _strain;
    private float _strainShown;    // smoothed for color/vibration
    private int _fiberThreshold;   // how many warning tiers have popped
    private Random _threadRng = new();
    private double _nextGustAt;
    private float _gustRemaining;
    private float _gustStrength;

    // Verlet rope state, in canvas-relative pixels.
    private readonly Vector2[] _nodes = new Vector2[ThreadNodes];
    private readonly Vector2[] _nodesPrev = new Vector2[ThreadNodes];
    private Vector2[] _snapAfterimage = Array.Empty<Vector2>();
    private float _snapFade;
    private Vector2 _snapPoint;

    // Collections.
    private readonly List<Ember> _embers = new(MaxEmbers);
    private readonly List<Ring> _rings = new();
    private readonly List<Fiber> _fibers = new();
    private readonly List<Knot> _knots = new();
    private readonly List<Vector2> _stains = new();
    private readonly Random _vfxRng = new();
    private float _shake;
    private Vector2 _shakeOffset;
    private float _vignettePulse;
    private double _beaconClock;

    // Layers and HUD.
    private DrawLayer _ink = null!;
    private DrawLayer _glow = null!;
    private Label _title = null!;
    private Label _subtitle = null!;
    private Label _hint = null!;
    private Label _tally = null!;
    private ProgressBar _powerBar = null!;
    private ProgressBar _controlBar = null!;
    private Button _skip = null!;

    public bool Active => _completion is not null;

    public override void _Ready()
    {
        Visible = false;
        MouseFilter = MouseFilterEnum.Stop;
        ZIndex = 60;
        BuildLayers();
        BuildHud();
        SetProcess(false);
    }

    /// <summary>
    /// Runs the minigame until the provider settles (plus a short grace to finish the pull in
    /// hand) or the player skips. Returns the engine-facing performance for await_cast.
    /// </summary>
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
        _threadIndex = 0;
        _providerReady = false;
        _everPulled = false;
        _skipped = false;
        _holding = false;
        _activeSeconds = 0;
        _bankedUnits = 0;
        _knotCount = 0;
        _snapCount = 0;
        _graceClock = 0;
        _embers.Clear();
        _rings.Clear();
        _fibers.Clear();
        _knots.Clear();
        _stains.Clear();
        _snapAfterimage = Array.Empty<Vector2>();
        _snapFade = 0f;
        _shake = 0f;
        _vignettePulse = 0f;
        _beaconClock = 0;
        Modulate = new Color(1f, 1f, 1f, 0f);
        _phase = Phase.FadeIn;
        _phaseClock = 0;
        Visible = true;
        SetProcess(true);
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

        // Defensive release: if the press ended outside our input stream, notice it here.
        if (_holding
            && !Input.IsMouseButtonPressed(MouseButton.Left)
            && !Input.IsKeyPressed(Key.Space))
        {
            ReleasePull();
        }

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
                    StartThread();
                }

                break;

            case Phase.Spool:
                if (_phaseClock >= SpoolSeconds)
                {
                    _phase = Phase.Draw;
                    _phaseClock = 0;
                }

                break;

            case Phase.Draw:
                StepDraw(delta);
                break;

            case Phase.Tie:
                if (_phaseClock >= TieSeconds)
                {
                    AfterThreadResolved();
                }

                break;

            case Phase.Snap:
                _snapFade = Mathf.Max(0f, _snapFade - ((float)delta / (SnapSeconds * 0.55f)));
                if (_phaseClock >= SnapSeconds)
                {
                    AfterThreadResolved();
                }

                break;

            case Phase.Finale:
                Modulate = new Color(
                    1f,
                    1f,
                    1f,
                    Mathf.Clamp(1f - (float)((_phaseClock - 0.15f) / (FinaleSeconds - 0.15f)), 0f, 1f));
                if (_phaseClock >= FinaleSeconds)
                {
                    Complete();
                    return;
                }

                break;
        }

        StepThread(delta);
        StepVfx(delta);
        UpdateHud();
        _ink.QueueRedraw();
        _glow.QueueRedraw();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        if (@event is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            if (button.Pressed)
            {
                BeginPull();
            }
            else
            {
                ReleasePull();
            }

            AcceptEvent();
        }
    }

    public override void _UnhandledKeyInput(InputEvent @event)
    {
        if (_phase == Phase.Idle || @event is not InputEventKey key || key.Echo)
        {
            return;
        }

        if (key.Keycode == Key.Escape && key.Pressed)
        {
            Skip();
            GetViewport().SetInputAsHandled();
        }
        else if (key.Keycode == Key.Space)
        {
            if (key.Pressed)
            {
                BeginPull();
            }
            else
            {
                ReleasePull();
            }

            GetViewport().SetInputAsHandled();
        }
    }

    private void Skip()
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        _skipped = true;
        Complete();
    }

    private void Complete()
    {
        var performance = _skipped
            ? CastPerformance.Neutral
            : ThreadKnotScoring.ToPerformance(new ThreadKnotMetrics(
                Pulled: _everPulled,
                ActiveSeconds: _activeSeconds,
                BankRateRatio: _activeSeconds > 0.001
                    ? (_bankedUnits / _activeSeconds) / ParBankUnitsPerSecond
                    : 0,
                Knots: _knotCount,
                Snaps: _snapCount));

        _phase = Phase.Idle;
        Visible = false;
        SetProcess(false);
        _holding = false;
        _providerSettled = null;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(performance);
    }

    // ---------------------------------------------------------------- thread lifecycle

    private void StartThread()
    {
        // Seeded like rune shapes: the same phrase always spools a thread with the same
        // temperament of gusts, so a recast spell feels familiar rather than arbitrary.
        _threadRng = new Random(RuneShape.SeedFor(_spellText, _threadIndex));
        _threadIndex++;
        _holding = false;
        _holdSeconds = 0f;
        _drawnUnits = 0f;
        _strain = 0f;
        _strainShown = 0f;
        _fiberThreshold = 0;
        _fibers.Clear();
        _gustRemaining = 0f;
        _gustStrength = 0f;
        _nextGustAt = 1.1 + (_threadRng.NextDouble() * 1.6);
        _beaconClock = 0;

        var spool = SpoolPoint();
        var head = HeadPoint();
        for (var i = 0; i < ThreadNodes; i++)
        {
            var t = (float)i / (ThreadNodes - 1);
            var slack = spool.Lerp(head, t);
            slack.Y += Mathf.Sin(t * Mathf.Pi) * 24f;
            _nodes[i] = slack;
            _nodesPrev[i] = slack;
        }

        SpawnRing(spool, 6f, CanvasRadius() * 0.5f, 0.3f, 0.5f, EmberGold, 1.8f);
        _phase = Phase.Spool;
        _phaseClock = 0;
        _graceClock = 0;
    }

    private void BeginPull()
    {
        if (_phase != Phase.Draw || _holding)
        {
            return;
        }

        _holding = true;
        _everPulled = true;
        _holdSeconds = 0f;
        SpawnRing(HeadPoint(), 4f, CanvasRadius() * 0.35f, 0.35f, 0.4f, Jade, 1.8f);
    }

    private void ReleasePull()
    {
        if (_phase != Phase.Draw || !_holding)
        {
            return;
        }

        _holding = false;
        TieKnot();
    }

    private void StepDraw(double delta)
    {
        _activeSeconds += delta;
        var dt = (float)delta;

        if (_holding)
        {
            _holdSeconds += dt;
            _drawnUnits += (BasePullUnitsPerSecond + (PullAcceleration * _holdSeconds)) * dt;

            // Gust schedule: seeded turbulence that multiplies fray while it blows.
            var gust = 1f;
            if (_gustRemaining > 0f)
            {
                _gustRemaining -= dt;
                gust = 1f + ((GustStrainFactor - 1f) * _gustStrength);
            }
            else if (_holdSeconds >= _nextGustAt)
            {
                _gustRemaining = 0.35f + ((float)_threadRng.NextDouble() * 0.25f);
                _gustStrength = 0.6f + ((float)_threadRng.NextDouble() * 0.4f);
                _nextGustAt = _holdSeconds + 0.9f + ((float)_threadRng.NextDouble() * 1.4f);
                _shake = Mathf.Max(_shake, 2.2f);
            }

            var escalation = 1f + (EscalationPerKnot * Mathf.Min(_knotCount, EscalationKnotCap));
            var strainRate = (StrainBasePerSecond + (StrainRampPerSecondSquared * _holdSeconds))
                * escalation
                * gust;
            _strain += strainRate * dt;

            PopFibersAtThresholds();
            SpawnDrawSparks(dt);

            if (_strain >= 1f)
            {
                SnapThread();
                return;
            }
        }
        else
        {
            // An idle thread invites: pulse a beacon at the head every second or so.
            _beaconClock += delta;
            if (_beaconClock >= 1.1)
            {
                _beaconClock = 0;
                SpawnRing(HeadPoint(), 3f, CanvasRadius() * 0.4f, 0.3f, 0.8f, Jade, 1.5f);
            }
        }

        _strainShown = Mathf.Lerp(_strainShown, _strain, 1f - Mathf.Exp(-8f * dt));

        if (_providerReady)
        {
            _graceClock += delta;
            if (!_holding)
            {
                ExitEarly();
            }
            else if (_graceClock >= GraceSeconds)
            {
                // Generous grace: a pull in hand when time runs out ties itself off.
                _holding = false;
                TieKnot();
            }
        }
    }

    private void TieKnot()
    {
        if (_drawnUnits <= 0.05f)
        {
            // A twitch, not a pull: no knot, no snap, just let the thread settle.
            _strain = 0f;
            return;
        }

        _knotCount++;
        _bankedUnits += _drawnUnits;

        var head = HeadPoint();
        _knots.Add(new Knot
        {
            Pos = head,
            From = head,
            Target = ChainSlot(_knots.Count),
            Size = Mathf.Clamp(5.5f + (_drawnUnits * 2.1f), 6f, 14f),
            Flight = 0f,
            PulsePhase = (float)_vfxRng.NextDouble() * Mathf.Tau,
            Seed = _vfxRng.Next(),
        });

        SpawnTieBurst(head);
        _shake = Mathf.Max(_shake, 4f);
        _phase = Phase.Tie;
        _phaseClock = 0;
    }

    private void SnapThread()
    {
        _snapCount++;
        _holding = false;

        // The thread bursts into embers along its whole length and leaves a fading afterimage.
        _snapAfterimage = new Vector2[ThreadNodes];
        for (var i = 0; i < ThreadNodes; i++)
        {
            _snapAfterimage[i] = _nodes[i];
        }

        _snapFade = 1f;
        var breakIndex = ThreadNodes / 2 + _vfxRng.Next(-4, 5);
        breakIndex = Mathf.Clamp(breakIndex, 2, ThreadNodes - 3);
        _snapPoint = _nodes[breakIndex];

        for (var i = 0; i < ThreadNodes; i += 1)
        {
            var away = (_nodes[i] - _snapPoint).Normalized();
            if (!away.IsFinite() || away == Vector2.Zero)
            {
                away = RandomDirection();
            }

            var speed = 90f + ((float)_vfxRng.NextDouble() * 220f);
            var color = _vfxRng.NextDouble() < 0.4
                ? WildViolet
                : EmberColor(0.5f + ((float)_vfxRng.NextDouble() * 0.5f));
            SpawnEmber(
                _nodes[i],
                (away * speed) + (RandomDirection() * 40f),
                0.5f + ((float)_vfxRng.NextDouble() * 0.7f),
                1.6f + ((float)_vfxRng.NextDouble() * 2.4f),
                color,
                2.2f,
                18f);
        }

        SpawnRing(_snapPoint, 6f, CanvasRadius() * 1.6f, 0.5f, 0.5f, WildViolet, 2.6f);
        SpawnRing(_snapPoint, 3f, CanvasRadius() * 0.8f, 0.6f, 0.35f, SnapRed, 2f);
        _stains.Add(ChainSlot(_knots.Count + 1) + new Vector2(NextSigned() * 10f, 16f + (NextSigned() * 4f)));
        _shake = Mathf.Max(_shake, 7f);
        _vignettePulse = 1f;
        _fibers.Clear();
        _phase = Phase.Snap;
        _phaseClock = 0;
    }

    private void AfterThreadResolved()
    {
        if (_providerReady)
        {
            ExitEarly();
        }
        else
        {
            StartThread();
        }
    }

    private void ExitEarly()
    {
        if (_everPulled)
        {
            EnterFinale();
        }
        else
        {
            Complete();
        }
    }

    private void EnterFinale()
    {
        for (var index = 0; index < _embers.Count; index++)
        {
            var ember = _embers[index];
            ember.PullToCenter = true;
            ember.Life = Mathf.Max(ember.Life, 0.3f);
            _embers[index] = ember;
        }

        // The necklace feeds the spell: every banked knot streaks to the center.
        foreach (var knot in _knots)
        {
            for (var i = 0; i < 6; i++)
            {
                SpawnEmber(
                    knot.Pos,
                    RandomDirection() * (40f + ((float)_vfxRng.NextDouble() * 80f)),
                    0.4f + ((float)_vfxRng.NextDouble() * 0.4f),
                    1.5f + ((float)_vfxRng.NextDouble() * 1.8f),
                    EmberGold,
                    2.0f,
                    24f)
                ;
                _embers[^1] = _embers[^1] with { PullToCenter = true };
            }
        }

        SpawnRing(Vector2.Zero, CanvasRadius() * 0.2f, CanvasRadius() * 2.4f, 0.5f, 0.45f, Jade, 2.5f);
        _shake = Mathf.Max(_shake, 6f);
        _phase = Phase.Finale;
        _phaseClock = 0;
    }

    // ---------------------------------------------------------------- verlet thread

    private Vector2 SpoolPoint()
    {
        var radius = CanvasRadius();
        return new Vector2(-radius * 1.02f, radius * 0.18f);
    }

    private Vector2 AnchorPoint()
    {
        var radius = CanvasRadius();
        return new Vector2(radius * 1.02f, -radius * 0.14f);
    }

    /// <summary>
    /// Where the draw head currently floats: pulled along a gentle arc from beside the spool
    /// toward the anchor eyelet as the drawn length grows, approaching but never quite
    /// reaching it - there is always more thread to want.
    /// </summary>
    private Vector2 HeadPoint()
    {
        var progress = 1f - Mathf.Exp(-_drawnUnits / 2.2f);
        var spool = SpoolPoint() + new Vector2(34f, -10f);
        var anchor = AnchorPoint();
        var mid = ((spool + anchor) / 2f) + new Vector2(0f, -CanvasRadius() * 0.34f);
        var t = 0.08f + (progress * 0.92f);
        var a = spool.Lerp(mid, t);
        var b = mid.Lerp(anchor, t);
        return a.Lerp(b, t);
    }

    private void StepThread(double delta)
    {
        if (_phase is not (Phase.Spool or Phase.Draw or Phase.Tie))
        {
            return;
        }

        var dt = Mathf.Min((float)delta, 1f / 30f);
        var spool = SpoolPoint();
        var head = HeadPoint();

        // Verlet integration with gentle gravity and a seeded lateral breeze.
        var breeze = new Vector2(
            Mathf.Sin(((float)_time * 0.7f) + 1.3f) * 14f,
            Mathf.Sin(((float)_time * 0.53f) + 4.1f) * 6f);
        if (_gustRemaining > 0f)
        {
            breeze *= 1f + (_gustStrength * 5f);
        }

        for (var i = 1; i < ThreadNodes - 1; i++)
        {
            var current = _nodes[i];
            var velocity = (current - _nodesPrev[i]) * 0.985f;
            _nodesPrev[i] = current;
            _nodes[i] = current + velocity + ((new Vector2(0f, 150f) + breeze) * dt * dt);
        }

        // Rest length eases from a lazy catenary to nearly taut as strain rises.
        var sag = Mathf.Lerp(SlackSag, TautSag, Smooth(_holding ? Mathf.Max(_strainShown, 0.25f) : 0f));
        var restLength = (spool.DistanceTo(head) * sag) / (ThreadNodes - 1);

        for (var pass = 0; pass < ConstraintPasses; pass++)
        {
            _nodes[0] = spool;
            _nodes[ThreadNodes - 1] = head;
            for (var i = 0; i < ThreadNodes - 1; i++)
            {
                var a = _nodes[i];
                var b = _nodes[i + 1];
                var deltaVec = b - a;
                var distance = deltaVec.Length();
                if (distance < 0.0001f)
                {
                    continue;
                }

                var correction = deltaVec * ((distance - restLength) / distance) * 0.5f;
                if (i != 0)
                {
                    _nodes[i] = a + correction;
                }

                if (i + 1 != ThreadNodes - 1)
                {
                    _nodes[i + 1] = b - correction;
                }
            }
        }
    }

    /// <summary>
    /// The taut thread hums: a standing wave perpendicular to the chord whose amplitude and
    /// frequency climb with strain, so the player can hear danger with their eyes.
    /// </summary>
    private Vector2 VibrationAt(float t01)
    {
        if (!_holding || _strainShown < 0.08f)
        {
            return Vector2.Zero;
        }

        var chord = (HeadPoint() - SpoolPoint()).Normalized();
        var normal = new Vector2(-chord.Y, chord.X);
        var envelope = Mathf.Sin(t01 * Mathf.Pi);
        var frequency = 16f + (_strainShown * 30f);
        var amplitude = _strainShown * _strainShown * 7.5f;
        var wave = Mathf.Sin(((float)_time * frequency) + (t01 * Mathf.Pi * 3f));
        return normal * envelope * amplitude * wave;
    }

    // ---------------------------------------------------------------- fray and sparks

    private void PopFibersAtThresholds()
    {
        var tiers = _strain switch
        {
            >= 0.82f => 3,
            >= 0.60f => 2,
            >= 0.35f => 1,
            _ => 0,
        };
        while (_fiberThreshold < tiers)
        {
            _fiberThreshold++;
            var cluster = 2 + _fiberThreshold;
            for (var i = 0; i < cluster; i++)
            {
                _fibers.Add(new Fiber
                {
                    Along01 = 0.25f + ((float)_vfxRng.NextDouble() * 0.6f),
                    Angle = (float)_vfxRng.NextDouble() * Mathf.Tau,
                    Length = 7f + ((float)_vfxRng.NextDouble() * 9f) + (_fiberThreshold * 2f),
                    Age = 0f,
                    Life = 999f,
                    Curl = NextSigned() * 2.4f,
                });
            }

            var at = NodeAt(0.3f + ((float)_vfxRng.NextDouble() * 0.5f));
            for (var i = 0; i < 6; i++)
            {
                SpawnEmber(
                    at,
                    RandomDirection() * (30f + ((float)_vfxRng.NextDouble() * 60f)),
                    0.3f + ((float)_vfxRng.NextDouble() * 0.35f),
                    1.3f + ((float)_vfxRng.NextDouble() * 1.4f),
                    _fiberThreshold >= 3 ? SnapRed : WildViolet,
                    3.0f,
                    -6f);
            }

            _shake = Mathf.Max(_shake, 1.5f + _fiberThreshold);
        }
    }

    private double _drawSparkAccumulator;

    private void SpawnDrawSparks(float dt)
    {
        _drawSparkAccumulator += dt * (6.0 + (_strainShown * 14.0));
        var count = (int)_drawSparkAccumulator;
        if (count <= 0)
        {
            return;
        }

        _drawSparkAccumulator -= count;
        var head = HeadPoint();
        for (var i = 0; i < count; i++)
        {
            var heat = 0.6f + ((float)_vfxRng.NextDouble() * 0.4f);
            SpawnEmber(
                head + (RandomDirection() * 4f),
                RandomDirection() * (14f + ((float)_vfxRng.NextDouble() * 40f)),
                0.3f + ((float)_vfxRng.NextDouble() * 0.4f),
                1.2f + ((float)_vfxRng.NextDouble() * 1.6f),
                EmberColor(heat).Lerp(WildViolet, _strainShown * 0.6f),
                2.8f,
                26f);
        }
    }

    private void SpawnTieBurst(Vector2 head)
    {
        for (var i = 0; i < 26; i++)
        {
            SpawnEmber(
                head + (RandomDirection() * 3f),
                RandomDirection() * (60f + ((float)_vfxRng.NextDouble() * 130f)),
                0.4f + ((float)_vfxRng.NextDouble() * 0.5f),
                1.6f + ((float)_vfxRng.NextDouble() * 2.0f),
                EmberColor(0.7f + ((float)_vfxRng.NextDouble() * 0.3f)),
                2.4f,
                30f);
        }

        SpawnRing(head, 5f, CanvasRadius() * 0.9f, 0.45f, 0.4f, EmberGold, 2.4f);
        SpawnRing(head, 3f, CanvasRadius() * 0.45f, 0.5f, 0.3f, WhiteHot, 1.6f);
    }

    // ---------------------------------------------------------------- vfx simulation

    private void StepVfx(double delta)
    {
        var dt = (float)delta;
        _shake *= Mathf.Exp(-6f * dt);
        _shakeOffset = _shake <= 0.05f
            ? Vector2.Zero
            : new Vector2(NextSigned() * _shake, NextSigned() * _shake);
        _vignettePulse = Mathf.Max(0f, _vignettePulse - (2.4f * dt));

        for (var i = _embers.Count - 1; i >= 0; i--)
        {
            var ember = _embers[i];
            ember.Life -= dt;
            if (ember.Life <= 0f)
            {
                _embers.RemoveAt(i);
                continue;
            }

            if (ember.PullToCenter)
            {
                ember.Vel += (-ember.Pos).Normalized() * 2600f * dt;
                ember.Vel *= Mathf.Exp(-2.2f * dt);
            }
            else
            {
                ember.Vel = new Vector2(ember.Vel.X, ember.Vel.Y - (ember.Rise * dt));
                ember.Vel *= Mathf.Exp(-ember.Drag * dt);
            }

            ember.Pos += ember.Vel * dt;
            _embers[i] = ember;
        }

        for (var i = _rings.Count - 1; i >= 0; i--)
        {
            var ring = _rings[i];
            ring.Age += dt;
            if (ring.Age >= ring.Life)
            {
                _rings.RemoveAt(i);
                continue;
            }

            _rings[i] = ring;
        }

        for (var i = 0; i < _fibers.Count; i++)
        {
            var fiber = _fibers[i];
            fiber.Age += dt;
            _fibers[i] = fiber;
        }

        for (var i = 0; i < _knots.Count; i++)
        {
            var knot = _knots[i];
            if (knot.Flight < 1f)
            {
                knot.Flight = Mathf.Min(1f, knot.Flight + (dt / 0.55f));
                var eased = 1f - Mathf.Pow(1f - knot.Flight, 3f);
                var lift = Mathf.Sin(eased * Mathf.Pi) * -46f;
                knot.Pos = knot.From.Lerp(knot.Target, eased) + new Vector2(0f, lift);
                if (knot.Flight >= 1f)
                {
                    SpawnRing(knot.Target, 3f, 26f, 0.4f, 0.3f, EmberGold, 1.4f);
                }
            }

            _knots[i] = knot;
        }

        // Ambient motes drifting off the living thread keep long waits alive.
        if (_phase is Phase.Draw && _vfxRng.NextDouble() < 0.3)
        {
            var at = NodeAt((float)_vfxRng.NextDouble());
            SpawnEmber(at, RandomDirection() * 10f, 0.9f, 1.4f, EmberColor(0.4f), 1.2f, 22f);
        }
    }

    private void SpawnRing(Vector2 center, float radius, float growTo, float alpha, float life, Color color, float width) =>
        _rings.Add(new Ring
        {
            Center = center,
            Radius = radius,
            Speed = (growTo - radius) / life,
            Age = 0f,
            Life = life,
            Alpha = alpha,
            Width = width,
            Color = color,
        });

    private void SpawnEmber(Vector2 position, Vector2 velocity, float life, float size, Color color, float drag, float rise)
    {
        if (_embers.Count >= MaxEmbers)
        {
            _embers.RemoveAt(0);
        }

        _embers.Add(new Ember
        {
            Pos = position,
            Vel = velocity,
            Life = life,
            MaxLife = life,
            Size = size,
            Color = color,
            Drag = drag,
            Rise = rise,
        });
    }

    private Vector2 NodeAt(float t01)
    {
        var index = Mathf.Clamp((int)(t01 * (ThreadNodes - 1)), 0, ThreadNodes - 1);
        return _nodes[index];
    }

    private Vector2 RandomDirection()
    {
        var angle = (float)(_vfxRng.NextDouble() * Math.Tau);
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private float NextSigned() => ((float)_vfxRng.NextDouble() * 2f) - 1f;

    private static float Smooth(float t)
    {
        t = Mathf.Clamp(t, 0f, 1f);
        return t * t * (3f - (2f * t));
    }

    private static Color EmberColor(float heat)
    {
        heat = Mathf.Clamp(heat, 0f, 1f);
        return heat switch
        {
            >= 0.66f => EmberGold.Lerp(WhiteHot, (heat - 0.66f) / 0.34f),
            >= 0.33f => EmberOrange.Lerp(EmberGold, (heat - 0.33f) / 0.33f),
            _ => DeepEmber.Lerp(EmberOrange, heat / 0.33f),
        };
    }

    // ---------------------------------------------------------------- drawing

    private Vector2 CanvasCenter() => new(Size.X / 2f, (Size.Y / 2f) - (Size.Y * 0.045f));

    private float CanvasRadius() => Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * 0.30f, 140f, 340f);

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

        var center = CanvasCenter() + _shakeOffset;
        var radius = CanvasRadius();
        layer.DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.015f, 0.008f, 0.03f, 0.88f));

        // A soft indigo pool of light behind the working keeps the thread readable.
        for (var i = 5; i >= 1; i--)
        {
            layer.DrawCircle(
                center,
                radius * 0.36f * i,
                new Color(0.11f, 0.07f, 0.20f, 0.045f));
        }

        // A snap stings the whole frame for a breath: wild magic bites back.
        if (_vignettePulse > 0.01f)
        {
            layer.DrawRect(
                new Rect2(Vector2.Zero, Size),
                new Color(0.35f, 0.05f, 0.10f, 0.16f * _vignettePulse));
        }

        // Violet stains under the necklace: the run's snaps, worn like small shames.
        foreach (var stain in _stains)
        {
            layer.DrawCircle(center + stain, 3.4f, new Color(WildViolet, 0.22f));
            layer.DrawCircle(center + stain, 1.8f, new Color(WildViolet, 0.30f));
        }
    }

    private void DrawGlow(DrawLayer layer)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        var center = CanvasCenter() + _shakeOffset;

        DrawSpool(layer, center);
        DrawAnchor(layer, center);
        DrawThread(layer, center);
        DrawFibers(layer, center);
        DrawHeadBead(layer, center);
        DrawNecklace(layer, center);

        foreach (var ring in _rings)
        {
            var fade = 1f - (ring.Age / ring.Life);
            layer.DrawArc(
                center + ring.Center,
                ring.Radius + (ring.Speed * ring.Age),
                0f,
                Mathf.Tau,
                64,
                new Color(ring.Color, ring.Alpha * fade),
                ring.Width,
                antialiased: true);
        }

        foreach (var ember in _embers)
        {
            var lifeFrac = Mathf.Clamp(ember.Life / ember.MaxLife, 0f, 1f);
            layer.DrawCircle(
                center + ember.Pos,
                ember.Size * (0.45f + (0.55f * lifeFrac)),
                new Color(ember.Color, 0.8f * lifeFrac));
        }
    }

    private void DrawSpool(DrawLayer layer, Vector2 center)
    {
        var spool = center + SpoolPoint();
        var breathe = 0.5f + (0.16f * Mathf.Sin((float)_time * 1.7f));
        var spin = (float)_time * 0.5f;

        layer.DrawCircle(spool, 30f, new Color(DeepEmber, 0.10f));
        for (var i = 0; i < 3; i++)
        {
            var r = 13f + (i * 6.5f);
            var direction = i % 2 == 0 ? 1f : -1f;
            var start = spin * direction + (i * 1.9f);
            layer.DrawArc(spool, r, start, start + 4.4f, 40, new Color(EmberOrange, 0.35f * breathe), 2.2f, antialiased: true);
            layer.DrawArc(spool, r, start + Mathf.Pi, start + Mathf.Pi + 3.2f, 32, new Color(EmberGold, 0.22f * breathe), 1.4f, antialiased: true);
        }

        layer.DrawCircle(spool, 4.5f, new Color(EmberGold, 0.7f * breathe));
    }

    private void DrawAnchor(DrawLayer layer, Vector2 center)
    {
        var anchor = center + AnchorPoint();
        var near = 1f - Mathf.Clamp((HeadPoint().DistanceTo(AnchorPoint())) / (CanvasRadius() * 1.4f), 0f, 1f);
        var gleam = 0.18f + (near * 0.5f) + (0.06f * Mathf.Sin((float)_time * 2.3f));

        layer.DrawArc(anchor, 13f, 0f, Mathf.Tau, 40, new Color(EmberGold, gleam), 2.4f, antialiased: true);
        layer.DrawArc(anchor, 19f, 0f, Mathf.Tau, 48, new Color(EmberGold, gleam * 0.4f), 1.2f, antialiased: true);
        layer.DrawCircle(anchor, 3f, new Color(WhiteHot, gleam));
    }

    private void DrawThread(DrawLayer layer, Vector2 center)
    {
        if (_phase == Phase.Snap)
        {
            if (_snapFade > 0.01f && _snapAfterimage.Length >= 2)
            {
                var ghost = new Vector2[_snapAfterimage.Length];
                for (var i = 0; i < _snapAfterimage.Length; i++)
                {
                    ghost[i] = center + _snapAfterimage[i];
                }

                layer.DrawPolyline(ghost, new Color(SnapRed, 0.5f * _snapFade), 3.4f, antialiased: true);
                layer.DrawPolyline(ghost, new Color(WhiteHot, 0.7f * _snapFade * _snapFade), 1.4f, antialiased: true);
            }

            return;
        }

        if (_phase is not (Phase.Spool or Phase.Draw or Phase.Tie))
        {
            return;
        }

        var reveal = _phase == Phase.Spool
            ? Mathf.Clamp((float)(_phaseClock / SpoolSeconds), 0f, 1f)
            : 1f;
        var visibleNodes = Mathf.Max(2, (int)(ThreadNodes * reveal));
        var points = new Vector2[visibleNodes];
        var colors = new Color[visibleNodes];
        for (var i = 0; i < visibleNodes; i++)
        {
            var t01 = (float)i / (ThreadNodes - 1);
            points[i] = center + _nodes[i] + VibrationAt(t01);

            // Heat climbs toward the draw head; strain bruises the middle toward violet.
            var heat = 0.35f + (0.62f * t01);
            var color = EmberColor(heat);
            var bruise = Mathf.Sin(t01 * Mathf.Pi) * _strainShown;
            color = color.Lerp(WildViolet.Lerp(SnapRed, Mathf.Clamp((_strainShown - 0.6f) / 0.4f, 0f, 1f)), bruise * 0.85f);
            var shimmer = 0.85f + (0.15f * Mathf.Sin(((float)_time * 3.1f) + (t01 * 9f)));
            colors[i] = new Color(color, shimmer);
        }

        // Layered widths fake bloom: wide faint halo, tight bright core.
        DrawPolylinePass(layer, points, colors, 9f + (_strainShown * 3f), 0.10f);
        DrawPolylinePass(layer, points, colors, 4f + (_strainShown * 1.5f), 0.30f);
        DrawPolylinePass(layer, points, colors, 1.7f, 0.95f);

        if (_phase == Phase.Tie && _phaseClock < 0.16)
        {
            var flashAlpha = 1f - (float)(_phaseClock / 0.16);
            layer.DrawPolyline(points, new Color(WhiteHot, flashAlpha), 4f, antialiased: true);
        }
    }

    private void DrawFibers(DrawLayer layer, Vector2 center)
    {
        if (_phase != Phase.Draw || _fibers.Count == 0)
        {
            return;
        }

        foreach (var fiber in _fibers)
        {
            var root = center + NodeAt(fiber.Along01) + VibrationAt(fiber.Along01);
            var flicker = 0.5f + (0.5f * Mathf.Sin(((float)_time * 13f) + (fiber.Angle * 7f)));
            var color = _strainShown > 0.8f ? SnapRed : WildViolet;
            var wisp = new Vector2[4];
            wisp[0] = root;
            var direction = Vector2.FromAngle(fiber.Angle);
            for (var i = 1; i < 4; i++)
            {
                var t = (float)i / 3f;
                var bend = Vector2.FromAngle(fiber.Angle + (fiber.Curl * t));
                wisp[i] = wisp[i - 1] + (bend * (fiber.Length / 3f)) + (direction * Mathf.Sin(((float)_time * 9f) + i) * 1.2f);
            }

            layer.DrawPolyline(wisp, new Color(color, 0.55f * flicker), 1.3f, antialiased: true);
        }
    }

    private void DrawHeadBead(DrawLayer layer, Vector2 center)
    {
        if (_phase is not (Phase.Spool or Phase.Draw or Phase.Tie))
        {
            return;
        }

        var head = center + HeadPoint();
        var pulse = 1f + (0.22f * Mathf.Sin((float)_time * 6f));

        if (_phase == Phase.Draw && !_holding)
        {
            layer.DrawArc(head, 18f * pulse, 0f, Mathf.Tau, 40, new Color(Jade, 0.35f), 1.6f, antialiased: true);
            layer.DrawCircle(head, 8f * pulse, new Color(Jade, 0.16f));
        }

        var beadColor = _holding
            ? WhiteHot.Lerp(SnapRed, Mathf.Clamp((_strainShown - 0.55f) / 0.45f, 0f, 1f))
            : EmberGold;
        layer.DrawCircle(head, 5.5f * pulse, new Color(beadColor, 0.95f));
        layer.DrawCircle(head, 11f * pulse, new Color(beadColor, 0.20f));

        if (_holding)
        {
            // The strain arc: the danger gauge worn openly around the bead.
            var strainColor = _strainShown < 0.5f
                ? Jade.Lerp(UiTheme.Warning, _strainShown * 2f)
                : UiTheme.Warning.Lerp(SnapRed, (_strainShown - 0.5f) * 2f);
            layer.DrawArc(
                head,
                16f,
                -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * Mathf.Clamp(_strainShown, 0f, 1f)),
                48,
                new Color(strainColor, 0.85f),
                2.6f,
                antialiased: true);

            // Two orbiting sparks make the pull feel alive in the hand.
            for (var i = 0; i < 2; i++)
            {
                var angle = ((float)_time * (3.4f + (_strainShown * 4f))) + (i * Mathf.Pi);
                var orbit = head + (Vector2.FromAngle(angle) * 22f);
                layer.DrawCircle(orbit, 1.8f, new Color(EmberGold, 0.7f));
            }
        }
    }

    private void DrawNecklace(DrawLayer layer, Vector2 center)
    {
        if (_knots.Count == 0)
        {
            return;
        }

        // The banked chain: knots strung on a faint thread of light along the bottom arc.
        if (_knots.Count >= 2)
        {
            var strand = new Vector2[_knots.Count];
            for (var i = 0; i < _knots.Count; i++)
            {
                strand[i] = center + ChainSlot(i + 1) + new Vector2(0f, 2f * Mathf.Sin(((float)_time * 1.2f) + i));
            }

            layer.DrawPolyline(strand, new Color(EmberGold, 0.18f), 1.2f, antialiased: true);
        }

        foreach (var knot in _knots)
        {
            var position = center + (knot.Flight >= 1f
                ? knot.Target + new Vector2(0f, 2f * Mathf.Sin(((float)_time * 1.2f) + knot.PulsePhase))
                : knot.Pos);
            var pulse = 0.85f + (0.15f * Mathf.Sin(((float)_time * 2.1f) + knot.PulsePhase));
            DrawKnotMedallion(layer, position, knot.Size * pulse, knot.Seed);
        }
    }

    /// <summary>A tiny trefoil of light: the banked knot itself, ornate at any size.</summary>
    private static void DrawKnotMedallion(DrawLayer layer, Vector2 at, float size, int seed)
    {
        const int samples = 30;
        var spin = (seed % 628) / 100f;
        var points = new Vector2[samples + 1];
        for (var i = 0; i <= samples; i++)
        {
            var theta = (Mathf.Tau * i / samples) + spin;
            var x = (Mathf.Sin(theta) + (2f * Mathf.Sin(2f * theta))) / 3f;
            var y = (Mathf.Cos(theta) - (2f * Mathf.Cos(2f * theta))) / 3f;
            points[i] = at + (new Vector2(x, y) * size);
        }

        layer.DrawPolyline(points, new Color(EmberGold, 0.22f), size * 0.5f, antialiased: true);
        layer.DrawPolyline(points, new Color(EmberGold, 0.65f), 1.4f, antialiased: true);
        layer.DrawCircle(at, size * 0.22f, new Color(WhiteHot, 0.5f));
    }

    private Vector2 ChainSlot(int oneBasedIndex)
    {
        // Slots fan out from the center of a shallow arc under the working, newest outward.
        var radius = CanvasRadius();
        var offset = oneBasedIndex / 2;
        var side = oneBasedIndex % 2 == 0 ? 1f : -1f;
        var x = side * offset * 30f;
        var y = (radius * 1.28f) - (Mathf.Cos(x / (radius * 1.6f)) * 8f);
        return new Vector2(x, y);
    }

    private static void DrawPolylinePass(DrawLayer layer, Vector2[] points, Color[] colors, float width, float alpha)
    {
        var pass = new Color[colors.Length];
        for (var i = 0; i < colors.Length; i++)
        {
            pass[i] = new Color(colors[i], colors[i].A * alpha);
        }

        layer.DrawPolylineColors(points, pass, width, antialiased: true);
    }

    // ---------------------------------------------------------------- hud

    private void BuildHud()
    {
        var top = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Ignore,
            Alignment = BoxContainer.AlignmentMode.Center,
        };
        top.SetAnchorsAndOffsetsPreset(LayoutPreset.TopWide);
        top.OffsetTop = 34;
        top.AddThemeConstantOverride("separation", UiTheme.SpaceXs);
        AddChild(top);

        _title = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _title.AddThemeFontSizeOverride("font_size", 24);
        _title.AddThemeColorOverride("font_color", EmberGold);
        top.AddChild(_title);

        _subtitle = new Label
        {
            Text = "",
            HorizontalAlignment = HorizontalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _subtitle.AddThemeFontSizeOverride("font_size", 13);
        _subtitle.AddThemeColorOverride("font_color", UiTheme.Muted);
        top.AddChild(_subtitle);

        var bottom = new VBoxContainer
        {
            MouseFilter = MouseFilterEnum.Pass,
            Alignment = BoxContainer.AlignmentMode.End,
        };
        bottom.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
        bottom.OffsetTop = -132;
        bottom.OffsetBottom = -26;
        bottom.AddThemeConstantOverride("separation", UiTheme.SpaceSm);
        AddChild(bottom);

        _hint = new Label
        {
            Text = "Hold the mouse (or Space) to draw the thread — release to tie the knot before it snaps. Longer pulls bank more, and fray faster.",
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
            Text = "",
            VerticalAlignment = VerticalAlignment.Center,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        _tally.AddThemeFontSizeOverride("font_size", 12);
        _tally.AddThemeColorOverride("font_color", UiTheme.Muted);
        row.AddChild(_tally);

        _skip = new Button
        {
            Text = "Cast Unshaped  (Esc)",
            CustomMinimumSize = new Vector2(150, 34),
        };
        _skip.Pressed += Skip;
        row.AddChild(_skip);
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
        _title.Text = _phase switch
        {
            Phase.Spool => "A fresh thread spools out…",
            Phase.Draw when !_holding => "Hold to draw the thread",
            Phase.Draw when _strainShown >= 0.82f => "It's about to snap—",
            Phase.Draw when _strainShown >= 0.5f => "The thread frays…",
            Phase.Draw => "The thread sings…",
            Phase.Tie => "Tied!",
            Phase.Snap => "Snapped.",
            Phase.Finale => _everPulled ? "The knots feed the spell…" : "The spell resolves, unshaped.",
            _ => "",
        };
        _subtitle.Text = $"“{_spellText}”";
        var knots = _knotCount == 1 ? "1 knot" : $"{_knotCount} knots";
        _tally.Text = _snapCount == 0 ? knots : $"{knots} · {_snapCount} snapped";

        // Bars sit in score space: 0.5 is the neutral center, matching ThreadKnotScoring.
        var liveBank = _activeSeconds > 0.4
            ? Mathf.Clamp(
                (float)((_bankedUnits / _activeSeconds) / ParBankUnitsPerSecond
                    / ThreadKnotScoring.BankCeilingRatio),
                0f,
                1f)
            : 0.5f;
        var resolved = _knotCount + _snapCount;
        var liveClean = resolved > 0
            ? Mathf.Clamp(
                (float)((((double)_knotCount / resolved) - ThreadKnotScoring.CleanFloor)
                    / (ThreadKnotScoring.CleanCeiling - ThreadKnotScoring.CleanFloor)),
                0f,
                1f)
            : 0.5f;
        _powerBar.Value = liveBank;
        _controlBar.Value = liveClean;
        _hint.Visible = !_everPulled;
    }
}
