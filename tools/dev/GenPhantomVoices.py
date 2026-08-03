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

def heavy_step(seed, dur=0.42, thump=58.0, slap=0.55, drag=0.0):
    """The revealed creature's own footfall. Not a louder human step — a different EVENT.

    Three layers, because a single one always reads as a click or as a drum:
      * THUMP  — a low sine dropping in pitch (a mass landing, not a note). The pitch drop is what
                 makes it feel heavy; a fixed one sounds like a kick drum.
      * SLAP   — a short broadband transient through a mid band-pass: the actual contact.
      * TAIL   — a quiet resonant ring, so it sounds like it happened in a corridor.
    `drag` adds a filtered-noise scrape after the impact — bare feet that do not lift cleanly.
    """
    rnd = random.Random(seed)
    n = int(dur * SR)
    slap_bp = BiquadBP(1500.0, 1.4)
    tail_bp = BiquadBP(240.0, 7.0)
    drag_bp = BiquadBP(2600.0, 1.0)

    out = []
    phase = 0.0
    for i in range(n):
        t = i / n

        # Body: 58 Hz falling to ~60 % of it over the first third, then gone.
        f = thump * (1.0 - 0.40 * min(1.0, t * 3.0))
        phase += f / SR
        body = math.sin(2.0 * math.pi * phase) * math.exp(-13.0 * t)

        # Contact: loudest in the first ~25 ms.
        tr = rnd.uniform(-1.0, 1.0) * math.exp(-70.0 * t)
        contact = slap_bp.step(tr) * slap

        # Room.
        tail = tail_bp.step(rnd.uniform(-1.0, 1.0)) * 0.22 * math.exp(-7.0 * t)

        s = body * 1.15 + contact + tail
        if drag > 0.0:
            # Starts after the impact and fades — the foot peeling off the floor.
            scrape = drag_bp.step(rnd.uniform(-1.0, 1.0))
            s += scrape * drag * max(0.0, math.sin(math.pi * min(1.0, t * 1.7))) * 0.5
        out.append(soft_clip(s * 0.8))
    return out


def far_roar(seed, dur=3.2, f0=52.0, growl=0.16):
    """The answer from far away. What you hear a second after a shot, from somewhere out there.

    Built to survive DISTANCE, which is a different job from the close-up scream: the energy sits
    LOW (air eats treble long before bass), it is long enough to arrive as a shape rather than a
    click, and it swells before it falls so it reads as approaching even when it is not.

    Deliberately vague in space — that is the design. At this range you learn that something
    answered, not where it is.
    """
    rnd = random.Random(seed)
    n = int(dur * SR)
    # Low formants only. Anything above ~1.2 kHz would not survive the distance anyway, and putting
    # it there just makes the clip sound close.
    forms = [BiquadBP(140.0, 5.0), BiquadBP(430.0, 7.0), BiquadBP(900.0, 9.0)]
    weights = [1.0, 0.6, 0.22]
    sub = BiquadBP(70.0, 3.0)

    out = []
    phase = 0.0
    for i in range(n):
        t = i / n
        # Swell in, hold, long fall — an arc, not an envelope.
        env = math.sin(math.pi * min(1.0, t * 1.12)) ** 0.75

        f = f0 * (1.0 + 0.16 * math.sin(math.pi * t))        # rises then settles
        f *= 1.0 + rnd.uniform(-0.015, 0.015)
        f *= 1.0 + growl * math.sin(2.0 * math.pi * 23.0 * i / SR)  # chest roughness

        phase += f / SR
        if phase >= 1.0:
            phase -= 1.0
        pulse = math.exp(-16.0 * phase) * 2.0 - 0.30
        pulse += rnd.uniform(-0.035, 0.035)

        s = sum(w * fl.step(pulse) for fl, w in zip(forms, weights))
        s += sub.step(pulse) * 1.5                            # the part that actually travels
        out.append(soft_clip(s * 0.5) * env)
    return out


def sated(seed, dur=1.9, f0=68.0):
    """After it has killed. Lower, wetter, unhurried — it is done with you.

    Falls in pitch the whole way and never swells: the opposite shape to `far_roar`, so the two
    can never be confused even though both are low. That contrast is the information.
    """
    rnd = random.Random(seed)
    n = int(dur * SR)
    forms = [BiquadBP(220.0, 5.0), BiquadBP(560.0, 8.0)]
    weights = [1.0, 0.42]
    wet = BiquadBP(1900.0, 1.1)

    # Built ONCE. Calling adsr() inside the loop rebuilds the whole envelope per sample, which is
    # O(n²) — at 44.1 kHz that is ~11 billion operations for a two-second clip and the generator
    # simply never returns. Cost me a hung background job to notice.
    env = adsr(n, 0.05, 0.25, 0.6, dur * 0.55)

    out = []
    phase = 0.0
    for i in range(n):
        t = i / n
        f = f0 * (1.0 - 0.34 * t)                             # sinks the whole way
        f *= 1.0 + rnd.uniform(-0.03, 0.03)
        phase += f / SR
        if phase >= 1.0:
            phase -= 1.0
        pulse = math.exp(-22.0 * phase) * 2.0 - 0.26

        s = sum(w * fl.step(pulse) for fl, w in zip(forms, weights))
        # A breathy, liquid layer that fades in as it settles — the "wet" part.
        s += wet.step(rnd.uniform(-1.0, 1.0)) * 0.30 * t
        out.append(soft_clip(s * 0.7) * env[i])
    return out


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

    # Four, not three: footfalls repeat far more often than screams, so the loop is easier to hear.
    #
    # PEAK ~0.55 AND NOT ~0.9, ON PURPOSE: the hook multiplies these by `_revealedVolume` (~1.7) to
    # make the creature louder than a person. Normalised to 0.9 like the screams, that multiply
    # would clip on every single footfall — the loudness has to have somewhere to go.
    write_wav("PhantomStep_A.wav", heavy_step(3301, 0.42, 58.0, 0.55, drag=0.00), peak=0.55)
    write_wav("PhantomStep_B.wav", heavy_step(3302, 0.48, 51.0, 0.42, drag=0.35), peak=0.53)
    write_wav("PhantomStep_C.wav", heavy_step(3303, 0.38, 64.0, 0.62, drag=0.00), peak=0.57)
    write_wav("PhantomStep_D.wav", heavy_step(3304, 0.52, 46.0, 0.38, drag=0.55), peak=0.51)

    # The long-range answer to a gunshot. Headroom left on purpose: it is played through a very wide
    # curve and the client lifts it, so mastering it hot would clip the one sound that has to arrive
    # clean from hundreds of metres away.
    write_wav("PhantomVoice_Answer_A.wav", far_roar(4401, 3.2, 52.0, 0.16), peak=0.62)
    write_wav("PhantomVoice_Answer_B.wav", far_roar(4402, 4.0, 44.0, 0.22), peak=0.60)
    write_wav("PhantomVoice_Answer_C.wav", far_roar(4403, 2.7, 61.0, 0.11), peak=0.64)

    # After a kill. Falls the whole way and never swells — the exact opposite shape to the answer
    # roar, so the two can never be confused even though both sit low.
    write_wav("PhantomVoice_Sated_A.wav", sated(5501, 1.9, 68.0), peak=0.70)
    write_wav("PhantomVoice_Sated_B.wav", sated(5502, 2.4, 58.0), peak=0.68)
    write_wav("PhantomVoice_Sated_C.wav", sated(5503, 1.6, 76.0), peak=0.72)


if __name__ == "__main__":
    main()
