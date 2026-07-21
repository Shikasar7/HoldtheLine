"""Generate the three original combat cues added for the Duskweaver FX pass.

The synthesis is deterministic and dependency-free. The short cues are emitted
as 44.1 kHz PCM WAV files, which Godot imports directly.
"""

from __future__ import annotations

import math
import random
import wave
from pathlib import Path


RATE = 44_100
ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "game" / "assets" / "audio" / "sfx"


def envelope(t: float, attack: float, decay: float) -> float:
    return min(1.0, t / attack) * math.exp(-decay * t)


def noise(rng: random.Random) -> float:
    return rng.random() * 2.0 - 1.0


def molten_slam(t: float, rng: random.Random) -> float:
    # Falling blade whistle, low iron impact, then a short ember crackle tail.
    whistle = math.sin(2 * math.pi * (920 - 620 * min(t / 0.28, 1)) * t) * envelope(t, 0.012, 5.2)
    impact_t = max(0.0, t - 0.24)
    impact = math.sin(2 * math.pi * 72 * impact_t) * envelope(impact_t, 0.003, 7.5) if t >= 0.24 else 0.0
    crackle = noise(rng) * envelope(impact_t, 0.001, 5.8) if t >= 0.24 else 0.0
    return 0.24 * whistle + 0.62 * impact + 0.25 * crackle


def spell_ward(t: float, rng: random.Random) -> float:
    # Glassy rune bloom, resonant block, crystalline shards.
    bloom = sum(math.sin(2 * math.pi * f * t) for f in (523.25, 783.99, 1046.5)) / 3
    hit_t = max(0.0, t - 0.20)
    hit = math.sin(2 * math.pi * 196 * hit_t) * envelope(hit_t, 0.002, 10) if t >= 0.20 else 0.0
    shards = noise(rng) * math.sin(2 * math.pi * 2350 * hit_t) * envelope(hit_t, 0.001, 8) if t >= 0.20 else 0.0
    return 0.38 * bloom * envelope(t, 0.025, 3.8) + 0.38 * hit + 0.16 * shards


def phoenix_rebirth(t: float, rng: random.Random) -> float:
    # Rising harmonic flame with a soft ignition rush and a resolved high chime.
    rise = 180 + 540 * min(t / 0.85, 1) ** 1.5
    tone = math.sin(2 * math.pi * rise * t) + 0.45 * math.sin(2 * math.pi * rise * 1.5 * t)
    flame = noise(rng) * envelope(t, 0.05, 2.2)
    chime_t = max(0.0, t - 0.62)
    chime = sum(math.sin(2 * math.pi * f * chime_t) for f in (659.25, 987.77, 1318.51)) / 3
    chime *= envelope(chime_t, 0.012, 3.4) if t >= 0.62 else 0.0
    return 0.30 * tone * envelope(t, 0.07, 1.65) + 0.14 * flame + 0.34 * chime


def render(name: str, duration: float, synth) -> None:
    rng = random.Random(name)
    samples = []
    for i in range(round(duration * RATE)):
        value = max(-1.0, min(1.0, synth(i / RATE, rng)))
        samples.append(round(value * 23_000))

    OUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as wav:
        wav.setnchannels(1)
        wav.setsampwidth(2)
        wav.setframerate(RATE)
        wav.writeframes(b"".join(sample.to_bytes(2, "little", signed=True) for sample in samples))


if __name__ == "__main__":
    render("molten_slam", 0.72, molten_slam)
    render("spell_ward", 0.72, spell_ward)
    render("phoenix_rebirth", 1.25, phoenix_rebirth)
