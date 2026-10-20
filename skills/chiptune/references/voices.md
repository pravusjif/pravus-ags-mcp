# Voices, drums, effects and kits

1. [Voices](#voices)
2. [Drums](#drums)
3. [Effects](#effects)
4. [Kits](#kits)
5. [Textures: kit layers](#textures-kit-layers)
6. [The master](#the-master)
7. [Era checklists](#era-checklists)

`python chip.py voices` prints the live list with signatures. A voice is `fn(hz, seconds, **params) -> array`; it rings past `seconds` (its release, a bell's decay, a piano's ring), and the sequencer places whatever comes back, so notes overlap their successors as on an instrument. Any key in a kit spec that is not `voice`, `drum`, `gain`, `gate`, `octave`, `chord`, `fx`, `pan`, `width`, `pingpong` or `jitter` is passed to the voice.

Common voice parameters: `env=(attack, decay, sustain, release)` in seconds and level (the release is added after the gate); `vib` in semitones with a delayed onset; `drift` in cents (a random detune per note: 5–10 is a tired instrument, 20 a fairground); `sag` in cents (the pitch falls over the note); `lp` in Hz (a low-pass on the note: 5–8 kHz takes the edge off a pulse, 1.5–2.5 kHz makes a pad sit behind the lead); `clean=False` on a pulse or saw for the raw aliasing square of a naive digital oscillator (band-limited polyBLEP is the default and the reason the mix does not buzz).

## Voices

| Voice | Character | Parameters | Use it for |
|---|---|---|---|
| `pulse` | NES pulse channel. `duty` 0.5 hollow and round, 0.25 the classic lead, 0.125 thin and nasal | `duty`, `vib`, `arp` (semitone offsets cycled at `arp_hz`), `glide` (Hz to slide in from), `drift`, `sag`, `lp`, `clean`, `env` | Leads, counters, arps, 8-bit bass (duty 0.5, low) |
| `tri` | NES triangle, 16 steps: soft, flute-like high, a round bass low | `vib`, `arp`, `drift`, `env` | Bass; a quiet lead an octave up |
| `saw` | Sawtooth, buzzy; band-limited unless `clean=False` | `vib`, `drift`, `sag`, `lp`, `clean`, `env` | C64/Genesis leads, pads through a filter |
| `sine` | Pure tone | `vib`, `drift`, `sag`, `env` | Whines, sub-bass, soft leads |
| `fm` | Two-operator FM: `ratio` sets the timbre's harmonic family, `index` its brightness, `decay` how fast it mellows | `ratio`, `index`, `decay`, `vib`, `drift`, `lp`, `env` | Ratio 1–2, index 2–4: e-piano, bass; ratio 3.5–7, long decay: bells; ratio 1, index 6: brass |
| `fmbass` | `fm` preset: ratio 1, index 3, fast decay | as `fm` | 16-bit bass |
| `fmbell` | `fm` preset: ratio 3.5, index 4, rings 1.2 s+ | as `fm` | Bells, chimes, arps |
| `flute` | Sine with vibrato and a breath of noise | — | Pastoral leads |
| `organ` | Square plus octaves and a sub | `lp`, `env` | Church, dirges, stabs |
| `pad` | Three detuned saws with a slow swell, low-passed | `detune`, `lp` | Beds under everything; add `width` in the kit |
| `choir` | Three detuned sines with vibrato plus overtones | — | "Aah" pads, finales |
| `musicbox` | Modal tine: pure fundamental, weak overtones, rings 1.4 s | — | Lullabies, innocence, the motif slow |
| `bell` | Inharmonic modes, rings 3.5 s | — | Tolls, one note a bar |
| `glock` | Short bright modes | — | Sparkle, victory runs |
| `pluck` | Harmonic modes, fast decay | — | Banjo, harp, lute |
| `calliope` | Square + octave with a random detune per note and a wobble | `detune_cents`, `sag`, `lp` | Fairgrounds, anything out of tune |
| `harmonium` | Additive reeds, slow swell, slow release, slight beat (rings 1.6 s past the note) | — | The acoustic kit's pad |
| `bowed` | String section: saw-like partials, slow bow, vibrato that grows in | `att`, `rel`, `vib`, `trem` | Strings, a solo string with `att=0.4` |
| `piano` | Upright: three detuned strings, stretched partials, a hammer (ring depends on pitch) | `vel`, `detune` (cents per string), `b` (inharmonicity) | Parlour piano; `detune=(0, 7, -5)` for a bad one |

Doubling: a lead on `pulse` duty 0.25 plus the same pattern an octave down on duty 0.5 at half the gain is the standard NES lead; a lead plus `glock` on the same pattern is the 16-bit sparkle.

Register: pulses and FM sound best between C3 and C6; `tri` bass C1–C3; pads C3–C5; bells and music boxes C5–C7; the piano anywhere.

## Drums

Every drum is `fn(**params) -> array`, called once per hit (so noise differs hit to hit; the score's seed keeps the whole render repeatable).

| Drum | Sound | Parameters |
|---|---|---|
| `kick` | A pitch sweep down with a short noise click | `seconds`, `f0`, `f1`, `hard` (0–1 click) |
| `snare` | Noise with a tone: NES sample-and-hold grit, or `white=True` for the cleaner 16-bit snare | `seconds`, `tone`, `white` |
| `hat` / `ohat` | Closed / open: high-passed noise | `seconds` |
| `tom` | A sweep | `hz`, `seconds` |
| `clap` | Three noise bursts | `seconds` |
| `wood` | Two short modes | `hz`, `seconds` |
| `metal` | Inharmonic modes plus a burst | `seconds`, `hz` |
| `nhit` | The bare NES noise channel: `hold` 1 is a snare, 2–4 a hat, 8–16 a kick-ish thud | `seconds`, `hold`, `decay` |
| `heartbeat` | Two soft kicks | `seconds` |

Custom drums: any callable works, `kick2=dict(drum=lambda: kick(0.3, 160, 40, hard=0.6))`, or a numpy array via `drum=lambda: my_array`.

## Effects

A part's `fx` is a list of tuples, applied in order after the part is rendered: `fx=[("filt", 60, 6000), ("echo", 0.25, 0.3, 2), ("crush", 6)]`.

| Effect | Arguments | Notes |
|---|---|---|
| `filt` | `lo`, `hi`, `tilt` | A band filter with soft edges; `tilt` 1 darkens like pink noise. Circular on loops. |
| `echo` | `delay_s`, `feedback`, `taps` | A delay line. Make the delay a whole number of steps (`3 * score.step` is a dotted eighth at 4 steps per beat), or it fights the grid; `kit_for()` does this for the lead and the arp. Circular on loops. |
| `crush` | `bits`, `hold` | Quantise to `bits` (4–6 gritty, 8 subtle) and hold samples (`hold` 4 at 44.1 kHz is an early sampler). Per-sample, so loops stay seamless. |
| `saturate` | `drive` | Tape-style soft clipping, level-normalised: 1.2 barely there, 2–3 warmth, more is distortion. |

A custom effect is a callable `fn(x, *args, circular=...)` in the tuple's first slot. Reverb is a master effect, not a part effect (one room per cue).

**Stereo keys** (per part, in a kit that is stereo): `pan` −1..1 (equal-power), `width` 0..1 (the part rendered twice, detuned ±7 cents × width, spread left and right of its pan: the pad and the lead want it, the bass and the kick do not), and `pingpong=(delay_s, feedback, taps)`, an echo whose taps alternate right and left. A kit is stereo when any part has one of these or when `Kit(stereo=True)`; `GRAYBOX` and `nes` are mono by design.

## Kits

```python
Kit(lead=dict(voice="pulse", duty=0.25, vib=0.15, gain=0.45, gate=0.75, fx=[("echo", 0.3516, 0.25, 2)]),
    counter="fm",                                  # a bare voice name is a spec with defaults
    bass=dict(voice="tri", gain=0.85, gate=0.8),
    pad=dict(voice="pad", gain=0.14, gate=1.0, fx=[("filt", 60, 1400)]),
    kick=dict(drum="kick", gain=0.9, hard=0.3),
    master=dict(rms_db=-15, verb=(1.4, 0.25)),
    layers=[(lambda L: noise(L, 120, 1400, tilt=1, circular=True) * (0.35 + 0.65 * lfo(L, 3)), 0.15, None)],
    name="snes-ish")
```

- **Lookup order** for a part: the kit's entry with the part's name, then with its role (`lead`, `counter`, `arp`, `bass`, `pad`, `melodic`, `drums`), then `GRAYBOX`'s. So a kit that names only `lead` and `bass` still plays everything.
- `gain` is the part's level in the mix. Starting points: lead 0.4–0.5, counter 0.25, arp 0.1–0.2, pad 0.1–0.15, bass 0.7–0.9 (`tri` is quiet), kick 0.8–0.9, snare 0.3–0.4, hat 0.1–0.15. The master then sets the loudness of the whole.
- `gate` here is the instrument's default articulation; the score's `gate` wins when set.
- `octave` shifts the part (a kit may put the acoustic bass an octave lower than the chip one).
- `chord="arp"` plays a chord pattern on one voice cycling its notes at `arp_hz` (the NES way); `"stack"` (default) sounds every note.
- `.with_parts(**parts)` copies a kit with some entries replaced; `.part(name, **spec)` adds one in place; `.layer(fn, gain, fx)` adds a texture.
- **Era kits** in `KITS`: `graybox`, `nes` (mono, dry, raw), `snes`, `genesis`, `acoustic` (stereo), `lofi` (`snes` through a crushed sampler). `kit_for(score, "snes", lead=..., ...)` returns that base with your overrides and, for `nes`, `snes` and `lofi`, grid-timed echoes on the lead (dotted eighth) and the arp (sixteenth): a ping-pong in a stereo kit, a mono echo otherwise. An override that brings its own `fx` or `pingpong` keeps exactly that, one without gets the echo added. Build per-cue kits from these rather than from scratch, and keep them in the music script under the scores so the two passes are visibly separate.

Per-cue variation on one kit: `KITS = {"explore": lambda: kit_for(explore(), "snes"), "boss": lambda: kit_for(boss(), "snes", lead=dict(voice="pulse", duty=0.125, vib=0.25, gain=0.5))}`.

## Textures: kit layers

Layers are sound design, not composition, so they live in the kit. A layer is `(fn, gain, fx)` where `fn(total_seconds)` returns an array. For a loop it must be periodic over the length: circular noise, LFOs with whole cycles, tones from `loop_safe()`, events placed with `wrap=True`.

```python
def wind(L):     return noise(L, 120, 1400, tilt=1, circular=True) * (0.35 + 0.65 * lfo(L, 3))
def sea(L):      return noise(L, 40, 600, tilt=1.5, circular=True) * (0.5 + 0.5 * lfo(L, 2)) ** 2
def hum(L):      return sum(osc(loop_safe(f, L), L) * a for f, a in ((50, 0.5), (100, 0.25), (150, 0.1)))
def drone(L, notes=("D2", "A2", "D3")):
    return sum(osc(loop_safe(note(p), L), L) / (i + 1) for i, p in enumerate(notes)) * (0.6 + 0.4 * lfo(L, 2))
def crickets(L, count=14):
    t = tt(n(0.25)); chirp = np.sin(2 * np.pi * 4300 * t) * (np.sin(2 * np.pi * 24 * t) > 0.3) * np.exp(-t / 0.15)
    out = silence(L)
    for _ in range(count):
        place(out, (0.5 + 0.5 * rng().random()) * chirp, rng().random() * L, wrap=True)
    return out
def drips(L, count=6):
    drip = modes([1800], [0.05], [1], 0.3); out = silence(L)
    for _ in range(count):
        place(out, drip * (0.6 + 0.4 * rng().random()), rng().random() * L, wrap=True)
    return out
def creaks(L):
    out = silence(L)
    for at in (3.1, 13.7, 24.2):
        place(out, sfx.creak(260 + 60 * rng().random(), 1.4, 40, ring=1.6), at % L, wrap=True)
    return out

kit = kit_for(score, "snes").layer(wind, 0.15).layer(crickets, 0.06, [("filt", 2000, 9000)])
```

Keep layers under the music (gain 0.05–0.2 before the master); a bed that competes with the pad reads as mud, not atmosphere.

## The master

`master=dict(rms_db=-15, peak_db=-1, verb=(1.2, 0.25), fx=(), drive=0.0, soft=True)`:

- `rms_db` is the cue's loudness; `peak_db` the ceiling; with `soft=True` the peaks above it are soft-limited rather than the whole clip turned down. Music −16 to −13; title and beds −20 to −17; stingers −15 to −12. Keep siblings within 2 dB.
- `verb=(seconds, wet)` is one room per cue: `None` for a dry NES mix, `(0.8, 0.15)` a small room, `(1.5, 0.3)` a hall, `(2.5, 0.45)` a cathedral or a dream. Circular on loops, so it never seams; in a stereo kit the two channels get different tails, which is what makes the room wide.
- `fx` runs before the reverb: `[("filt", 25, 12000)]` is the SNES's gentle top-end roll-off; `[("crush", 8, 4)]` the crushed sampler; `[("filt", 40, 9000)]` tames a hissy mix.
- `drive` is tape saturation after the reverb (1.3 on the `snes` kit): rounds the peaks and glues the mix.
- The master also removes DC and, for stingers, fades the end.

## Era checklists

**NES:** `nes` kit. ≤ 2 pulses + `tri` + `nhit` at once (count the parts: a lead doubled an octave down is two pulses, so no arp then). Chords only as `chord="arp"`. Drums `nhit` only (`hold` 12 for a kick, 1 for a snare, 1 with `seconds=0.04` for a hat). `verb=None`, mono, `clean=False` pulses. Echo is allowed (games faked it with a quieter delayed copy on the second pulse). No `pad`, `saw`, `fm`.

**SNES:** `snes` kit. Low-passed pulses with a ping-pong echo, a wide `pad`, `fmbass`, `white` snare and kick with a body, a short stereo room, the console's 12 kHz roll-off and a little drive. Up to 8 parts. `lofi` for the crushed-sampler version.

**Genesis:** `genesis` kit. `fm` family and `saw`, dry master, `nhit` or `snare` drums, bright and buzzy.

**Acoustic (Mansion of Cosmic Horrors):** `acoustic` kit. `piano`, `bowed`, `harmonium`, `musicbox`, `bell`; `tom`/`wood` percussion if any; slow tempos, long reverb (2–4 s, wet 0.4–0.6), `rms_db` −26 to −20. Pitch-detectable leads: keep the melody above the pad (C5+) or it disappears on small speakers. It renders slowly (the piano is 36 partials a note: about 30 s for a busy 8-bar stereo cue, against 1–3 s for the chip kits), so draft with `GRAYBOX` and dress once.

**Mixed ("NES instruments with 16-bit effects"):** `kit_for(score, "snes")` with `pulse` leads and `tri` bass; that is the Deck of Destiny sound.
