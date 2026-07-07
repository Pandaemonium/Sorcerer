using Godot;

namespace Sorcerer.Godot.Minigames;

/// <summary>
/// The Bone-Song percussion voice: Sorcerer's first audio, synthesized entirely in code
/// through an <see cref="AudioStreamGenerator"/> so the game ships no sound assets. One-shot
/// voices (bone tok, deep accent doom, rest yelp, miss crack, carving scrape) mix over a
/// streak drone - hummed bone-singer fifths whose level the minigame raises as clean hits
/// chain, so the mix itself is the feedback ladder.
///
/// Audio is decoration on top of the minigame's visual clock, never the source of truth:
/// Bone-Song stays fully playable with sound off (docs/CASTING_AND_MINIGAMES.md).
/// </summary>
public partial class BoneSongVoice : Node
{
    public enum Hit
    {
        Tok,
        Doom,
        Yelp,
        Crack,
        Carve,
    }

    private const float MixRate = 44100f;
    private const int MaxPushFrames = 4096;
    private const int MaxVoices = 24;

    private struct Voice
    {
        public Hit Kind;
        public double T;
    }

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

    /// <summary>The bone-singers join a steady drummer: 0 silence, 1 full chorus.</summary>
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
        for (var i = 0; i < frames; i++)
        {
            _droneLevel = Mathf.Lerp(_droneLevel, _droneTarget, droneEase);
            var sample = DroneSample(dt) * _droneLevel * 0.10f;

            for (var v = _voices.Count - 1; v >= 0; v--)
            {
                var voice = _voices[v];
                var (value, alive) = VoiceSample(voice.Kind, voice.T);
                sample += value;
                voice.T += dt;
                if (!alive)
                {
                    _voices.RemoveAt(v);
                }
                else
                {
                    _voices[v] = voice;
                }
            }

            // Soft clip keeps stacked accents from ever cracking the speakers.
            var soft = Mathf.Tanh(sample);
            buffer[i] = new Vector2(soft, soft);
        }

        _playback.PushBuffer(buffer);
    }

    private float DroneSample(double dt)
    {
        // A low bone-hall hum: root, a near-unison beat, and a fifth that swells with level.
        _dronePhaseA += Mathf.Tau * 98.0 * dt;
        _dronePhaseB += Mathf.Tau * 98.7 * dt;
        _dronePhaseC += Mathf.Tau * 147.0 * dt;
        return (float)((Math.Sin(_dronePhaseA) * 0.5)
            + (Math.Sin(_dronePhaseB) * 0.3)
            + (Math.Sin(_dronePhaseC) * 0.35 * _droneLevel));
    }

    private (float Value, bool Alive) VoiceSample(Hit kind, double t)
    {
        switch (kind)
        {
            case Hit.Tok:
            {
                // A dry knuckle on whalebone: fast pitch drop, sharp noise transient.
                const double duration = 0.09;
                if (t >= duration)
                {
                    return (0f, false);
                }

                var frequency = Mathf.Lerp(620f, 180f, (float)(t / duration));
                var tone = Math.Sin(Mathf.Tau * frequency * t) * Math.Exp(-45.0 * t) * 0.55;
                var snap = Noise() * Math.Exp(-120.0 * t) * 0.35;
                return ((float)(tone + snap), true);
            }

            case Hit.Doom:
            {
                // The accent: a deep hall drum with a long belly.
                const double duration = 0.30;
                if (t >= duration)
                {
                    return (0f, false);
                }

                var frequency = Mathf.Lerp(150f, 55f, (float)(t / duration));
                var tone = Math.Sin(Mathf.Tau * frequency * t) * Math.Exp(-12.0 * t) * 0.9;
                var snap = Noise() * Math.Exp(-90.0 * t) * 0.2;
                return ((float)(tone + snap), true);
            }

            case Hit.Yelp:
            {
                // A struck rest: the drum complains, high and wrong.
                const double duration = 0.18;
                if (t >= duration)
                {
                    return (0f, false);
                }

                var frequency = 880.0 + (260.0 * Math.Sin(t * 60.0));
                var tone = Math.Sin(Mathf.Tau * frequency * t) * Math.Exp(-26.0 * t) * 0.5;
                return ((float)tone, true);
            }

            case Hit.Crack:
            {
                // A miss: dull splintering, no pitch to be proud of.
                const double duration = 0.07;
                if (t >= duration)
                {
                    return (0f, false);
                }

                return ((float)(Noise() * Math.Exp(-80.0 * t) * 0.45), true);
            }

            default:
            {
                // Carving: the soft scrape of new notches being cut between phrases.
                const double duration = 0.35;
                if (t >= duration)
                {
                    return (0f, false);
                }

                return ((float)(Noise() * Math.Exp(-9.0 * t) * 0.12), true);
            }
        }
    }

    private double Noise() => (_rng.NextDouble() * 2.0) - 1.0;
}
