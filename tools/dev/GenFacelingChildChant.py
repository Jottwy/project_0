#!/usr/bin/env python3
"""Synthesise the faceling children's DISTANT CHANT bank (ADR-094 Enmienda 9, kind 4).

Run:  python tools/dev/GenFacelingChildChant.py

Writes mono 44.1 kHz 16-bit WAVs into Assets/_Migration/STPIntegration/Facelings/Audio.
MONO on purpose: Unity only spatialises mono clips properly, and every one of these plays
through a 3D AudioSource with a hard distance cutoff (ADR-042).

WHY A CHANT AND NOT A QUIETER GIGGLE. Enmienda 9 splits the pack's voice into bands by distance,
and the far band is not "the near band, further away" — it is a different sound with a different
job. The giggle is a reaction to YOU; this is not. It is what the floor sounds like when it is
inhabited and has not noticed you yet, so it has to work heard for an hour without becoming
furniture, and it has to survive an 80 m falloff, which is why it sits low and sustained.

THE MELODY is the taunt cantillation every playground on earth converges on — sol-mi-la-sol-mi,
the "na-na na-na-na". Chosen because it is instantly legible as children and needs no words to be
so; slowed to a third of playground tempo and detuned, the same five notes stop being teasing.

THE SYNTHESIS, deliberately not "a sine with an envelope", which is what makes a generated voice
read as a beep:
  * SOURCE  = jittered glottal pulse train (a real larynx never repeats a period exactly). The
              jitter is most of what separates a voice from an oscillator.
  * TRACT   = three resonant band-passes at CHILD formant frequencies — a child's vocal tract is
              about three quarters the length of an adult's, so every formant sits much higher.
              This is the single parameter that decides the age of the thing singing.
  * TWO VOICES per clip, detuned a few cents and offset by a few tens of milliseconds. One voice
              is a soloist and reads as a person; two slightly-out-of-tune ones read as a GROUP,
              which is the fact this bank exists to communicate.
  * ROOM    = a Schroeder reverb with a long tail. Not decoration: the tail is what says "this is
              arriving from somewhere down the corridor" before the distance curve has said
              anything at all.

Deterministic — same seed, same bytes. The parameters below are the tuning surface.
"""

import math
import os
import random
import struct
import wave

SR = 44100
OUT_DIR = os.path.join(
    os.path.dirname(os.path.abspath(__file__)),
    "..",
    "..",
    "Assets",
    "_Migration",
    "STPIntegration",
    "Facelings",
    "Audio",
)

# sol-mi-la-sol-mi. Written as semitone offsets from A4 so the transpose below is one number.
TAUNT = [-2, -5, 0, -2, -5]

# Child fundamentals sit around an octave above an adult male's. Each clip gets its own base so
# the bank does not read as one child recorded three times.
CLIPS = [
    # (name, semitone transpose, note seconds, gap seconds, seed)
    ("FacelingChild_Chant_01.wav", 12, 0.62, 0.10, 20940),
    ("FacelingChild_Chant_02.wav", 10, 0.78, 0.16, 20941),
    ("FacelingChild_Chant_03.wav", 14, 0.54, 0.07, 20942),
]

# A child's tract: F1/F2/F3 far above the adult values GenPhantomVoices uses. Vowel is an open
# "aa" drifting toward "oo" across each note, which is what gives it a sung rather than spoken
# quality.
FORMANTS_OPEN = [(900.0, 90.0, 1.0), (2300.0, 130.0, 0.55), (3350.0, 190.0, 0.22)]
FORMANTS_CLOSED = [(560.0, 80.0, 1.0), (1150.0, 120.0, 0.42), (2900.0, 200.0, 0.15)]


def note_hz(semitones):
    return 440.0 * (2.0 ** (semitones / 12.0))


def glottal_source(n, f0_at, rng, jitter=0.021):
    """Pulse train with per-period jitter, band-limited by using a raised-cosine pulse shape.

    A perfectly periodic train sounds synthetic no matter what you filter it with; the jitter
    (and the shimmer on pulse amplitude) is most of the difference between a voice and a buzz.
    """
    out = [0.0] * n
    i = 0
    while i < n:
        f0 = f0_at(i)
        if f0 <= 0.0:
            i += 1
            continue
        period = SR / f0
        period *= 1.0 + rng.uniform(-jitter, jitter)
        width = max(3, int(period * 0.32))
        amp = 1.0 + rng.uniform(-0.13, 0.13)
        for k in range(width):
            p = i + k
            if p >= n:
                break
            # Raised cosine: soft enough not to alias into a buzz saw, sharp enough to excite
            # the formants above.
            out[p] += amp * 0.5 * (1.0 - math.cos(2.0 * math.pi * k / width))
        i += max(1, int(round(period)))
    return out


def band_pass(x, centre_at, bw_at, gain):
    """One resonant biquad, coefficients recomputed per sample so the formant can SWEEP.

    The sweep is the vowel changing shape mid-note. A fixed formant reads as a synth pad.
    """
    n = len(x)
    out = [0.0] * n
    z1 = z2 = 0.0
    for i in range(n):
        f = centre_at(i)
        bw = bw_at(i)
        w = 2.0 * math.pi * f / SR
        r = math.exp(-math.pi * bw / SR)
        a1 = 2.0 * r * math.cos(w)
        a2 = -(r * r)
        b0 = (1.0 - r) * math.sqrt(1.0 - 2.0 * r * math.cos(2.0 * w) + r * r)
        y = b0 * x[i] + a1 * z1 + a2 * z2
        z2 = z1
        z1 = y
        out[i] = y * gain
    return out


