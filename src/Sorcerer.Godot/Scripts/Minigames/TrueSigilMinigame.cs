using Godot;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// The True-Sigil casting minigame: while the provider resolves a wild spell, the wild shows
/// the spell's true shape once - a sigil burns into the air, holds, and crumbles to ash -
/// then ghosts rise from the ashes and ask to be chosen. One is true; the rest are lies:
/// mirrors, rotations, and near-perfect forgeries whose single wrong stroke gets subtler as
/// rounds chain. Sure recall feeds control, swift recall feeds power, guessing feeds
/// wildness. Rounds chain indefinitely until the provider returns, so unknown latency is the
/// game's natural clock. Pure recognition memory, no dexterity: skipping, or never choosing,
/// is a neutral cast, never a punishment.
///
/// The overlay owns presentation and gesture reduction only; it reduces the session to
/// <see cref="TrueSigilMetrics"/> and lets <see cref="TrueSigilScoring"/> (core) decide the
/// <see cref="CastPerformance"/>, so calibration stays engine-owned and testable. Sigils and
/// their lies are seeded from the spell text (like rune shapes), so recasting the same
/// phrase meets the same shapes and the same liars.
/// </summary>
public partial class TrueSigilMinigame : Control
{
    private enum Phase
    {
        Idle,
        FadeIn,
        Flash,
        Dissolve,
        Rise,
        Choose,
        Reveal,
        Finale,
    }

    private const float FadeInSeconds = 0.18f;
    private const float DissolveSeconds = 0.5f;
    private const float RiseSeconds = 0.42f;
    private const float RevealTrueSeconds = 0.7f;
    private const float RevealFalseSeconds = 0.95f;
    private const float FinaleSeconds = 0.5f;
    private const float GraceSeconds = 2.0f;
    private const int MaxEmbers = 720;

    // Presentation-side calibration; the core scoring sees only ratios.
    private const float ParAnswerSeconds = 2.2f;
    private const float SoftTimerSeconds = 6f;   // the drain arc; slow answers are safe, just weaker
    private const float FlashSecondsEasy = 1.35f;
    private const float FlashSecondsHard = 0.80f;
    private const int MaxTier = 3;

    private static readonly Color DeepEmber = new(0.55f, 0.16f, 0.09f);
    private static readonly Color EmberOrange = new(1.0f, 0.47f, 0.16f);
    private static readonly Color EmberGold = new(1.0f, 0.78f, 0.40f);
    private static readonly Color WhiteHot = new(1.0f, 0.96f, 0.86f);
    private static readonly Color Jade = UiTheme.Wild;
    private static readonly Color WildViolet = new(0.69f, 0.48f, 1.0f);
    private static readonly Color LieRed = new(1.0f, 0.32f, 0.28f);
    private static readonly Color AshGrey = new(0.48f, 0.44f, 0.52f);

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

    /// <summary>One rising ghost: a candidate sigil, true or lying.</summary>
    private struct Candidate
    {
        public Vector2[] Points;   // canvas-relative, pre-scaled around Center
        public Vector2 Center;
        public float CellRadius;
        public bool IsTrue;
        public float BobPhase;
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
    private bool _everAnswered;
    private bool _skipped;

    // Scoring totals (ratio-based; see TrueSigilScoring).
    private double _activeSeconds;
    private double _responseSecondsTotal;
    private int _rounds;
    private int _correct;

    // Current round.
    private int _roundIndex;
    private float _flashSeconds = FlashSecondsEasy;
    private Vector2[] _truthPoints = Array.Empty<Vector2>(); // canvas-relative, at flash center
    private float[] _truthArclen = Array.Empty<float>();
    private float _truthLength;
    private Candidate[] _candidates = Array.Empty<Candidate>();
    private double _responseClock;
    private int _chosenIndex = -1;
    private bool _revealCorrect;
    private double _ashAccumulator;

    // The run's memory: gold beads for truths chosen, violet for lies believed.
    private readonly List<bool> _verdicts = new();

