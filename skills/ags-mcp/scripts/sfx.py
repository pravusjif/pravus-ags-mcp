"""Small helpers for synthesising game sound effects, ambience and music with numpy, for AGS audio clips.

Everything is mono float arrays at RATE Hz, roughly -1..1; write_wav() writes 16-bit PCM WAV, which
import_audio takes as is. Nothing is sampled: oscillators, modal "struck object" synthesis, FFT-shaped
noise and a noise-tail reverb cover most adventure-game sounds. Seed the RNG so a clip regenerates
identically.

    from sfx import (RATE, seed, n, note, osc, sweep, modes, noise, burst, creak, adsr, fade, lfo,
                     loop_safe, mix, place, reverb, level, check_oneshot, check_loop, write_wav)

Loops: build them from parts that are periodic over the loop length -- noise(..., circular=True),
lfo(length, whole_cycles), tones at loop_safe(hz, length), place(..., wrap=True) and
reverb(..., circular=True) -- and the end joins the start with no click. check_loop() verifies it.

Run as a script:
    python sfx.py info a.wav [b.wav ...]         duration, peak/RMS dBFS, ends and loop seam
    python sfx.py preview out.png a.wav [...]    stacked log-frequency spectrograms (needs PIL)
    python sfx.py demo <folder>                  writes three example clips and checks them
"""
import sys
import wave
from pathlib import Path

import numpy as np

RATE = 22050
_R = np.random.default_rng(1)
NOTE_NAMES = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"]
_FLATS = {"Db": "C#", "Eb": "D#", "Gb": "F#", "Ab": "G#", "Bb": "A#", "Cb": "B", "Fb": "E"}


def seed(s):
    """Reseed the module RNG used by noise(), burst(), creak() and reverb()."""
    global _R
    _R = np.random.default_rng(s)


def n(seconds):
    """Seconds to a sample count."""
    return int(round(seconds * RATE))


def tt(count):
    """Time in seconds of each of 'count' samples."""
    return np.arange(count) / RATE


def note(spec):
    """'A4', 'Bb2', 'F#5' to Hz (equal temperament, A4 = 440)."""
    name, octave = spec[:-1], int(spec[-1])
    name = _FLATS.get(name, name)
    k = NOTE_NAMES.index(name) + 12 * (octave + 1) - 69
    return 440.0 * 2 ** (k / 12)


def silence(seconds):
    return np.zeros(n(seconds))


# --- sources -------------------------------------------------------------------------------

def osc(hz, seconds, wave="sine", phase=0.0):
    """An oscillator: 'sine', 'square', 'saw' or 'triangle'. hz may be an array (one value per sample) to glide.
    Square/saw/triangle are naive (they alias a little at high pitch); keep them under ~2 kHz or filter them."""
    count = n(seconds)
    f = np.broadcast_to(np.asarray(hz, dtype=float), (count,)) if np.ndim(hz) == 0 else np.asarray(hz, dtype=float)[:count]
    ph = phase + 2 * np.pi * np.cumsum(f) / RATE
    if wave == "sine":
        return np.sin(ph)
    frac = (ph / (2 * np.pi)) % 1.0
    if wave == "square":
        return np.where(frac < 0.5, 1.0, -1.0)
    if wave == "saw":
        return 2.0 * frac - 1.0
    if wave == "triangle":
        return 1.0 - 4.0 * np.abs(frac - 0.5)
    raise ValueError("wave must be sine, square, saw or triangle")


def sweep(f0, f1, seconds, wave="sine", curve="exp"):
    """A pitch glide from f0 to f1 Hz ('exp' sounds even to the ear, 'lin' is linear in Hz)."""
    u = np.linspace(0.0, 1.0, n(seconds))
    hz = f0 * (f1 / f0) ** u if curve == "exp" else f0 + (f1 - f0) * u
    return osc(hz, seconds, wave)


def modes(freqs, decays, amps, seconds, attack=0.0008):
    """A struck object (wood, metal, glass): decaying sines with a few-sample attack so the strike is not a step.
    Low, fast-decaying modes read as wood; high, long, inharmonic ones as metal or glass."""
    t = tt(n(seconds))
    out = np.zeros(len(t))
    for f, d, a in zip(freqs, decays, amps):
        out += a * np.sin(2 * np.pi * f * t) * np.exp(-t / d)
    return out * (1.0 - np.exp(-t / attack))


