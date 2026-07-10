using Godot;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// Synthesized percussion for Bone-Song's three marked registers, modeled as struck membranes
/// rather than raw tones: a deep "duum" for the center, a higher open "doo" for the edge, and a
/// dry woodblock "tick" for the rim. Each voice integrates its own phase through an exponential
/// pitch scoop (the skin tightening after the strike), carries an inharmonic overtone that dies
/// faster than the fundamental, and opens with a lowpass-filtered mallet thump. The registers are
/// tuned to one chord — duum 58 Hz, doo a perfect twelfth above at 174 Hz, drone on the octave
/// and twelfth — so a groove rings like one instrument. Gold flourishes add a hall-sized boom
/// with a fifth in it; misses crack dully without pitch. Audio follows the same visual clock but
/// never owns timing, so muted play is complete and no latency calibration is required.
/// No sound assets ship.
/// </summary>
public partial class BoneSongVoice : Node
{
    public enum Hit
    {
        Deep,
        Heart,
        Bright,
        Boast,
        Miss,
        Count,
    }

    private const float MixRate = 44100f;
    private const int MaxPushFrames = 4096;
    private const int MaxVoices = 28;

    private struct Voice
    {
        public Hit Kind;
        public double T;
        public double PhaseA;
        public double PhaseB;
        public double PhaseC;
        public double Lowpass;
    }

    /// <summary>
    /// One struck drum skin. The pitch starts <see cref="ScoopHz"/> above <see cref="SettleHz"/>
    /// and relaxes down at <see cref="ScoopRate"/> — the "oo" in duum. <see cref="ThumpTone"/> is
    /// the one-pole lowpass coefficient shaping the mallet-contact noise (higher = brighter).
    /// </summary>
    private readonly record struct Skin(
        double SettleHz,
        double ScoopHz,
        double ScoopRate,
        double OvertoneRatio,
        double OvertoneLevel,
        double FifthLevel,
        double Attack,
        double Decay,
        double Duration,
        double ThumpLevel,
        double ThumpDecay,
        double ThumpTone,
        double Gain);

    private static readonly Skin DeepSkin = new(
        SettleHz: 58, ScoopHz: 54, ScoopRate: 30, OvertoneRatio: 1.62, OvertoneLevel: 0.20,
        FifthLevel: 0, Attack: 0.004, Decay: 5.5, Duration: 0.90,
        ThumpLevel: 0.45, ThumpDecay: 90, ThumpTone: 0.20, Gain: 1.00);

    private static readonly Skin HeartSkin = new(
        SettleHz: 174, ScoopHz: 66, ScoopRate: 40, OvertoneRatio: 2.09, OvertoneLevel: 0.16,
        FifthLevel: 0, Attack: 0.003, Decay: 9.5, Duration: 0.50,
        ThumpLevel: 0.35, ThumpDecay: 150, ThumpTone: 0.35, Gain: 0.72);

    private static readonly Skin BoastSkin = new(
        SettleHz: 58, ScoopHz: 64, ScoopRate: 24, OvertoneRatio: 1.50, OvertoneLevel: 0.30,
        FifthLevel: 0.35, Attack: 0.004, Decay: 3.8, Duration: 1.20,
        ThumpLevel: 0.40, ThumpDecay: 70, ThumpTone: 0.18, Gain: 1.05);

    private static readonly Skin CountSkin = new(
        SettleHz: 410, ScoopHz: 140, ScoopRate: 60, OvertoneRatio: 2.40, OvertoneLevel: 0.10,
        FifthLevel: 0, Attack: 0.002, Decay: 22, Duration: 0.16,
        ThumpLevel: 0.30, ThumpDecay: 200, ThumpTone: 0.45, Gain: 0.32);

    private AudioStreamPlayer _player = null!;
    private AudioStreamGeneratorPlayback? _playback;
    private readonly List<Voice> _voices = new(MaxVoices);
    private readonly Random _rng = new();
    private float _droneLevel;
    private float _droneTarget;
    private double _dronePhaseA;
    private double _dronePhaseB;
    private double _dronePhaseC;

    public override void _Ready()
    {
        _player = new AudioStreamPlayer
        {
            Stream = new AudioStreamGenerator { MixRate = MixRate, BufferLength = 0.12f },
            VolumeDb = -8f,
        };
        AddChild(_player);
    }

    public void Begin()
    {
        _voices.Clear();
        _droneLevel = 0f;
        _droneTarget = 0f;
        _player.Play();
        _playback = (AudioStreamGeneratorPlayback)_player.GetStreamPlayback();
    }

    public void End()
    {
        _playback = null;
        _player.Stop();
    }

    public void Strike(Hit kind)
    {
        if (_playback is null)
        {
            return;
        }

        if (_voices.Count >= MaxVoices)
        {
            _voices.RemoveAt(0);
        }

        _voices.Add(new Voice { Kind = kind, T = 0 });
    }

    public void SetDrone(float level01) => _droneTarget = Mathf.Clamp(level01, 0f, 1f);

