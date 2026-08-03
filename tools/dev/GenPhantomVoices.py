#!/usr/bin/env python3
"""Synthesise the robapieles' non-scream voices (ADR-048 banks 2 and 3).

Run:  python tools/dev/GenPhantomVoices.py

Writes mono 44.1 kHz 16-bit WAVs into Assets/_Migration/STPIntegration/RemoteAvatar/Audio.
MONO on purpose: Unity only spatialises mono clips properly, and every one of these is played
through a 3D AudioSource with a hard distance cutoff (ADR-042).

WHY THIS FILE EXISTS AT ALL: the three PhantomScream_*.wav in that folder were produced by a
one-off that was never committed, so nobody could regenerate or tweak them. These two are
reproducible — same seed, same bytes — and the parameters are the tuning surface.

The synthesis is deliberately NOT "a sine with an envelope", which is what makes a generated
creature sound read as a beep:
  * GRUNT  = jittered glottal pulse train through three resonant band-passes (formants), with a
             downward pitch glide. The glide is what turns a hum into a grunt.
  * BREATH = band-passed noise shaped into an inhale/exhale pair, with the band centre sweeping
             (a throat changing shape) and a body rumble underneath.
"""

import math
import os
import random
import struct
import wave

SR = 44100
OUT_DIR = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..", "..", "Assets", "_Migration", "STPIntegration", "RemoteAvatar", "Audio",
)


# ── DSP primitives ───────────────────────────────────────────────────────────────────────────────

class BiquadBP:
    """Constant-skirt-gain band-pass. Coefficients recomputed on demand so the centre can sweep."""

    def __init__(self, f0, q):
        self.x1 = self.x2 = self.y1 = self.y2 = 0.0
        self.set(f0, q)

    def set(self, f0, q):
        f0 = max(20.0, min(f0, SR * 0.45))
        w0 = 2.0 * math.pi * f0 / SR
        alpha = math.sin(w0) / (2.0 * q)
        cosw = math.cos(w0)
        a0 = 1.0 + alpha
        self.b0 = alpha / a0
        self.b1 = 0.0
        self.b2 = -alpha / a0
        self.a1 = (-2.0 * cosw) / a0
        self.a2 = (1.0 - alpha) / a0

    def step(self, x):
        y = (self.b0 * x + self.b1 * self.x1 + self.b2 * self.x2
             - self.a1 * self.y1 - self.a2 * self.y2)
        self.x2, self.x1 = self.x1, x
        self.y2, self.y1 = self.y1, y
        return y


def adsr(n, attack, decay, sustain, release):
    """Per-sample envelope. Times in seconds; `sustain` is a level, not a time."""
    a = max(1, int(attack * SR))
    d = max(1, int(decay * SR))
    r = max(1, int(release * SR))
    s = max(0, n - a - d - r)
    out = []
    for i in range(a):
        out.append((i / a) ** 0.7)                       # slightly convex: less of a click
    for i in range(d):
        out.append(1.0 + (sustain - 1.0) * (i / d))
    out.extend([sustain] * s)
    for i in range(r):
        out.append(sustain * (1.0 - i / r) ** 1.8)       # concave tail: dies away, never cuts
    return (out + [0.0] * n)[:n]


def soft_clip(x):
    """Tanh-ish saturation. Adds the harmonics that make a voice sound like flesh, not maths."""
    return math.tanh(1.6 * x) / math.tanh(1.6)


def write_wav(name, samples, peak=0.82):
    hi = max(1e-9, max(abs(s) for s in samples))
    gain = peak / hi
    path = os.path.abspath(os.path.join(OUT_DIR, name))
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(b"".join(
            struct.pack("<h", max(-32768, min(32767, int(s * gain * 32767.0))))
            for s in samples))
    print("wrote %-34s %6.2f s  %d B" % (name, len(samples) / SR, os.path.getsize(path)))


# ── Bank 2: the reaction grunt ───────────────────────────────────────────────────────────────────