def shape(x, lo, hi, tilt=0.0, circular=False):
    """FFT band filter between lo and hi Hz with soft edges, tilted by f^-tilt (1 = pink-ish, 2 = brown-ish).
    Zero-padded unless circular (then the result stays periodic, for loops)."""
    size = len(x) if circular else len(x) + n(0.5)
    f = np.maximum(np.fft.rfftfreq(size, 1.0 / RATE), 1e-3)
    mag = 1.0 / np.sqrt(1.0 + (lo / f) ** 4) / np.sqrt(1.0 + (f / hi) ** 4)
    mag *= (f / max(lo, 1.0)) ** (-tilt / 2.0)
    mag[0] = 0.0
    return np.fft.irfft(np.fft.rfft(x, n=size) * mag, n=size)[: len(x)]


def noise(seconds, lo=20, hi=10000, tilt=0.0, circular=False):
    """Band-limited noise with RMS 1. circular=True makes it periodic over its length (seamless loops)."""
    x = shape(_R.standard_normal(n(seconds)), lo, hi, tilt, circular)
    return x / (np.sqrt(np.mean(x ** 2)) + 1e-12)


def burst(seconds, lo, hi, decay):
    """A noise hit decaying with time constant 'decay' seconds: the scrape or thud on top of modes()."""
    x = noise(seconds, lo, hi)
    return x * np.exp(-tt(len(x)) / decay)


def creak(f0, seconds, rate_hz, ring=1.0, f1=None):
    """Stick-slip creak (door hinge, floorboard, rope): clicks at about rate_hz, each ringing at a pitch gliding f0 -> f1."""
    f1 = f0 * 1.25 if f1 is None else f1
    out = np.zeros(n(seconds))
    g = tt(n(0.03))
    t = 0.0
    while t < seconds:
        f = (f0 + (f1 - f0) * t / seconds) * (1 + 0.04 * _R.standard_normal())
        grain = np.sin(2 * np.pi * f * g) * np.exp(-g / (0.004 * ring))
        place(out, np.sin(np.pi * t / seconds) ** 0.7 * grain, t)
        t += (0.6 + 0.8 * _R.random()) / rate_hz
    return out


# --- envelopes and placement ---------------------------------------------------------------

def adsr(seconds, attack=0.005, decay=0.05, sustain=0.7, release=0.1):
    """An attack/decay/sustain/release envelope 'seconds' long (release included), 0..1."""
    count = n(seconds)
    a, d, r = n(attack), n(decay), n(release)
    s = max(0, count - a - d - r)
    env = np.concatenate([np.linspace(0, 1, a, endpoint=False), np.linspace(1, sustain, d, endpoint=False),
                          np.full(s, sustain), np.linspace(sustain, 0, r)])
    return np.pad(env, (0, max(0, count - len(env))))[:count]


def fade(x, in_s=0.002, out_s=0.05):
    """Fade the ends so a one-shot starts and ends at silence (no click)."""
    x = np.array(x, dtype=float)
    a, b = min(n(in_s), len(x)), min(n(out_s), len(x))
    if a:
        x[:a] *= np.linspace(0, 1, a)
    if b:
        x[-b:] *= np.cos(np.linspace(0, np.pi / 2, b)) ** 2
    return x


def lfo(seconds, cycles, phase=0.0):
    """A 0..1 sine with 'cycles' cycles over 'seconds'. Whole cycles keep a loop seamless."""
    t = tt(n(seconds))
    return 0.5 + 0.5 * np.sin(2 * np.pi * cycles * t / seconds + phase)


def loop_safe(hz, seconds):
    """Round a frequency to a whole number of cycles in 'seconds', so a tone loops without a seam."""
    return max(1, round(hz * seconds)) / seconds


def mix(*parts):
    """Sum arrays of different lengths (padded at the end)."""
    out = np.zeros(max(len(p) for p in parts))
    for p in parts:
        out[: len(p)] += p
    return out


def place(buf, ev, at, wrap=False):
    """Add event 'ev' into 'buf' (in place) at 'at' seconds. wrap=True brings what runs past the end round to the start."""
    i = n(at)
    if wrap:
        np.add.at(buf, (i + np.arange(len(ev))) % len(buf), ev)
        return buf
    if i < len(buf):
        j = min(len(buf), i + len(ev))
        buf[i:j] += ev[: j - i]
    return buf


