"""A small tracker for game music on top of sfx.py: numpy synthesis, nothing sampled.

A cue is a **Score** (what is played: tempo, meter, bars, named parts with patterns, drum grids)
rendered through a **Kit** (what plays it: a voice and effects per part, textures, the master).
The same score renders through GRAYBOX today and through a dressed kit later, and exports to
MIDI, so the composition is never locked to the instruments.

    from chip import *
    s = Score(bpm=120, steps_per_beat=4, beats_per_bar=4, bars=4, seed=7)
    s.part("lead", "E5:2 D5:2 C5:4 | r:4 G4:4 | A4:2 B4:2 C5:4 | D5:8")
    s.part("bass", roots(["C3+E3+G3", "G2+B2+D3", "A2+C3+E3", "G2+B2+D3"], [2, 2, -2, 2]))
    s.drums("kick", "x...x...x...x...").drums("snare", "....x.......x..o").drums("hat", "x.x.x.x.x.x.x.xx")
    x = render(s, GRAYBOX)                      # or render(s, kit_for(s, "snes")) for a stereo, dressed mix
    write_wav("theme.wav", x); write_midi(s, "theme.mid")

Patterns:  "A4:4 D5:4 r:2 C5+E5+G5:8@0.7"   note[:steps][@velocity], r = rest, + stacks a chord,
           default length 1 step, '|' is an ignored bar line. A pattern shorter than the score
           repeats to fill it. Drum grids: "x..o" (x hit, o soft, . rest).

Voices are (hz, seconds, **params) -> array and ring past 'seconds' by their release; see VOICES. The
NES set is pulse (duty 0.125/0.25/0.5), tri (4-bit), noise drums; on top of it FM, saws, pads, organ,
bells, music box, calliope, and acoustic voices (piano, bowed strings, harmonium). Oscillators are
band-limited (clean) by default; clean=False gives the raw aliasing chip buzz. Effects per part: filt,
echo, crush; per part in a stereo kit: pan, width (detuned stereo unison), pingpong echo. The master adds
a room (decorrelated left/right), optional saturation and soft-limited loudness. Loops of whole bars
wrap with no seam; check_loop() proves it.

Run as a script:
    python chip.py demo <folder>     renders one score through GRAYBOX and a dressed kit, plus .mid and a spectrogram sheet
    python chip.py voices            lists voices and drums with their parameters
    python chip.py info/preview ...  as sfx.py
"""
import inspect
import struct
import sys
from pathlib import Path

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sfx  # noqa: E402
from sfx import (adsr, check_loop, check_oneshot, fade, level, lfo, loop_safe, modes, n, noise, note, osc,  # noqa: E402,F401
                 place, reverb, saturate, seed, set_rate, shape, silence, stats, sweep, tt, write_wav)

NOTE_NAMES = sfx.NOTE_NAMES
RATE = sfx.RATE   # the rate at import; after set_rate() read sfx.RATE


def rng():
    """The module RNG (reseeded by seed(); Score() seeds it)."""
    return sfx._R


# --- pitch helpers ----------------------------------------------------------------------------------

def pitch(spec):
    """'A4' -> 69: the MIDI number of a note name (flats accepted)."""
    name, octave = spec[:-1], int(spec[-1])
    name = sfx._FLATS.get(name, name)
    return NOTE_NAMES.index(name) + 12 * (octave + 1)


def name_of(midi):
    return f"{NOTE_NAMES[midi % 12]}{midi // 12 - 1}"


def transpose(text, semis):
    """Transpose every note in a pattern by semitones; rests, lengths, velocities and bar lines stay."""
    out = []
    for tok in text.split():
        body, at, vel = tok.partition("@")
        head, sep, ln = body.partition(":")
        if head in ("r", "-", "|"):
            out.append(tok)
            continue
        head = "+".join(name_of(pitch(p) + semis) for p in head.split("+"))
        out.append(head + sep + ln + at + vel)
    return " ".join(out)


def _hz(hz, seconds, vib=0.0, vib_hz=5.5, vib_delay=0.15, arp=None, arp_hz=50.0, glide=None, drift=0.0, sag=0.0):
    """A pitch track: delayed vibrato (vib in semitones), a fast one-channel arpeggio (arp = semitone offsets
    cycled at arp_hz, the NES way of faking a chord), a glide from 'glide' Hz into hz, a random per-note
    detune of up to 'drift' cents (an instrument that is a little out of tune) and a 'sag' in cents over the
    note (a reed running out of air, a tape slowing)."""
    if drift:
        hz = hz * 2 ** (rng().uniform(-drift, drift) / 1200)
    if not vib and not arp and glide is None and not sag:
        return hz
    t = tt(n(seconds))
    f = np.full(len(t), float(hz))
    if glide is not None:
        f = f * (glide / hz) ** np.clip(1 - t / 0.08, 0, 1)
    if arp:
        idx = (np.floor(t * arp_hz).astype(int)) % len(arp)
        f = f * 2 ** (np.asarray(arp, dtype=float)[idx] / 12)
    if vib:
        depth = vib * np.clip((t - vib_delay) / 0.2, 0, 1)
        f = f * 2 ** (depth * np.sin(2 * np.pi * vib_hz * t) / 12)
    if sag:
        f = f * 2 ** (-sag * np.clip(t / max(seconds, 1e-3), 0, 1.5) / 1200)
    return f


def gate_env(seconds, env):
    """An ADSR for a note held 'seconds' then let go: attack and decay to the sustain level within the hold, the
    release after it. The result is n(seconds + release) long, so a note rings into the next one the way an
    instrument does instead of stopping dead at its gate."""
    a, d, s, r = env
    hold = n(seconds)
    rel = n(seconds + r) - hold
    att = min(n(a), hold)
    dec = min(n(d), max(0, hold - att))
    e = np.concatenate([np.linspace(0, 1, att, endpoint=False), np.linspace(1, s, dec, endpoint=False)])
    e = np.concatenate([e, np.full(max(0, hold - len(e)), float(s))])[:hold]
    last = e[-1] if len(e) else float(s)
    return np.concatenate([e, last * np.cos(np.linspace(0, np.pi / 2, rel)) ** 2])


def _tone(x, lp):
    return shape(x, 10, lp) if lp else x


# --- voices: (hz, seconds, **params) -> array ------------------------------------------------------------
# A voice rings past 'seconds' (its release, or a bell's decay); the sequencer places whatever comes back.

def pulse(hz, seconds, duty=0.5, vib=0.0, arp=None, arp_hz=50.0, glide=None, drift=0.0, sag=0.0, lp=None, clean=True,
          env=(0.004, 0.08, 0.6, 0.06)):
    """NES pulse channel: duty 0.5 is hollow and round, 0.25 is the classic lead, 0.125 thin and nasal.
    arp=(0, 4, 7) cycles semitone offsets at arp_hz: a chord on one channel, the NES way. lp low-passes the
    note (Hz); clean=False gives the raw aliasing square."""
    total = seconds + env[3]
    f = _hz(hz, total, vib, arp=arp, arp_hz=arp_hz, glide=glide, drift=drift, sag=sag)
    w = osc(f, total, "square", duty=duty, clean=clean) - (2 * duty - 1)  # remove the duty's DC offset
    return _tone(w * gate_env(seconds, env), lp)


