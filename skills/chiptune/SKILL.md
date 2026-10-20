---
name: chiptune
description: Compose and synthesise game music and sound effects in Python with numpy, in an 8-bit / 16-bit style (NES, SNES, Genesis) or with acoustic-flavoured voices, with no samples, DAW or asset packs. Use this whenever a game or app needs music, a soundtrack, a theme, a jingle, a stinger, an ambience loop or sound effects, whenever the user wants a placeholder ("graybox") soundtrack to replace later, wants generated music re-instrumented or upgraded without changing the composition, or says "chiptune", "retro", "8-bit", "16-bit", "tracker" or "like a NES game", even if they only say "it needs music", "make a battle theme" or "add a sound when the door opens". Engine-agnostic: it writes WAV/OGG files plus MIDI of the composition, for AGS, Decentraland, Unity, Godot, web or anything that plays a file.
---

# Game music and sound effects, synthesised

Two scripts, numpy, nothing sampled. [scripts/sfx.py](scripts/sfx.py) is the synthesis layer: oscillators, "struck object" modal voices, shaped noise, reverb, seamless-loop helpers, checks, WAV. [scripts/chip.py](scripts/chip.py) is a tracker on top of it. A cue is a **Score** (tempo, meter, bars, named parts with note patterns, drum grids: the composition) rendered through a **Kit** (a voice and effects per part, textures, the master: the instruments). The same score through another kit is the same tune on other instruments, and a score exports to MIDI. That split is what makes a soundtrack cheap to draft and safe to upgrade later.

Composition is the part that takes thought; the rendering is seconds. Spend your effort on the motif, the harmony and the rhythm, draft through the plain `GRAYBOX` kit, and only then choose instruments.

## Setup

- Python 3 with `numpy`. Optional: `soundfile` for OGG, `PIL` for spectrogram previews.
- **Copy `scripts/sfx.py` and `scripts/chip.py` into the game's audio-generator folder** (`design/audio/`, `<game>/Audio/`, wherever the generators will live), next to the scripts you write. The game then regenerates its own sound without this skill installed, pinned to the version it was built with. Do not import them from this skill's folder by path.
- `python chip.py demo <folder>` renders one score through `GRAYBOX` and a dressed kit, writes its `.mid` and a spectrogram sheet; `python chip.py voices` lists every voice, drum and effect with their parameters.

## The workflow

1. **Brief → cue list.** Read the design docs and the code that will trigger music (scene changes, combat, menus). Write a table: cue name, where it plays, loop or stinger, mood in one line, tempo, length in bars. Do the same for sound effects: name, what triggers it, length. Ask the user only for what you cannot infer (the era or palette, how many cues they want, whether this is a graybox or the final sound). Record the brief at the top of the generator script's docstring.
2. **Choose the palette.** Base kits in `chip.KITS`: `nes` (two raw pulses, a triangle, noise drums, mono, dry), `snes` (stereo: pulse lead with a ping-pong echo, FM counter, wide saw pad, FM bass, a gentle console low-pass), `genesis` (FM lead and bass, saws, dry), `acoustic` (piano, bowed strings, harmonium, music box, a long room), `lofi` (`snes` through an 8-bit low-rate sampler) and `graybox` (plain NES voices by role, mono). "NES instruments with 16-bit effects on top" is `kit_for(score, "snes")`. Mix freely with `.with_parts(...)`: the kit is a dict of part name → voice, effects and stereo position ([references/voices.md](references/voices.md)).
3. **Motif and harmony first.** Write one short motif (4–8 notes) and a chord progression per mood, then build every cue from them: the same motif slower on a music box for the title, a tone up and twice as fast for the last fight, in the parallel major for the ending. Shared material is what makes six cues sound like one game, and a motif is what a player hums. Theory cheat sheet, pattern grammar and structure in [references/composing.md](references/composing.md).
4. **Write the generator.** One `music.py` with the scores at the top, the kits at the bottom and `cli(CUES, KITS, out, midi_dir)` as its main; one `effects.py` with `sfx_cli(EFFECTS, out)` ([references/sfx.md](references/sfx.md)). Every score has a `seed`, so a cue regenerates byte-for-byte. Keep the scripts in the repo, outside the deployed assets, with the `.mid` files beside them.
5. **Render the graybox and check it.** `python music.py --graybox --midi midi/`, then `python chip.py preview sheet.png *.wav` and look at the spectrogram: a melody steps between lines, a bass holds a low band, drums are short broadband columns; a smear with no lines means the parts are fighting. Fix the composition now, while a change is one pattern string. `render()` raises if a loop has a seam or a stinger clicks.
6. **Dress it.** Write the kits: voices per part, echo on the lead, a pad under it, textures (wind, hum, crickets) as kit layers. Re-render. The scores do not change in this pass.
7. **Export and integrate** per engine ([references/engines.md](references/engines.md)): WAV always works; OGG for long loops (never MP3 for loops: encoder padding breaks the seam). Set the loudness in the file (`master=dict(rms_db=...)`), because an engine can only turn a clip down.
8. **Report what you cannot hear.** You cannot listen. Say so, give the user the cue table with the levels, what each cue should sound like, what to listen for (does the loop seam? is the lead above the pad? is it combat or just busier?), and the knobs that are one-line reverts (tempo, `rms_db`, a kit voice). The Deck of Destiny soundtrack's exploration note is the model: it listed every decision as `[agent-decided]` and the open questions for the owner's ear.

