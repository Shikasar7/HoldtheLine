using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Procedurally synthesized placeholder SFX — plan §10 item 7's ~12 cues, built as short 16-bit PCM
/// tones (and two multi-note phrases for win/lose) at load time so the prototype needs no external
/// audio assets. Swap for real samples later. A small voice pool lets rapid events (a multi-hit turn)
/// overlap instead of cutting each other off.
/// </summary>
public sealed class SfxBank
{
    private const int MixRate = 22050;
    private readonly Dictionary<string, AudioStreamWav> _sounds = new();
    private readonly AudioStreamPlayer[] _voices;
    private int _next;

    private enum Wave { Sine, Square, Triangle }

    public SfxBank(Node owner, int voices = 6)
    {
        _voices = new AudioStreamPlayer[voices];
        for (int i = 0; i < voices; i++)
        {
            _voices[i] = new AudioStreamPlayer { VolumeDb = -9f };
            owner.AddChild(_voices[i]);
        }

        // --- combat ---
        _sounds["attack"] = Tone(120f, 0.12f, 10f, Wave.Square, 0.5f, noise: 0.4f);   // melee / ranged impact
        _sounds["shoot"] = Tone(520f, 0.12f, 9f, Wave.Triangle, 0.4f, noise: 0.15f);  // ranged launch — airy "pew"
        _sounds["cast"] = Tone(784f, 0.16f, 6f, Wave.Triangle, 0.42f);                // 指令施放 — shimmer
        _sounds["death"] = Tone(90f, 0.22f, 7f, Wave.Square, 0.5f, noise: 0.55f);     // unit death — low crunch
        _sounds["leaderhit"] = Tone(60f, 0.30f, 4f, Wave.Sine, 0.85f, noise: 0.15f);  // face damage — heavier low boom
        // --- flow / feedback ---
        _sounds["play"] = Tone(196f, 0.10f, 8f, Wave.Triangle, 0.5f);                 // deploy — soft "thup"
        _sounds["move"] = Tone(392f, 0.05f, 14f, Wave.Sine, 0.35f);                   // move — light tick
        _sounds["draw"] = Tone(1180f, 0.045f, 20f, Wave.Triangle, 0.28f, noise: 0.3f);// draw — paper flick
        _sounds["turnstart"] = Tone(262f, 0.18f, 6f, Wave.Triangle, 0.45f);           // turn banner — soft horn
        _sounds["tide"] = Tone(55f, 0.5f, 3f, Wave.Sine, 0.6f, noise: 0.1f);          // 压力潮汐 — ominous swell
        _sounds["button"] = Tone(330f, 0.03f, 26f, Wave.Square, 0.3f);                // UI tick
        // --- win / lose stings (short arpeggio phrases, ~2s) ---
        _sounds["victory"] = Phrase(new[]                                             // ascending major, resolves high
        {
            (523f, 0.00f, 0.45f), (659f, 0.16f, 0.45f), (784f, 0.32f, 0.55f), (1047f, 0.52f, 1.25f),
        }, 0.5f);
        _sounds["defeat"] = Phrase(new[]                                              // descending minor, settles low
        {
            (440f, 0.00f, 0.55f), (349f, 0.34f, 0.65f), (262f, 0.72f, 1.5f),
        }, 0.55f);
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

        return Wav(data);
    }

    /// <summary>Sum several decaying triangle notes into one buffer — a tiny arpeggio for the win/lose stings.</summary>
    private static AudioStreamWav Phrase((float Freq, float Start, float Dur)[] notes, float amp)
    {
        float total = 0f;
        foreach (var n in notes)
            total = Mathf.Max(total, n.Start + n.Dur);
        int samples = (int)(total * MixRate);
        var buf = new float[samples];

        foreach (var (freq, start, dur) in notes)
        {
            int s0 = (int)(start * MixRate);
            int sn = (int)(dur * MixRate);
            for (int i = 0; i < sn && s0 + i < samples; i++)
            {
                float t = (float)i / MixRate;
                float phase = freq * t;
                float w = 2f * Mathf.Abs(2f * (phase - Mathf.Floor(phase + 0.5f))) - 1f; // triangle
                float atk = Mathf.Min(1f, t / 0.02f);   // quick attack ramp
                buf[s0 + i] += w * atk * Mathf.Exp(-3.0f * t);
            }
        }

        var data = new byte[samples * 2];
        for (int i = 0; i < samples; i++)
        {
            short s = (short)(Mathf.Clamp(buf[i] * amp, -1f, 1f) * short.MaxValue);
            data[i * 2] = (byte)(s & 0xFF);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return Wav(data);
    }

    private static AudioStreamWav Wav(byte[] data) => new()
    {
        Format = AudioStreamWav.FormatEnum.Format16Bits,
        MixRate = MixRate,
        Stereo = false,
        Data = data,
    };
}