    // VFX state.
    private readonly List<Ember> _embers = new(MaxEmbers);
    private readonly List<Ring> _rings = new();
    private readonly Random _vfxRng = new();
    private float _shake;
    private Vector2 _shakeOffset;
    private float _vignettePulse;
    private Vector2 _cursor;
    private bool _cursorKnown;

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
    /// Runs the minigame until the provider settles (plus a short grace to answer the round
    /// in hand) or the player skips. Returns the engine-facing performance for await_cast.
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
        _roundIndex = 0;
        _providerReady = false;
        _everAnswered = false;
        _skipped = false;
        _cursorKnown = false;
        _activeSeconds = 0;
        _responseSecondsTotal = 0;
        _rounds = 0;
        _correct = 0;
        _graceClock = 0;
        _verdicts.Clear();
        _embers.Clear();
        _rings.Clear();
        _shake = 0f;
        _vignettePulse = 0f;
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
                    StartRound();
                }

                break;

            case Phase.Flash:
                _activeSeconds += delta;
                if (_providerReady)
                {
                    ExitEarly();
                    return;
                }

                if (_phaseClock >= _flashSeconds)
                {
                    _phase = Phase.Dissolve;
                    _phaseClock = 0;
                    SpawnRing(FlashCenter(), 6f, CanvasRadius() * 0.8f, 0.35f, 0.5f, AshGrey, 1.8f);
                }

                break;

            case Phase.Dissolve:
                StepDissolve(delta);
                if (_providerReady)
                {
                    ExitEarly();
                    return;
                }

                if (_phaseClock >= DissolveSeconds)
                {
                    _phase = Phase.Rise;
                    _phaseClock = 0;
                    SpawnRiseAsh();
                }

                break;

            case Phase.Rise:
                if (_providerReady)
                {
                    ExitEarly();
                    return;
                }

                if (_phaseClock >= RiseSeconds)
                {
                    _phase = Phase.Choose;
                    _phaseClock = 0;
                    _responseClock = 0;
                }

                break;

            case Phase.Choose:
                _activeSeconds += delta;
                _responseClock += delta;
                if (_providerReady)
                {
                    _graceClock += delta;
                    if (_graceClock >= GraceSeconds)
                    {
                        // The round in hand is abandoned uncounted: never scored, never punished.
                        ExitEarly();
                        return;
                    }
                }

                break;

