using Godot;
using Sorcerer.Core.Commands;
using Sorcerer.Core.Magic;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// The Bone-Song casting minigame: while the provider resolves a wild spell, its pulse runs
/// a carved whalebone drum-rim like a lit fuse, and the player keeps the song - Bralli
/// scrimshaw drumming, where boasting is hospitality. Strike as the ember crosses each
/// carving: plain notches feed control, tight gold accent knots feed power (declining one is
/// safe - it only costs power), and hollow rests must not be struck at all. Bars chain
/// indefinitely, re-carving faster and stranger, and every completed bar etches one more
/// stroke of a scrimshaw whale-hunt into the drumhead - the wait itself becomes art. Sound
/// is synthesized decoration over the visual clock (<see cref="BoneSongVoice"/>): the game
/// is fully playable with the volume at zero, so the beat is geometry, not anticipation.
///
/// The overlay owns presentation and gesture reduction only; it reduces the session to
/// <see cref="BoneSongMetrics"/> and lets <see cref="BoneSongScoring"/> (core) decide the
/// <see cref="CastPerformance"/>, so calibration stays engine-owned and testable. Bars are
/// seeded from the spell text (like rune shapes), so the same phrase always sings the same
/// song - learnable, never arbitrary.
/// </summary>
public partial class BoneSongMinigame : Control
{
    private enum Phase
    {
        Idle,
        FadeIn,
        Carve,
        Lap,
        Finale,
    }

    private enum NotchKind
    {
        Plain,
        Accent,
        Rest,
    }

    private const float FadeInSeconds = 0.18f;
    private const float CarveSeconds = 0.7f;
    private const float FinaleSeconds = 0.5f;
    private const float GraceSeconds = 2.0f;
    private const int MaxEmbers = 720;

    // Tempo and windows (presentation-side calibration; core scoring sees only counts).
    private const int SlotsPerBar = 8;
    private const int BeatsPerLap = SlotsPerBar + 1;   // one silent lead-in beat before slot 0
    private const float BaseBpm = 84f;
    private const float BpmPerBar = 6f;
    private const float MaxBpm = 126f;
    private const float PlainWindowMs = 120f;
    private const float AccentWindowMs = 60f;
    private const float RestWindowMs = 140f;
    private const float SwingConsiderMs = 260f;

    private static readonly Color BoneIvory = new(0.93f, 0.90f, 0.80f);
    private static readonly Color SeaInk = new(0.34f, 0.56f, 0.62f);
    private static readonly Color SeaDeep = new(0.20f, 0.36f, 0.44f);
    private static readonly Color EmberOrange = new(1.0f, 0.47f, 0.16f);
    private static readonly Color AleGold = new(1.0f, 0.78f, 0.40f);
    private static readonly Color WhiteHot = new(1.0f, 0.96f, 0.86f);
    private static readonly Color Jade = UiTheme.Wild;
    private static readonly Color WildViolet = new(0.69f, 0.48f, 1.0f);
    private static readonly Color CrackRed = new(1.0f, 0.32f, 0.28f);

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

    private struct Notch
    {
        public float Angle;
        public NotchKind Kind;
        public bool Resolved;
        public bool Hit;
        public float ResolvedAge;
    }

    private struct TrailDot
    {
        public float Theta;
        public float Age;
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
    private bool _everStruck;
    private bool _skipped;

    // Scoring totals (per-notch counts; see BoneSongScoring).
    private double _activeSeconds;
    private int _cleanHits;
    private int _misses;
    private int _accentsOffered;
    private int _accentHits;

    // Current bar.
    private int _barIndex;
    private int _completedBars;
    private Notch[] _notches = Array.Empty<Notch>();
    private float _lapSeconds = 6f;
    private float _angularSpeed = 1f;
    private int _streak;
    private int _carveSoundsPlayed;

    // Scrimshaw: the whale-hunt etched one stroke per completed bar.
    private static readonly Vector2[][] ScrimshawStrokes = BuildScrimshawStrokes();

    // VFX state.
    private readonly List<Ember> _embers = new(MaxEmbers);
    private readonly List<Ring> _rings = new();
    private readonly List<TrailDot> _trail = new();
    private readonly List<Vector2[]> _cracks = new();
    private readonly Random _vfxRng = new();
    private float _shake;
    private Vector2 _shakeOffset;
    private float _vignettePulse;