## The graybox contract

A graybox soundtrack is a finished composition on placeholder instruments. The contract between the passes:

- **Pass 1 changes scores; pass 2 changes kits.** The second pass, by you or by someone else months later, re-renders every cue through new kits and never edits a pattern. If it needs the tune changed, that is a composition change and is said so.
- **The `.mid` is the composition's record.** `write_midi` writes tempo, meter and one track per part with the gates and velocities; it ignores kits. Before and after a dressing pass, the `.mid` files are byte-identical: check with a hash. Hand the `.mid` files to anyone taking the music into a DAW or a tracker.
- **Names carry roles.** Part names decide the kit's defaults when a kit does not name them: `lead*`, `counter*`/`harm*`/`answer*`, `arp*`, `bass*`, `pad*`/`chord*`/`strings*`, and drums by their sound (`kick`, `snare`, `hat`, `ohat`, `tom`, `clap`, `wood`, `metal`, `nhit`, `heartbeat`). Name parts by role and the graybox needs no kit at all.

## Writing a cue

```python
from chip import *

CH = ["A3+C4+E4", "A3+C4+E4", "F3+A3+C4", "E3+G#3+B3"]          # Am Am F E, one chord per bar
MOTIF = "A4:4 E5:4 D5:4 C5:4 | B4:8 E4:8 | A4:4 E5:4 G5:4 A5:4 | G#5:8 E5:8"   # four bars; the title plays stretch(MOTIF, 2)

def battle():
    s = Score(bpm=150, steps_per_beat=4, beats_per_bar=4, bars=8, seed=21, name="battle")
    s.part("lead", "A4:2 C5:2 E5:4 D5:2 C5:2 B4:4 | " * 3 + "A4:2 C5:2 E5:4 A5:4 G#4:2 E4:2 | " + MOTIF)
    s.part("counter", "r:8 E4:2 D4:2 C4:4 | r:16 | r:8 F4:2 E4:2 D4:4 | r:16", gate=0.6)
    s.part("arp", arp(CH, [0, 1, 2, 1], 16, 1))                 # broken chords, one step each, an octave up
    s.part("bass", roots(CH, [2, 2, (12, 2), 2, 2, 2, (7, 2), (12, 2)]), gate=0.7)   # root, root, octave, ..., fifth
    s.part("pad", chords(CH, 16))
    s.drums("kick", "x...x...x...x...").drums("snare", "....x..o....x..o").drums("hat", "x.o.x.o.x.o.x.oo")
    return s

CUES = {"battle": battle}
KITS = {"battle": lambda: kit_for(battle(), "snes", lead=dict(voice="pulse", duty=0.25, vib=0.2, gain=0.45, gate=0.75))}

if __name__ == "__main__":
    cli(CUES, KITS, out="../../assets/audio/music", midi_dir="midi")
```

Patterns: `"A4:4 D5:4 r:2 C5+E5+G5:8@0.7"` is note[:steps][@velocity], `r` a rest, `+` stacks a chord, `|` an ignored bar line; the default length is one step. A pattern shorter than the score repeats to fill it; one that does not divide it evenly raises, naming the part. Drum grids: `x` hit, `o` soft, `.` rest. `steps_per_beat=4` makes a step a sixteenth; `steps_per_beat=2, beats_per_bar=3` is a waltz; `2, 6` a 6/8 gallop; `2, 5` a 5/4 chase.