def grunt(seed, f0, dur, glide=0.82, growl=0.0):
    """Short, low, guttural. What it makes when it hears something worth walking toward.

    A glottal pulse train (not a sine) through three formants. `glide` is the fraction of f0 it
    falls to by the end — the single most important parameter here, because a steady pitch reads
    as a machine and a falling one reads as an exhalation.
    """
    rnd = random.Random(seed)
    n = int(dur * SR)
    env = adsr(n, 0.018, 0.10, 0.55, dur * 0.62)

    # Three resonances, roughly a low back vowel, dropped an octave into a chest register.
    forms = [BiquadBP(310.0, 6.0), BiquadBP(780.0, 8.0), BiquadBP(2100.0, 11.0)]
    weights = [1.0, 0.55, 0.18]

    out = []
    phase = 0.0
    for i in range(n):
        t = i / n
        f = f0 * (1.0 + (glide - 1.0) * t)
        # Jitter: real vocal folds are not a clock. Without it the pulse train buzzes.
        f *= 1.0 + rnd.uniform(-0.022, 0.022)
        # Sub-harmonic roughness — the "creaky" register that makes a grunt sound like an animal.
        if growl > 0.0:
            f *= 1.0 + growl * math.sin(2.0 * math.pi * 31.0 * i / SR)

        phase += f / SR
        if phase >= 1.0:
            phase -= 1.0
        # Narrow glottal pulse: energy across the whole spectrum for the formants to shape.
        pulse = math.exp(-28.0 * phase) * 2.0 - 0.28
        pulse += rnd.uniform(-0.05, 0.05)  # breath noise through the same throat

        s = sum(w * fl.step(pulse) for fl, w in zip(forms, weights))
        out.append(soft_clip(s * 0.75) * env[i])
    return out


# ── Bank 3: the stalking breath ──────────────────────────────────────────────────────────────────

def breath(seed, dur, cycles=2, centre=(700.0, 1700.0), rumble=95.0):
    """Long, quiet, rhythmic. What you hear when it is behind you and has not decided yet.

    Band-passed noise with the centre SWEEPING per cycle (a throat changing shape), shaped into
    inhale→exhale pairs. Quiet by design: this is the sound that has to make a corridor feel
    occupied without ever announcing a position.
    """
    rnd = random.Random(seed)
    n = int(dur * SR)
    bp = BiquadBP(centre[0], 1.1)
    low = BiquadBP(rumble, 2.5)

    out = []
    per = n / cycles
    for i in range(n):
        c = (i % per) / per  # 0..1 within one breath

        # Inhale: slow rise, sharp top. Exhale: quick onset, long decay. Asymmetric on purpose —
        # a symmetric envelope reads as a wave lapping, not as something breathing.
        if c < 0.45:
            amp = (c / 0.45) ** 1.6
        elif c < 0.52:
            amp = 1.0
        else:
            amp = (1.0 - (c - 0.52) / 0.48) ** 1.3

        # Overall arc so the loop point is not a hard edge.
        amp *= 0.35 + 0.65 * math.sin(math.pi * (i / n)) ** 0.5

        bp.set(centre[0] + (centre[1] - centre[0]) * (0.5 - 0.5 * math.cos(2.0 * math.pi * c)), 1.1)
        noise = rnd.uniform(-1.0, 1.0)
        body = low.step(noise) * 1.9  # chest, felt more than heard
        out.append(soft_clip((bp.step(noise) * 1.4 + body) * 0.5) * amp)
    return out


# ── Bake ─────────────────────────────────────────────────────────────────────────────────────────

def main():
    if not os.path.isdir(os.path.abspath(OUT_DIR)):
        raise SystemExit("audio dir not found: %s" % os.path.abspath(OUT_DIR))

    # Three of each: ProxyVocalHook picks at random, and one clip repeated is instantly a loop.
    write_wav("PhantomVoice_Grunt_A.wav", grunt(1101, 96.0, 0.72, glide=0.80, growl=0.05))
    write_wav("PhantomVoice_Grunt_B.wav", grunt(1102, 78.0, 0.95, glide=0.86, growl=0.09))
    write_wav("PhantomVoice_Grunt_C.wav", grunt(1103, 112.0, 0.58, glide=0.74, growl=0.02))

    # Quieter peak than the grunts: this one is ambience, not an event.
    write_wav("PhantomVoice_Breath_A.wav", breath(2201, 3.4, 2, (640.0, 1600.0)), peak=0.46)
    write_wav("PhantomVoice_Breath_B.wav", breath(2202, 4.1, 3, (720.0, 1850.0)), peak=0.42)
    write_wav("PhantomVoice_Breath_C.wav", breath(2203, 2.9, 2, (560.0, 1400.0)), peak=0.50)


if __name__ == "__main__":
    main()