def reverb(x, seconds, wet, lo=80, hi=6000, circular=False):
    """A room: x plus 'wet' times x convolved with decaying filtered noise ('seconds' long). circular for loops.
    A one-shot's tail is cut at len(x): pad it with silence first if the tail must ring out."""
    m = n(seconds)
    ir = _R.standard_normal(m) * np.exp(-tt(m) / (seconds / 6.0)) * (1 - np.exp(-tt(m) / 0.008))
    ir = shape(ir, lo, hi)
    ir /= np.sqrt(np.sum(ir ** 2))
    size = len(x) if circular else len(x) + m
    if circular and m > len(x):
        raise ValueError("a circular reverb must be shorter than the loop")
    wet_sig = np.fft.irfft(np.fft.rfft(x, n=size) * np.fft.rfft(ir, n=size), n=size)[: len(x)]
    return x + wet * wet_sig


# --- level, checks and files ---------------------------------------------------------------

def db(v):
    return 20 * np.log10(max(float(v), 1e-12))


def level(x, rms_db=None, peak_db=-3.0, remove_dc=False):
    """Scale to an RMS target (if given) without letting the peak pass peak_db; else peak to peak_db.
    Rough targets: one-shots peak -3 dBFS; music RMS about -24; ambience beds RMS -26 to -32.
    remove_dc subtracts the mean first (for loops with an offset; on a one-shot it lifts the faded ends off zero)."""
    x = np.asarray(x, dtype=float)
    if remove_dc:
        x = x - np.mean(x)
    if rms_db is not None:
        x = x * (10 ** (rms_db / 20) / (np.sqrt(np.mean(x ** 2)) + 1e-12))
        ceiling = 10 ** (peak_db / 20)
        peak = np.max(np.abs(x))
        if peak > ceiling:
            x *= ceiling / peak
        return x
    return x * (10 ** (peak_db / 20) / (np.max(np.abs(x)) + 1e-12))


def stats(x):
    """Duration, peak and RMS (dBFS), the start/end levels relative to the peak, and the loop seam versus typical steps."""
    x = np.asarray(x, dtype=float)
    peak = np.max(np.abs(x)) + 1e-12
    edge = max(1, n(0.005))
    steps = np.abs(np.diff(x))
    return {
        "seconds": len(x) / RATE,
        "peak_db": db(peak),
        "rms_db": db(np.sqrt(np.mean(x ** 2))),
        "start": abs(x[0]) / peak,
        "end": np.max(np.abs(x[-edge:])) / peak,
        "seam": abs(x[-1] - x[0]),
        "p99_step": float(np.percentile(steps, 99)) if len(steps) else 0.0,
    }


def check_oneshot(x, tolerance=0.01):
    """Raise unless x starts at silence, ends in silence (last 5 ms) and does not clip."""
    s = stats(x)
    if s["peak_db"] > -0.1:
        raise ValueError(f"clips: peak {s['peak_db']:.1f} dBFS")
    if s["start"] > tolerance:
        raise ValueError(f"starts with a click ({s['start']:.3f} of peak); fade() the start")
    if s["end"] > tolerance:
        raise ValueError(f"does not end in silence ({s['end']:.3f} of peak); fade() the end or pad the tail")
    return s


def check_loop(x, headroom=1.5):
    """Raise unless the jump from the last sample back to the first is no bigger than a typical (99th percentile) step,
    with some headroom (a periodic tone's seam can fall on its steepest step). A real seam is many times bigger."""
    s = stats(x)
    if s["peak_db"] > -0.1:
        raise ValueError(f"clips: peak {s['peak_db']:.1f} dBFS")
    if s["seam"] > headroom * s["p99_step"]:
        raise ValueError(f"loop seam {s['seam']:.4f} is bigger than a typical step {s['p99_step']:.4f}; "
                         "build it from periodic parts (circular noise, whole-cycle lfo/loop_safe tones, wrap, circular reverb)")
    return s


def write_wav(path, x):
    """Write 16-bit mono PCM WAV at RATE. Values beyond -1..1 are clipped, so level() first."""
    pcm = np.clip(np.round(np.asarray(x) * 32767.0), -32768, 32767).astype("<i2")
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    with wave.open(str(path), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())


def read_wav(path):
    """Read an 8/16/32-bit PCM WAV as mono floats. Returns (samples, sample_rate)."""
    with wave.open(str(path), "rb") as w:
        width, channels, rate = w.getsampwidth(), w.getnchannels(), w.getframerate()
        raw = w.readframes(w.getnframes())
    if width == 1:
        x = (np.frombuffer(raw, dtype=np.uint8).astype(float) - 128) / 128
    elif width == 2:
        x = np.frombuffer(raw, dtype="<i2").astype(float) / 32768
    elif width == 4:
        x = np.frombuffer(raw, dtype="<i4").astype(float) / 2147483648
    else:
        raise ValueError(f"{path}: unsupported sample width {width}")
    return x.reshape(-1, channels).mean(axis=1), rate