    // Layers, audio, HUD.
    private DrawLayer _ink = null!;
    private DrawLayer _glow = null!;
    private BoneSongVoice _voice = null!;
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
        _voice = new BoneSongVoice();
        AddChild(_voice);
        SetProcess(false);
    }

    /// <summary>
    /// Runs the minigame until the provider settles (plus a short grace to finish the bar in
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
        _barIndex = 0;
        _completedBars = 0;
        _providerReady = false;
        _everStruck = false;
        _skipped = false;
        _activeSeconds = 0;
        _cleanHits = 0;
        _misses = 0;
        _accentsOffered = 0;
        _accentHits = 0;
        _streak = 0;
        _graceClock = 0;
        _embers.Clear();
        _rings.Clear();
        _trail.Clear();
        _cracks.Clear();
        _shake = 0f;
        _vignettePulse = 0f;
        Modulate = new Color(1f, 1f, 1f, 0f);
        _phase = Phase.FadeIn;
        _phaseClock = 0;
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
                    StartBar();
                }

                break;

            case Phase.Carve:
                StepCarve();
                if (_providerReady)
                {
                    ExitEarly();
                    return;
                }

                if (_phaseClock >= CarveSeconds)
                {
                    _phase = Phase.Lap;
                    _phaseClock = 0;
                }

                break;

            case Phase.Lap:
                StepLap(delta);
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
        _voice.SetDrone(Mathf.Min(_streak / 10f, 1f));
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

        if (@event is InputEventMouseButton button
            && button.ButtonIndex == MouseButton.Left
            && button.Pressed)
        {
            Strike();
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
        else if (key.Keycode == Key.Space)
        {
            Strike();
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
            : BoneSongScoring.ToPerformance(new BoneSongMetrics(
                Struck: _everStruck,
                ActiveSeconds: _activeSeconds,
                CleanHits: _cleanHits,
                Misses: _misses,
                AccentsOffered: _accentsOffered,
                AccentHits: _accentHits));

        _phase = Phase.Idle;
        Visible = false;
        SetProcess(false);
        _voice.End();
        _providerSettled = null;
        var completion = _completion;
        _completion = null;
        completion?.TrySetResult(performance);
    }

    // ---------------------------------------------------------------- bar lifecycle

    private int Tier => Mathf.Min(_barIndex / 2, 3);

    private void StartBar()
    {
        // Seeded like rune shapes: the same phrase always sings the same song, so the drum
        // is learnable rather than arbitrary.
        var rng = new Random(RuneShape.SeedFor(_spellText, 211 + _barIndex));
        var tier = Tier;
        var bpm = Mathf.Min(BaseBpm + (_barIndex * BpmPerBar), MaxBpm);
        var beatSeconds = 60f / bpm;
        _lapSeconds = BeatsPerLap * beatSeconds;
        _angularSpeed = Mathf.Tau / _lapSeconds;

        // Carve the bar: which slots hold bone, which hold knots, which are hollow.
        var carvedCount = Mathf.Min(5 + Mathf.Min(tier, 2), SlotsPerBar);
        var slots = Enumerable.Range(0, SlotsPerBar).ToList();
        var carved = new List<int> { 0 };
        slots.Remove(0);
        while (carved.Count < carvedCount && slots.Count > 0)
        {
            var pick = slots[rng.Next(slots.Count)];
            slots.Remove(pick);
            carved.Add(pick);
        }

        var accentCount = Mathf.Min(1 + tier, 3);
        var accentSlots = carved.Where(slot => slot != 0)
            .OrderBy(_ => rng.Next())
            .Take(accentCount)
            .ToHashSet();

        var restCount = tier >= 3 ? 2 : tier >= 2 ? 1 : 0;
        var restSlots = slots.OrderBy(_ => rng.Next()).Take(restCount).ToHashSet();

        var notches = new List<Notch>();
        foreach (var slot in carved.Concat(restSlots))
        {
            var beat = slot + 1f; // the lead-in beat leaves one silent gap before slot 0
            if (Tier >= 1 && slot != 0 && !restSlots.Contains(slot))
            {
                beat += ((float)rng.NextDouble() - 0.5f) * 0.24f; // syncopation drift
            }

            notches.Add(new Notch
            {
                Angle = (-Mathf.Pi / 2f) + (Mathf.Tau * beat / BeatsPerLap),
                Kind = restSlots.Contains(slot) ? NotchKind.Rest
                    : accentSlots.Contains(slot) ? NotchKind.Accent
                    : NotchKind.Plain,
                Resolved = false,
                Hit = false,
                ResolvedAge = 0f,
            });
        }

        _notches = notches.OrderBy(notch => Mathf.Wrap(notch.Angle + (Mathf.Pi / 2f), 0f, Mathf.Tau)).ToArray();
        _barIndex++;
        _carveSoundsPlayed = 0;
        _trail.Clear();
        _phase = Phase.Carve;
        _phaseClock = 0;
        _graceClock = 0;
    }

    private void StepCarve()
    {
        // Notches etch in one by one, each with a scrape of bone-dust.
        var reveal = Mathf.Clamp((float)(_phaseClock / CarveSeconds), 0f, 1f);
        var shouldHavePlayed = Mathf.Clamp((int)(reveal * (_notches.Length + 1)), 0, _notches.Length);
        while (_carveSoundsPlayed < shouldHavePlayed)
        {
            var notch = _notches[_carveSoundsPlayed];
            var at = RingPoint(notch.Angle, RimRadius());
            SpawnEmber(
                at,
                RandomDirection() * (16f + ((float)_vfxRng.NextDouble() * 30f)),
                0.35f + ((float)_vfxRng.NextDouble() * 0.3f),
                1.2f + ((float)_vfxRng.NextDouble() * 1.2f),
                BoneIvory,
                2.6f,
                14f);
            _voice.Strike(BoneSongVoice.Hit.Carve);
            _carveSoundsPlayed++;
        }
    }

    private void StepLap(double delta)
    {
        _activeSeconds += delta;
        var theta = EmberTheta();
        _trail.Add(new TrailDot { Theta = theta, Age = 0f });

        // Resolve notches the pulse has passed beyond saving.
        for (var i = 0; i < _notches.Length; i++)
        {
            if (_notches[i].Resolved)
            {
                continue;
            }

            var behind = Mathf.Wrap(theta - _notches[i].Angle, -Mathf.Pi, Mathf.Pi);
            var passAngle = WindowAngle(_notches[i].Kind) + (_angularSpeed * 0.04f);
            if (behind <= passAngle)
            {
                continue;
            }

            var notch = _notches[i];
            notch.Resolved = true;
            notch.Hit = false;
            _notches[i] = notch;
            switch (notch.Kind)
            {
                case NotchKind.Plain:
                    // A plain carving left unstruck is a dropped beat.
                    _misses++;
                    _streak = 0;
                    AddCrack(notch.Angle);
                    _voice.Strike(BoneSongVoice.Hit.Crack);
                    break;
                case NotchKind.Accent:
                    // Declining a knot is hospitality refused, not a crime: it only costs power.
                    _accentsOffered++;
                    break;
                default:
                    // A rest respected: the hollow hums approval.
                    SpawnRing(RingPoint(notch.Angle, RimRadius()), 3f, 20f, 0.25f, 0.3f, Jade, 1.2f);
                    break;
            }
        }

        if (_phaseClock >= _lapSeconds)
        {
            _completedBars++;
            SpawnRing(Vector2.Zero, RimRadius() * 0.3f, RimRadius() * 1.3f, 0.3f, 0.45f, SeaInk, 2f);
            if (_providerReady)
            {
                ExitEarly();
                return;
            }

            StartBar();
            return;
        }

        if (_providerReady)
        {
            // A short grace to land the strikes in reach; the bar in hand then ends unjudged.
            _graceClock += delta;
            if (_graceClock >= GraceSeconds)
            {
                ExitEarly();
            }
        }
    }

    private float EmberTheta() =>
        (-Mathf.Pi / 2f) + ((float)_phaseClock * _angularSpeed);

    private float WindowAngle(NotchKind kind) =>
        _angularSpeed * (WindowMs(kind) / 1000f);

    private static float WindowMs(NotchKind kind) => kind switch
    {
        NotchKind.Accent => AccentWindowMs,
        NotchKind.Rest => RestWindowMs,
        _ => PlainWindowMs,
    };

    private void Strike()
    {
        if (_phase != Phase.Lap)
        {
            return;
        }

        _everStruck = true;
        var theta = EmberTheta();
        var considerAngle = _angularSpeed * (SwingConsiderMs / 1000f);

        // The nearest strikeable carving decides what this swing meant.
        var best = -1;
        var bestDistance = float.MaxValue;
        for (var i = 0; i < _notches.Length; i++)
        {
            if (_notches[i].Resolved || _notches[i].Kind == NotchKind.Rest)
            {
                continue;
            }

            var distance = Mathf.Abs(Mathf.Wrap(theta - _notches[i].Angle, -Mathf.Pi, Mathf.Pi));
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        if (best >= 0 && bestDistance <= considerAngle)
        {
            var notch = _notches[best];
            notch.Resolved = true;
            if (bestDistance <= WindowAngle(notch.Kind))
            {
                notch.Hit = true;
                _cleanHits++;
                _streak++;
                if (notch.Kind == NotchKind.Accent)
                {
                    _accentsOffered++;
                    _accentHits++;
                    _voice.Strike(BoneSongVoice.Hit.Doom);
                    SpawnAccentStomp(notch.Angle);
                }
                else
                {
                    _voice.Strike(BoneSongVoice.Hit.Tok);
                    SpawnHitWave(notch.Angle, false);
                }
            }
            else
            {
                notch.Hit = false;
                _misses++;
                _streak = 0;
                if (notch.Kind == NotchKind.Accent)
                {
                    _accentsOffered++;
                }

                AddCrack(notch.Angle);
                _voice.Strike(BoneSongVoice.Hit.Crack);
                _shake = Mathf.Max(_shake, 2.5f);
            }

            _notches[best] = notch;
            return;
        }

        // No carving in reach: was that a hollow? Striking a rest makes the drum yelp.
        for (var i = 0; i < _notches.Length; i++)
        {
            if (_notches[i].Resolved || _notches[i].Kind != NotchKind.Rest)
            {
                continue;
            }

            var distance = Mathf.Abs(Mathf.Wrap(theta - _notches[i].Angle, -Mathf.Pi, Mathf.Pi));
            if (distance <= WindowAngle(NotchKind.Rest))
            {
                var notch = _notches[i];
                notch.Resolved = true;
                notch.Hit = false;
                _notches[i] = notch;
                _misses++;
                _streak = 0;
                _vignettePulse = 1f;
                _shake = Mathf.Max(_shake, 5f);
                _voice.Strike(BoneSongVoice.Hit.Yelp);
                SpawnRing(RingPoint(notch.Angle, RimRadius()), 5f, RimRadius() * 0.9f, 0.5f, 0.4f, WildViolet, 2.4f);
                return;
            }
        }

        // A swing at empty air: the hall notices, the ledger does not.
        _streak = 0;
        _voice.Strike(BoneSongVoice.Hit.Crack);
        SpawnRing(RingPoint(theta, RimRadius()), 3f, 18f, 0.2f, 0.25f, BoneIvory, 1.2f);
    }

    private void ExitEarly()
    {
        if (_everStruck)
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
        // The carved hunt lifts off the drum and feeds the spell.
        var drumRadius = RimRadius() * 0.8f;
        for (var stroke = 0; stroke < Mathf.Min(_completedBars, ScrimshawStrokes.Length); stroke++)
        {
            foreach (var point in ScrimshawStrokes[stroke])
            {
                SpawnEmber(
                    point * drumRadius,
                    RandomDirection() * (30f + ((float)_vfxRng.NextDouble() * 60f)),
                    0.4f + ((float)_vfxRng.NextDouble() * 0.4f),
                    1.4f + ((float)_vfxRng.NextDouble() * 1.6f),
                    SeaInk.Lerp(WhiteHot, 0.4f),
                    2.0f,
                    24f);
                _embers[^1] = _embers[^1] with { PullToCenter = true };
            }
        }

        for (var index = 0; index < _embers.Count; index++)
        {
            var ember = _embers[index];
            ember.PullToCenter = true;
            ember.Life = Mathf.Max(ember.Life, 0.3f);
            _embers[index] = ember;
        }

        SpawnRing(Vector2.Zero, RimRadius() * 0.2f, RimRadius() * 2.4f, 0.5f, 0.45f, Jade, 2.5f);
        _shake = Mathf.Max(_shake, 6f);
        _phase = Phase.Finale;
        _phaseClock = 0;
    }

    // ---------------------------------------------------------------- vfx

    private void SpawnHitWave(float angle, bool accent)
    {
        var at = RingPoint(angle, RimRadius());
        SpawnRing(at, 4f, RimRadius() * (accent ? 1.1f : 0.55f), 0.45f, 0.35f, accent ? AleGold : BoneIvory, accent ? 2.6f : 1.8f);
        var count = accent ? 16 : 8;
        for (var i = 0; i < count; i++)
        {
            SpawnEmber(
                at,
                RandomDirection() * (30f + ((float)_vfxRng.NextDouble() * 80f)),
                0.3f + ((float)_vfxRng.NextDouble() * 0.4f),
                1.3f + ((float)_vfxRng.NextDouble() * 1.6f),
                accent ? AleGold : BoneIvory,
                2.6f,
                24f);
        }
    }

    private void SpawnAccentStomp(float angle)
    {
        SpawnHitWave(angle, true);
        SpawnRing(Vector2.Zero, RimRadius() * 0.15f, RimRadius() * 1.05f, 0.35f, 0.4f, AleGold, 2f);
        _shake = Mathf.Max(_shake, 4.5f);
        _vignettePulse = Mathf.Max(_vignettePulse, 0.35f);
    }

    private void AddCrack(float angle)
    {
        // A hairline splinter near the dropped beat: the run's wear, worn on the rim.
        var at = RingPoint(angle, RimRadius());
        var direction = Vector2.FromAngle(angle + (NextSigned() * 0.6f));
        var crack = new Vector2[4];
        crack[0] = at;
        for (var i = 1; i < crack.Length; i++)
        {
            crack[i] = crack[i - 1]
                + (direction * (5f + ((float)_vfxRng.NextDouble() * 7f)))
                + (new Vector2(-direction.Y, direction.X) * NextSigned() * 4f);
        }

        _cracks.Add(crack);
        if (_cracks.Count > 40)
        {
            _cracks.RemoveAt(0);
        }
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

        for (var i = _trail.Count - 1; i >= 0; i--)
        {
            var dot = _trail[i];
            dot.Age += dt;
            if (dot.Age > 0.4f)
            {
                _trail.RemoveAt(i);
                continue;
            }

            _trail[i] = dot;
        }

        for (var i = 0; i < _notches.Length; i++)
        {
            if (_notches[i].Resolved)
            {
                var notch = _notches[i];
                notch.ResolvedAge += dt;
                _notches[i] = notch;
            }
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

    private static Vector2 RingPoint(float angle, float radius) =>
        new(Mathf.Cos(angle) * radius, Mathf.Sin(angle) * radius);

    // ---------------------------------------------------------------- scrimshaw

    /// <summary>
    /// The whale-hunt, one stroke per completed bar, in drumhead-unit coordinates: waves,
    /// a boat, its mast, the whale, the flukes, the spout, the harpoon line, the sun, gulls.
    /// </summary>
    private static Vector2[][] BuildScrimshawStrokes()
    {
        var sun = new Vector2[9];
        for (var i = 0; i < sun.Length; i++)
        {
            var angle = Mathf.Tau * i / (sun.Length - 1);
            sun[i] = new Vector2(-0.58f, -0.44f) + (new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 0.09f);
        }

        return new[]
        {
            new[] { new Vector2(-0.75f, 0.30f), new Vector2(-0.45f, 0.24f), new Vector2(-0.15f, 0.32f), new Vector2(0.15f, 0.24f), new Vector2(0.45f, 0.32f), new Vector2(0.75f, 0.26f) },
            new[] { new Vector2(-0.65f, 0.45f), new Vector2(-0.35f, 0.39f), new Vector2(-0.05f, 0.47f), new Vector2(0.25f, 0.39f), new Vector2(0.55f, 0.47f) },
            new[] { new Vector2(-0.62f, 0.16f), new Vector2(-0.54f, 0.26f), new Vector2(-0.30f, 0.28f), new Vector2(-0.18f, 0.18f) },
            new[] { new Vector2(-0.38f, 0.24f), new Vector2(-0.38f, -0.08f), new Vector2(-0.22f, 0.08f), new Vector2(-0.38f, 0.08f) },
            new[] { new Vector2(0.10f, 0.30f), new Vector2(0.20f, 0.10f), new Vector2(0.40f, 0.02f), new Vector2(0.60f, 0.10f), new Vector2(0.70f, 0.26f) },
            new[] { new Vector2(0.66f, 0.28f), new Vector2(0.80f, 0.12f), new Vector2(0.88f, 0.28f) },
            new[] { new Vector2(0.14f, -0.14f), new Vector2(0.20f, -0.02f), new Vector2(0.26f, -0.16f) },
            new[] { new Vector2(-0.20f, 0.14f), new Vector2(0.04f, 0.10f), new Vector2(0.26f, 0.06f) },
            sun,
            new[] { new Vector2(-0.10f, -0.38f), new Vector2(-0.02f, -0.44f), new Vector2(0.06f, -0.38f) },
            new[] { new Vector2(0.16f, -0.30f), new Vector2(0.24f, -0.36f), new Vector2(0.32f, -0.30f) },
        };
    }

    /// <summary>Interpolated prefix of a polyline up to a fraction of its arclength.</summary>
    private static Vector2[] PartialPolyline(Vector2[] points, float fraction)
    {
        fraction = Mathf.Clamp(fraction, 0f, 1f);
        var total = 0f;
        for (var i = 1; i < points.Length; i++)
        {
            total += points[i - 1].DistanceTo(points[i]);
        }

        var target = total * fraction;
        var kept = new List<Vector2> { points[0] };
        var walked = 0f;
        for (var i = 1; i < points.Length; i++)
        {
            var span = points[i - 1].DistanceTo(points[i]);
            if (walked + span <= target)
            {
                kept.Add(points[i]);
                walked += span;
                continue;
            }

            var t = span < 0.0001f ? 0f : (target - walked) / span;
            kept.Add(points[i - 1].Lerp(points[i], Mathf.Clamp(t, 0f, 1f)));
            break;
        }

        return kept.ToArray();
    }

    // ---------------------------------------------------------------- drawing

    private Vector2 CanvasCenter() => new(Size.X / 2f, (Size.Y / 2f) - (Size.Y * 0.045f));

    private float CanvasRadius() => Mathf.Clamp(Mathf.Min(Size.X, Size.Y) * 0.30f, 140f, 340f);

    private float RimRadius() => CanvasRadius() * 0.92f;

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
        var rim = RimRadius();
        layer.DrawRect(new Rect2(Vector2.Zero, Size), new Color(0.012f, 0.014f, 0.03f, 0.88f));

        // The drumhead: aged bone, faintly warm, with growth-grain rings.
        layer.DrawCircle(center, rim * 0.86f, new Color(0.10f, 0.095f, 0.082f, 0.92f));
        for (var i = 1; i <= 4; i++)
        {
            layer.DrawArc(
                center + new Vector2(6f, 4f),
                rim * 0.18f * i,
                0f,
                Mathf.Tau,
                72,
                new Color(BoneIvory, 0.030f),
                1.2f,
                antialiased: true);
        }

        // Cracks: the run's dropped beats, splintered into the rim.
        foreach (var crack in _cracks)
        {
            var offset = new Vector2[crack.Length];
            for (var i = 0; i < crack.Length; i++)
            {
                offset[i] = center + crack[i];
            }

            layer.DrawPolyline(offset, new Color(0.05f, 0.04f, 0.05f, 0.75f), 1.6f, antialiased: true);
        }

        // A struck hollow stings the whole frame for a breath.
        if (_vignettePulse > 0.01f)
        {
            layer.DrawRect(
                new Rect2(Vector2.Zero, Size),
                new Color(0.30f, 0.08f, 0.16f, 0.14f * _vignettePulse));
        }
    }

    private void DrawGlow(DrawLayer layer)
    {
        if (_phase == Phase.Idle)
        {
            return;
        }

        var center = CanvasCenter() + _shakeOffset;

        DrawRim(layer, center);
        DrawScrimshaw(layer, center);
        DrawNotches(layer, center);
        DrawPulse(layer, center);

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

    private void DrawRim(DrawLayer layer, Vector2 center)
    {
        var rim = RimRadius();
        var breathe = 0.20f + (0.05f * Mathf.Sin((float)_time * 1.4f));

        // The whalebone rim itself, with a streak-fed ale-gold warmth.
        layer.DrawArc(center, rim, 0f, Mathf.Tau, 128, new Color(BoneIvory, breathe), 5f, antialiased: true);
        layer.DrawArc(center, rim - 9f, 0f, Mathf.Tau, 128, new Color(BoneIvory, breathe * 0.4f), 1.4f, antialiased: true);
        layer.DrawArc(center, rim + 9f, 0f, Mathf.Tau, 128, new Color(BoneIvory, breathe * 0.4f), 1.4f, antialiased: true);

        var warmth = Mathf.Min(_streak / 10f, 1f);
        if (warmth > 0.01f)
        {
            layer.DrawArc(center, rim, 0f, Mathf.Tau, 128, new Color(AleGold, 0.22f * warmth), 8f, antialiased: true);
        }
    }

    private void DrawScrimshaw(DrawLayer layer, Vector2 center)
    {
        var drumRadius = RimRadius() * 0.8f;
        var etched = Mathf.Min(_completedBars, ScrimshawStrokes.Length);

        // Fully etched strokes: the hunt so far, inked in cold sea-blue.
        var full = _phase == Phase.Carve ? etched - 1 : etched;
        for (var i = 0; i < full; i++)
        {
            DrawStroke(layer, center, ScrimshawStrokes[i], drumRadius, 1f);
        }

        // The stroke being carved right now, burning in behind the new bar.
        if (_phase == Phase.Carve && etched >= 1 && etched <= ScrimshawStrokes.Length)
        {
            var fraction = Mathf.Clamp((float)(_phaseClock / CarveSeconds), 0f, 1f);
            var partial = PartialPolyline(ScrimshawStrokes[etched - 1], fraction);
            if (partial.Length >= 2)
            {
                DrawStroke(layer, center, partial, drumRadius, 1f);
                var head = center + (partial[^1] * drumRadius);
                layer.DrawCircle(head, 3.4f, new Color(WhiteHot, 0.9f));
            }
        }
    }

    private static void DrawStroke(DrawLayer layer, Vector2 center, Vector2[] stroke, float drumRadius, float alpha)
    {
        if (stroke.Length < 2)
        {
            return;
        }

        var points = new Vector2[stroke.Length];
        for (var i = 0; i < stroke.Length; i++)
        {
            points[i] = center + (stroke[i] * drumRadius);
        }

        layer.DrawPolyline(points, new Color(SeaDeep, 0.16f * alpha), 5f, antialiased: true);
        layer.DrawPolyline(points, new Color(SeaInk, 0.75f * alpha), 1.6f, antialiased: true);
    }

    private void DrawNotches(DrawLayer layer, Vector2 center)
    {
        if (_notches.Length == 0 || _phase is not (Phase.Carve or Phase.Lap))
        {
            return;
        }

        var rim = RimRadius();
        var reveal = _phase == Phase.Carve
            ? Mathf.Clamp((float)(_phaseClock / CarveSeconds), 0f, 1f)
            : 1f;

        for (var index = 0; index < _notches.Length; index++)
        {
            var notch = _notches[index];
            var visible = Mathf.Clamp((reveal * (_notches.Length + 1)) - index, 0f, 1f);
            if (visible <= 0.01f)
            {
                continue;
            }

            var direction = Vector2.FromAngle(notch.Angle);
            var inner = center + (direction * (rim - 10f));
            var outer = center + (direction * (rim + 10f));
            var at = center + (direction * rim);

            if (notch.Kind == NotchKind.Rest)
            {
                // The hollow: no bone to strike, only two warning motes around a gap.
                var restAlpha = (notch.Resolved ? 0.25f : 0.6f) * visible;
                var flickerPulse = 0.7f + (0.3f * Mathf.Sin(((float)_time * 3.4f) + notch.Angle));
                layer.DrawCircle(center + (direction * (rim - 12f)), 2.2f, new Color(WildViolet, restAlpha * flickerPulse));
                layer.DrawCircle(center + (direction * (rim + 12f)), 2.2f, new Color(WildViolet, restAlpha * flickerPulse));
                continue;
            }

            Color color;
            float alpha;
            if (!notch.Resolved)
            {
                color = notch.Kind == NotchKind.Accent ? AleGold : BoneIvory;
                alpha = 0.85f;
            }
            else if (notch.Hit)
            {
                color = notch.Kind == NotchKind.Accent ? WhiteHot : AleGold;
                alpha = Mathf.Max(0.15f, 0.9f - (notch.ResolvedAge * 0.9f));
            }
            else
            {
                color = CrackRed;
                alpha = Mathf.Max(0.10f, 0.6f - (notch.ResolvedAge * 0.6f));
            }

            var width = notch.Kind == NotchKind.Accent ? 4.2f : 2.4f;
            layer.DrawLine(inner, outer, new Color(color, alpha * visible), width, antialiased: true);

            if (notch.Kind == NotchKind.Accent)
            {
                // The knot: a carved diamond riding the rim, the boast made visible.
                var perpendicular = new Vector2(-direction.Y, direction.X);
                var knot = new[]
                {
                    at + (direction * 15f),
                    at + (perpendicular * 7f),
                    at - (direction * 15f),
                    at - (perpendicular * 7f),
                    at + (direction * 15f),
                };
                layer.DrawPolyline(knot, new Color(color, 0.8f * alpha * visible), 1.6f, antialiased: true);
                layer.DrawCircle(at, 2.6f, new Color(color, 0.9f * alpha * visible));
            }
        }
    }

    private void DrawPulse(DrawLayer layer, Vector2 center)
    {
        if (_phase != Phase.Lap)
        {
            return;
        }

        var rim = RimRadius();
        var theta = EmberTheta();

        // The comet tail: where the song has just been.
        foreach (var dot in _trail)
        {
            var fade = 1f - (dot.Age / 0.4f);
            layer.DrawCircle(
                center + RingPoint(dot.Theta, rim),
                4.5f * fade,
                new Color(EmberOrange, 0.30f * fade));
        }

        // The pulse ember itself, white-hot on the bone.
        var at = center + RingPoint(theta, rim);
        var pulse = 1f + (0.2f * Mathf.Sin((float)_time * 9f));
        layer.DrawCircle(at, 6.5f * pulse, new Color(WhiteHot, 0.95f));
        layer.DrawCircle(at, 13f * pulse, new Color(EmberOrange, 0.28f));
        layer.DrawCircle(at, 22f * pulse, new Color(EmberOrange, 0.10f));
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
        _title.AddThemeColorOverride("font_color", AleGold);
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
            Text = "Strike (click or Space) as the ember crosses each carving. Gold knots feed power but their window is tight — letting one pass is safe. Never strike a hollow.",
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
            Phase.Carve => "The bone carves a new bar…",
            Phase.Lap when !_everStruck => "Strike as the ember crosses the carvings",
            Phase.Lap when _streak >= 10 => "The hall stomps along—",
            Phase.Lap when _streak >= 5 => "The bone-singers join in…",
            Phase.Lap => "Keep the pulse…",
            Phase.Finale => _everStruck ? "The song feeds the spell…" : "The spell resolves, unshaped.",
            _ => "",
        };
        _subtitle.Text = $"“{_spellText}”";
        _tally.Text = $"{_cleanHits} clean · {_misses} cracked · {_accentHits}/{_accentsOffered} knots";

        // Bars sit in score space: 0.5 is the neutral center, matching BoneSongScoring.
        var resolved = _cleanHits + _misses;
        var liveAccuracy = resolved > 0
            ? Mathf.Clamp(
                (float)((((double)_cleanHits / resolved) - BoneSongScoring.AccuracyFloor)
                    / (BoneSongScoring.AccuracyCeiling - BoneSongScoring.AccuracyFloor)),
                0f,
                1f)
            : 0.5f;
        var liveAccent = _accentsOffered > 0
            ? Mathf.Clamp(
                (float)(((double)_accentHits / _accentsOffered)
                    / (2.0 * BoneSongScoring.ParAccentTake)),
                0f,
                1f)
            : 0.5f;
        _powerBar.Value = liveAccent;
        _controlBar.Value = liveAccuracy;
        _hint.Visible = !_everStruck;
    }
}