def tri(hz, seconds, vib=0.0, arp=None, arp_hz=50.0, drift=0.0, env=(0.003, 0.02, 0.9, 0.04)):
    """NES triangle channel: 16 steps per cycle, no volume control on the real chip, so the envelope is flat."""
    total = seconds + env[3]
    w = osc(_hz(hz, total, vib, arp=arp, arp_hz=arp_hz, drift=drift), total, "triangle")
    return np.round(w * 7.5) / 7.5 * gate_env(seconds, env)


def saw(hz, seconds, vib=0.0, drift=0.0, sag=0.0, lp=None, clean=True, env=(0.005, 0.05, 0.7, 0.08)):
    """A sawtooth (C64 / Genesis flavour), band-limited unless clean=False."""
    total = seconds + env[3]
    w = osc(_hz(hz, total, vib, drift=drift, sag=sag), total, "saw", clean=clean)
    return _tone(w * gate_env(seconds, env), lp)


def sine(hz, seconds, vib=0.0, drift=0.0, sag=0.0, env=(0.02, 0.1, 0.8, 0.2)):
    total = seconds + env[3]
    return osc(_hz(hz, total, vib, drift=drift, sag=sag), total) * gate_env(seconds, env)


def fm(hz, seconds, ratio=2.0, index=2.5, decay=0.4, vib=0.0, drift=0.0, lp=None, env=(0.003, 0.1, 0.6, 0.1)):
    """Two-operator FM (Genesis / OPL): a modulator at hz*ratio whose depth 'index' decays with 'decay'.
    ratio 1-2 with index 2-4 is an electric piano or bass; ratio 3.5-7 with a long decay is a bell."""
    total = seconds + env[3]
    f = _hz(hz, total, vib, drift=drift)
    count = n(total)
    fa = np.broadcast_to(np.asarray(f, dtype=float), (count,)) if np.ndim(f) == 0 else np.asarray(f)[:count]
    ph = 2 * np.pi * np.cumsum(fa) / sfx.RATE
    t = tt(count)
    mod = index * np.exp(-t / decay) * np.sin(ph * ratio)
    return _tone(np.sin(ph + mod) * gate_env(seconds, env), lp)


def fmbass(hz, seconds, **kw):
    kw = dict(dict(ratio=1.0, index=3.0, decay=0.15, env=(0.002, 0.08, 0.5, 0.05)), **kw)
    return fm(hz, seconds, **kw)


def fmbell(hz, seconds, **kw):
    kw = dict(dict(ratio=3.5, index=4.0, decay=0.8, env=(0.002, 0.3, 0.3, 0.6)), **kw)
    return fm(hz, max(seconds, 1.2), **kw)


def flute(hz, seconds):
    t = sine(hz, seconds, vib=0.18, env=(0.06, 0.1, 0.8, 0.15))
    return sfx.mix(t, 0.15 * noise(seconds, hz * 0.8, hz * 3) * adsr(seconds, 0.03, 0.1, 0.3, 0.1))


def organ(hz, seconds, lp=6000, env=(0.03, 0.05, 0.9, 0.12)):
    total = seconds + env[3]
    w = (osc(hz, total, "square") * 0.5 + osc(hz * 2, total, "square") * 0.2
         + osc(hz / 2, total) * 0.6 + osc(hz * 3, total) * 0.1)
    return _tone(w * gate_env(seconds, env), lp)


def pad(hz, seconds, detune=0.004, lp=2500):
    """Three detuned saws with a slow swell: the SNES / early-90s pad."""
    env = (min(1.2, seconds * 0.4), 0.2, 0.8, min(1.2, seconds * 0.4))
    total = seconds + env[3]
    w = sum(osc(hz * d, total, "saw") for d in (1 - detune, 1.0, 1 + detune)) / 3
    return _tone(w * gate_env(seconds, env), lp)


def choir(hz, seconds):
    env = (min(0.6, seconds * 0.3), 0.2, 0.85, min(0.8, seconds * 0.4))
    total = seconds + env[3]
    w = sum(osc(_hz(hz * d, total, 0.12, 4.8 + i), total) for i, d in enumerate((0.995, 1.0, 1.005)))
    w = w + 0.35 * osc(hz * 2, total) + 0.2 * osc(hz * 3, total)
    return w * gate_env(seconds, env)


def musicbox(hz, seconds):
    return modes([hz, hz * 2.002, hz * 3.98, hz * 6.1], [0.9, 0.5, 0.18, 0.08], [1, 0.4, 0.15, 0.06], max(seconds, 1.4))


def bell(hz, seconds):
    return modes([hz * r for r in (1, 2.76, 5.4, 8.93, 0.5)], [2.2, 1.2, 0.6, 0.3, 3.0], [1, 0.55, 0.3, 0.15, 0.4], max(seconds, 3.5))


def glock(hz, seconds):
    return modes([hz, hz * 2.76, hz * 5.4], [0.6, 0.25, 0.1], [1, 0.3, 0.1], max(seconds, 0.9))


def pluck(hz, seconds):
    """Banjo / harp-ish: bright harmonic modes, fast decay."""
    return modes([hz * k for k in (1, 2, 3, 4, 5)], [0.35, 0.22, 0.14, 0.09, 0.06], [1, 0.6, 0.4, 0.25, 0.15], max(seconds, 0.5))


def calliope(hz, seconds, detune_cents=18, sag=0.0, lp=5000):
    """Out-of-tune steam organ: a random detune per note, a wobble, square + octave."""
    env = (0.01, 0.06, 0.75, 0.05)
    total = seconds + env[3]
    f = _hz(hz, total, 0.25, 6.5, 0.05, drift=detune_cents, sag=sag)
    w = osc(f, total, "square") * 0.45 + osc(np.asarray(f) * 2, total, "triangle") * 0.35 + osc(f, total) * 0.4
    return _tone(w * gate_env(seconds, env), lp)


# acoustic voices (from the Mansion of Cosmic Horrors soundtrack): slower, for the un-chip kit

def harmonium(hz, seconds):
    """A reed pad: odd-leaning partials, a slow swell, a slower release, a slight beat. Rings 1.6 s past 'seconds'."""
    t = tt(n(seconds + 1.6))
    env = np.clip(t / 0.9, 0, 1) ** 2 * np.where(t < seconds, 1.0, np.exp(-(t - seconds) / 0.5))
    x = np.zeros(len(t))
    for k, a in ((1, 1.0), (2, 0.45), (3, 0.5), (4, 0.18), (5, 0.25), (7, 0.08)):
        ph = rng().random() * 6.283
        x += a * (np.sin(2 * np.pi * hz * k * t + ph) + 0.6 * np.sin(2 * np.pi * (hz * k + 0.35) * t + ph))
    x *= 0.3 * env
    x[-n(0.5):] *= np.cos(np.linspace(0, np.pi / 2, n(0.5))) ** 2
    return x


