# Sound effects and ambience

1. [The effects script](#the-effects-script)
2. [The toolbox](#the-toolbox)
3. [Recipes](#recipes)
4. [Ambience loops](#ambience-loops)
5. [Levels](#levels)

## The effects script

One `effects.py` per game, beside `music.py`, seeded, with `sfx_cli` as its main:

```python
"""The game's sound effects: one-shots for the duel, the dialogue, the found card.
    python effects.py [--ogg] [--wav DIR] [--preview sheet.png] [name ...]
"""
from chip import *

def card_play():
    """A card slapped onto felt: a bright paper snap over a soft thump."""
    seed(101)
    snap = sfx.burst(0.25, 900, 7000, 0.010)
    thump = modes([150, 310], [0.04, 0.02], [1, 0.3], 0.25)
    return oneshot(0.8 * snap + 0.7 * thump, peak_db=-5)

EFFECTS = {f.__name__: f for f in (card_play, ...)}

if __name__ == "__main__":
    sfx_cli(EFFECTS, out="../../assets/audio/sfx")
```

Each effect is a function that returns a finished array. **Seed inside each effect** (`seed(101)` as its first line, a different constant per effect) rather than once in `main`: then `python effects.py card_play` alone renders the same bytes as a full run, and adding an effect in the middle of the list does not change the ones after it. `oneshot(x, peak_db=-3, rms_db=None, tail=0.05, reverb_s=0, wet=0.25)` adds an optional room (padding the clip so the tail rings), fades both ends, sets the level and runs `check_oneshot`, which raises on a click at either end or clipping. Give every function a one-line docstring saying what the sound is; the user reads those as the sound list.

## The toolbox

From `sfx.py` (all re-exported by `from chip import *`):

| Call | What it makes |
|---|---|
| `osc(hz, s, "sine"\|"square"\|"saw"\|"triangle")` | A tone; `hz` may be an array for glides |
| `sweep(f0, f1, s, wave, curve)` | A pitch glide (`"exp"` sounds even) |
| `modes(freqs, decays, amps, s)` | A struck object: decaying sines. Low, fast-decaying modes are wood; high, long, inharmonic ones are metal or glass |
| `noise(s, lo, hi, tilt, circular)` | Band noise, RMS 1 |
| `burst(s, lo, hi, decay)` | A noise hit with a time constant: the contact on top of `modes` |
| `creak(f0, s, rate_hz, ring, f1)` | Stick-slip: a hinge, a floorboard, a rope |
| `adsr(s, a, d, sus, r)`, `fade(x, in_s, out_s)`, `lfo(s, cycles)` | Envelopes |
| `place(buf, ev, at, wrap)`, `mix(*parts)`, `silence(s)` | Arranging |
| `reverb(x, s, wet, lo, hi, circular)` | A room |
| `shape(x, lo, hi, tilt, circular)` | The band filter |
| `level`, `limit`, `stats`, `check_oneshot`, `check_loop`, `write_wav`, `preview` | Finishing and checking |

From `chip.py`: the voices (`pulse(hz, s, duty=..., env=...)`, `tri`, `glock`, `bell`, `musicbox`, `fm`, ...) are the fastest way to a jingle note, and the drums (`kick()`, `snare()`, `nhit()`, `heartbeat()`) to a thud. `shnoise(s, hold)` is the NES noise channel; `crush(x, bits, hold)` adds grit; `echo(x, delay_s, feedback, taps, circular=False)` for one-shots.

Modal ratios that read as materials: wood `[1, 2.3, 3.8, 6.4]` with decays under 0.06 s; glass and bells `[1, 2.76, 5.4, 8.93]` with decays over 0.5 s; a music-box tine `[1, 2.0, 4.07, 6.3]` with a strong fundamental; metal plates `[1, 1.47, 2.09, 2.56, 3.39]`; a clock rod `[1, 2.76, 5.4, 8.93]` with decays `[d / r ** 0.5]`.

## Recipes

**UI and feedback**

```python
blip   = oneshot(sweep(660, 990, 0.12) * adsr(0.12, 0.004, 0.04, 0.5, 0.06), -6)                 # a confirm
talk   = silence(0.25); place(talk, pulse(note("A3"), 0.07, duty=0.5, env=(0.002, 0.02, 0.6, 0.02)), 0)
place(talk, pulse(note("E4"), 0.09, duty=0.5, env=(0.002, 0.02, 0.6, 0.04)), 0.07)
talk   = oneshot(shape(talk, 80, 3000), -10)                                                      # someone starts talking
voice  = pulse(note("A2") * (1 + 0.03 * rng().standard_normal()), 0.055, duty=0.3, env=(0.002, 0.01, 0.8, 0.02))
voice  = oneshot(shape(np.concatenate([voice, silence(0.03)]), 180, 1400), -12, 0.03)             # one typewriter beat of a voice
hp_you = silence(0.9)
for i, p in enumerate(("E5", "C5", "A4", "F4")):                                                 # lose a heart: falling blips + thud
    place(hp_you, pulse(note(p), 0.09, duty=0.5, env=(0.002, 0.02, 0.7, 0.02)), i * 0.08)
place(hp_you, 1.2 * sweep(120, 45, 0.4) * np.exp(-tt(n(0.4)) / 0.12), 0.3)
hp_you = oneshot(hp_you, -4, 0.1, reverb_s=0.5, wet=0.2)
hp_foe = silence(1.2)
for i, p in enumerate(("A5", "C6", "E6")):                                                       # the foe loses one: rising glock
    place(hp_foe, glock(note(p), 0.2), i * 0.07)
hp_foe = oneshot(hp_foe, -6, 0.2, reverb_s=0.6, wet=0.25)
found  = silence(1.5)
for i, p in enumerate(("E5", "A5", "C6", "E6", "A6")):                                           # a pickup: a sparkle run and shimmer
    place(found, glock(note(p), 0.4), i * 0.06)
place(found, 0.12 * noise(0.8, 5000, 10000) * np.exp(-tt(n(0.8)) / 0.25), 0.2)
found  = oneshot(found, -5, 0.3, reverb_s=0.8, wet=0.3)
```

**Jingles** (win, lose) are better as stingers in `music.py` (`Score(loop=False)`), so they share the game's motif and kit. In `effects.py` the same thing by hand:

```python
win = silence(2.6)
for i, p in enumerate(("D5", "F5", "A5", "D6")):
    place(win, pulse(note(p), 0.13, duty=0.25), i * 0.11)
for p in ("D5", "F5", "A5"):
    place(win, 0.6 * pulse(note(p), 1.5, duty=0.25, vib=0.2, env=(0.01, 0.2, 0.6, 0.6)), 0.46)
place(win, 0.8 * tri(note("D3"), 1.6, env=(0.005, 0.1, 0.8, 0.5)), 0.46)
place(win, 0.7 * glock(note("D6"), 1.5), 0.46)
win = oneshot(shape(win, 60, 6000), -3, 0.3, reverb_s=1.2, wet=0.3)
lose = silence(3.0)
for i, p in enumerate(("A4", "G#4", "G4", "F#4", "F4")):                                        # a chromatic fall, a low bell
    place(lose, pulse(note(p), 0.32, duty=0.5, vib=0.25, env=(0.005, 0.05, 0.7, 0.1)), i * 0.3)
place(lose, 0.9 * tri(note("D2"), 1.6, env=(0.005, 0.1, 0.8, 0.8)), 1.5)
place(lose, 0.6 * bell(note("D3"), 1.5), 1.5)
lose = oneshot(shape(lose, 50, 5000), -3, 0.3, reverb_s=1.4, wet=0.35)
```

**Objects and materials**

```python
hit   = modes([180, 410, 690, 1150], [0.06, 0.035, 0.02, 0.012], [1, 0.5, 0.3, 0.15], 0.4) + 0.25 * burst(0.4, 300, 3000, 0.006)
knock = silence(0.9)
for at in (0.0, 0.22, 0.44):
    place(knock, hit, at + 0.01)
knock = oneshot(knock, -3, 0.08, reverb_s=0.5, wet=0.25)                                         # three knocks on wood
latch = modes([2100, 3400, 5200], [0.02, 0.012, 0.008], [1, 0.6, 0.3], 0.15) + 0.4 * burst(0.15, 2000, 7000, 0.003)
door  = oneshot(creak(320, 1.2, 60, ring=1.5, f1=410), -3, 0.1)                                   # a hinge
bellx = oneshot(bell(440, 3.0), -3, 0.4)                                                          # a bell, one strike
tick  = modes([2400, 3900, 420, 760], [0.006, 0.004, 0.03, 0.02], [0.6, 0.3, 0.8, 0.4], 0.12) + 0.3 * burst(0.12, 2000, 8000, 0.0015)
cards = silence(1.1); t, gap = 0.02, 0.045                                                        # a riffle shuffle
while t < 0.75:
    place(cards, (0.5 + 0.5 * rng().random()) * burst(0.02, 1500, 9000, 0.002), t)
    t += gap * (0.7 + 0.6 * rng().random()); gap = max(0.012, gap * 0.94)
place(cards, 0.5 * noise(0.3, 1200, 6000) * np.hanning(n(0.3)), 0.72)
cards = oneshot(cards, -6, 0.08, reverb_s=0.3, wet=0.15)
draw  = oneshot(noise(0.24, 1800, 8000) * np.hanning(n(0.24)) ** 1.5, -10)                        # a card off the deck
coin  = oneshot(mix(glock(note("B6"), 0.3), 0.6 * glock(note("E7"), 0.3)), -4, 0.2)
```

**Machines**

```python
def ratchet(strokes, clicks, gap, f):                                                             # a wind-up key, a crank
    x = silence(strokes * (clicks / 16 + gap) + 0.4); at = 0.03
    for s in range(strokes):
        for c in range(clicks):
            place(x, (0.5 + 0.3 * rng().random()) * modes([f, f * 1.6], [0.01, 0.006], [1, 0.4], 0.05), at)
            at += 1 / 16 * (1 + 0.15 * c / clicks)
        at += gap
    return x
windup = oneshot(ratchet(4, 6, 0.18, 2600), -6, 0.2, reverb_s=0.5, wet=0.2)
phone  = (np.sin(2 * np.pi * 20 * tt(n(1.1))) > 0).astype(float) * modes([1350, 2650, 4100], [0.15, 0.1, 0.06], [1, 0.5, 0.25], 1.1)
phone  = oneshot(shape(phone, 400, 6000), -7, 0.15, reverb_s=0.5, wet=0.2)                      # an old bell phone, once
u = np.linspace(0, 1, n(1.0)); rev = np.interp(u, [0, 0.15, 0.55, 1], [18, 22, 46, 30])          # a two-stroke engine revving
pops = np.clip(np.sin(2 * np.pi * np.cumsum(rev) / RATE), 0, None) ** 6
engine = oneshot(shape(pops * noise(1.0, 120, 2500) + 0.5 * pops * osc(rev * 3, 1.0, "saw"), 80, 5000) * np.interp(u, [0, 0.08, 0.7, 1], [0, 1, 0.9, 0]), -6)
```

**Weather and movement**

```python
whoosh = noise(1.4, 200, 4000, tilt=0.5) * np.sin(np.pi * np.linspace(0, 1, n(1.4))) ** 2          # into a fight
whoosh = oneshot(whoosh + 0.6 * noise(1.4, 30, 120, tilt=1) * np.sin(np.pi * np.linspace(0, 1, n(1.4))), -6, 0.2, reverb_s=0.8, wet=0.3)
gust   = np.sin(np.pi * np.linspace(0, 1, n(2.2))) ** 2
wind   = oneshot(shape(noise(2.2, 250, 2000, tilt=0.3) * gust + 0.25 * osc(np.interp(np.linspace(0, 1, n(2.2)), [0, 0.5, 1], [210, 290, 190]), 2.2) * gust, 150, 2600), -13, 0.3)
def step(lo, hi, crunch=0.0):                                                                     # one footstep: heel thud, toe scuff
    out = silence(0.4)
    place(out, burst(0.12, 60, 400, 0.025) + 0.4 * modes([95, 180], [0.03, 0.02], [1, 0.4], 0.12), 0)
    place(out, 0.5 * burst(0.08, lo, hi, 0.02), 0.07)
    for k in range(5 if crunch else 0):
        place(out, crunch * (0.4 + 0.6 * rng().random()) * burst(0.03, 1500, 7000, 0.006), 0.02 + 0.025 * k)
    return out
steps = silence(3.6)
for t0 in (0.0, 0.62, 1.22, 1.85, 2.95):                                                          # ... and one more after they should stop
    place(steps, step(300, 2500), t0 + 0.03 * rng().random())
steps = oneshot(steps, -8, 0.4, reverb_s=1.4, wet=0.35)
drip  = oneshot(mix(*[np.pad(sweep(f, f * 2.2, 0.06) * np.exp(-tt(n(0.06)) / 0.018), (n(t0), 0)) for t0, f in ((0, 1500), (0.42, 1250), (1.1, 1650))]), -8, 0.3, reverb_s=1.0, wet=0.4)
```

**Voices without words** (formant synthesis: a buzzy source through vowel resonances)

```python
VOWEL_A = [(700, 110, 1.0), (1220, 120, 0.5), (2600, 160, 0.25)]         # (centre Hz, bandwidth, gain)
def formant(x, formants):
    size = len(x) + n(0.2); f = np.fft.rfftfreq(size, 1 / RATE)
    mag = sum(a * np.exp(-0.5 * ((f - fc) / bw) ** 2) for fc, bw, a in formants) + 0.02
    return np.fft.irfft(np.fft.rfft(x, size) * mag, size)[: len(x)]
def syllable(f0, dur, f0_end=None, breath=0.06):
    hz = np.geomspace(f0, f0_end or f0 * 0.85, n(dur)) * (1 + 0.01 * np.sin(2 * np.pi * 7 * tt(n(dur))))
    src = osc(hz, dur, "saw") + 0.5 * osc(hz / 2, dur, "saw")
    out = silence(dur + breath)
    place(out, 0.5 * formant(noise(breath, 300, 6000), VOWEL_A) * np.hanning(n(breath)), 0)
    place(out, formant(src, VOWEL_A) * adsr(dur, 0.02, 0.05, 0.8, dur * 0.4), breath * 0.6)
    return out
laugh = silence(3.3); t, f0 = 0.05, 92                                                            # six low "ha"s and a long one
for i in range(6):
    place(laugh, syllable(f0, 0.2), t); t += 0.3 - i * 0.015; f0 *= 0.965
place(laugh, syllable(f0, 0.8, f0 * 0.7, breath=0.08), t + 0.05)
laugh = oneshot(shape(crush(laugh, 7), 50, 5000), -3, 0.25, reverb_s=1.6, wet=0.4)
giggle = silence(0.9); t, f0 = 0.0, 520                                                           # a child: four quick blips, falling
for _ in range(4):
    place(giggle, 0.8 * syllable(f0, 0.07, f0 * 0.9, breath=0.02), t); t += 0.12; f0 *= 0.94
giggle = oneshot(shape(giggle, 300, 7000), -14, 0.15)
```

A whisper is `formant(noise(d, 200, 7000), vowel)` for 9 short vowels from a list, with a hissed `noise(0.07, 3500, 9000)` before some. A breath through a mask is `noise(0.9, 300, 1800, tilt=0.6) * sin(pi u) ** 1.5`, in then out lower.

## Ambience loops

Beds are `Score`-less: build the array directly and `check_loop` it. Every part must be periodic over the loop: `noise(..., circular=True)`, `lfo(L, whole_cycles)`, tones at `loop_safe(hz, L)`, `place(..., wrap=True)`, `reverb(..., circular=True)`.

```python
L = 24.0
bed = noise(L, 60, 900, tilt=1, circular=True) * (0.6 + 0.4 * lfo(L, 3))        # a still room
bed += 0.3 * osc(loop_safe(55, L), L) * lfo(L, 1, 1.0)                            # a hum
for at in (1.3, 5.9, 10.4):
    place(bed, 0.4 * modes([1800], [0.05], [1], 0.3), at, wrap=True)              # drips
bed = level(reverb(bed, 1.5, 0.4, circular=True), rms_db=-24)
check_loop(bed); write_wav("amb_cellar.wav", bed)
```

Sea: `noise(L, 40, 600, tilt=1.5, circular=True) * (0.5 + 0.5 * lfo(L, 2)) ** 2` plus a `noise(L, 800, 4000, circular=True) * lfo(L, 2) ** 3` for the foam. Wind over leads: `noise(L, 200, 2200, tilt=0.3, circular=True) * (0.3 + 0.7 * lfo(L, 4))` with a thin `osc(loop_safe(2100, L), L) * lfo(L, 4) ** 4 * 0.05` whistle. A presence: a low drone of `loop_safe` partials at inharmonic ratios, each a slowly beating pair (two sines a few cents apart), well under −26 dB. A phonograph: hiss modulated at the turn rate (`lfo(L, turns)`), 30–120 Hz rumble, seventy tiny `burst(0.004, 800, 9000, 0.0007)` pops placed at random with `wrap=True`.

Loop lengths of 12–30 s hide the repetition. Layer two or three beds in the engine (if its channel limit allows) rather than one busy one.

## Levels

Render loud; an engine can only turn a file down.

| Kind | Target |
|---|---|
| UI blips, small props | peak −6 to −3 dBFS |
| Doors, hits, events | peak −3 to −1 |
| Jingles | peak −3; RMS about −16 |
| Ambience beds | RMS −26 to −20, under the music and the dialogue |

Check RMS as well as peak: a card snap peaked at −3 can sit at RMS −26 and vanish under music at −16; give such sounds `oneshot(x, peak_db=-1, rms_db=-20)`, which soft-limits the peak. Generate a family in one script and compare their `sfx_cli` lines: siblings within 3 dB of each other, quieter things actually quieter. An engine-level master (`master()` in Deck of Destiny lifted everything 13.5 dB for the Explorer's volume cap) belongs in `engines.md`, not in the recipes.