            case Phase.Reveal:
                if (_phaseClock >= (_revealCorrect ? RevealTrueSeconds : RevealFalseSeconds))
                {
                    if (_providerReady)
                    {
                        ExitEarly();
                        return;
                    }

                    StartRound();
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

        if (@event is InputEventMouseMotion motion)
        {
            _cursor = motion.Position - CanvasCenter();
            _cursorKnown = true;
            AcceptEvent();
        }
        else if (@event is InputEventMouseButton button
            && button.ButtonIndex == MouseButton.Left
            && button.Pressed
            && _phase == Phase.Choose)
        {
            _cursor = button.Position - CanvasCenter();
            _cursorKnown = true;
            var hit = HoveredCandidate();
            if (hit >= 0)
            {
                Pick(hit);
            }

            AcceptEvent();
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
            : TrueSigilScoring.ToPerformance(new TrueSigilMetrics(
                Answered: _everAnswered,
                ActiveSeconds: _activeSeconds,
                Rounds: _rounds,
                Accuracy01: _rounds > 0 ? (double)_correct / _rounds : 0,
                SpeedRatio: _rounds > 0 && _responseSecondsTotal > 0.001
                    ? ParAnswerSeconds / (_responseSecondsTotal / _rounds)
                    : 0));

        _phase = Phase.Idle;
        Visible = false;
        SetProcess(false);
        _providerSettled = null;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(performance);
    }

    // ---------------------------------------------------------------- round lifecycle

    private int Tier => Mathf.Min(_roundIndex / 2, MaxTier);

    private void StartRound()
    {
        var radius = CanvasRadius();
        var tier = Tier;
        _flashSeconds = Mathf.Lerp(FlashSecondsEasy, FlashSecondsHard, (float)tier / MaxTier);

        // Seeded like rune shapes: the same phrase always shows the same truths and the same
        // lies, so a recast spell tests memory, not luck.
        var seed = RuneShape.SeedFor(_spellText, 101 + _roundIndex);
        var rng = new Random(seed);
        var truth = RuneShape.Generate(seed).Points;

        var flashScale = radius * 0.40f;
        _truthPoints = ScaleTo(truth, FlashCenter(), flashScale);
        MeasureTruth();

        var count = tier >= 2 ? 4 : 3;
        var lieMagnitude = tier switch
        {
            0 => 0.34f,
            1 => 0.30f,
            2 => 0.26f,
            _ => 0.20f,
        };
        var shapes = new List<(Vector2[] Points, bool IsTrue)> { (truth, true) };
        foreach (var kind in DecoyRecipe(tier))
        {
            shapes.Add((MakeDecoy(truth, kind, rng, lieMagnitude), false));
        }

        // Seeded shuffle so the true sigil's slot is stable per round, not per frame.
        for (var i = shapes.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (shapes[i], shapes[j]) = (shapes[j], shapes[i]);
        }

        var cellRadius = count == 3 ? radius * 0.27f : radius * 0.23f;
        var spacing = count == 3 ? radius * 0.85f : radius * 0.68f;
        var rowY = radius * 0.56f;
        _candidates = new Candidate[count];
        for (var i = 0; i < count; i++)
        {
            var center = new Vector2((i - ((count - 1) / 2f)) * spacing, rowY);
            _candidates[i] = new Candidate
            {
                Points = ScaleTo(shapes[i].Points, center, cellRadius * 0.82f),
                Center = center,
                CellRadius = cellRadius,
                IsTrue = shapes[i].IsTrue,
                BobPhase = (float)rng.NextDouble() * Mathf.Tau,
            };
        }

        _roundIndex++;
        _chosenIndex = -1;
        _graceClock = 0;
        _ashAccumulator = 0;
        _phase = Phase.Flash;
        _phaseClock = 0;
        SpawnRing(FlashCenter(), 4f, radius * 0.6f, 0.4f, 0.45f, EmberGold, 2f);
    }

    private void Pick(int index)
    {
        _chosenIndex = index;
        _revealCorrect = _candidates[index].IsTrue;
        _everAnswered = true;
        _rounds++;
        _responseSecondsTotal += _responseClock;
        _verdicts.Add(_revealCorrect);
        if (_revealCorrect)
        {
            _correct++;
            SpawnTrueBurst(_candidates[index].Center);
        }
        else
        {
            SpawnLieShatter(_candidates[index]);
        }

        _phase = Phase.Reveal;
        _phaseClock = 0;
    }

    private void ExitEarly()
    {
        if (_everAnswered)
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

        SpawnRing(Vector2.Zero, CanvasRadius() * 0.2f, CanvasRadius() * 2.4f, 0.5f, 0.45f, Jade, 2.5f);
        _shake = Mathf.Max(_shake, 6f);
        _phase = Phase.Finale;
        _phaseClock = 0;
    }

    // ---------------------------------------------------------------- decoys

    private enum DecoyKind
    {
        OtherSigil,
        Mirror,
        Rotate,
        Lie,
    }

    private static DecoyKind[] DecoyRecipe(int tier) => tier switch
    {
        0 => new[] { DecoyKind.OtherSigil, DecoyKind.Mirror },
        1 => new[] { DecoyKind.Mirror, DecoyKind.Lie },
        2 => new[] { DecoyKind.Mirror, DecoyKind.Lie, DecoyKind.Rotate },
        _ => new[] { DecoyKind.Lie, DecoyKind.Lie, DecoyKind.Mirror },
    };

    private static Vector2[] MakeDecoy(Vector2[] truth, DecoyKind kind, Random rng, float lieMagnitude)
    {
        var decoy = kind switch
        {
            DecoyKind.OtherSigil => RuneShape.Generate(rng.Next()).Points,
            DecoyKind.Mirror => Transform(truth, point => new Vector2(-point.X, point.Y)),
            DecoyKind.Rotate => Transform(truth, point => -point),
            _ => VertexLie(truth, rng, lieMagnitude),
        };

        // A mirrored or rotated near-symmetric sigil can land on top of the truth; a lie that
        // is not a lie would be unfair, so fall back to a bolder vertex lie.
        return MaxPointDistance(decoy, truth) < 0.18f
            ? VertexLie(truth, rng, Mathf.Max(lieMagnitude, 0.32f))
            : decoy;
    }

    private static Vector2[] Transform(Vector2[] points, Func<Vector2, Vector2> map)
    {
        var result = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
        {
            result[i] = map(points[i]);
        }

        return result;
    }

    /// <summary>The forgery: one interior vertex quietly moved. The subtlest lie of all.</summary>
    private static Vector2[] VertexLie(Vector2[] truth, Random rng, float magnitude)
    {
        var lie = (Vector2[])truth.Clone();
        if (lie.Length < 3)
        {
            return Transform(lie, point => new Vector2(-point.X, point.Y));
        }

        var index = 1 + rng.Next(lie.Length - 2);
        var angle = (float)(rng.NextDouble() * Math.Tau);
        var moved = lie[index] + (Vector2.FromAngle(angle) * magnitude);
        lie[index] = new Vector2(Mathf.Clamp(moved.X, -1.1f, 1.1f), Mathf.Clamp(moved.Y, -1.1f, 1.1f));
        return lie;
    }

    private static float MaxPointDistance(Vector2[] a, Vector2[] b)
    {
        if (a.Length != b.Length)
        {
            return float.MaxValue;
        }

        var max = 0f;
        for (var i = 0; i < a.Length; i++)
        {
            max = Mathf.Max(max, a[i].DistanceTo(b[i]));
        }

        return max;
    }

    private static Vector2[] ScaleTo(Vector2[] normalized, Vector2 center, float scale)
    {
        var result = new Vector2[normalized.Length];
        for (var i = 0; i < normalized.Length; i++)
        {
            result[i] = center + new Vector2(normalized[i].X * scale * 0.94f, normalized[i].Y * scale);
        }

        return result;
    }

    private void MeasureTruth()
    {
        _truthArclen = new float[_truthPoints.Length];
        var total = 0f;
        for (var i = 1; i < _truthPoints.Length; i++)
        {
            total += _truthPoints[i - 1].DistanceTo(_truthPoints[i]);
            _truthArclen[i] = total;
        }

        _truthLength = Mathf.Max(total, 0.001f);
    }

    /// <summary>Interpolated prefix of a polyline up to a fraction of its arclength.</summary>
    private static Vector2[] PartialPolyline(Vector2[] points, float[] arclen, float totalLength, float fraction)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        var target = totalLength * fraction;
        var kept = new List<Vector2> { points[0] };
        for (var i = 1; i < points.Length; i++)
        {
            if (arclen[i] <= target)
            {
                kept.Add(points[i]);
                continue;
            }

            var span = arclen[i] - arclen[i - 1];
            var t = span < 0.0001f ? 0f : (target - arclen[i - 1]) / span;
            kept.Add(points[i - 1].Lerp(points[i], Mathf.Clamp(t, 0f, 1f)));
            break;
        }

        return kept.ToArray();
    }

