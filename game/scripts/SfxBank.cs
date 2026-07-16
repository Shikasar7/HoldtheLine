using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Procedurally synthesized placeholder SFX — plan §8's four core sounds, built as short 16-bit PCM
/// tones at load time so the prototype needs no external audio assets. Swap for real samples later.
/// A small voice pool lets rapid events (a multi-hit turn) overlap instead of cutting each other off.
/// </summary>
public sealed class SfxBank
{
    private const int MixRate = 22050;
    private readonly Dictionary<string, AudioStreamWav> _sounds = new();
    private readonly AudioStreamPlayer[] _voices;
    private int _next;

    private enum Wave { Sine, Square, Triangle }

    public SfxBank(Node owner, int voices = 4)
    {
        _voices = new AudioStreamPlayer[voices];
        for (int i = 0; i < voices; i++)
        {
            _voices[i] = new AudioStreamPlayer { VolumeDb = -9f };
            owner.AddChild(_voices[i]);
        }

        _sounds["play"] = Tone(196f, 0.10f, 8f, Wave.Triangle, 0.5f);            // deploy — soft "thup"
        _sounds["move"] = Tone(392f, 0.05f, 14f, Wave.Sine, 0.35f);             // move — light tick
        _sounds["attack"] = Tone(120f, 0.12f, 10f, Wave.Square, 0.5f, noise: 0.4f); // impact
        _sounds["leaderhit"] = Tone(72f, 0.24f, 5f, Wave.Sine, 0.75f);          // face damage — low boom
    }

    public void Play(string name)
    {
        if (!_sounds.TryGetValue(name, out var wav))
            return;
        var voice = _voices[_next];
        _next = (_next + 1) % _voices.Length;
        voice.Stream = wav;
        voice.Play();
    }

    private static AudioStreamWav Tone(float freq, float dur, float decay, Wave wave, float amp, float noise = 0f)
    {
        int samples = (int)(dur * MixRate);
        var data = new byte[samples * 2];
        uint rng = 0x1234567u;

        for (int i = 0; i < samples; i++)
        {
            float t = (float)i / MixRate;
            float phase = freq * t;
            float w = wave switch
            {
                Wave.Square => Mathf.Sin(phase * Mathf.Tau) >= 0f ? 1f : -1f,
                Wave.Triangle => 2f * Mathf.Abs(2f * (phase - Mathf.Floor(phase + 0.5f))) - 1f,
                _ => Mathf.Sin(phase * Mathf.Tau),
            };
            if (noise > 0f)
            {
                rng = rng * 1664525u + 1013904223u;
                float n = ((rng >> 8) & 0xFFFF) / 32768f - 1f;
                w = Mathf.Lerp(w, n, noise);
            }
            float env = Mathf.Exp(-decay * t);
            short s = (short)(Mathf.Clamp(w * env * amp, -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }

        return new AudioStreamWav
        {
            Format = AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = MixRate,
            Stereo = false,
            Data = data,
        };
    }
}
