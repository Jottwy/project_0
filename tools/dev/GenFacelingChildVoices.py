#!/usr/bin/env python3
"""Cut and process the faceling children's voice banks from a real recording (ADR-094 Enmienda 10).

Run:  python tools/dev/GenFacelingChildVoices.py --source "<path to the source mp3/wav>"

Replaces the synthesised placeholder banks. Writes mono 44.1 kHz 16-bit WAVs into
Assets/_Migration/STPIntegration/Facelings/Audio. MONO on purpose: Unity only spatialises mono
clips properly, and every one of these plays through a 3D AudioSource with a hard distance
cutoff (ADR-042).

WHY THIS REPLACES THE SYNTHESIS. The Enmienda 9 chant was built out of glottal pulses and
formant filters, and the ADR said out loud that it was a placeholder — a generated voice reads as
generated no matter how carefully the formants are placed, because a real larynx has irregularity
that no parameter set reproduces. A real recording, cut well, wins outright. What the synthesis
was actually good for was proving the BANDS worked before any audio existed.

THE PIPELINE, and each stage is there for a reason:

  1. SEGMENT by energy envelope, twice. The first pass finds "scenes" (a gate with a 220 ms gap
     tolerance); the second cuts each scene into individual laughs with a 75 ms tolerance and a
     threshold local to that scene — the source's scenes differ by ~20 dB, and one global gate
     either merges the loud ones or misses the quiet ones entirely.

  2. MEASURE each cut: duration, spectral centroid, 85% rolloff, autocorrelation pitch,
     PERIODICITY (how voiced — breath and hiss score low, a sung note scores high) and ATTACK
     sharpness (a shriek jumps, a moan swells). Those last two are what actually separate a
     giggle from a scream; duration and loudness do not.

  3. SORT into banks by those features rather than by hand. The same recording therefore yields
     a different, sensible split if the source is ever swapped.

  4. PROCESS per bank, and this is the half that answers "no se siente distanciado". Distance is
     not volume. A sound 60 m away has lost its treble to air absorption, arrives after the room
     has smeared it, and reaches you with more reflected energy than direct. So the far banks get
     an aggressive low-pass, a long dark tail and a high wet mix; the whisper gets almost no tail
     at all and a presence lift, because a whisper WITH a tail is a whisper across the room.

  5. PITCH-SHIFT each clip by its own amount, by resampling (which moves formants and duration
     together, exactly like a smaller child). Eight clips from four sources become eight
     different children instead of four children heard twice.

The three impulse responses are generated here too, with FREQUENCY-DEPENDENT decay: a tail whose
bands all die at the same rate reads as an effect, while a real room eats treble far faster than
bass. The hall additionally carries FLUTTER — evenly spaced slap-back off parallel hard walls,
which is what a long corridor does to a sound, and the Backrooms are nothing but long corridors.

Deterministic: same source, same bytes.
"""

import argparse
import json
import math
import os
import struct
import subprocess
import sys
import wave

import numpy as np

SR = 44100
HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.abspath(
    os.path.join(HERE, "..", "..", "Assets", "_Migration", "STPIntegration", "Facelings", "Audio")
)

