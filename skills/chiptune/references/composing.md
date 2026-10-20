# Composing with chip.py

1. [The pattern grammar](#the-pattern-grammar)
2. [Score](#score)
3. [Pattern helpers](#pattern-helpers)
4. [Harmony by mood](#harmony-by-mood)
5. [Rhythm: grids and bass lines](#rhythm-grids-and-bass-lines)
6. [Tempo and length](#tempo-and-length)
7. [Structure, motifs and variation](#structure-motifs-and-variation)
8. [Worked examples](#worked-examples)

## The pattern grammar

A pattern is a string of tokens separated by spaces:

| Token | Meaning |
|---|---|
| `C5` | C5 for one step (equal temperament, A4 = 440). Flats are fine: `Bb3`, `Eb4`. |
| `C5:4` | C5 for four steps. Fractions work: `C5:1.5`, `C5:0.5`. |
| `C5+E5+G5:8` | A chord for eight steps. In MIDI every note is written; in a kit with `chord="arp"` one voice cycles them. |
| `C5:2@0.6` | Velocity 0.6 (default 1). Scales the voice's output and the MIDI velocity. |
| `r:4` or `-:4` | A rest. `r` alone is one step. |
| `\|` | A bar line, ignored. Use it so you can count bars. |

A pattern's length is the sum of its tokens' lengths (`span(text)`). A part shorter than the score repeats to fill it; one that does not divide the score evenly raises, naming the part, so a 12-step phrase in a 16-step bar is caught before it drifts. Pass `repeat=False` to play a pattern once (an intro figure, a bell on bar 1 only).

A step is `60 / bpm / steps_per_beat` seconds. With `steps_per_beat=4` a step is a sixteenth, a beat is `:4`, a bar of 4/4 is `:16`. With `steps_per_beat=2` a step is an eighth; with `3`, a triplet eighth.

## Score

```python
Score(bpm, steps_per_beat=4, beats_per_bar=4, bars=4, seed=1, loop=True, tail=1.5, name="")
```

- `steps`, `step` (seconds), `length` (seconds, without the tail) are computed.
- `seed` seeds the RNG before rendering, so noise, drums and detuned voices repeat exactly.
- `loop=False` makes a stinger: notes are not wrapped, `tail` seconds are added for the ring-out, the end is faded, and `check_oneshot` runs instead of `check_loop`.

```python
s.part(name, pattern, role=None, gate=None, octave=0, offset=0, repeat=True)
s.drums(name, grid, offset=0)
s.describe()      # a text summary: tempo, bars, every part's length
```

- `role` is inferred from the name (`lead`, `counter`/`harm`/`answer`, `arp`, `bass`, `pad`/`chord`/`strings`, else `melodic`) and decides which kit entry plays a part the kit does not name. Pass it when the name says otherwise (`s.part("ghost", ..., role="counter")`).
- `gate` is the fraction of each note that sounds (0.5 staccato, 0.9 legato, 1.0 for pads that must not gap). Set it in the score when it is the music's articulation; leave it to the kit when it is the instrument's.
- `octave` transposes the whole pattern; `offset` delays it by steps (an answer that starts on the "and").
- Drum names pick the sound unless the kit remaps them: `kick`, `snare`, `hat`, `ohat`, `tom`, `clap`, `wood`, `metal`, `nhit`, `heartbeat`. Several grids may use the same sound under different names (`s.drums("kick2", ...)` plays `nhit` unless the kit says `kick2=dict(drum="kick")`), so prefer the sound's name.

## Pattern helpers

```python
chords(["A3+C4+E4", "F3+A3+C4"], 16)        # "A3+C4+E4:16 F3+A3+C4:16"   — a pad, one chord a bar
arp(CH, [0, 1, 2, 1], 16, 1)                 # per chord, 16 one-step notes cycling its tones an octave up
arp(CH, [0, 2, None, 2], 8)                  # None is a rest; an index past the chord wraps up an octave
roots(CH, [2, 2, (12, 2), 2, 2, 2, (7, 2), (12, 2)])   # bass from chord roots: lengths, (semitones, length), negative = rest
roots(CH, [2, -4], octave=-1)                # a waltz "oom": root, rest
transpose(MOTIF, 2)                          # a tone up; transpose(MOTIF, -12) an octave down (double a lead with it)
stretch(MOTIF, 2)                            # twice as slow (augmentation); stretch(MOTIF, 0.5) twice as fast
times(bar, 4)                                # the same bar four times
span(pattern)                                # length in steps
pitch("A4") -> 69; name_of(69) -> "A4"
```

Chords are strings of stacked notes, low to high: `"A3+C4+E4"`. Write them in the register the pad should play (C3–C5 for pads; the bass takes the root an octave or two down through `roots`). Bare fifths (`"E3+B3"`) are darker than triads; add a 7th (`"A2+G3+C#4+E4"`) for tension.

## Harmony by mood

Chord symbols below are in A minor / C major; transpose to taste. Roman numerals are relative.

| Mood | Scale / mode | Progression (one chord a bar) | Notes |
|---|---|---|---|
| Heroic, overworld | Major, or mixolydian (b7) | I–V–vi–IV: `C3+E3+G3 G3+B3+D4 A3+C4+E4 F3+A3+C4`; I–bVII–IV–I | Lead on the 3rd and 5th, bass on roots in octaves |
| Travel, exploring | Dorian (minor with a raised 6th) | i–IV–i–bVII: `A3+C4+E4 D3+F#3+A3 A3+C4+E4 G3+B3+D4` | Keep it moving: eighths in the arp, a walking bass |
| Wonder, discovery | Lydian (#4) | I–II–I: `C3+E3+G3 D3+F#3+A3`; or I–iii–IV | Bell or music-box lead, long pad, high register |
| Sad, loss | Natural minor | i–VI–III–VII: `A3+C4+E4 F3+A3+C4 C3+E3+G3 G3+B3+D4` | Slow, lead descends, ends on the VI not the i |
| Dread, haunting | Minor on bare fifths | i–bVI–i–V as fifths: `A3+E4 F3+C4 A3+E4 E3+B3`; a pedal tone under everything | No thirds; a tritone toll (`A`, `Eb` on a bell) every few bars |
| Villain, boss | Phrygian (b2), chromatic | i–bII–i–bvii: `E3+G3+B3 F3+Ab3+C4 E3+G3+B3 D3+F3+A3`; a minor-second cluster `E2+F2` held | Repeated-note lead figures; bass walks a semitone and a tritone |
| Madness, asylum | Clusters, tritones | `B1+C2` and `B1+F2` held; a music box in tritones | Mains hum (50/100/150 Hz sines) as a texture |
| Fairground | Major waltz | I–V7–I–IV with chromatic neighbours: `F3+A3+C4 C3+E3+G3+Bb3 F3+A3+C4 Bb2+D3+F3` | Calliope with `detune_cents=18–25`, oompah: bass on 1, chord stabs on 2 and 3 |
| Lullaby, innocence | Major or minor, 3/4 or 6/8 | I–V–I–IV | Music box, slow; make it run down (`stretch` the last phrase, detune a few cents per note) for innocence gone wrong |
| Victory stinger | Major | IV–V–I, or I–IV–I: arpeggio up, held chord with the 3rd on top, a bell | 2–4 s, `loop=False` |
| Defeat stinger | Minor, chromatic | A line falling by semitones over i, a low bell | 3 s, let the bell ring in the tail |
| Mystery | Whole-tone or augmented | `C3+E3+G#3`, `D3+F#3+A#3` alternating; or minor-major 7th `A3+C4+E4+G#4` | Sine or flute lead with vibrato, long echo |

Turning a cue into its sibling: the same motif a tone up and faster is "the same fight, worse"; the parallel major (`C` for `A minor` motifs transposed up 3 and with the 3rd raised) is "the same place, safe now"; bare fifths instead of triads is "darker, same tune"; a different mode with the same rhythm is the same character in another mood.

## Rhythm: grids and bass lines

Drum grids repeat to fill the score. `x` is a hit, `o` a soft hit, `.` a rest; spaces and `|` are ignored.

**4/4, `steps_per_beat=4` (16 steps a bar):**

| Feel | kick | snare | hat |
|---|---|---|---|
| Straight rock / action | `x...x...x...x...` | `....x.......x...` | `x.x.x.x.x.x.x.x.` |
| Driving, syncopated | `x..x..x.x..x..x.` | `....x..o....x..o` | `x.o.x.o.x.o.x.oo` |
| Half-time, heavy | `x.......x.x.....` | `........x.......` | `x.o.x.o.x.o.x.o.` |
| March | `x...x...x...x...` | `x.xxx.xxx.xxx.xx` (snare only) | — |
| Funeral march | `x...............` | `....x.......x.xx` | — |
| Busy 16ths | `x..xx...x.x.x...` | `....x..o....x.xx` | `xxoxxxoxxxoxxxox` |

**Waltz, `steps_per_beat=2, beats_per_bar=3` (6 steps):** kick `x.....`, snare `..x..x`, hat `xxxxxx`; bass `roots(CH, [2, -4])`; chord stabs on 2 and 3: `" ".join(f"r:2 {c}:2 {c}:2" for c in CH)`.

**6/8 gallop, `steps_per_beat=2, beats_per_bar=6` (12 steps, or `bars` doubled with `beats_per_bar=3`):** kick `x..x..`, snare `..x..x`, hat `xoxxox`, wood `x.x.x.`; bass `roots(CH, [1, 1, 2, 1, 1])`.

**5/4 chase, `steps_per_beat=2, beats_per_bar=5` (10 steps):** kick `x.x...x.x.`, snare `....x....x`, hat `xoxoxoxoxo`; an ostinato arp of ten notes per chord.

**Shuffle / swing, `steps_per_beat=3`:** kick `x..x..x..x..`, hat `x.xx.xx.xx.x`, lead notes `:2 :1` pairs.

**Heartbeat:** `s.drums("heartbeat", "x" + "." * 31)` at any tempo; a kit can swap it for a kick.

**Bass lines** (`roots`, one list per chord; lengths in steps of a 16-step bar):

| Feel | rhythm |
|---|---|
| Chug (eighths) | `[2] * 8` |
| Octave pump | `[2, (12, 2)] * 4` |
| Root–fifth–octave | `[2, 2, (12, 2), 2, 2, 2, (7, 2), (12, 2)]` |
| Walking | `[4, (7, 4), (12, 4), (7, 4)]` or `[4, (3, 4), (5, 4), (7, 4)]` in minor |
| Held | `[16]` (pads, dread) |
| Semitone menace | `[2, 2, (1, 2), 2, 2, 2, (6, 2), 2]` (a minor second and a tritone) |

A bass that moves is what makes a loop feel like it goes somewhere. Give it a short `gate` (0.6–0.7) when it chugs.

## Tempo and length

| Use | bpm | Bars × meter | Seconds |
|---|---|---|---|
| Title, menu | 56–72 | 8 × 4/4 | 27–34 |
| Slow place, dread | 52–66 | 8 × 4/4 | 29–37 |
| Exploring | 80–100 | 8 × 4/4 | 19–24 |
| Fight | 130–170 | 8 × 4/4 | 11–15 (use 16 bars for the main fight) |
| Frantic, chase | 150 (5/4), 190–210 (waltz) | 8 × 5/4, 16–24 × 3/4 | 16, 14–21 |
| Stinger | 100–140 | 1–2 bars + tail | 2–5 |

`length = bars * beats_per_bar * 60 / bpm`. A loop the player hears for minutes should be at least 15 s, and 8 or 16 bars of a four-bar progression; shorter loops are heard as loops. Longer (32 bars) means composing a B section: write it as a second set of chords and patterns continuing the same strings.

## Structure, motifs and variation

- **Phrases ask and answer.** A two-bar phrase that ends off the tonic (on the 2nd, 5th or 7th) is a question; the next two bars answer, ending on the tonic or the 3rd. Four such pairs make an eight-bar cue that breathes. The last bar leads back to the first: end the progression on V (or bVII in dorian) so the loop's wrap is a downbeat, and leave the lead's last note short so the restart is clean.
- **A motif is 4–8 notes with a shape**: a leap up and steps down (`A4 D5 C5 A4`), a repeated note and a fall, a rising scale that overshoots. Write it once as a constant and reuse it: the title plays `stretch(MOTIF, 2)` on a music box over a pad; the fight plays it at speed on a pulse with `transpose(MOTIF, -12)` doubled on a second pulse; the ending plays it in the parallel major; a bell plays its first four notes every four bars in the exploration cue. Shared material is what makes separate cues one soundtrack.
- **Counter lines live in the lead's rests** and move the other way: when the lead rises, the counter falls; a lower register (an octave below the lead), fewer notes, a softer gate. Start it on the "and" with `offset=2`.
- **Four melodic parts are plenty**: lead, counter, arp, bass, plus a pad as a bed. The ear follows the lead and the bass; everything else is colour. If the spectrogram is a smear, remove a part rather than turning one up.
- **Rests are composition.** A lead that plays every step is a drone. Leave a bar out every four for the arp to show; leave the counter out of the first half.
- **Dynamics across a cue:** `@0.7` velocities on the off-beat notes, the arp quieter in the first half (write two patterns and join them), the drums entering on bar 3 (`"." * 32 + grid`; the grid then must still divide the score).
- **Variation for the second half:** repeat the first four bars' lead with its last bar changed (the answer goes somewhere else), move the counter up an octave, double the bass rhythm.

## Worked examples

**A title: music box over a reed pad, 60 bpm, the motif stretched.**

```python
def title():
    s = Score(bpm=60, bars=8, seed=101, name="title")
    s.part("pad", chords(["D3+F3+A3", "Bb2+D3+F3", "G2+Bb2+D3", "A2+C#3+E3"], 16), gate=1.0)
    s.part("lead", stretch(MOTIF, 2), gate=1.0)                     # the four-bar motif at half speed fills the 8 bars
    s.part("bass", roots(["D3", "Bb2", "G2", "A2"], [16], octave=-1), gate=1.0)
    s.drums("heartbeat", "x" + "." * 63)
    return s
# kit: lead=musicbox with a long echo, pad=harmonium or pad, verb (2.5, 0.45), rms_db -20
```

**Exploring: dorian, 90 bpm, a pluck that keeps walking.**

```python
CH_WOOD = ["E3+G3+B3", "A3+C#4+E4", "E3+G3+B3", "D3+F#3+A3"]
def forest():
    s = Score(bpm=90, bars=8, seed=202, name="forest")
    s.part("lead", "E4:6 F#4:2 G4:8 | F#4:4 E4:4 D4:8 | E4:6 G4:2 B4:4 C#5:4 | B4:16 | "
                   "r:8 G4:4 A4:4 | B4:6 A4:2 G4:4 F#4:4 | E4:16 | r:16")
    s.part("arp", arp(CH_WOOD, [0, 2, 1, 3, 2, 1, None, 1], 16))
    s.part("bass", roots(CH_WOOD, [4, (7, 4), (12, 4), (7, 4)]), gate=0.8)
    s.drums("wood", "x...x.x.x...x...").drums("hat", "..x...x...x...x.")
    return s
# kit: lead=flute, arp=pluck, bass=tri; a wind layer; verb (2.0, 0.35)
```

**A boss: phrygian, 160 bpm, bare fifths, a semitone bass.**

```python
CH_BOSS = ["E3+B3", "F3+C4", "E3+B3", "D3+A3", "E3+B3", "F3+C4", "G3+D4", "F3+C4"]
def boss():
    s = Score(bpm=160, bars=8, seed=301, name="boss")
    lead = ("E4:2 E4:2 F4:2 E4:2 G4:4 F4:2 E4:2 | E4:2 E4:2 F4:2 E4:2 Bb4:6 A4:2 | "
            "B4:2 B4:2 C5:2 B4:2 A4:4 G4:2 F4:2 | F4:4 E4:4 D4:4 E4:4")
    s.part("lead", lead, gate=0.7).part("lead_low", transpose(lead, -12), role="counter", gate=0.7)
    s.part("pad", chords(CH_BOSS, 16), gate=1.0)
    s.part("bass", roots(CH_BOSS, [2, 2, (1, 2), 2, 2, 2, (6, 2), 2]), gate=0.65)
    s.part("bell", "E3:64 Bb3:64", role="melodic")                   # a tritone toll
    s.drums("tom", "x..x..x.x..x..x.").drums("snare", "....x.......x.xx").drums("hat", "xoxoxoxoxoxoxoxo")
    return s
```

**A victory stinger.**

```python
def win():
    s = Score(bpm=120, bars=2, loop=False, tail=2.0, seed=9, name="win")
    s.part("arp", "D5 F5 A5 D6 r:12 | r:16", repeat=False)
    s.part("lead", "r:4 D5+F5+A5:28", gate=1.0, repeat=False)
    s.part("bass", "r:4 D3:28", gate=1.0, repeat=False)
    s.part("bell", "r:4 D6:28", role="melodic", repeat=False)
    return s
# kit: arp and lead=pulse duty 0.25 with vib, bass=tri, bell=glock; verb (1.2, 0.3); rms_db -14
```