    // ---------------------------------------------------------------- vfx

    private void StepDissolve(double delta)
    {
        // The sigil crumbles: ash streams off the fading strokes and falls.
        _ashAccumulator += delta * 90.0;
        var count = (int)_ashAccumulator;
        if (count <= 0)
        {
            return;
        }

        _ashAccumulator -= count;
        for (var i = 0; i < count && _truthPoints.Length >= 2; i++)
        {
            var s = (float)_vfxRng.NextDouble() * _truthLength;
            var at = PointAlongTruth(s);
            var color = _vfxRng.NextDouble() < 0.3
                ? EmberColor(0.4f + ((float)_vfxRng.NextDouble() * 0.3f))
                : AshGrey;
            SpawnEmber(
                at + (RandomDirection() * 2f),
                new Vector2(NextSigned() * 18f, 12f + ((float)_vfxRng.NextDouble() * 30f)),
                0.6f + ((float)_vfxRng.NextDouble() * 0.7f),
                1.2f + ((float)_vfxRng.NextDouble() * 1.6f),
                color,
                1.6f,
                -26f);
        }
    }

    private Vector2 PointAlongTruth(float s)
    {
        for (var i = 1; i < _truthPoints.Length; i++)
        {
            if (_truthArclen[i] >= s)
            {
                var span = _truthArclen[i] - _truthArclen[i - 1];
                var t = span < 0.0001f ? 0f : (s - _truthArclen[i - 1]) / span;
                return _truthPoints[i - 1].Lerp(_truthPoints[i], t);
            }
        }

        return _truthPoints[^1];
    }