# ── banks ────────────────────────────────────────────────────────────────────────────────────
#
# `count`  how many clips to write. More variety where the player hears it most.
# `pitch`  semitone offsets, one per clip, cycled. Each clip becomes a different child.
# `pre`      colour applied BEFORE the reverb; `dry`/`wet` the convolution mix; `post` dynamics.
# `min_dur`  source length FLOOR. The gate stops a cut where the energy dips, which inside a
#            laugh is a breath, not an ending — so short events got clipped to a stub. Below this
#            the cut is EXTENDED into the real audio that follows rather than discarded: it is
#            the same child in the same room, so the extension is material, not padding.
# `max_dur`  source length cap, in seconds. Matched to the band's BEAT — a 5 s whisper on a 3.5 s
#            beat means every mouth overlaps itself, which is a drone, not a whisper. The cap is
#            on the SOURCE; the reverb tail is added after it and may run past.
# `peak`     final normalisation target. ffmpeg's compressor is a dynamics tool, not a leveller,
#            and left alone it landed the screams at 0.16 peak — the loudest event in the game,
#            quieter than the giggles.
#
# The numbers are the tuning surface; the comments say what each one is FOR.
BANKS = {
    # Kind 0. Near, ordinary, the sound you hear most — so the widest spread of voices.
    "Giggle": dict(
        count=10,
        min_dur=0.55, max_dur=1.2, peak=0.80,
        pitch=[0, 2, -1, 3, 1, -2, 4, 0, 2, -3],
        pre="highpass=f=115,lowpass=f=9500",
        # Room tail, present enough to sit in the building, dry enough to point at.
        dry=10, wet=3.6,
        post="acompressor=threshold=0.12:ratio=3:attack=8:release=180",
    ),
    # Kind 1. An event. Wants to carry, and wants the corridor behind it.
    "Scream": dict(
        count=6,
        min_dur=0.95, max_dur=3.0, peak=0.94,
        pitch=[0, -1, 2, 1, -2, 3],
        pre="highpass=f=95",
        dry=10, wet=4.0,
        post="acompressor=threshold=0.09:ratio=4:attack=4:release=260",
    ),
    # Kind 2. A cry FOR other packs — it is meant to be heard from far away, by them.
    "Call": dict(
        count=5,
        min_dur=1.5, max_dur=5.0, peak=0.86,
        pitch=[-2, 0, -4, 1, -3],
        pre="highpass=f=90,lowpass=f=5200",
        dry=10, wet=6.2,
        post="acompressor=threshold=0.10:ratio=3:attack=12:release=400",
    ),
    # Kind 3. Next to your ear. Almost no tail, and a presence lift instead — the intimacy lives
    # in the 1-4 kHz band, which is exactly the range a close mouth excites and a distant one has
    # already lost. A whisper WITH a tail is a whisper across the room.
    "Whisper": dict(
        count=9,
        min_dur=0.7, max_dur=2.2, peak=0.72,
        pitch=[-3, -1, -4, -2, 0, -5, -1, -3, -2],
        pre="highpass=f=170,lowpass=f=7000,equalizer=f=2400:width_type=o:width=1.6:g=3",
        dry=10, wet=1.5,
        post="acompressor=threshold=0.14:ratio=2.5:attack=10:release=140",
    ),
    # Kind 4. THE FAR BAND. Every number here is about distance:
    #   * pitch well down — a big shift also slows the clip, and a slow chant is a chant;
    #   * lowpass 2200 — AIR ABSORPTION. Sixty metres of air is a low-pass filter, and leaving
    #     the treble in is the one thing that makes a "distant" sound read as a near one turned
    #     down, which is precisely what Joel described as "plano";
    #   * wet above dry — past a certain distance you genuinely hear more room than source.
    "Chant": dict(
        count=8,
        min_dur=1.6, max_dur=6.0, peak=0.84,
        pitch=[-5, -7, -4, -8, -6, -3, -9, -5],
        pre="highpass=f=80,lowpass=f=2200",
        dry=6, wet=10,
        post="acompressor=threshold=0.10:ratio=2:attack=20:release=600,volume=1.8",
    ),
}

IR_FOR_BANK = {
    "Giggle": "ir_room.wav",
    "Scream": "ir_hall.wav",
    "Call": "ir_hall.wav",
    "Whisper": "ir_close.wav",
    "Chant": "ir_hall.wav",
}


# ── impulse responses ────────────────────────────────────────────────────────────────────────
def band_split(x):
    X = np.fft.rfft(x)
    f = np.fft.rfftfreq(len(x), 1.0 / SR)
    return (
        np.fft.irfft(X * (f < 500), len(x)),
        np.fft.irfft(X * ((f >= 500) & (f < 3000)), len(x)),
        np.fft.irfft(X * (f >= 3000), len(x)),
    )


def decay_tail(rng, seconds, rt_low, rt_mid, rt_high):
    n = int(seconds * SR)
    lo, mid, hi = band_split(rng.standard_normal(n).astype(np.float32))
    t = np.arange(n) / SR
    return (
        lo * np.exp(-6.908 * t / rt_low)
        + mid * np.exp(-6.908 * t / rt_mid)
        + hi * np.exp(-6.908 * t / rt_high)
    ).astype(np.float32)


def write_wav(path, x, peak=0.89):
    x = np.asarray(x, dtype=np.float32)
    m = float(np.abs(x).max())
    if m > 1e-9:
        x = x * (peak / m)
    frames = b"".join(
        struct.pack("<h", int(max(-32767, min(32767, v * 32767)))) for v in x
    )
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(frames)