def spectrogram(x, width=640, height=96, rate=RATE):
    """A greyscale log-frequency (40 Hz - 10 kHz, high at the top) spectrogram strip as a PIL image."""
    from PIL import Image
    win = 1024
    x = np.asarray(x, dtype=float)
    if len(x) < win * 2:
        x = np.pad(x, (0, win * 2 - len(x)))
    hop = int(np.clip((len(x) - win) // width, 16, 256))  # short clips still get enough columns
    frames = 1 + (len(x) - win) // hop
    w = np.hanning(win)
    spec = np.array([np.abs(np.fft.rfft(x[i * hop:i * hop + win] * w)) for i in range(frames)]).T
    freqs = np.fft.rfftfreq(win, 1.0 / rate)
    rows = np.geomspace(40, min(10000, rate / 2), height)[::-1]
    s = 20 * np.log10(spec[np.clip(np.searchsorted(freqs, rows), 0, len(freqs) - 1)] + 1e-9)
    s = np.clip((s - (s.max() - 70)) / 70, 0, 1)
    return Image.fromarray((s * 255).astype(np.uint8), "L").resize((width, height), Image.BILINEAR)


def preview(out_png, *paths, width=640, height=96):
    """Stack a labelled spectrogram strip per WAV file into one PNG, to look at what was made."""
    from PIL import Image, ImageDraw
    cell = height + 16
    sheet = Image.new("L", (width, cell * len(paths)), 0)
    draw = ImageDraw.Draw(sheet)
    for i, p in enumerate(paths):
        x, rate = read_wav(p)
        sheet.paste(spectrogram(x, width, height, rate), (0, i * cell + 16))
        draw.text((4, i * cell + 2), f"{Path(p).name}  {len(x) / rate:.2f} s  (40 Hz - 10 kHz, log)", fill=220)
    sheet.save(out_png)
    return out_png


# --- demo ----------------------------------------------------------------------------------

def demo(folder):
    """Three example clips covering the main techniques: a UI blip, a door knock and a seamless room tone."""
    folder = Path(folder)
    seed(7)

    blip = fade(sweep(660, 990, 0.12) * adsr(0.12, 0.004, 0.04, 0.5, 0.06), 0.002, 0.02)
    blip = level(blip, peak_db=-6)

    knock = silence(0.9)
    for at in (0.0, 0.22, 0.44):
        hit = modes([180, 410, 690, 1150], [0.06, 0.035, 0.02, 0.012], [1.0, 0.5, 0.3, 0.15], 0.4)
        place(knock, hit + 0.25 * burst(0.4, 300, 3000, 0.006), at + 0.01)
    knock = level(fade(reverb(knock, 0.5, 0.25), 0.002, 0.08))

    loop = 4.0
    bed = noise(loop, 60, 900, tilt=1.0, circular=True) * (0.6 + 0.4 * lfo(loop, 2))
    bed += 0.3 * osc(loop_safe(55, loop), loop) * lfo(loop, 1, 1.0)
    bed = level(reverb(bed, 1.2, 0.4, circular=True), rms_db=-28)

    made = {"demo_blip.wav": (blip, check_oneshot), "demo_knock.wav": (knock, check_oneshot),
            "demo_roomtone.wav": (bed, check_loop)}
    for name, (x, check) in made.items():
        check(x)
        write_wav(folder / name, x)
        print(f"wrote {folder / name}")
    return [folder / name for name in made]


def _info(paths):
    print(f"{'file':28} {'sec':>6} {'peak':>6} {'rms':>6} {'start':>6} {'end':>6} {'seam':>7} {'p99':>7}")
    for p in paths:
        x, rate = read_wav(p)
        s = stats(x)
        print(f"{Path(p).name:28} {len(x) / rate:6.2f} {s['peak_db']:6.1f} {s['rms_db']:6.1f} "
              f"{s['start']:6.3f} {s['end']:6.3f} {s['seam']:7.4f} {s['p99_step']:7.4f}")
    print("start/end: level at the ends relative to the peak (one-shots want ~0); "
          "seam <= 1.5 x p99 means it loops without a click.")


def main(argv):
    if len(argv) >= 2 and argv[0] == "info":
        _info(argv[1:])
    elif len(argv) >= 3 and argv[0] == "preview":
        print(preview(argv[1], *argv[2:]))
    elif len(argv) == 2 and argv[0] == "demo":
        paths = demo(argv[1])
        _info(paths)
    else:
        print(__doc__)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