    private void SpawnRiseAsh()
    {
        foreach (var candidate in _candidates)
        {
            for (var i = 0; i < 10; i++)
            {
                SpawnEmber(
                    candidate.Center + new Vector2(NextSigned() * candidate.CellRadius * 0.7f, candidate.CellRadius * 0.6f),
                    new Vector2(NextSigned() * 12f, -(22f + ((float)_vfxRng.NextDouble() * 40f))),
                    0.45f + ((float)_vfxRng.NextDouble() * 0.5f),
                    1.2f + ((float)_vfxRng.NextDouble() * 1.4f),
                    _vfxRng.NextDouble() < 0.4 ? Jade : AshGrey,
                    2.2f,
                    30f);
            }
        }
    }

    private void SpawnTrueBurst(Vector2 center)
    {
        for (var i = 0; i < 30; i++)
        {
            SpawnEmber(
                center + (RandomDirection() * 4f),
                RandomDirection() * (60f + ((float)_vfxRng.NextDouble() * 150f)),
                0.4f + ((float)_vfxRng.NextDouble() * 0.6f),
                1.6f + ((float)_vfxRng.NextDouble() * 2.2f),
                EmberColor(0.7f + ((float)_vfxRng.NextDouble() * 0.3f)),
                2.4f,
                34f);
        }

        SpawnRing(center, 5f, CanvasRadius() * 1.1f, 0.5f, 0.45f, EmberGold, 2.6f);
        SpawnRing(center, 3f, CanvasRadius() * 0.5f, 0.55f, 0.3f, WhiteHot, 1.8f);
        _shake = Mathf.Max(_shake, 4f);
    }

    private void SpawnLieShatter(Candidate candidate)
    {
        foreach (var point in candidate.Points)
        {
            var away = (point - candidate.Center).Normalized();
            if (!away.IsFinite() || away == Vector2.Zero)
            {
                away = RandomDirection();
            }

            for (var i = 0; i < 3; i++)
            {
                SpawnEmber(
                    point,
                    (away * (70f + ((float)_vfxRng.NextDouble() * 140f))) + (RandomDirection() * 30f),
                    0.4f + ((float)_vfxRng.NextDouble() * 0.5f),
                    1.4f + ((float)_vfxRng.NextDouble() * 1.8f),
                    _vfxRng.NextDouble() < 0.5 ? LieRed : WildViolet,
                    2.2f,
                    10f);
            }
        }

        SpawnRing(candidate.Center, 5f, CanvasRadius() * 1.2f, 0.5f, 0.45f, WildViolet, 2.4f);
        SpawnRing(candidate.Center, 3f, CanvasRadius() * 0.6f, 0.55f, 0.3f, LieRed, 1.8f);
        _shake = Mathf.Max(_shake, 6f);
        _vignettePulse = 1f;
    }

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

        // Ambient motes drifting through the memory ring keep long waits alive.
        if (_phase is Phase.Choose or Phase.Flash && _vfxRng.NextDouble() < 0.25)
        {
            var at = FlashCenter() + (RandomDirection() * CanvasRadius() * 0.5f * (float)_vfxRng.NextDouble());
            SpawnEmber(at, RandomDirection() * 8f, 0.9f, 1.3f, EmberColor(0.35f), 1.2f, 20f);
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

    private Vector2 RandomDirection()
    {
        var angle = (float)(_vfxRng.NextDouble() * Math.Tau);
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
    }

    private float NextSigned() => ((float)_vfxRng.NextDouble() * 2f) - 1f;

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

    private Vector2 FlashCenter() => new(0f, -CanvasRadius() * 0.30f);

    private int HoveredCandidate()
    {
        if (!_cursorKnown || _phase != Phase.Choose)
        {
            return -1;
        }

        for (var i = 0; i < _candidates.Length; i++)
        {
            if (_cursor.DistanceTo(_candidates[i].Center) <= _candidates[i].CellRadius * 1.25f)
            {
                return i;
            }
        }

        return -1;
    }

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

        for (var i = 5; i >= 1; i--)
        {
            layer.DrawCircle(
                center + FlashCenter(),
                radius * 0.32f * i,
                new Color(0.11f, 0.07f, 0.20f, 0.045f));
        }

        // Believing a lie stings the whole frame for a breath.
        if (_vignettePulse > 0.01f)
        {
            layer.DrawRect(
                new Rect2(Vector2.Zero, Size),
                new Color(0.35f, 0.05f, 0.10f, 0.16f * _vignettePulse));
        }
    }