def build_irs(tmp):
    rng = np.random.default_rng(4242)
    specs = {
        # A small hard room: barely a tail. For the whisper.
        "ir_close.wav": (0.34, 0.34, 0.26, 0.16, 0.004, [(0.007, 0.34), (0.013, 0.22)]),
        # An office floor: a place with furniture in it.
        "ir_room.wav": (1.05, 1.15, 0.85, 0.45, 0.011,
                        [(0.017, 0.30), (0.029, 0.24), (0.041, 0.17), (0.058, 0.12)]),
        # The corridor: long, dark, and fluttering.
        "ir_hall.wav": (2.7, 2.9, 2.0, 0.75, 0.024,
                        [(0.026 + k * 0.037, 0.30 * (0.72 ** k)) for k in range(9)]),
    }
    out = {}
    for name, (secs, rl, rm, rh, pre, early) in specs.items():
        body = decay_tail(rng, secs, rl, rm, rh)
        n = int(pre * SR) + len(body)
        buf = np.zeros(n, dtype=np.float32)
        buf[int(pre * SR):] = body
        buf[0] = 1.0  # the direct path
        for d, g in early:
            i = int(d * SR)
            if i < n:
                buf[i] += g
        path = os.path.join(tmp, name)
        write_wav(path, buf, peak=0.98)
        out[name] = path
    return out