    public override void _Process(double delta)
    {
        if (_playback is null)
        {
            return;
        }

        var frames = Mathf.Min(_playback.GetFramesAvailable(), MaxPushFrames);
        if (frames <= 0)
        {
            return;
        }

        var buffer = new Vector2[frames];
        const double dt = 1.0 / MixRate;
        var droneEase = 1f - Mathf.Exp(-3f / MixRate);
        for (var index = 0; index < frames; index++)
        {
            _droneLevel = Mathf.Lerp(_droneLevel, _droneTarget, droneEase);
            var sample = DroneSample(dt) * _droneLevel * 0.095f;
            for (var voiceIndex = _voices.Count - 1; voiceIndex >= 0; voiceIndex--)
            {
                var voice = _voices[voiceIndex];
                var (value, alive) = VoiceSample(ref voice, dt);
                sample += value;
                voice.T += dt;
                if (!alive)
                {
                    _voices.RemoveAt(voiceIndex);
                }
                else
                {
                    _voices[voiceIndex] = voice;
                }
            }

            var soft = Mathf.Tanh(sample);
            buffer[index] = new Vector2(soft, soft);
        }

        _playback.PushBuffer(buffer);
    }

    /// <summary>
    /// The bone-singers hum the drum's octave with a slow two-voice beat; the twelfth joins as
    /// the streak feeds the hall. Everything stays inside the duum's chord.
    /// </summary>
    private float DroneSample(double dt)
    {
        _dronePhaseA += Mathf.Tau * 116.0 * dt;
        _dronePhaseB += Mathf.Tau * 116.7 * dt;
        _dronePhaseC += Mathf.Tau * 174.0 * dt;
        return (float)((Math.Sin(_dronePhaseA) * 0.5)
            + (Math.Sin(_dronePhaseB) * 0.3)
            + (Math.Sin(_dronePhaseC) * 0.35 * _droneLevel));
    }

    private (float Value, bool Alive) VoiceSample(ref Voice voice, double dt)
    {
        return voice.Kind switch
        {
            Hit.Deep => Membrane(ref voice, dt, DeepSkin),
            Hit.Heart => Membrane(ref voice, dt, HeartSkin),
            Hit.Bright => Tick(ref voice, dt),
            Hit.Boast => Membrane(ref voice, dt, BoastSkin),
            Hit.Count => Membrane(ref voice, dt, CountSkin),
            _ => Crack(ref voice, dt),
        };
    }

    private (float Value, bool Alive) Membrane(ref Voice voice, double dt, in Skin skin)
    {
        if (voice.T >= skin.Duration)
        {
            return (0f, false);
        }

        var t = voice.T;

        // Integrated phase through the pitch scoop: computing sin(2π·f(t)·t) directly would
        // sweep the instantaneous frequency twice as far as f(t) and sound like a laser.
        var frequency = skin.SettleHz + (skin.ScoopHz * Math.Exp(-skin.ScoopRate * t));
        voice.PhaseA += Mathf.Tau * frequency * dt;
        voice.PhaseB += Mathf.Tau * frequency * skin.OvertoneRatio * dt;
        var partials = Math.Sin(voice.PhaseA)
            + (Math.Sin(voice.PhaseB) * skin.OvertoneLevel * Math.Exp(-3.0 * t));
        if (skin.FifthLevel > 0)
        {
            voice.PhaseC += Mathf.Tau * frequency * 1.5 * dt;
            partials += Math.Sin(voice.PhaseC) * skin.FifthLevel * Math.Exp(-4.5 * t);
        }

        var attack = Math.Min(t / skin.Attack, 1.0);
        var body = partials * attack * Math.Exp(-skin.Decay * t);

        voice.Lowpass += (Noise() - voice.Lowpass) * skin.ThumpTone;
        var thump = voice.Lowpass * skin.ThumpLevel * Math.Exp(-skin.ThumpDecay * t);
        return ((float)((body + thump) * skin.Gain), true);
    }

    /// <summary>
    /// The rim "tick": three inharmonic woodblock partials over a bright, very short stick
    /// click. No pitch scoop — bone on bone, not skin.
    /// </summary>
    private (float Value, bool Alive) Tick(ref Voice voice, double dt)
    {
        const double Duration = 0.16;
        if (voice.T >= Duration)
        {
            return (0f, false);
        }

        var t = voice.T;
        voice.PhaseA += Mathf.Tau * 860.0 * dt;
        voice.PhaseB += Mathf.Tau * 1520.0 * dt;
        voice.PhaseC += Mathf.Tau * 2350.0 * dt;
        var body = (Math.Sin(voice.PhaseA) * 0.55 * Math.Exp(-34.0 * t))
            + (Math.Sin(voice.PhaseB) * 0.30 * Math.Exp(-48.0 * t))
            + (Math.Sin(voice.PhaseC) * 0.15 * Math.Exp(-70.0 * t));
        voice.Lowpass += (Noise() - voice.Lowpass) * 0.55;
        var click = voice.Lowpass * 0.6 * Math.Exp(-260.0 * t);
        return ((float)((body + click) * 0.55), true);
    }

    /// <summary>A miss: dull lowpassed crack with a faint dead-skin thud, no ring.</summary>
    private (float Value, bool Alive) Crack(ref Voice voice, double dt)
    {
        const double Duration = 0.12;
        if (voice.T >= Duration)
        {
            return (0f, false);
        }

        var t = voice.T;
        voice.Lowpass += (Noise() - voice.Lowpass) * 0.18;
        voice.PhaseA += Mathf.Tau * 130.0 * dt;
        var value = (voice.Lowpass * 1.4 * Math.Exp(-48.0 * t))
            + (Math.Sin(voice.PhaseA) * 0.25 * Math.Exp(-40.0 * t));
        return ((float)(value * 0.5), true);
    }

    private double Noise() => (_rng.NextDouble() * 2.0) - 1.0;
}