def bowed(hz, seconds, att=1.2, rel=1.8, vib=0.004, trem=0.0):
    """A string section: saw-like partials, a slow bow, vibrato that grows in. Rings 'rel' past 'seconds'."""
    t = tt(n(seconds + rel))
    vibr = 1 + vib * np.sin(2 * np.pi * 5.1 * t + rng().random() * 6.283) * np.clip(t / 1.5, 0, 1)
    phase = 2 * np.pi * np.cumsum(hz * vibr) / sfx.RATE
    x = np.zeros(len(t))
    for k in range(1, 9):
        if k * hz > 8000:
            break
        x += (1.0 / k) * (0.7 if k % 2 == 0 else 1.0) * np.sin(k * phase + rng().random() * 6.283)
    env = np.clip(t / att, 0, 1) ** 2 * np.where(t < seconds, 1.0, np.exp(-(t - seconds) / (rel / 3)))
    if trem:
        env = env * (1 - 0.5 * trem + 0.5 * trem * np.sin(2 * np.pi * 7.0 * t))
    return 0.3 * x * env


def piano(hz, seconds, vel=0.6, detune=(0.0, 4.5, -3.0), b=0.00035):
    """An upright: three strings a little apart, stretched partials, a hammer. Ring depends on pitch, not 'seconds'."""
    ring = float(np.clip(3.2 * (220.0 / hz) ** 0.4, 1.0, 6.0))
    t = tt(n(max(seconds, ring * 1.4)))
    x = np.zeros(len(t))
    for d in detune:
        h = hz * 2 ** (d / 1200)
        for k in range(1, 13):
            fk = k * h * np.sqrt(1 + b * k * k)
            if fk > 9000:
                break
            tau = ring / (1 + 0.35 * (k - 1))
            stage = 0.6 * np.exp(-t / (tau * 0.22)) + 0.4 * np.exp(-t / tau)
            x += (1.0 / k ** (1.7 - 0.6 * vel)) * np.sin(2 * np.pi * fk * t + rng().random() * 6.283) * stage
    x /= len(detune)
    hammer = noise(0.03, 200, 3000 + 3000 * vel) * np.exp(-tt(n(0.03)) / 0.006)
    x[: len(hammer)] += 0.25 * vel * hammer
    x *= 1 - np.exp(-t / 0.0015)
    x[-n(0.05):] *= np.linspace(1, 0, n(0.05))
    return 0.8 * vel * x


VOICES = dict(pulse=pulse, tri=tri, saw=saw, sine=sine, fm=fm, fmbass=fmbass, fmbell=fmbell, flute=flute, organ=organ,
              pad=pad, choir=choir, musicbox=musicbox, bell=bell, glock=glock, pluck=pluck, calliope=calliope,
              harmonium=harmonium, bowed=bowed, piano=piano)


# --- drums: () -> array. Sample-and-hold noise is the NES noise channel; white noise is the cleaner kit. -----