def one_voice(seconds, transpose, note_s, gap_s, seed, detune_cents, start_offset_s):
    n = int(seconds * SR)
    rng = random.Random(seed)

    # ── the melody line as a per-sample frequency function ──
    detune = 2.0 ** (detune_cents / 1200.0)
    schedule = []  # (start, end, hz)
    t = start_offset_s
    for step in TAUNT:
        hz = note_hz(step + transpose) * detune
        schedule.append((t, t + note_s, hz))
        t += note_s + gap_s

    # Vibrato: slow, shallow, and with its own slow drift so it never locks to a grid.
    vib_hz = 4.4 + rng.uniform(-0.5, 0.5)
    vib_depth = 0.016

    def f0_at(i):
        tt = i / SR
        for a, b, hz in schedule:
            if a <= tt < b:
                # A short portamento into each note. Children do not hit pitches cleanly, and
                # the slide is a large part of why this reads as singing rather than as tones.
                into = (tt - a) / max(1e-6, note_s)
                slide = 1.0 - 0.045 * math.exp(-into * 26.0)
                vib = 1.0 + vib_depth * math.sin(2.0 * math.pi * vib_hz * tt)
                # Slow downward drift across the note: the breath running out.
                drift = 1.0 - 0.012 * into
                return hz * slide * vib * drift
        return 0.0

    src = glottal_source(n, f0_at, rng)

    # ── amplitude envelope, per note ──
    env = [0.0] * n
    for a, b, _ in schedule:
        ia, ib = int(a * SR), min(n, int(b * SR))
        span = max(1, ib - ia)
        atk = int(span * 0.18)
        rel = int(span * 0.42)
        for k in range(ia, ib):
            k0 = k - ia
            if k0 < atk:
                e = k0 / max(1, atk)
                e = e * e * (3.0 - 2.0 * e)  # smoothstep: no click, no thump
            elif k0 > span - rel:
                e = (span - k0) / max(1, rel)
                e = e * e
            else:
                e = 1.0
            env[k] = e
    src = [s * env[i] for i, s in enumerate(src)]

    # ── vowel sweep: open at the attack, closing toward the release ──
    def make_at(idx, key):
        def at(i):
            tt = i / SR
            for a, b, _ in schedule:
                if a <= tt < b:
                    u = (tt - a) / max(1e-6, note_s)
                    o = FORMANTS_OPEN[idx][key]
                    c = FORMANTS_CLOSED[idx][key]
                    return o + (c - o) * min(1.0, u * 1.15)
            return FORMANTS_OPEN[idx][key]

        return at

    voiced = [0.0] * n
    for idx in range(3):
        band = band_pass(src, make_at(idx, 0), make_at(idx, 1), FORMANTS_OPEN[idx][2])
        for i in range(n):
            voiced[i] += band[i]

    # A little breath noise riding the same envelope — a dry larynx sounds like a machine.
    for i in range(n):
        voiced[i] += rng.uniform(-1.0, 1.0) * env[i] * 0.02

    return voiced


def reverb(x, room=0.82, mix=0.44):
    """Schroeder: four combs into two allpasses. The long tail is the corridor."""
    n = len(x)
    combs = [(1687, room), (1601, room * 0.98), (2053, room * 0.96), (2251, room * 0.94)]
    acc = [0.0] * n
    for delay, fb in combs:
        buf = [0.0] * delay
        p = 0
        for i in range(n):
            v = buf[p]
            acc[i] += v
            buf[p] = x[i] + v * fb
            p += 1
            if p >= delay:
                p = 0
    acc = [v * 0.25 for v in acc]

    for delay, g in ((389, 0.5), (127, 0.5)):
        buf = [0.0] * delay
        p = 0
        for i in range(n):
            v = buf[p]
            y = -g * acc[i] + v
            buf[p] = acc[i] + g * y
            acc[i] = y
            p += 1
            if p >= delay:
                p = 0

    return [x[i] * (1.0 - mix) + acc[i] * mix for i in range(n)]


def write_wav(path, samples):
    peak = max(1e-9, max(abs(s) for s in samples))
    # -3 dBFS. Headroom on purpose: the bank plays at a 1.15 volume multiplier (Enmienda 9's
    # per-kind range table), and a clip mastered at peak would clip instead of carry.
    scale = 0.707 / peak
    frames = b"".join(
        struct.pack("<h", int(max(-32767, min(32767, s * scale * 32767)))) for s in samples
    )
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(frames)


def main():
    out = os.path.abspath(OUT_DIR)
    os.makedirs(out, exist_ok=True)

    for name, transpose, note_s, gap_s, seed in CLIPS:
        span = len(TAUNT) * (note_s + gap_s)
        seconds = span + 2.4  # room for the reverb tail to actually decay inside the clip

        # TWO children, detuned and offset. One is a soloist; two slightly out of tune is a group.
        a = one_voice(seconds, transpose, note_s, gap_s, seed, -7.0, 0.05)
        b = one_voice(seconds, transpose, note_s, gap_s, seed + 977, +11.0, 0.14)
        mixed = [a[i] * 0.62 + b[i] * 0.48 for i in range(len(a))]

        path = os.path.join(out, name)
        write_wav(path, reverb(mixed))
        print(f"wrote {path} ({seconds:.1f}s)")


if __name__ == "__main__":
    main()