A stinger (win, lose, discovery) is `Score(..., loop=False, tail=1.5)`: it places notes without wrapping, lets the tail ring and checks that the ends are silent.

## What makes it sound like the era, and what makes it sound good

The engine is clean by default: 44.1 kHz, band-limited oscillators (no aliasing buzz), notes whose release rings past the gate into the next note, stereo kits with a pan and a detuned `width` per part, a decorrelated left/right room and a touch of saturation on the master. That is what separates "sounds like a game" from "sounds like a cheap emulator": the chip voices stay, the digital grit goes. Grit is a choice, not a default: `clean=False` on a pulse or saw gives the raw aliasing square, `nhit` drums are the real sample-and-hold noise channel, the `nes` kit is all of that mono and dry, and `lofi` adds the crushed sampler.

- **NES:** at most two pulses, one triangle and the noise channel sounding at once. Lead on pulse duty 0.25 with delayed vibrato, counter on 0.5, bass on the triangle an octave or two below. Chords are a fast arpeggio on one pulse (`chord="arp"`), not a pad. Drums are noise only (`nhit` with a short decay; a long `hold` for a kick). No reverb (`verb=None`); echo is the one effect, as a delayed copy a sixteenth or a dotted eighth later.
- **SNES / 16-bit:** the same voices, low-passed a little (`lp`), with a ping-pong echo on the lead, a wide pad, FM or sampled-sounding bass, kick and snare with a tonal body, a short room. The `lofi` kit's `crush(8, 4)` is the console's 8-bit samples at a low rate; use it when the brief says "crunchy", not by default.
- **Genesis:** FM everywhere (`fm`, `fmbass`, `fmbell`, with `ratio` and `index`), saws, a dry master.
- **Common to all:** the lead is the loudest part; the pad sits 10 dB under it; the bass line moves (root, octave, fifth) rather than holding; tempos are decisive (slow is 56–72, exploring 80–100, action 130–170, frantic 190+); loops are a whole number of bars, 8 or 16 for a cue you hear for minutes, and end where they start harmonically (on the V or the i) so the wrap is a downbeat.
- **Mood shortcuts:** minor with bare fifths (no third) for dread; phrygian (b2) and a tritone toll for the villain; dorian for travel; lydian or the parallel major for wonder and endings; a minor-second cluster that beats for madness; a waltz on a detuned calliope for a fairground; a slowing music box for innocence gone wrong. The composing reference has the chords for each.

## Checks (your ears are measurements)

- `render()` enforces the loop seam (last sample to first, against the clip's own typical step) or, for stingers, silent ends and no clipping. A seam means a part is not periodic over the loop: an echo or reverb that is not circular, or a texture with a non-whole number of cycles.
- `cli` prints seconds, mono/stereo, peak and RMS per cue. Targets: music RMS −16 to −13 dBFS with peaks soft-limited at −1 (the default master), stingers peak −3, beds RMS −26 to −20, under the music. Siblings should match: a cue 4 dB louder than its neighbours is a bug. Louder reads as better in any A/B, so compare cues at the same RMS before judging a kit.
- `python chip.py preview sheet.png a.wav b.wav` draws stacked log-frequency spectrograms (40 Hz–10 kHz). Read them: lines are pitches, columns are hits, a bright floor under 100 Hz is bass, haze is noise or reverb. Compare a cue with its graybox: the same shapes, different colours.
- A melody you cannot see in the spectrogram is one the player will not hear over the pad; raise its gain or thin the pad before touching the tune.

## References

- [references/composing.md](references/composing.md): the pattern grammar in full, the `Score` API, the chord/arpeggio/bass helpers, the theory and rhythm cheat sheets, structure and variation across cues, and worked examples. Read it before writing the first score.
- [references/voices.md](references/voices.md): every voice, drum and effect with its parameters and character, the era kits and how to write one, textures as kit layers, mastering. Read it when dressing.
- [references/sfx.md](references/sfx.md): one-shot sound effects and ambience loops with `sfx.py` and the chip voices: recipes (UI, hits, doors, machines, voices, weather, jingles), the `oneshot()` finisher, level targets, and the `effects.py` layout.
- [references/engines.md](references/engines.md): formats, loudness and headroom per engine, where the files and the generators go, and how to regenerate. For AGS games the `ags-mcp` skill's `references/audio.md` has the import steps.