def shnoise(seconds, hold=2):
    """Sample-and-hold noise: a random +-1 held for 'hold' samples (bigger = darker, more metallic). The hold is
    scaled to the sample rate so it sounds the same at 22 and 44 kHz. Starts with a 0.4 ms ramp, so a hit on
    the first sample of a loop is a transient, not a step (which would read as a seam)."""
    count = n(seconds)
    hold = max(1, int(round(hold * sfx.RATE / 22050)))
    v = np.sign(rng().standard_normal(count // hold + 1))
    return np.repeat(v, hold)[:count] * (1 - np.exp(-tt(count) / 0.0004))


def kick(seconds=0.35, f0=150, f1=42, hard=0.0):
    body = sweep(f0, f1, seconds) * np.exp(-tt(n(seconds)) / 0.12)
    return body + hard * shnoise(seconds, 1) * np.exp(-tt(n(seconds)) / 0.01)


def snare(seconds=0.22, tone=185, white=False):
    """Noise plus a tone. white=True uses filtered white noise (cleaner, 16-bit); default is the NES grit."""
    t = tt(n(seconds))
    nz = noise(seconds, 150, 9000) * (1 - np.exp(-t / 0.0004)) if white else shnoise(seconds, 1)
    return 0.8 * nz * np.exp(-t / 0.06) + 0.5 * np.sin(2 * np.pi * tone * t) * np.exp(-t / 0.04)


def hat(seconds=0.05):
    t = tt(n(seconds))
    return shape(shnoise(seconds, 1), 6000, 14000) * np.exp(-t / 0.015)


def ohat(seconds=0.25):
    t = tt(n(seconds))
    return shape(shnoise(seconds, 1), 6000, 14000) * np.exp(-t / 0.08)


def tom(hz=110, seconds=0.5):
    return sweep(hz, hz * 0.6, seconds) * np.exp(-tt(n(seconds)) / 0.18)


def clap(seconds=0.3):
    out = silence(seconds)
    for k in range(3):
        place(out, shape(shnoise(0.25, 1), 800, 6000) * np.exp(-tt(n(0.25)) / (0.012 if k < 2 else 0.07)), 0.011 * k)
    return out


def wood(hz=820, seconds=0.15):
    return modes([hz, hz * 2.4], [0.03, 0.015], [1, 0.4], seconds)


def metal(seconds=0.6, hz=300):
    return modes([hz * r for r in (1, 1.47, 2.09, 2.56, 3.39)], [0.3, 0.2, 0.15, 0.12, 0.08], [1, 0.8, 0.6, 0.5, 0.4], seconds) \
        + 0.4 * sfx.burst(seconds, 2000, 8000, 0.02)


def nhit(seconds=0.12, hold=1, decay=0.03):
    """A bare NES noise-channel hit: hold 1 is a snare, 8-16 a kick-ish thud, 2-4 a hat with a short decay."""
    return shnoise(seconds, hold) * np.exp(-tt(n(seconds)) / decay)


def heartbeat(seconds=0.6):
    out = silence(seconds)
    place(out, kick(0.25, 70, 40), 0)
    place(out, 0.7 * kick(0.25, 65, 38), 0.18)
    return out


DRUMS = dict(kick=kick, snare=snare, hat=hat, ohat=ohat, tom=tom, clap=clap, wood=wood, metal=metal, nhit=nhit, heartbeat=heartbeat)

# General MIDI drum notes for write_midi, by drum (or part) name
GM_DRUMS = dict(kick=36, snare=38, hat=42, ohat=46, tom=45, clap=39, wood=76, metal=49, nhit=38, heartbeat=35, rim=37, ride=51)
GM_PROGRAMS = dict(lead=80, counter=81, arp=80, bass=38, pad=89, melodic=80)


# --- effects --------------------------------------------------------------------------------------------

def echo(x, delay_s, feedback=0.35, taps=3, circular=True):
    """A delay line: 'taps' repeats, each 'feedback' quieter. circular wraps the tail round a loop."""
    d = n(delay_s)
    out = np.array(x, dtype=float)
    for k in range(1, taps + 1):
        if circular:
            out += feedback ** k * np.roll(x, k * d)
        else:
            out[k * d:] += feedback ** k * x[: len(x) - k * d]
    return out


def crush(x, bits=8, hold=1):
    """Bit-crush: quantise to 2**bits levels (4-6 is gritty, 8 is subtle) and hold each sample 'hold' times
    (a lower sample rate: 4 at 44 kHz is an early sampler). Per-sample, so loops stay seamless."""
    x = np.asarray(x, dtype=float)
    peak = np.max(np.abs(x)) + 1e-12
    q = 2 ** (bits - 1)
    y = np.round(x / peak * q) / q * peak
    if hold > 1:
        y = np.repeat(y[::hold], hold)[: len(x)]
    return y


FX = dict(filt=lambda x, lo=40, hi=8000, tilt=0.0, circular=True: shape(x, lo, hi, tilt, circular),
          echo=echo, crush=lambda x, bits=8, hold=1, circular=True: crush(x, bits, hold),
          saturate=lambda x, drive=1.5, circular=True: saturate(x, drive))


def apply_fx(x, chain, circular):
    """chain: [("filt", 60, 6000), ("echo", 0.25, 0.3, 2), ("crush", 6), ("saturate", 1.5)] (positional after the name)."""
    for step in chain or ():
        name, args = step[0], step[1:]
        fn = FX[name] if isinstance(name, str) else name
        kw = {"circular": circular} if "circular" in inspect.signature(fn).parameters else {}
        x = fn(x, *args, **kw)
    return x


# --- patterns -------------------------------------------------------------------------------------------

class Event:
    def __init__(self, at, notes, steps, vel):
        self.at, self.notes, self.steps, self.vel = at, notes, steps, vel   # at/steps in grid steps; notes are MIDI numbers


def seq(text, what="pattern"):
    """Parse a pattern into Events. Raises on a bad token, naming it."""
    out, at = [], 0.0
    for tok in text.split():
        if tok == "|":
            continue
        body, _, vel = tok.partition("@")      # the velocity comes last: C5:2@0.6
        head, _, ln = body.partition(":")
        try:
            steps = float(ln) if ln else 1.0
            v = float(vel) if vel else 1.0
            if head not in ("r", "-"):
                out.append(Event(at, [pitch(p) for p in head.split("+")], steps, v))
        except (ValueError, IndexError):
            raise ValueError(f"{what}: cannot read token '{tok}' (want NAME[+NAME][:steps][@vel], r[:steps] or |)")
        at += steps
    return out, at


def hits(grid):
    """'x..o' -> [(step, velocity)]: x = 1.0, o = 0.55."""
    cells = grid.replace(" ", "").replace("|", "")
    return [(i, 1.0 if c == "x" else 0.55) for i, c in enumerate(cells) if c in "xo"], len(cells)


def chords(chs, steps):
    """['C3+E3+G3', ...] held 'steps' each."""
    return " ".join(f"{c}:{steps}" for c in chs)


def arp(chs, order, steps_each, octave_shift=0):
    """A broken-chord pattern: for each chord, 'steps_each' one-step notes following 'order' (indices into the
    chord; an index past the chord's size wraps up an octave; None is a rest)."""
    out = []
    for ch in chs:
        tones = ch.split("+")
        for i in range(steps_each):
            k = order[i % len(order)]
            if k is None:
                out.append("r")
                continue
            out.append(name_of(pitch(tones[k % len(tones)]) + 12 * (k // len(tones) + octave_shift)))
    return " ".join(out)


def roots(chs, rhythm, octave=-1):
    """A bass line from chord roots. rhythm per chord: a step length, a negative length for a rest, or
    (semitones, length) for another note of the chord ((7, 2) is the fifth, (12, 2) the octave)."""
    out = []
    for ch in chs:
        root = pitch(ch.split("+")[0]) + 12 * octave
        for ln in rhythm:
            if isinstance(ln, tuple):
                out.append(f"{name_of(root + ln[0])}:{ln[1]}")
            elif ln < 0:
                out.append(f"r:{-ln}")
            else:
                out.append(f"{name_of(root)}:{ln}")
    return " ".join(out)


def times(pattern, k):
    """Repeat a pattern k times."""
    return " | ".join([pattern] * k)


def stretch(text, k):
    """Multiply every length in a pattern by k (2 = twice as slow, 0.5 = twice as fast); notes and velocities stay."""
    out = []
    for tok in text.split():
        if tok == "|":
            out.append(tok)
            continue
        body, at, vel = tok.partition("@")
        head, _, ln = body.partition(":")
        steps = (float(ln) if ln else 1.0) * k
        out.append(f"{head}:{steps:g}{at}{vel}")
    return " ".join(out)


def span(text):
    """Length of a pattern in steps."""
    return seq(text)[1]


# --- the score ------------------------------------------------------------------------------------------

def infer_role(name):
    for role, keys in (("bass", ("bass",)), ("lead", ("lead", "melody", "tune", "theme", "voice")),
                       ("pad", ("pad", "chord", "organ", "strings")), ("arp", ("arp",)), ("counter", ("counter", "harm", "answer"))):
        if any(name.lower().startswith(k) for k in keys):
            return role
    return "melodic"


class Part:
    def __init__(self, name, pattern, role, gate, octave, offset, repeat):
        self.name, self.pattern, self.role = name, pattern, role
        self.gate, self.octave, self.offset, self.repeat = gate, octave, offset, repeat
        self.events, self.span = seq(pattern, f"part '{name}'")


class DrumPart:
    def __init__(self, name, grid, offset):
        self.name, self.grid, self.offset, self.role = name, grid, offset, "drums"
        self.hits, self.span = hits(grid)


class Score:
    """What is played. One loop (or, with loop=False, a stinger) of whole bars."""

    def __init__(self, bpm, steps_per_beat=4, beats_per_bar=4, bars=4, seed=1, loop=True, tail=1.5, name=""):
        self.bpm, self.steps_per_beat, self.beats_per_bar, self.bars = bpm, steps_per_beat, beats_per_bar, bars
        self.seed, self.loop, self.tail, self.name = seed, loop, tail, name
        self.step = 60.0 / bpm / steps_per_beat                    # seconds per grid step
        self.steps = steps_per_beat * beats_per_bar * bars
        self.length = self.step * self.steps                       # seconds (without the tail)
        self.parts = {}

    def part(self, name, pattern, role=None, gate=None, octave=0, offset=0, repeat=True):
        """A melodic part. gate: fraction of each note's length that sounds (None: the kit's). octave shifts the
        pattern. offset delays it (steps). A pattern shorter than the score repeats unless repeat=False."""
        if octave:
            pattern = transpose(pattern, 12 * octave)
        p = Part(name, pattern, role or infer_role(name), gate, octave, offset, repeat)
        self._fit(p)
        self.parts[name] = p
        return self

    def drums(self, name, grid, offset=0):
        """A drum grid; the name picks the sound (kick, snare, hat, ohat, tom, clap, wood, metal, nhit, heartbeat)
        unless the kit maps it to another."""
        p = DrumPart(name, grid, offset)
        self._fit(p)
        self.parts[name] = p
        return self

    def _fit(self, p):
        if p.span > self.steps:
            raise ValueError(f"part '{p.name}' is {p.span:g} steps long but the score has {self.steps} "
                             f"({self.bars} bars of {self.beats_per_bar}x{self.steps_per_beat})")
        if getattr(p, "repeat", True) and p.span and self.steps % p.span:
            raise ValueError(f"part '{p.name}' is {p.span:g} steps; it cannot repeat evenly into {self.steps}. "
                             "Make it a whole number of bars, or pass repeat=False to play it once.")

    def describe(self):
        lines = [f"{self.name or 'score'}: {self.bpm} bpm, {self.beats_per_bar} beats per bar, {self.steps_per_beat} steps per beat, "
                 f"{self.bars} bars = {self.steps} steps, {self.length:.2f} s {'loop' if self.loop else 'one-shot'}, seed {self.seed}"]
        for p in self.parts.values():
            kind = "drums" if p.role == "drums" else p.role
            lines.append(f"  {p.name:12} {kind:8} {p.span:g} steps" + (" (repeats)" if p.span < self.steps else ""))
        return "\n".join(lines)


# --- the kit --------------------------------------------------------------------------------------------

MASTER = dict(rms_db=-15.0, peak_db=-1.0, verb=(1.2, 0.25), fx=(), soft=True, drive=0.0)
SPEC_KEYS = ("voice", "drum", "gain", "gate", "octave", "chord", "fx", "pan", "width", "pingpong", "jitter")


class Kit:
    """What plays the score. Kit(lead=dict(voice="pulse", duty=0.25, vib=0.15, gain=0.5, gate=0.75, pan=0.1, fx=[("echo", 0.25, 0.3, 2)]),
    bass="tri", kick=dict(drum="kick", gain=0.8), master=dict(rms_db=-15, verb=(1.5, 0.3)), layers=[...]).
    A part spec names a voice (or drum), a gain, a gate, an octave, an fx chain, and in a stereo kit a pan (-1..1),
    a width (0..1: a detuned copy spread left/right) and a pingpong echo (delay_s, feedback, taps); every other
    key goes to the voice. Keys are part names; a role name (lead, bass, pad, arp, counter, melodic, drums) covers
    parts not named. layers: [(fn, gain, fx)] textures added at render time, fn(length_seconds) -> array (periodic,
    for a loop). stereo: True/False, or None to decide from the specs (any pan, width or pingpong makes it stereo)."""

    def __init__(self, master=None, layers=None, name="", stereo=None, **parts):
        self.parts = {k: (dict(voice=v) if isinstance(v, str) else dict(v)) for k, v in parts.items()}
        self.master = dict(MASTER, **(master or {}))
        self.layers = list(layers or [])
        self.name = name
        self.stereo = stereo

    def part(self, name, **spec):
        self.parts[name] = spec
        return self

    def layer(self, fn, gain=1.0, fx=None):
        self.layers.append((fn, gain, fx))
        return self

    def resolve(self, part):
        spec = self.parts.get(part.name) or self.parts.get(part.role) or GRAYBOX.parts.get(part.name) or GRAYBOX.parts[part.role]
        return dict(spec)

    def is_stereo(self):
        if self.stereo is not None:
            return self.stereo
        return any(s.get("pan") or s.get("width") or s.get("pingpong") for s in self.parts.values())

    def with_parts(self, **parts):
        """A copy with some parts replaced (for variations of one kit)."""
        k = Kit(master=self.master, layers=self.layers, name=self.name, stereo=self.stereo, **self.parts)
        for name, v in parts.items():
            k.parts[name] = dict(voice=v) if isinstance(v, str) else dict(v)
        return k


# Plain NES voices by role, mono, dry: what a score sounds like before anyone chooses instruments.
GRAYBOX = Kit(
    name="graybox", stereo=False,
    lead=dict(voice="pulse", duty=0.5, gain=0.45, gate=0.8),
    counter=dict(voice="pulse", duty=0.25, gain=0.28, gate=0.8),
    arp=dict(voice="pulse", duty=0.125, gain=0.2, gate=0.5),
    melodic=dict(voice="pulse", duty=0.5, gain=0.3, gate=0.8),
    bass=dict(voice="tri", gain=0.8, gate=0.8),
    pad=dict(voice="organ", gain=0.15, gate=1.0, lp=2500),
    drums=dict(gain=0.5),
    kick=dict(drum="kick", gain=0.8), snare=dict(drum="snare", gain=0.35), hat=dict(drum="hat", gain=0.15),
    ohat=dict(drum="ohat", gain=0.15), tom=dict(drum="tom", gain=0.6), clap=dict(drum="clap", gain=0.3),
    wood=dict(drum="wood", gain=0.3), metal=dict(drum="metal", gain=0.3), nhit=dict(drum="nhit", gain=0.4),
    heartbeat=dict(drum="heartbeat", gain=0.5),
    master=dict(rms_db=-15.0, verb=(0.8, 0.15)),
)

# Era kits. Start from one with .with_parts(...) rather than from nothing; kit_for() adds grid-timed echoes.
NES = GRAYBOX.with_parts(
    lead=dict(voice="pulse", duty=0.25, vib=0.15, gain=0.45, gate=0.75, clean=False),
    counter=dict(voice="pulse", duty=0.5, gain=0.25, gate=0.8, clean=False),
    arp=dict(voice="pulse", duty=0.125, gain=0.18, gate=0.5, clean=False),
    pad=dict(voice="pulse", duty=0.5, gain=0.12, gate=1.0, chord="arp", arp_hz=40, clean=False),   # no pad on a NES: one pulse cycles the chord
    bass=dict(voice="tri", gain=0.85, gate=0.8),
    kick=dict(drum="nhit", hold=12, decay=0.05, gain=0.9), snare=dict(drum="nhit", hold=1, decay=0.05, gain=0.4),
    hat=dict(drum="nhit", hold=1, seconds=0.04, decay=0.012, gain=0.15),
)
NES.name, NES.stereo = "nes", False
NES.master = dict(MASTER, rms_db=-15.0, verb=None)

SNES = GRAYBOX.with_parts(
    lead=dict(voice="pulse", duty=0.25, vib=0.18, gain=0.45, gate=0.75, lp=7000, pan=0.08, width=0.25),
    counter=dict(voice="fm", ratio=2.0, index=2.0, decay=0.3, gain=0.25, lp=6000, pan=-0.3),
    arp=dict(voice="pulse", duty=0.125, gain=0.12, gate=0.5, lp=6000, pan=0.4),
    pad=dict(voice="pad", gain=0.14, gate=1.0, lp=1800, pan=-0.15, width=0.6),
    bass=dict(voice="fmbass", gain=0.75, gate=0.7, lp=3000),
    kick=dict(drum="kick", gain=0.9, hard=0.3), snare=dict(drum="snare", gain=0.35, white=True, pan=0.05),
    hat=dict(drum="hat", gain=0.12, pan=0.3), ohat=dict(drum="ohat", gain=0.12, pan=0.3),
)
SNES.name, SNES.stereo = "snes", True
SNES.master = dict(MASTER, rms_db=-14.0, verb=(1.4, 0.25), fx=[("filt", 25, 12000)], drive=1.3)   # the console's gentle low-pass

GENESIS = GRAYBOX.with_parts(
    lead=dict(voice="fm", ratio=2.0, index=3.0, decay=0.25, vib=0.1, gain=0.4, gate=0.8, pan=0.1, width=0.2),
    counter=dict(voice="saw", gain=0.2, gate=0.8, lp=5000, pan=-0.35),
    arp=dict(voice="fmbell", gain=0.12, gate=0.5, pan=0.4),
    pad=dict(voice="saw", gain=0.1, gate=1.0, lp=1800, pan=-0.1, width=0.5),
    bass=dict(voice="fmbass", index=4.0, gain=0.75, gate=0.7),
    kick=dict(drum="kick", gain=0.9, hard=0.5, f0=200), snare=dict(drum="snare", gain=0.35, tone=220, white=True), hat=dict(drum="hat", gain=0.1, pan=0.25),
)
GENESIS.name, GENESIS.stereo = "genesis", True
GENESIS.master = dict(MASTER, rms_db=-14.0, verb=(0.9, 0.15), drive=1.4)

ACOUSTIC = GRAYBOX.with_parts(
    lead=dict(voice="piano", vel=0.65, gain=0.5, pan=0.1),
    counter=dict(voice="bowed", gain=0.3, att=0.4, rel=0.8, pan=-0.3, width=0.4),
    arp=dict(voice="musicbox", gain=0.3, pan=0.35),
    pad=dict(voice="harmonium", gain=0.25, gate=0.95, pan=-0.1, width=0.5),
    bass=dict(voice="piano", vel=0.55, gain=0.6),
    kick=dict(drum="tom", hz=70, gain=0.6), snare=dict(drum="wood", hz=600, gain=0.25, pan=0.2), hat=dict(drum="wood", hz=1800, seconds=0.06, gain=0.1, pan=-0.3),
)
ACOUSTIC.name, ACOUSTIC.stereo = "acoustic", True
ACOUSTIC.master = dict(MASTER, rms_db=-17.0, verb=(2.2, 0.4))

LOFI = SNES.with_parts()
LOFI.name = "lofi"
LOFI.master = dict(SNES.master, fx=[("crush", 8, 4), ("filt", 25, 9000)])   # 8-bit samples at a low rate: the grainy early-90s sound

KITS = dict(graybox=GRAYBOX, nes=NES, snes=SNES, genesis=GENESIS, acoustic=ACOUSTIC, lofi=LOFI)


def kit_for(score, base, **parts):
    """A kit for one score: 'base' (a Kit or a KITS name) plus any part overrides, and, for the nes, snes and
    lofi bases, echoes on the lead (a dotted eighth) and the arp (a sixteenth) timed to the score's grid: a
    stereo ping-pong in a stereo kit, a mono echo otherwise. An override that brings its own 'fx' or 'pingpong'
    keeps it as given; one without gets the echo added. Echo delays must be step multiples to sit in time, so
    they are set per score, not in a shared kit."""
    base = KITS[base] if isinstance(base, str) else base
    k = base.with_parts(**parts)
    sixteenth = score.step * score.steps_per_beat / 4
    for name, sixteenths, fb in (("lead", 3, 0.25), ("arp", 1, 0.3)):
        given = parts.get(name) or {}
        if name in k.parts and base.name in ("snes", "nes", "lofi") and "fx" not in given and "pingpong" not in given:
            spec = dict(k.parts[name])
            if k.is_stereo():
                spec["pingpong"] = (sixteenths * sixteenth, fb, 2)
            else:
                spec["fx"] = list(spec.get("fx") or []) + [("echo", sixteenths * sixteenth, fb, 2)]
            k.parts[name] = spec
    return k


# --- rendering --------------------------------------------------------------------------------------------

def _pan(pan):
    """Equal-power pan gains for -1..1."""
    a = (np.clip(pan, -1, 1) + 1) * np.pi / 4
    return np.cos(a), np.sin(a)


def render(score, kit=GRAYBOX, check=True):
    """Render a score through a kit: one buffer per part with its fx, panned into a stereo pair when the kit is
    stereo, the kit's layers, then the master (a room, decorrelated left/right, for loops a circular one; optional
    saturation; RMS level with soft-limited peaks). Checks the loop seam or the one-shot ends. Returns (N,) for a
    mono kit or (N, 2) for a stereo one."""
    seed(score.seed)
    total = score.length + (0 if score.loop else score.tail)
    stereo = kit.is_stereo()
    count = n(total)
    L, R = np.zeros(count), (np.zeros(count) if stereo else None)

    def add(buf, pan=0.0):
        if not stereo:
            L[:] += buf
            return
        gl, gr = _pan(pan)
        L[:] += gl * buf
        R[:] += gr * buf

    for part in score.parts.values():
        spec = kit.resolve(part)
        gain, fx_chain = spec.pop("gain", 1.0), spec.pop("fx", None)
        pan, width, pingpong = spec.pop("pan", 0.0), spec.pop("width", 0.0), spec.pop("pingpong", None)
        if part.role == "drums":
            buf = np.zeros(count)
            _render_drums(score, part, spec, buf)
            sides = [(apply_fx(buf, fx_chain, score.loop), pan)]
        elif stereo and width:
            cents = 7.0 * width
            sides = []
            for sign in (-1, 1):
                buf = np.zeros(count)
                _render_part(score, part, dict(spec), buf, cents=sign * cents)
                sides.append((apply_fx(buf, fx_chain, score.loop) / np.sqrt(2), float(np.clip(pan + sign * width, -1, 1))))
        else:
            buf = np.zeros(count)
            _render_part(score, part, spec, buf)
            sides = [(apply_fx(buf, fx_chain, score.loop), pan)]
        for buf, p in sides:
            add(gain * buf, p)
            if pingpong and stereo:
                delay_s, fb, taps = pingpong
                d = n(delay_s)
                for k in range(1, taps + 1):
                    tap = fb ** k * gain * (np.roll(buf, k * d) if score.loop else np.concatenate([np.zeros(k * d), buf[: len(buf) - k * d]]))
                    (R if k % 2 else L)[:] += tap
            elif pingpong:
                add(apply_fx(gain * buf, [("echo",) + tuple(pingpong)], score.loop) - gain * buf, p)
    for fn, gain, fx_chain in kit.layers:
        x = np.asarray(fn(total), dtype=float)
        buf = np.zeros(count)
        buf[: min(len(x), count)] = x[:count]
        add(gain * apply_fx(buf, fx_chain, score.loop))

    m = kit.master
    chans = [L, R] if stereo else [L]
    out = []
    for c in chans:
        c = apply_fx(c, m.get("fx"), score.loop)
        if m.get("verb"):
            sec, wet = m["verb"]
            c = reverb(c, min(sec, total * 0.9), wet, circular=score.loop)   # a new tail per channel: decorrelated
        if m.get("drive"):
            c = saturate(c, m["drive"])
        out.append(c)
    x = np.stack(out, axis=1) if stereo else out[0]
    if not score.loop:
        x = fade(x, 0.002, min(0.4, score.tail / 3))
    x = level(x, rms_db=m.get("rms_db"), peak_db=m.get("peak_db", -1.0), remove_dc=True, soft=m.get("soft", True))
    if check:
        (check_loop if score.loop else check_oneshot)(x)
    return x


def _render_part(score, part, spec, buf, cents=0.0):
    fn = VOICES[spec.pop("voice", "pulse")] if not callable(spec.get("voice")) else spec.pop("voice")
    gate = part.gate if part.gate is not None else spec.pop("gate", 0.9)
    spec.pop("gate", None)
    octave = spec.pop("octave", 0)
    chord = spec.pop("chord", "stack")   # "stack": every note of a chord sounds; "arp": one voice cycles them (NES)
    reps = int(score.steps // part.span) if part.repeat and part.span else 1
    for r in range(reps):
        for ev in part.events:
            dur = max(ev.steps * score.step * gate, 0.02)
            at = (part.offset + r * part.span + ev.at) * score.step
            if at >= score.length:
                continue
            notes, kw = ev.notes, spec
            if chord == "arp" and len(notes) > 1:
                low = min(notes)
                notes, kw = [low], dict(spec, arp=tuple(m - low for m in sorted(notes)))
            for midi in notes:
                hz = 440.0 * 2 ** ((midi + 12 * octave - 69) / 12 + cents / 1200)
                x = fn(hz, dur, **kw) * ev.vel / max(1, len(notes)) ** 0.5
                place(buf, x, at, wrap=score.loop)


def _render_drums(score, part, spec, buf):
    drum = spec.pop("drum", part.name)
    fn = DRUMS.get(drum, nhit) if isinstance(drum, str) else drum
    jitter = spec.pop("jitter", 0.0)
    reps = int(np.ceil(score.steps / part.span)) if part.span else 0
    for r in range(reps):
        for i, vel in part.hits:
            at = (part.offset + r * part.span + i) * score.step + jitter * rng().standard_normal()
            if at >= score.length or at < 0:
                continue
            place(buf, vel * fn(**spec), at, wrap=score.loop)


# --- MIDI export: the composition as a file any DAW or tracker opens -------------------------------------------

def _vlq(v):
    out = [v & 0x7F]
    v >>= 7
    while v:
        out.append((v & 0x7F) | 0x80)
        v >>= 7
    return bytes(reversed(out))


def _track(events):
    """events: (tick, order, bytes) -> an MTrk chunk."""
    data = bytearray()
    last = 0
    for tick, _, msg in sorted(events, key=lambda e: (e[0], e[1])):
        data += _vlq(tick - last) + msg
        last = tick
    data += b"\x00\xff\x2f\x00"
    return b"MTrk" + struct.pack(">I", len(data)) + bytes(data)


def write_midi(score, path, ppq=480):
    """Write the score as a format-1 MIDI file: tempo and meter, one track per part (drums on channel 10,
    GM programs by role). Gates come from the score, else 0.9. Kits are not represented: this is the composition."""
    tps = ppq / score.steps_per_beat                                 # ticks per step
    tempo = int(60_000_000 / score.bpm)
    head = [(0, 0, b"\xff\x51\x03" + struct.pack(">I", tempo)[1:]),
            (0, 0, b"\xff\x58\x04" + bytes([score.beats_per_bar, 2, 24, 8])),
            (0, 0, b"\xff\x03" + _vlq(len(score.name.encode())) + score.name.encode())]
    tracks = [_track(head)]
    chan = 0
    for part in score.parts.values():
        ev = [(0, 0, b"\xff\x03" + _vlq(len(part.name.encode())) + part.name.encode())]
        if part.role == "drums":
            ch, key = 9, GM_DRUMS.get(part.name, 38)
            reps = int(np.ceil(score.steps / part.span)) if part.span else 0
            for r in range(reps):
                for i, vel in part.hits:
                    step = part.offset + r * part.span + i
                    if step >= score.steps:
                        continue
                    t0 = int(round(step * tps))
                    ev.append((t0, 1, bytes([0x90 | ch, key, int(vel * 110)])))
                    ev.append((t0 + int(tps / 2), 0, bytes([0x80 | ch, key, 0])))
        else:
            ch = chan if chan != 9 else 10
            chan = ch + 1
            ev.append((0, 0, bytes([0xC0 | ch, GM_PROGRAMS.get(part.role, 80)])))
            gate = part.gate if part.gate is not None else 0.9
            reps = int(score.steps // part.span) if part.repeat and part.span else 1
            for r in range(reps):
                for e in part.events:
                    step = part.offset + r * part.span + e.at
                    if step >= score.steps:
                        continue
                    t0 = int(round(step * tps))
                    t1 = t0 + max(1, int(round(e.steps * gate * tps)))
                    for midi in e.notes:
                        ev.append((t0, 1, bytes([0x90 | ch, midi, max(1, min(127, int(e.vel * 100)))])))
                        ev.append((t1, 0, bytes([0x80 | ch, midi, 0])))
        tracks.append(_track(ev))
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    with open(path, "wb") as f:
        f.write(b"MThd" + struct.pack(">IHHH", 6, 1, len(tracks), ppq))
        for t in tracks:
            f.write(t)
    return path


# --- files ----------------------------------------------------------------------------------------------------

def save_ogg(path, x, quality=0.45):
    """OGG Vorbis (gapless loops, a tenth of the WAV), mono or stereo. Needs the soundfile package; raises ImportError without it."""
    import soundfile as sf
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    x = np.asarray(x, dtype=np.float32)
    channels = x.shape[1] if x.ndim == 2 else 1
    with sf.SoundFile(str(path), "w", sfx.RATE, channels, format="OGG", subtype="VORBIS", compression_level=1.0 - quality) as f:
        for i in range(0, len(x), 4096):              # libsndfile's Vorbis encoder fails on one large write
            f.write(x[i:i + 4096])
    return path


def oneshot(x, peak_db=-3.0, rms_db=None, tail=0.05, reverb_s=0.0, wet=0.25):
    """Finish a sound effect: optional room, fades at both ends, level (peak, or RMS with soft-limited peaks),
    and check_oneshot. Returns the array."""
    x = np.asarray(x, dtype=float)
    if reverb_s:
        x = reverb(np.concatenate([x, silence(reverb_s)]), reverb_s, wet)
    x = fade(x, 0.002, tail)
    x = level(x, rms_db=rms_db, peak_db=peak_db, soft=rms_db is not None)
    check_oneshot(x)
    return x


def sfx_cli(effects, out=".", argv=None):
    """A main() for a game's effects script: python effects.py [--ogg] [--wav DIR] [--preview sheet.png] [name ...]
    effects: {name: function returning a finished array (see oneshot())}. Prints a stats line per file."""
    argv = list(sys.argv[1:] if argv is None else argv)
    opts = {"ogg": False, "wav": None, "preview": None}
    names = []
    while argv:
        a = argv.pop(0)
        if a == "--ogg":
            opts["ogg"] = True
        elif a in ("--wav", "--preview"):
            opts[a[2:]] = argv.pop(0)
        else:
            names.append(a)
    out = Path(out)
    written = []
    for name in names or effects:
        x = effects[name]()
        if opts["ogg"]:
            save_ogg(out / f"{name}.ogg", x)
            if opts["wav"]:
                write_wav(Path(opts["wav"]) / f"{name}.wav", x)
                written.append(Path(opts["wav"]) / f"{name}.wav")
        else:
            write_wav(out / f"{name}.wav", x)
            written.append(out / f"{name}.wav")
        s = stats(x)
        print(f"{name:22} {s['seconds']:6.2f}s  peak {s['peak_db']:5.1f}  rms {s['rms_db']:5.1f}  "
              f"start {s['start']:.3f} end {s['end']:.3f}  seam {s['seam']:.4f}")
    if opts["preview"] and written:
        sfx.preview(opts["preview"], *written)
        print(f"spectrograms: {opts['preview']}")


def report(name, x, score=None):
    s = stats(x)
    ch = "stereo" if np.ndim(x) == 2 else "mono"
    ends = f"seam {s['seam']:.4f} <= {1.5 * s['p99_step']:.4f}" if (score is None or score.loop) else f"start {s['start']:.3f} end {s['end']:.3f}"
    return f"{name:22} {s['seconds']:6.2f}s {ch:6}  peak {s['peak_db']:5.1f}  rms {s['rms_db']:5.1f}  {ends}"


def cli(cues, kits=None, out=".", midi_dir=None, argv=None):
    """A main() for a game's music script.

        python music.py [--graybox] [--ogg] [--wav DIR] [--midi DIR] [--preview sheet.png] [cue ...]

    cues: {name: function returning a Score}. kits: {name: Kit or function returning one}; a cue with no kit,
    or every cue with --graybox, renders through GRAYBOX. Writes <out>/<name>.wav (or .ogg with --ogg, then
    --wav DIR adds WAVs for listening), <midi_dir>/<name>.mid when midi_dir or --midi is given, and prints a
    stats line per cue."""
    argv = list(sys.argv[1:] if argv is None else argv)
    opts = {"graybox": False, "ogg": False, "wav": None, "midi": midi_dir, "preview": None}
    names = []
    while argv:
        a = argv.pop(0)
        if a == "--graybox":
            opts["graybox"] = True
        elif a == "--ogg":
            opts["ogg"] = True
        elif a in ("--wav", "--midi", "--preview"):
            opts[a[2:]] = argv.pop(0)
        else:
            names.append(a)
    kits = kits or {}
    out = Path(out)
    written = []
    for name in names or cues:
        score = cues[name]()
        score.name = score.name or name
        kit = None if opts["graybox"] else kits.get(name)
        kit = kit() if callable(kit) and not isinstance(kit, Kit) else kit
        x = render(score, kit or GRAYBOX)
        if opts["ogg"]:
            save_ogg(out / f"{name}.ogg", x)
            if opts["wav"]:
                write_wav(Path(opts["wav"]) / f"{name}.wav", x)
                written.append(Path(opts["wav"]) / f"{name}.wav")
        else:
            write_wav(out / f"{name}.wav", x)
            written.append(out / f"{name}.wav")
        if opts["midi"]:
            write_midi(score, Path(opts["midi"]) / f"{name}.mid")
        print(report(f"{name} [{(kit or GRAYBOX).name or 'kit'}]", x, score))
    if opts["preview"] and written:
        sfx.preview(opts["preview"], *written)
        print(f"spectrograms: {opts['preview']}")


# --- demo --------------------------------------------------------------------------------------------------------

def demo_score():
    """Eight bars at 128 bpm in A minor: a lead that answers itself, a chugging bass, a four-on-the-floor kit."""
    ch = ["A3+C4+E4", "A3+C4+E4", "F3+A3+C4", "G3+B3+D4", "A3+C4+E4", "A3+C4+E4", "F3+A3+C4", "E3+G#3+B3"]
    s = Score(bpm=128, steps_per_beat=4, beats_per_bar=4, bars=8, seed=11, name="demo")
    s.part("lead", "A4:2 C5:2 E5:4 D5:2 C5:2 B4:4 | A4:2 C5:2 E5:4 G5:4 F5:2 E5:2 | F4:2 A4:2 C5:4 B4:2 A4:2 G4:4 | "
                   "G4:2 B4:2 D5:4 C5:4 B4:2 A4:2 | A4:2 C5:2 E5:4 D5:2 C5:2 B4:4 | A4:2 C5:2 E5:4 A5:4 G5:2 E5:2 | "
                   "F5:4 E5:4 D5:4 C5:4 | B4:4 G#4:4 A4:8")
    s.part("counter", "r:8 E4:2 D4:2 C4:4 | r:8 A4:2 G4:2 E4:4 | r:8 F4:2 E4:2 D4:4 | r:8 D4:2 C4:2 B3:4 | "
                      "r:8 E4:2 D4:2 C4:4 | r:8 A4:2 G4:2 E4:4 | r:16 | r:16", gate=0.6)
    s.part("arp", arp(ch, [0, 1, 2, 1], 16, 1))
    s.part("bass", roots(ch, [2, 2, (12, 2), 2, 2, 2, (7, 2), (12, 2)]), gate=0.7)
    s.part("pad", chords(ch, 16))
    s.drums("kick", "x...x...x...x...").drums("snare", "....x..o....x..o").drums("hat", "x.o.x.o.x.o.x.oo")
    return s


def demo(folder):
    folder = Path(folder)
    s = demo_score()
    print(s.describe())
    gray = render(s, GRAYBOX)
    dressed = render(s, kit_for(s, "snes"))
    write_wav(folder / "demo_graybox.wav", gray)
    write_wav(folder / "demo_dressed.wav", dressed)
    write_midi(s, folder / "demo.mid")
    print(report("demo_graybox", gray, s))
    print(report("demo_dressed", dressed, s))
    try:
        sfx.preview(folder / "demo_spectra.png", folder / "demo_graybox.wav", folder / "demo_dressed.wav")
        print(f"wrote {folder / 'demo_spectra.png'}")
    except ImportError:
        pass
    print(f"wrote {folder / 'demo_graybox.wav'}, {folder / 'demo_dressed.wav'}, {folder / 'demo.mid'}")


def list_voices():
    print("voices (hz, seconds, ...):")
    for name, fn in VOICES.items():
        sig = str(inspect.signature(fn)).replace("hz, seconds", "")
        print(f"  {name:10} {sig}  {(fn.__doc__ or ' ').splitlines()[0]}")
    print("drums:")
    for name, fn in DRUMS.items():
        print(f"  {name:10} {inspect.signature(fn)}  {(fn.__doc__ or ' ').splitlines()[0]}")
    print("fx: filt(lo, hi, tilt)  echo(delay_s, feedback, taps)  crush(bits, hold)  saturate(drive)")
    print("stereo kit keys per part: pan (-1..1), width (0..1), pingpong (delay_s, feedback, taps)")


def main(argv):
    if len(argv) == 2 and argv[0] == "demo":
        demo(argv[1])
    elif argv[:1] == ["voices"]:
        list_voices()
    elif argv[:1] in (["info"], ["preview"]):
        return sfx.main(argv)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