# ── segmentation ─────────────────────────────────────────────────────────────────────────────
def envelope(x, win, hop):
    nf = max(1, (len(x) - win) // hop)
    e = np.empty(nf, dtype=np.float32)
    for i in range(nf):
        fr = x[i * hop:i * hop + win]
        e[i] = math.sqrt(float((fr * fr).mean()))
    return e


def gate(env, hi, lo, gap_frames, hop, pad_pre, pad_post, limit):
    spans = []
    i = 0
    while i < len(env):
        if env[i] < hi:
            i += 1
            continue
        j, quiet = i, 0
        while j < len(env):
            if env[j] < lo:
                quiet += 1
                if quiet > gap_frames:
                    break
            else:
                quiet = 0
            j += 1
        s = max(0, i * hop - pad_pre)
        e = min(limit, j * hop + pad_post)
        spans.append((s, e))
        i = j + 1
    return spans


def segment(a):
    """Two passes: scenes, then individual events inside each scene."""
    env = envelope(a, 1024, 256)
    floor, peak = np.percentile(env, 12), np.percentile(env, 99)
    scenes = gate(env, max(floor * 4.0, peak * 0.09), max(floor * 2.0, peak * 0.035),
                  int(0.22 * SR / 256), 256, int(0.04 * SR), int(0.10 * SR), len(a))
    scenes = [(s, e) for s, e in scenes if e - s > int(0.18 * SR)]

    events = []
    for s, e in scenes:
        x = a[s:e]
        env2 = envelope(x, 512, 128)
        if len(env2) < 8:
            continue
        # Local thresholds: the scenes differ by ~20 dB and one global gate cannot serve both.
        hi = max(float(np.percentile(env2, 55)), float(env2.max()) * 0.16)
        lo = max(float(np.percentile(env2, 30)), float(env2.max()) * 0.07)
        for ss, ee in gate(env2, hi, lo, int(0.075 * SR / 128), 128,
                           int(0.025 * SR), int(0.06 * SR), len(x)):
            if 0.30 <= (ee - ss) / SR <= 4.5:
                events.append((s + ss, s + ee))
    return events


def features(a, s, e):
    x = a[s:e]
    n = len(x)
    if n < 512 or float(np.abs(x).max()) < 0.02:
        return None
    sp = np.abs(np.fft.rfft(x * np.hanning(n)))
    fr = np.fft.rfftfreq(n, 1.0 / SR)
    tot = float(sp.sum()) + 1e-9
    cum = np.cumsum(sp) / tot
    ac = np.correlate(x, x, "full")[n - 1:]
    lo_lag, hi_lag = SR // 900, SR // 130
    if hi_lag >= len(ac):
        return None
    k = lo_lag + int(np.argmax(ac[lo_lag:hi_lag]))
    env = envelope(x, 512, 128)
    return dict(
        s=int(s), e=int(e), dur=n / SR,
        cent=float((sp * fr).sum() / tot),
        roll=float(fr[np.searchsorted(cum, 0.85)]),
        pitch=SR / k,
        # How voiced: breath and hiss score low, a sung note scores high.
        per=float(ac[k] / (ac[0] + 1e-9)),
        # How sudden: a shriek jumps, a moan swells.
        atk=float(env.max() / (env[: max(1, len(env) // 6)].mean() + 1e-9)),
        peak=float(np.abs(x).max()),
    )


def classify(rows):
    """Sort events into banks by what they ARE, not by hand."""
    cent = np.array([r["cent"] for r in rows])
    per = np.array([r["per"] for r in rows])
    atk = np.array([r["atk"] for r in rows])
    c_hi, p_hi, a_hi = (np.percentile(cent, 62), np.percentile(per, 55), np.percentile(atk, 60))

    picks = {k: [] for k in BANKS}
    for r in rows:
        bright = r["cent"] >= c_hi
        voiced = r["per"] >= p_hi
        sudden = r["atk"] >= a_hi

        # Screams: bright AND sudden AND loud. All three, or every emphatic laugh qualifies.
        if bright and sudden and r["peak"] > 0.25:
            picks["Scream"].append(r)
        # Calls: long, voiced, and not sudden — a held cry rather than a burst.
        elif r["dur"] >= 1.4 and voiced and not sudden:
            picks["Call"].append(r)
        # Chant material: the most sustained and most periodic. It gets pitched down hard, so
        # what matters is that it holds a note at all.
        elif r["dur"] >= 1.0 and voiced:
            picks["Chant"].append(r)
        # Whisper material: dark and unvoiced — breath, murmur, the hiss between laughs.
        elif not bright and not voiced:
            picks["Whisper"].append(r)
        else:
            picks["Giggle"].append(r)

    # Nothing may end up empty: a silent bank is a silent creature. Backfill from the fullest
    # neighbour, ranked by how well each candidate suits the bank being filled.
    order = dict(
        Giggle=lambda r: -r["per"],
        Scream=lambda r: -(r["peak"] * r["atk"]),
        Call=lambda r: -r["dur"],
        Whisper=lambda r: r["cent"],
        Chant=lambda r: -(r["dur"] * r["per"]),
    )
    for bank, want in BANKS.items():
        need = want["count"]
        if len(picks[bank]) < need:
            spare = sorted(
                (r for b, rs in picks.items() if b != bank for r in rs), key=order[bank]
            )
            have = {(r["s"], r["e"]) for r in picks[bank]}
            for r in spare:
                if len(picks[bank]) >= need:
                    break
                if (r["s"], r["e"]) not in have:
                    picks[bank].append(r)
                    have.add((r["s"], r["e"]))
        picks[bank] = sorted(picks[bank], key=order[bank])[:need]
    return picks


def settle_point(a, e, limit):
    """Where the sound actually STOPS, searching forward from the gate's guess.

    The gate ends a cut the moment energy dips below its threshold, and inside a laugh that dip is
    a breath. Cutting there is the "corte forzado" the play-test heard. This walks forward looking
    for the QUIETEST short window in the next stretch — the real valley between events — and ends
    the clip there instead.
    """
    win = int(0.020 * SR)
    best_i, best_v = e, None
    i = e
    while i + win <= limit:
        v = float(np.abs(a[i:i + win]).mean())
        if best_v is None or v < best_v:
            best_v, best_i = v, i
            if v < 0.004:  # genuinely silent; no point looking further
                break
        i += win
    return best_i + win


def cut(a, r, min_dur=None, max_dur=None):
    """Extract one event, ending where it really ends and fading out over a musical length.

    THREE fixes for "se cortan muy rápido... así forzado" (Joel, play-test 2026-08-25):
      * a length FLOOR, extending into the following audio rather than shipping a stub;
      * an end found by `settle_point` rather than taken from the gate;
      * a fade-out an order of magnitude longer than the old 12 ms, on a cosine rather than a
        line — a linear fade still has a corner in it, and the ear hears corners as clicks.
    """
    s0, e = r["s"], r["e"]

    if min_dur is not None and (e - s0) < int(min_dur * SR):
        e = min(len(a), s0 + int(min_dur * SR))
    e = min(settle_point(a, e, min(len(a), e + int(0.45 * SR))), len(a))
    if max_dur is not None:
        e = min(e, s0 + int(max_dur * SR))

    x = np.array(a[s0:e], dtype=np.float32)
    n = len(x)
    if n < 64:
        return x

    # In fast (a laugh starts abruptly and should), out slow.
    fin = min(int(0.010 * SR), n // 8)
    fout = min(max(int(0.075 * SR), int(n * 0.22)), int(0.22 * SR), n // 2)

    if fin > 1:
        x[:fin] *= np.linspace(0.0, 1.0, fin, dtype=np.float32) ** 0.6
    if fout > 1:
        t = np.linspace(0.0, 1.0, fout, dtype=np.float32)
        x[-fout:] *= (0.5 * (1.0 + np.cos(np.pi * t))).astype(np.float32)
    return x


def ir_seconds(path):
    w = wave.open(path)
    n = w.getnframes() / float(w.getframerate())
    w.close()
    return n


def process(src_wav, dst_wav, spec, ir_path, semitones):
    """One clip through ffmpeg: resample-pitch, colour, convolve, then dynamics.

    Built as an explicit three-node graph rather than one filter string, because `afir` takes a
    second input and therefore cannot live inside a plain `-af` chain.

    THE PAD IS NOT OPTIONAL. `afir` produces exactly as many samples as it is given, so without
    room at the end the reverb tail is guillotined at the last sample of the source — which is a
    hard edge on EVERY clip, and precisely the "cortado, así forzado" the play-test reported. It
    was masked by the fade, because the fade is applied to the dry signal before the tail exists.
    Padding by the impulse response's own length gives the tail somewhere to decay into.
    """
    pre = spec["pre"] + f",apad=pad_dur={ir_seconds(ir_path) + 0.15:.2f}"
    if semitones:
        # Resampling moves pitch, formants AND duration together — which is exactly how a smaller
        # or larger child differs from this one, and why it beats a formant-preserving shift here.
        rate = int(round(SR * (2.0 ** (semitones / 12.0))))
        pre = f"asetrate={rate},aresample={SR}," + pre  # noqa: E501 — order matters: pitch first

    graph = (
        f"[0:a]{pre}[pre];"
        f"[pre][1:a]afir=dry={spec['dry']}:wet={spec['wet']}[wet];"
        f"[wet]{spec['post']}[out]"
    )
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", src_wav, "-i", ir_path,
         "-filter_complex", graph, "-map", "[out]",
         "-ac", "1", "-ar", str(SR), "-c:a", "pcm_s16le", dst_wav],
        check=True, capture_output=True,
    )


def normalise(path, target):
    """Peak-normalise in place.

    Done here rather than with ffmpeg's `loudnorm` on purpose: loudnorm targets perceived
    LOUDNESS, which would drag the quiet intimate banks up to meet the screams and flatten
    exactly the dynamic range these bands exist to create. What is wanted is a fixed peak per
    bank, chosen by what that bank MEANS.
    """
    w = wave.open(path)
    x = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    w.close()

    # Trim the DEAD air the reverb pad leaves behind — not the tail, which is the point of the
    # pad, but the silence after it has decayed past hearing. Keeping it would ship an 8 s Call
    # whose last three seconds are digital zero, and the regroup call fires every 4 s.
    m = float(np.abs(x).max())
    if m > 1e-9:
        win = int(0.020 * SR)
        floor = m * 0.0018  # ≈ -55 dBFS: below this the tail is gone under any game mix
        end = len(x)
        i = len(x) - win
        while i > win:
            if float(np.abs(x[i:i + win]).mean()) > floor:
                end = min(len(x), i + win * 4)  # leave a short breath after the last audible bit
                break
            i -= win
        x = x[:end]

    write_wav(path, x, peak=target)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--source", required=True, help="source recording (mp3/wav)")
    ap.add_argument("--out", default=OUT_DIR)
    args = ap.parse_args()

    tmp = os.path.join(HERE, "_voicetmp")
    os.makedirs(tmp, exist_ok=True)
    os.makedirs(args.out, exist_ok=True)

    decoded = os.path.join(tmp, "_src.wav")
    subprocess.run(
        ["ffmpeg", "-v", "error", "-y", "-i", args.source,
         "-ac", "1", "-ar", str(SR), "-c:a", "pcm_s16le", decoded],
        check=True,
    )
    w = wave.open(decoded)
    a = np.frombuffer(w.readframes(w.getnframes()), dtype="<i2").astype(np.float32) / 32768.0
    print(f"source: {len(a)/SR:.1f}s")

    events = segment(a)
    rows = [f for f in (features(a, s, e) for s, e in events) if f]
    print(f"events: {len(events)} usable: {len(rows)}")

    irs = build_irs(tmp)
    picks = classify(rows)

    for bank, want in BANKS.items():
        chosen = picks[bank]
        print(f"{bank:8} {len(chosen)} clips")
        for i, r in enumerate(chosen):
            raw = os.path.join(tmp, f"_{bank}_{i}.wav")
            write_wav(raw, cut(a, r, want["min_dur"], want["max_dur"]))
            dst = os.path.join(args.out, f"FacelingChild_{bank}_{i+1:02d}.wav")
            process(raw, dst, want, irs[IR_FOR_BANK[bank]],
                    want["pitch"][i % len(want["pitch"])])
            normalise(dst, want["peak"])
            print(f"    {os.path.basename(dst)}  src={r['s']/SR:7.2f}s "
                  f"dur={r['dur']:.2f} cent={r['cent']:.0f} per={r['per']:.2f} "
                  f"pitch{want['pitch'][i % len(want['pitch'])]:+d}")

    json.dump({k: [dict(r) for r in v] for k, v in picks.items()},
              open(os.path.join(tmp, "picks.json"), "w"), indent=1)
    print(f"\nwrote to {args.out}")


if __name__ == "__main__":
    sys.exit(main())