    private void DrawGlow(DrawLayer layer)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        var center = CanvasCenter() + _shakeOffset;

        DrawMemoryRing(layer, center);
        DrawTruthSigil(layer, center);
        DrawCandidates(layer, center);
        DrawVerdictBeads(layer, center);

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

    /// <summary>
    /// The memory ring frames the flash: rotating dashes, a countdown arc while the truth
    /// shows, and the response arc draining while the player chooses.
    /// </summary>
    private void DrawMemoryRing(DrawLayer layer, Vector2 center)
    {
        var ringCenter = center + FlashCenter();
        var radius = CanvasRadius() * 0.55f;
        var breathe = 0.06f + (0.03f * Mathf.Sin((float)_time * 1.3f));

        layer.DrawArc(ringCenter, radius, 0f, Mathf.Tau, 96, new Color(EmberOrange, breathe), 1.5f, antialiased: true);

        var spin = (float)_time * 0.22f;
        const int dashes = 14;
        for (var i = 0; i < dashes; i++)
        {
            var start = spin + (Mathf.Tau * i / dashes);
            layer.DrawArc(
                ringCenter,
                radius * 0.93f,
                start,
                start + (Mathf.Tau / dashes * 0.5f),
                10,
                new Color(WildViolet, breathe * 1.7f),
                1.2f,
                antialiased: true);
        }

        if (_phase == Phase.Flash)
        {
            // Honest clock: how long the truth will keep holding still.
            var remaining = 1f - Mathf.Clamp((float)(_phaseClock / _flashSeconds), 0f, 1f);
            layer.DrawArc(
                ringCenter,
                radius * 1.06f,
                -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * remaining),
                72,
                new Color(EmberGold, 0.5f),
                2.4f,
                antialiased: true);
        }
        else if (_phase == Phase.Choose)
        {
            // The soft timer: slow answers are safe answers, just weaker ones.
            var left = 1f - Mathf.Clamp((float)(_responseClock / SoftTimerSeconds), 0f, 1f);
            var color = Jade.Lerp(UiTheme.Warning, 1f - left);
            layer.DrawArc(
                ringCenter,
                radius * 1.06f,
                -Mathf.Pi / 2f,
                (-Mathf.Pi / 2f) + (Mathf.Tau * Mathf.Max(left, 0.02f)),
                72,
                new Color(color, 0.45f),
                2.4f,
                antialiased: true);
        }
    }

    private void DrawTruthSigil(DrawLayer layer, Vector2 center)
    {
        if (_truthPoints.Length < 2 || _phase is not (Phase.Flash or Phase.Dissolve))
        {
            return;
        }

        if (_phase == Phase.Flash)
        {
            // Burn-in fast, then blaze and hold still, shimmering like held breath.
            var burnIn = Mathf.Clamp((float)(_phaseClock / 0.3), 0f, 1f);
            var eased = 1f - Mathf.Pow(1f - burnIn, 3f);
            var points = PartialPolyline(_truthPoints, _truthArclen, _truthLength, eased);
            var offset = new Vector2[points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                offset[i] = center + points[i];
            }

            if (offset.Length >= 2)
            {
                var shimmer = 0.85f + (0.15f * Mathf.Sin((float)_time * 5f));
                layer.DrawPolyline(offset, new Color(EmberGold, 0.12f * shimmer), CanvasRadius() * 0.075f, antialiased: true);
                layer.DrawPolyline(offset, new Color(EmberGold, 0.35f * shimmer), CanvasRadius() * 0.030f, antialiased: true);
                layer.DrawPolyline(offset, new Color(WhiteHot, 0.95f * shimmer), CanvasRadius() * 0.012f, antialiased: true);
            }

            if (burnIn < 1f && offset.Length >= 1)
            {
                var head = offset[^1];
                var pulse = 1f + (0.3f * Mathf.Sin((float)_time * 31f));
                layer.DrawCircle(head, CanvasRadius() * 0.028f * pulse, new Color(WhiteHot, 0.9f));
            }

            return;
        }

        // Dissolve: the strokes sink and cool while ash streams off them.
        var t = Mathf.Clamp((float)(_phaseClock / DissolveSeconds), 0f, 1f);
        var fading = new Vector2[_truthPoints.Length];
        for (var i = 0; i < _truthPoints.Length; i++)
        {
            fading[i] = center + _truthPoints[i] + new Vector2(0f, t * 7f);
        }

        var alpha = 1f - t;
        layer.DrawPolyline(fading, new Color(AshGrey, 0.5f * alpha), 3.2f, antialiased: true);
        layer.DrawPolyline(fading, new Color(EmberOrange, 0.35f * alpha * alpha), 1.4f, antialiased: true);
    }

    private void DrawCandidates(DrawLayer layer, Vector2 center)
    {
        if (_phase is not (Phase.Rise or Phase.Choose or Phase.Reveal) || _candidates.Length == 0)
        {
            return;
        }

        var rise = _phase == Phase.Rise
            ? 1f - Mathf.Pow(1f - Mathf.Clamp((float)(_phaseClock / RiseSeconds), 0f, 1f), 3f)
            : 1f;
        var hovered = HoveredCandidate();
        var revealT = _phase == Phase.Reveal
            ? Mathf.Clamp((float)(_phaseClock / (_revealCorrect ? RevealTrueSeconds : RevealFalseSeconds)), 0f, 1f)
            : 0f;

        for (var index = 0; index < _candidates.Length; index++)
        {
            var candidate = _candidates[index];
            var bob = Mathf.Sin(((float)_time * 1.3f) + candidate.BobPhase) * 3f;
            var lift = (1f - rise) * 46f;
            var shift = new Vector2(0f, bob + lift);
            var ghostAlpha = rise;

            var isChosen = _phase == Phase.Reveal && index == _chosenIndex;
            var isTrueReveal = _phase == Phase.Reveal && candidate.IsTrue;
            if (_phase == Phase.Reveal && !isChosen && !isTrueReveal)
            {
                ghostAlpha *= 1f - revealT;
            }

            // Pedestal ring.
            var pedestal = center + candidate.Center + shift;
            var frameColor = _phase == Phase.Choose && index == hovered ? EmberGold : Jade;
            var frameAlpha = (_phase == Phase.Choose && index == hovered ? 0.5f : 0.2f) * ghostAlpha;
            layer.DrawArc(pedestal, candidate.CellRadius * 1.18f, 0f, Mathf.Tau, 56, new Color(frameColor, frameAlpha), 1.6f, antialiased: true);
            if (_phase == Phase.Choose && index == hovered)
            {
                var spin = (float)_time * 2.6f;
                for (var d = 0; d < 2; d++)
                {
                    var start = spin + (d * Mathf.Pi);
                    layer.DrawArc(pedestal, candidate.CellRadius * 1.28f, start, start + 0.8f, 14, new Color(EmberGold, 0.6f), 2f, antialiased: true);
                }
            }

            // The ghost sigil itself.
            var points = new Vector2[candidate.Points.Length];
            for (var i = 0; i < points.Length; i++)
            {
                points[i] = center + candidate.Points[i] + shift;
            }

            if (points.Length < 2)
            {
                continue;
            }

            if (isChosen && _revealCorrect)
            {
                // The truth ignites: burn along the path, gold to white.
                var burn = Mathf.Clamp(revealT / 0.4f, 0f, 1f);
                var lit = (int)Mathf.Lerp(2, points.Length, burn);
                var blaze = new Vector2[lit];
                Array.Copy(points, blaze, lit);
                layer.DrawPolyline(points, new Color(EmberGold, 0.25f), candidate.CellRadius * 0.16f, antialiased: true);
                layer.DrawPolyline(blaze, new Color(EmberGold, 0.5f), candidate.CellRadius * 0.09f, antialiased: true);
                layer.DrawPolyline(blaze, new Color(WhiteHot, 0.95f), candidate.CellRadius * 0.035f, antialiased: true);
            }
            else if (isChosen)
            {
                // The lie cracks apart: jitter grows as it fails.
                var crack = revealT * 6f;
                for (var i = 0; i < points.Length; i++)
                {
                    points[i] += new Vector2(NextSigned() * crack, NextSigned() * crack);
                }

                var dying = 1f - revealT;
                layer.DrawPolyline(points, new Color(LieRed, 0.6f * dying), 2.6f, antialiased: true);
                layer.DrawPolyline(points, new Color(WildViolet, 0.4f * dying), 5f, antialiased: true);
            }
            else if (isTrueReveal && !_revealCorrect)
            {
                // The truth the player missed glimmers gold twice: the teaching moment.
                var flare = 0.4f + (0.6f * Mathf.Abs(Mathf.Sin(revealT * Mathf.Pi * 2f)));
                layer.DrawPolyline(points, new Color(EmberGold, 0.30f * flare), candidate.CellRadius * 0.10f, antialiased: true);
                layer.DrawPolyline(points, new Color(EmberGold, 0.85f * flare), 1.8f, antialiased: true);
            }
            else
            {
                var hover = _phase == Phase.Choose && index == hovered;
                var tint = hover ? Jade.Lerp(WhiteHot, 0.35f) : Jade;
                var alpha = hover ? 0.95f : 0.62f;
                var shimmer = 0.85f + (0.15f * Mathf.Sin(((float)_time * 2.4f) + candidate.BobPhase));
                layer.DrawPolyline(points, new Color(tint, 0.14f * ghostAlpha * shimmer), candidate.CellRadius * 0.13f, antialiased: true);
                layer.DrawPolyline(points, new Color(tint, alpha * ghostAlpha * shimmer), 1.7f, antialiased: true);
            }
        }
    }

    /// <summary>The run's verdicts, worn as beads above the ring: gold truths, violet lies.</summary>
    private void DrawVerdictBeads(DrawLayer layer, Vector2 center)
    {
        if (_verdicts.Count == 0)
        {
            return;
        }

        var radius = CanvasRadius();
        var origin = center + new Vector2(-(_verdicts.Count - 1) * 11f, -radius * 1.05f);
        for (var i = 0; i < _verdicts.Count; i++)
        {
            var at = origin + new Vector2(i * 22f, Mathf.Sin(((float)_time * 1.4f) + i) * 1.5f);
            if (_verdicts[i])
            {
                layer.DrawCircle(at, 4f, new Color(EmberGold, 0.85f));
                layer.DrawCircle(at, 7.5f, new Color(EmberGold, 0.18f));
            }
            else
            {
                layer.DrawCircle(at, 3.2f, new Color(WildViolet, 0.7f));
                layer.DrawCircle(at, 6f, new Color(WildViolet, 0.15f));
            }
        }
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
            Text = "One sigil is true. Watch it burn, then find it among the liars — sure answers feed control, swift ones feed power.",
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
            Phase.Flash => "Remember its true shape…",
            Phase.Dissolve => "It crumbles—",
            Phase.Rise => "The ashes stir…",
            Phase.Choose => "Which was true?",
            Phase.Reveal when _revealCorrect => "True.",
            Phase.Reveal => "A lie took its place.",
            Phase.Finale => _everAnswered ? "The true shapes feed the spell…" : "The spell resolves, unshaped.",
            _ => "",
        };
        _subtitle.Text = $"“{_spellText}”";
        var truths = _correct == 1 ? "1 truth" : $"{_correct} truths";
        var lies = _rounds - _correct;
        _tally.Text = lies == 0 ? truths : $"{truths} · {lies} lies believed";

        // Bars sit in score space: 0.5 is the neutral center, matching TrueSigilScoring.
        var liveSpeed = _rounds > 0 && _responseSecondsTotal > 0.001
            ? Mathf.Clamp(
                (float)((ParAnswerSeconds / (_responseSecondsTotal / _rounds))
                    / TrueSigilScoring.SpeedCeilingRatio),
                0f,
                1f)
            : 0.5f;
        var liveAccuracy = _rounds > 0
            ? Mathf.Clamp(
                (float)((((double)_correct / _rounds) - TrueSigilScoring.AccuracyFloor)
                    / (TrueSigilScoring.AccuracyCeiling - TrueSigilScoring.AccuracyFloor)),
                0f,
                1f)
            : 0.5f;
        _powerBar.Value = liveSpeed;
        _controlBar.Value = liveAccuracy;
        _hint.Visible = !_everAnswered;
    }
}
