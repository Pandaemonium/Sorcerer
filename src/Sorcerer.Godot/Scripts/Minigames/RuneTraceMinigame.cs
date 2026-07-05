using Godot;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// The rune-trace casting minigame: while the provider resolves a wild spell, runes burn into
/// the screen and the player traces them - fast tracing feeds power, faithful tracing feeds
/// control, and sloppy work feeds wildness. Runes chain indefinitely until the provider
/// returns (finishing the rune in hand gets a short grace), so unknown latency is the game's
/// natural clock. Skipping, or never touching a rune, is a neutral cast, never a punishment.
///
/// The overlay owns presentation and raw gesture math only; it reduces the session to
/// <see cref="RuneTraceMetrics"/> and lets <see cref="RuneTraceScoring"/> (core) decide the
/// <see cref="CastPerformance"/>, so calibration stays engine-owned and testable.
/// </summary>
public partial class RuneTraceMinigame : Control
{
    private enum Phase
    {
        Idle,
        FadeIn,
        BurnIn,
        Trace,
        Sealed,
        Finale,
    }

    private const float FadeInSeconds = 0.18f;
    private const float BurnSeconds = 0.72f;
    private const float SealSeconds = 0.52f;
    private const float FinaleSeconds = 0.45f;
    private const float GraceSeconds = 2.0f;
    private const float DenseSpacing = 6f;
    private const float ReactionPadSeconds = 0.45f;
    private const int MaxEmbers = 720;

    // Burn mark: the visible accuracy read. Scorch radius scales with off-path distance, so a
    // faithful trace lays a tight hairline sear and a sloppy one spreads a fat ugly burn.
    private const float BurnSpacing = 6.5f;
    private const float BurnMinScale = 0.16f;  // × tolerance, when dead-on the rune
    private const float BurnMaxScale = 1.25f;  // × tolerance, at the edge of the trace corridor
    private const float BurnCoolSeconds = 0.9f;
    private const int MaxBurns = 640;

    private static readonly Color DeepEmber = new(0.55f, 0.16f, 0.09f);
    private static readonly Color EmberOrange = new(1.0f, 0.47f, 0.16f);
    private static readonly Color EmberGold = new(1.0f, 0.78f, 0.40f);
    private static readonly Color WhiteHot = new(1.0f, 0.96f, 0.86f);
    private static readonly Color Jade = UiTheme.Wild;
    private static readonly Color JadeDeep = new(0.20f, 0.52f, 0.42f);
    private static readonly Color WildViolet = new(0.69f, 0.48f, 1.0f);
    private static readonly Color WarmAsh = new(0.22f, 0.13f, 0.08f);
    private static readonly Color ColdChar = new(0.14f, 0.09f, 0.14f);

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

    private struct BurnStamp
    {
        public Vector2 Pos;
        public float Radius;
        public float Heat;
        public float Offset01;
        public float Age;
    }

    // Session state.
    private TaskCompletionSource<CastPerformance>? _completion;
    private Func<bool>? _providerSettled;
    private string _spellText = "";
    private int _runeIndex;
    private int _sealedCount;
    private bool _providerReady;
    private bool _everTraced;
    private bool _skipped;
    private Phase _phase = Phase.Idle;
    private double _phaseClock;
    private double _graceClock;

    // Scoring totals (rate-based; see RuneTraceScoring).
    private double _activeSeconds;
    private double _accuracyWeighted;
    private double _tracedLength;
    private double _parSecondsEarned;

    // Current rune, densely resampled, in pixels relative to the canvas center.
    private Vector2[] _points = Array.Empty<Vector2>();
    private float[] _arclen = Array.Empty<float>();
    private float _runeLength;
    private float _parSeconds;
    private float _tolerance = 24f;
    private float _captureRadius = 36f;
    private float _lookahead = 130f;
    private float _burnHead;
    private float _traceHead;
    private bool _engaged;
    private Vector2 _cursor;
    private float _recentQuality = 0.7f;
    private readonly List<Vector2> _cursorTrail = new();
    private double _sputterAccumulator;
    private readonly List<BurnStamp> _burns = new(MaxBurns);
    private float _lastBurnS;

    // VFX state.
    private readonly List<Ember> _embers = new(MaxEmbers);
    private readonly List<Ring> _rings = new();
    private readonly Random _vfxRng = new();
    private float _shake;
    private Vector2 _shakeOffset;
    private double _beaconClock;
    private Vector2[] _ashPoints = Array.Empty<Vector2>();
    private float _ashAlpha;
    private double _time;

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
    /// Runs the minigame until the provider settles (plus a short grace to finish the rune in
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
        _runeIndex = 0;
        _sealedCount = 0;
        _providerReady = false;
        _everTraced = false;
        _skipped = false;
        _activeSeconds = 0;
        _accuracyWeighted = 0;
        _tracedLength = 0;
        _parSecondsEarned = 0;
        _embers.Clear();
        _rings.Clear();
        _burns.Clear();
        _lastBurnS = 0f;
        _ashPoints = Array.Empty<Vector2>();
        _ashAlpha = 0f;
        _shake = 0f;
        _recentQuality = 0.7f;
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
                    StartRune();
                }

                break;

            case Phase.BurnIn:
                StepBurnIn(delta);
                break;

            case Phase.Trace:
                StepTrace(delta);
                break;

            case Phase.Sealed:
                if (_phaseClock >= SealSeconds)
                {
                    if (_providerReady)
                    {
                        EnterFinale();
                    }
                    else
                    {
                        StartRune();
                    }
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

        if (@event is InputEventMouseButton button && button.ButtonIndex == MouseButton.Left)
        {
            _cursor = button.Position - CanvasCenter();
            if (button.Pressed)
            {
                TryEngage();
            }
            else
            {
                _engaged = false;
            }

            AcceptEvent();
            return;
        }

        if (@event is InputEventMouseMotion motion)
        {
            _cursor = motion.Position - CanvasCenter();
            if (_engaged)
            {
                _cursorTrail.Add(_cursor);
                if (_cursorTrail.Count > 9)
                {
                    _cursorTrail.RemoveAt(0);
                }
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
            : RuneTraceScoring.ToPerformance(new RuneTraceMetrics(
                Traced: _everTraced,
                ActiveSeconds: _activeSeconds,
                Accuracy01: _tracedLength > 0.001 ? _accuracyWeighted / _tracedLength : 0,
                SpeedRatio: _activeSeconds > 0.001 ? _parSecondsEarned / _activeSeconds : 0));

        _phase = Phase.Idle;
        Visible = false;
        SetProcess(false);
        _engaged = false;
        _providerSettled = null;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(performance);
    }

    // ---------------------------------------------------------------- rune lifecycle

    private void StartRune()
    {
        var radius = CanvasRadius();
        _tolerance = radius * 0.105f;
        _captureRadius = _tolerance * 1.6f;
        _lookahead = radius * 0.55f;

        var shape = RuneShape.Generate(RuneShape.SeedFor(_spellText, _runeIndex));
        _runeIndex++;
        Densify(shape, radius);
        _parSeconds = (_runeLength / (radius * 2.1f)) + ReactionPadSeconds;
        _burnHead = 0f;
        _traceHead = 0f;
        _lastBurnS = 0f;
        _burns.Clear();
        _engaged = false;
        _cursorTrail.Clear();
        _beaconClock = 0.6;
        _phase = Phase.BurnIn;
        _phaseClock = 0;
        _graceClock = 0;
    }

    private void Densify(RuneShape shape, float radius)
    {
        var scaled = new Vector2[shape.Points.Length];
        for (var i = 0; i < shape.Points.Length; i++)
        {
            scaled[i] = new Vector2(shape.Points[i].X * radius * 0.94f, shape.Points[i].Y * radius);
        }

        var points = new List<Vector2> { scaled[0] };
        var arclen = new List<float> { 0f };
        var total = 0f;
        for (var i = 1; i < scaled.Length; i++)
        {
            var from = scaled[i - 1];
            var to = scaled[i];
            var length = from.DistanceTo(to);
            var steps = Mathf.Max(1, Mathf.CeilToInt(length / DenseSpacing));
            for (var step = 1; step <= steps; step++)
            {
                var next = from.Lerp(to, (float)step / steps);
                total += points[^1].DistanceTo(next);
                points.Add(next);
                arclen.Add(total);
            }
        }

        _points = points.ToArray();
        _arclen = arclen.ToArray();
        _runeLength = total;
    }

    private void StepBurnIn(double delta)
    {
        if (_providerReady)
        {
            ExitEarly();
            return;
        }

        var t = Mathf.Clamp((float)(_phaseClock / BurnSeconds), 0f, 1f);
        var eased = 1f - Mathf.Pow(1f - t, 3f);
        var previousHead = _burnHead;
        _burnHead = eased * _runeLength;

        // The head crackles: embers stream off the newly seared path.
        var headPos = PointAt(_burnHead);
        var advance = _burnHead - previousHead;
        SpawnBurnEmbers(headPos, advance);
        _shake = Mathf.Max(_shake, 1.6f);

        if (t >= 1f)
        {
            _phase = Phase.Trace;
            _phaseClock = 0;
        }
    }

    private void StepTrace(double delta)
    {
        _activeSeconds += delta;
        _burnHead = _runeLength;

        if (_engaged)
        {
            AdvanceTrace(delta);
        }
        else
        {
            _beaconClock += delta;
            if (_beaconClock >= 1.0)
            {
                _beaconClock = 0;
                SpawnBeacon(PointAt(_traceHead));
            }
        }

        if (_traceHead >= _runeLength - (_tolerance * 0.5f))
        {
            SealRune();
            return;
        }

        if (_providerReady)
        {
            // A rune in progress gets a short grace to finish; an untouched rune ends now.
            _graceClock += delta;
            if (_traceHead <= 0.5f || _graceClock >= GraceSeconds)
            {
                ExitEarly();
            }
        }
    }

    private void SealRune()
    {
        _sealedCount++;
        _ashPoints = _points;
        _ashAlpha = 0.5f;
        SpawnSealBurst();
        _shake = Mathf.Max(_shake, 4.5f);
        _phase = Phase.Sealed;
        _phaseClock = 0;
    }

    private void ExitEarly()
    {
        if (_everTraced)
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

    // ---------------------------------------------------------------- tracing

    private void TryEngage()
    {
        if (_phase != Phase.Trace)
        {
            return;
        }

        var head = PointAt(_traceHead);
        if (_cursor.DistanceTo(head) <= _captureRadius)
        {
            _engaged = true;
            _everTraced = true;
            _cursorTrail.Clear();
            SpawnRing(head, 4f, CanvasRadius() * 0.7f, 0.30f, 0.55f, Jade, 2f);
        }
        else
        {
            SpawnSputter(_cursor, 6);
        }
    }

    private void AdvanceTrace(double delta)
    {
        var (bestS, bestD) = ProjectOnPath(_cursor, _traceHead, _traceHead + _lookahead);
        if (bestS > _traceHead && bestD <= _tolerance * 1.35f)
        {
            var ds = bestS - _traceHead;
            var quality = 1f - Mathf.Clamp(bestD / _tolerance, 0f, 1f);
            _accuracyWeighted += quality * ds;
            _tracedLength += ds;
            _parSecondsEarned += (ds / _runeLength) * _parSeconds;
            _traceHead = bestS;
            _recentQuality = Mathf.Lerp(_recentQuality, quality, 0.25f);
            StampBurn(bestD);
            SpawnTraceSparks(PointAt(_traceHead), ds, quality);
        }
        else if (bestD > _tolerance * 1.6f)
        {
            _recentQuality = Mathf.Lerp(_recentQuality, 0f, 0.08f);
            _sputterAccumulator += delta * 26.0;
            var count = (int)_sputterAccumulator;
            if (count > 0)
            {
                _sputterAccumulator -= count;
                SpawnSputter(_cursor, count);
            }
        }
    }

    /// <summary>
    /// Deposits scorch along the path the trace just covered. Radius scales with the player's
    /// off-path distance, so the burn's width reads as the accuracy signal: a faithful trace
    /// leaves a tight sear hugging the rune, a wandering one leaves a fat spreading burn.
    /// Stamps at a fixed arclength cadence via <see cref="_lastBurnS"/> so density stays even
    /// regardless of frame rate or trace speed.
    /// </summary>
    private void StampBurn(float offset)
    {
        var offset01 = Mathf.Clamp(offset / _tolerance, 0f, 1f);
        var scale = Mathf.Lerp(BurnMinScale, BurnMaxScale, offset01);
        while (_lastBurnS + BurnSpacing <= _traceHead)
        {
            _lastBurnS += BurnSpacing;
            _burns.Add(new BurnStamp
            {
                Pos = PointAt(_lastBurnS),
                Radius = _tolerance * scale * (0.85f + ((float)_vfxRng.NextDouble() * 0.3f)),
                Heat = 1f - (0.55f * offset01),
                Offset01 = offset01,
                Age = 0f,
            });
        }

        if (_burns.Count > MaxBurns)
        {
            _burns.RemoveRange(0, _burns.Count - MaxBurns);
        }
    }

    /// <summary>
    /// Projects a point onto the dense polyline within an arclength window, returning the
    /// best (arclength, distance). The window keeps self-crossing runes honest: the trace can
    /// only advance along the path, never jump to a later crossing stroke.
    /// </summary>
    private (float S, float D) ProjectOnPath(Vector2 point, float fromS, float toS)
    {
        var bestS = -1f;
        var bestD = float.MaxValue;
        var start = IndexAt(Mathf.Max(fromS - DenseSpacing, 0f));
        for (var i = start; i < _points.Length - 1 && _arclen[i] <= toS; i++)
        {
            var a = _points[i];
            var b = _points[i + 1];
            var ab = b - a;
            var lengthSquared = ab.LengthSquared();
            var t = lengthSquared < 0.0001f
                ? 0f
                : Mathf.Clamp((point - a).Dot(ab) / lengthSquared, 0f, 1f);
            var candidate = a + (ab * t);
            var s = _arclen[i] + (ab.Length() * t);
            var d = point.DistanceTo(candidate);
            if (s >= fromS && d < bestD)
            {
                bestD = d;
                bestS = s;
            }
        }

        return (bestS, bestD);
    }

    private int IndexAt(float s)
    {
        var low = 0;
        var high = _arclen.Length - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (_arclen[mid] < s)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return Mathf.Max(0, low - 1);
    }

    private Vector2 PointAt(float s)
    {
        if (_points.Length == 0)
        {
            return Vector2.Zero;
        }

        var i = IndexAt(s);
        if (i >= _points.Length - 1)
        {
            return _points[^1];
        }

        var span = _arclen[i + 1] - _arclen[i];
        var t = span < 0.0001f ? 0f : Mathf.Clamp((s - _arclen[i]) / span, 0f, 1f);
        return _points[i].Lerp(_points[i + 1], t);
    }

    // ---------------------------------------------------------------- vfx simulation

    private void StepVfx(double delta)
    {
        var dt = (float)delta;
        _shake *= Mathf.Exp(-6f * dt);
        _shakeOffset = _shake <= 0.05f
            ? Vector2.Zero
            : new Vector2(NextSigned() * _shake, NextSigned() * _shake);

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

        _ashAlpha = Mathf.Max(0f, _ashAlpha - (0.55f * dt));

        for (var i = 0; i < _burns.Count; i++)
        {
            var burn = _burns[i];
            burn.Age += dt;
            _burns[i] = burn;
        }

        // Ambient motes drifting off the smoldering rune keep long waits alive.
        if (_phase is Phase.Trace && _vfxRng.NextDouble() < 0.35)
        {
            var s = (float)_vfxRng.NextDouble() * _runeLength;
            SpawnEmber(PointAt(s), RandomDirection() * 12f, 0.9f, 1.6f, EmberColor(0.35f), 1.2f, 26f);
        }
    }

    private void SpawnBurnEmbers(Vector2 position, float pathAdvance)
    {
        var count = Mathf.Clamp(Mathf.CeilToInt(pathAdvance / 6f), 1, 8);
        for (var i = 0; i < count; i++)
        {
            var heat = (float)_vfxRng.NextDouble();
            SpawnEmber(
                position + (RandomDirection() * (float)_vfxRng.NextDouble() * 5f),
                RandomDirection() * (25f + ((float)_vfxRng.NextDouble() * 70f)),
                0.45f + ((float)_vfxRng.NextDouble() * 0.7f),
                1.6f + ((float)_vfxRng.NextDouble() * 2.2f),
                EmberColor(0.55f + (heat * 0.45f)),
                2.6f,
                42f);
        }
    }

    private void SpawnTraceSparks(Vector2 position, float pathAdvance, float quality)
    {
        var count = Mathf.Clamp(Mathf.CeilToInt(pathAdvance / 9f), 1, 5);
        for (var i = 0; i < count; i++)
        {
            var color = quality > 0.55f
                ? Jade.Lerp(WhiteHot, (float)_vfxRng.NextDouble() * 0.5f)
                : Jade.Lerp(WildViolet, 0.6f);
            SpawnEmber(
                position + (RandomDirection() * 3f),
                RandomDirection() * (18f + ((float)_vfxRng.NextDouble() * 55f)),
                0.35f + ((float)_vfxRng.NextDouble() * 0.5f),
                1.4f + ((float)_vfxRng.NextDouble() * 1.8f),
                color,
                3.0f,
                20f);
        }
    }

    private void SpawnSputter(Vector2 position, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var color = _vfxRng.NextDouble() < 0.5 ? UiTheme.Danger : WildViolet;
            SpawnEmber(
                position + (RandomDirection() * 4f),
                RandomDirection() * (40f + ((float)_vfxRng.NextDouble() * 90f)),
                0.25f + ((float)_vfxRng.NextDouble() * 0.4f),
                1.2f + ((float)_vfxRng.NextDouble() * 1.6f),
                color,
                3.4f,
                -8f);
        }
    }

    private void SpawnSealBurst()
    {
        for (var s = 0f; s < _runeLength; s += 24f)
        {
            var origin = PointAt(s);
            SpawnEmber(
                origin,
                RandomDirection() * (70f + ((float)_vfxRng.NextDouble() * 160f)),
                0.5f + ((float)_vfxRng.NextDouble() * 0.6f),
                1.8f + ((float)_vfxRng.NextDouble() * 2.4f),
                EmberColor(0.75f + ((float)_vfxRng.NextDouble() * 0.25f)),
                2.0f,
                30f);
        }

        SpawnRing(Vector2.Zero, CanvasRadius() * 0.25f, CanvasRadius() * 1.9f, 0.45f, 0.4f, EmberGold, 3f);
    }

    private void SpawnBeacon(Vector2 position) =>
        SpawnRing(position, 3f, CanvasRadius() * 0.55f, 0.35f, 0.9f, Jade, 1.6f);

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

        // A soft indigo pool of light behind the working keeps the rune readable.
        for (var i = 5; i >= 1; i--)
        {
            layer.DrawCircle(
                center,
                radius * 0.36f * i,
                new Color(0.11f, 0.07f, 0.20f, 0.045f));
        }

        DrawBurnChar(layer, center);

        if (_ashAlpha > 0.01f && _ashPoints.Length >= 2)
        {
            var ash = new Vector2[_ashPoints.Length];
            for (var i = 0; i < _ashPoints.Length; i++)
            {
                ash[i] = center + _ashPoints[i];
            }

            layer.DrawPolyline(ash, new Color(0.42f, 0.40f, 0.45f, _ashAlpha * 0.5f), 2.5f, antialiased: true);
        }
    }

    private void DrawGlow(DrawLayer layer)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        var center = CanvasCenter() + _shakeOffset;
        var radius = CanvasRadius();

        DrawMagicCircle(layer, center, radius);
        DrawBurnSmolder(layer, center);
        DrawRuneStrokes(layer, center, radius);
        DrawTraceOverlay(layer, center, radius);
        DrawCursor(layer, center);

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

        if (_phase == Phase.Sealed && _phaseClock < 0.14 && _points.Length >= 2)
        {
            var flash = new Vector2[_points.Length];
            for (var i = 0; i < _points.Length; i++)
            {
                flash[i] = center + _points[i];
            }

            var alpha = 1f - (float)(_phaseClock / 0.14);
            layer.DrawPolyline(flash, new Color(WhiteHot, alpha), radius * 0.02f, antialiased: true);
        }
    }

    // Burns fade out as their rune seals so the next sigil starts on a clean field.
    private float BurnLayerFade() => _phase switch
    {
        Phase.Sealed => Mathf.Clamp(1f - (float)(_phaseClock / SealSeconds), 0f, 1f),
        Phase.Finale => 0f,
        _ => 1f,
    };

    private void DrawBurnChar(DrawLayer layer, Vector2 center)
    {
        var fade = BurnLayerFade();
        if (fade <= 0.01f || _burns.Count == 0)
        {
            return;
        }

        // Lingering ashy scorch, a touch warmer than the void so the size read survives after
        // the glow cools. Warm char when tight, sickly violet-grey when the trace ran wide.
        foreach (var burn in _burns)
        {
            var ash = WarmAsh.Lerp(ColdChar, burn.Offset01);
            layer.DrawCircle(center + burn.Pos, burn.Radius * 1.4f, new Color(ash, 0.17f * fade));
            layer.DrawCircle(center + burn.Pos, burn.Radius * 0.75f, new Color(ash, 0.20f * fade));
        }
    }

    private void DrawBurnSmolder(DrawLayer layer, Vector2 center)
    {
        var fade = BurnLayerFade();
        if (fade <= 0.01f || _burns.Count == 0)
        {
            return;
        }

        // Live smoldering heat, drawn additive beneath the rune so it spills visibly to the
        // sides only when the trace strays. Color reinforces size: clean gold when tight,
        // angry red-violet when wide.
        foreach (var burn in _burns)
        {
            var cool = Mathf.Clamp(1f - (burn.Age / BurnCoolSeconds), 0f, 1f);
            if (cool <= 0.01f)
            {
                continue;
            }

            var color = EmberColor(0.85f - (burn.Offset01 * 0.55f));
            if (burn.Offset01 > 0.6f)
            {
                color = color.Lerp(WildViolet, (burn.Offset01 - 0.6f) * 0.7f);
            }

            var smolder = cool * burn.Heat * fade;
            layer.DrawCircle(center + burn.Pos, burn.Radius, new Color(color, 0.22f * smolder));
            layer.DrawCircle(center + burn.Pos, burn.Radius * 0.5f, new Color(color.Lerp(WhiteHot, 0.4f), 0.32f * smolder));
        }
    }

    private void DrawMagicCircle(DrawLayer layer, Vector2 center, float radius)
    {
        var breathe = 0.05f + (0.03f * Mathf.Sin((float)_time * 1.3f));
        var outer = radius * 1.24f;
        var inner = radius * 1.10f;
        layer.DrawArc(center, outer, 0f, Mathf.Tau, 96, new Color(EmberOrange, breathe), 1.5f, antialiased: true);

        var spin = (float)_time * 0.18f;
        for (var i = 0; i < 16; i++)
        {
            var angle = spin + (Mathf.Tau * i / 16f);
            var direction = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            layer.DrawLine(
                center + (direction * (outer - 4f)),
                center + (direction * (outer + 4f)),
                new Color(EmberGold, breathe * 2.2f),
                1.5f,
                antialiased: true);
        }

        var counterSpin = (float)_time * -0.12f;
        const int dashes = 12;
        for (var i = 0; i < dashes; i++)
        {
            var start = counterSpin + (Mathf.Tau * i / dashes);
            layer.DrawArc(
                center,
                inner,
                start,
                start + (Mathf.Tau / dashes * 0.55f),
                12,
                new Color(WildViolet, breathe * 1.6f),
                1.2f,
                antialiased: true);
        }
    }

    private void DrawRuneStrokes(DrawLayer layer, Vector2 center, float radius)
    {
        if (_points.Length < 2 || _burnHead <= DenseSpacing)
        {
            return;
        }

        var headIndex = Mathf.Min(IndexAt(_burnHead) + 1, _points.Length - 1);
        if (headIndex < 1)
        {
            return;
        }

        var visible = new Vector2[headIndex + 1];
        var colors = new Color[headIndex + 1];
        var coolLength = radius * 1.3f;
        var smolder = _phase == Phase.Trace
            ? 0.82f + (0.18f * Mathf.Sin(((float)_time * 2.2f) + 1f))
            : 1f;
        for (var i = 0; i <= headIndex; i++)
        {
            visible[i] = center + _points[i];
            var heat = Mathf.Clamp(1f - ((_burnHead - _arclen[i]) / coolLength), 0.12f, 1f);
            colors[i] = new Color(EmberColor(heat), 0.85f * smolder);
        }

        // Layered widths fake bloom: wide faint halo, tight bright core.
        DrawPolylinePass(layer, visible, colors, radius * 0.075f, 0.10f);
        DrawPolylinePass(layer, visible, colors, radius * 0.032f, 0.30f);
        DrawPolylinePass(layer, visible, colors, radius * 0.014f, 0.95f);

        if (_phase == Phase.BurnIn)
        {
            var head = center + PointAt(_burnHead);
            var pulse = 1f + (0.3f * Mathf.Sin((float)_time * 31f));
            layer.DrawCircle(head, radius * 0.030f * pulse, new Color(WhiteHot, 0.9f));
            layer.DrawCircle(head, radius * 0.065f * pulse, new Color(EmberGold, 0.30f));
        }
    }

    private void DrawTraceOverlay(DrawLayer layer, Vector2 center, float radius)
    {
        if (_phase is not (Phase.Trace or Phase.Sealed) || _points.Length < 2)
        {
            return;
        }

        if (_traceHead > DenseSpacing)
        {
            var headIndex = Mathf.Min(IndexAt(_traceHead) + 1, _points.Length - 1);
            var traced = new Vector2[headIndex + 1];
            for (var i = 0; i <= headIndex; i++)
            {
                traced[i] = center + _points[i];
            }

            var jade = Jade.Lerp(JadeDeep, 1f - _recentQuality);
            layer.DrawPolyline(traced, new Color(jade, 0.16f), radius * 0.055f, antialiased: true);
            layer.DrawPolyline(traced, new Color(jade, 0.55f), radius * 0.022f, antialiased: true);
            layer.DrawPolyline(traced, new Color(Jade.Lerp(WhiteHot, 0.35f), 0.9f), radius * 0.009f, antialiased: true);
        }

        if (_phase == Phase.Trace)
        {
            // The next node to engage: an inviting beacon while free, a bright bead while tracing.
            var head = center + PointAt(_traceHead);
            var pulse = 1f + (0.25f * Mathf.Sin((float)_time * 6f));
            if (!_engaged)
            {
                layer.DrawCircle(head, _captureRadius * 0.42f * pulse, new Color(Jade, 0.18f));
                layer.DrawArc(head, _captureRadius * pulse, 0f, Mathf.Tau, 40, new Color(Jade, 0.35f), 1.6f, antialiased: true);
            }

            layer.DrawCircle(head, radius * 0.020f * pulse, new Color(Jade.Lerp(WhiteHot, 0.5f), 0.95f));
        }
    }

    private void DrawCursor(DrawLayer layer, Vector2 center)
    {
        if (!_engaged || _phase != Phase.Trace)
        {
            return;
        }

        if (_cursorTrail.Count >= 2)
        {
            var trail = new Vector2[_cursorTrail.Count];
            var colors = new Color[_cursorTrail.Count];
            var tint = Jade.Lerp(WildViolet, 1f - _recentQuality);
            for (var i = 0; i < _cursorTrail.Count; i++)
            {
                trail[i] = center + _cursorTrail[i];
                colors[i] = new Color(tint, 0.4f * ((float)(i + 1) / _cursorTrail.Count));
            }

            layer.DrawPolylineColors(trail, colors, 2.5f, antialiased: true);
        }

        var cursorColor = _recentQuality > 0.45f
            ? Jade.Lerp(WhiteHot, 0.4f)
            : UiTheme.Danger.Lerp(WildViolet, 0.5f);
        layer.DrawCircle(center + _cursor, 4.5f, new Color(cursorColor, 0.9f));
        layer.DrawCircle(center + _cursor, 9f, new Color(cursorColor, 0.22f));
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
            Text = "Hold the left mouse button on the bright node and follow the burning path.",
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
            Phase.BurnIn => "The rune sears the air…",
            Phase.Trace => _engaged ? "Swift and true…" : "Trace the sigil",
            Phase.Sealed => "Sealed!",
            Phase.Finale => _everTraced ? "The spell surges toward its shape…" : "The spell resolves, unshaped.",
            _ => "",
        };
        _subtitle.Text = $"“{_spellText}”";
        _tally.Text = _sealedCount == 1 ? "1 sigil sealed" : $"{_sealedCount} sigils sealed";

        // Bars sit in score space: 0.5 is the neutral center, matching RuneTraceScoring.
        var liveSpeed = _activeSeconds > 0.4
            ? Mathf.Clamp((float)(_parSecondsEarned / _activeSeconds) / (float)RuneTraceScoring.SpeedCeilingRatio, 0f, 1f)
            : 0.5f;
        var liveAccuracy = _tracedLength > 0.001
            ? Mathf.Clamp(
                (float)(((_accuracyWeighted / _tracedLength) - RuneTraceScoring.AccuracyFloor)
                    / (RuneTraceScoring.AccuracyCeiling - RuneTraceScoring.AccuracyFloor)),
                0f,
                1f)
            : 0.5f;
        _powerBar.Value = liveSpeed;
        _controlBar.Value = liveAccuracy;
        _hint.Visible = !_everTraced || (_phase == Phase.Trace && !_engaged);
    }
}
