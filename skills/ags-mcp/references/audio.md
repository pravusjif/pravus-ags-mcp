# Making sound for an AGS game

There are no sound assets to fetch, so synthesise them: numpy writes a WAV in a few lines, and `import_audio` registers it as an audio clip. Never edit `Game.agf` by hand to add clips.

`scripts/sfx.py` (next to this skill) has the helpers used below. Copy it into the game's `Audio/` folder or import it from the skill path. It needs numpy; `preview` also needs PIL.

## The loop

1. **Look at what exists:**
   - `list_entities audioclip`, plus `get_properties audioclip <name>` on one clip, to see the naming, folders and types in use.
   - `list_entities audiocliptype` (usually 1 Ambient Sound, 2 Music, 3 Sound) and `list_entities audiofolder`, plus `get_properties audiofolder <name>` for each folder's defaults.
   - `find_usages` on a clip, to see how the scripts play it.
2. **Write one seeded generator script per family of sounds**, beside its output in the game's `Audio/` folder (for example `Audio/effects_gen.py` writes `Audio/fx_*.wav`). Seed it (`sfx.seed(...)`) so the sounds regenerate the same way, and keep it so they can be tweaked later.
3. **Check every clip in the script:** `check_oneshot(x)` for one-shots and `check_loop(x)` for loops. Then look at it: `python sfx.py info Audio/*.wav` for levels, ends and seams, and `python sfx.py preview Audio/sheet.png Audio/fx_*.wav` for a spectrogram you can open. You cannot listen, so these are your ears: a knock should show short broadband strikes, a hum a steady low line, a melody steps between lines.
4. **Import:** `import_audio path=<game>\Audio\fx_knock.wav name=aKnock folder=Sounds type=Sound`.
   - Without `name`, the clip is named `a` + the file name (`fx_knock.wav` → `aFx_knock`).
   - `type` and `bundling` default to the folder's defaults.
   - The result gives `scriptName` (for scripts) and `index` (for view frames' `sound`).
5. **Persist:** `save_project` (clips live in `Game.agf`).
6. **Play it in script** (`aKnock.Play();`), then `compile`, then play-test.

## Recipes

```python
from sfx import *
seed(11)

# UI blip: a short rising sweep with a fast envelope.
blip = level(fade(sweep(660, 990, 0.12) * adsr(0.12, 0.004, 0.04, 0.5, 0.06)), peak_db=-6)

# Knock / footstep / wooden thing: low modes that die fast, plus a little noise for the contact.
hit = modes([180, 410, 690, 1150], [0.06, 0.035, 0.02, 0.012], [1, 0.5, 0.3, 0.15], 0.4) + 0.25 * burst(0.4, 300, 3000, 0.006)
knock = silence(0.9)
for at in (0.0, 0.22, 0.44):
    place(knock, hit, at + 0.01)
knock = level(fade(reverb(knock, 0.5, 0.25), out_s=0.08))
check_oneshot(knock)
write_wav("fx_knock.wav", knock)

# Metal / bell / glass: higher, longer, inharmonic modes (ratios like 1, 2.76, 5.4, 8.93).
bell = modes([440 * r for r in (1, 2.76, 5.4, 8.93)], [1.6, 0.9, 0.5, 0.3], [1, 0.6, 0.35, 0.2], 3.0)

# Door creak: stick-slip clicks gliding in pitch.
door = level(fade(creak(320, 1.2, 60, ring=1.5, f1=410), out_s=0.1))

# Whoosh / wind gust: band noise with a swelling envelope.
whoosh = level(fade(noise(0.8, 300, 3000, tilt=1) * np.hanning(n(0.8))))
```

**Ambience beds (seamless loops).** Every part must be periodic over the loop length:

```python
L = 12.0                                                   # loop length in seconds
bed = noise(L, 60, 900, tilt=1, circular=True) * (0.6 + 0.4 * lfo(L, 3))   # whole cycles only
bed += 0.3 * osc(loop_safe(55, L), L) * lfo(L, 1, 1.0)     # hum, rounded to whole cycles
drip = modes([1800], [0.05], [1], 0.3)
for at in (1.3, 5.9, 10.4):
    place(bed, 0.4 * drip, at, wrap=True)                  # events near the end wrap to the start
bed = level(reverb(bed, 1.5, 0.4, circular=True), rms_db=-28)
check_loop(bed)
```

**Music.** Build notes with `note("D4")`, voice them with `modes()` (plucked or bell-like) or stacked `osc()` partials times `adsr()` (pads, organ), and `place()` them on a beat grid (`step = 60 / bpm`).
- **Looping cues:** make the length a whole number of bars, use `wrap=True` and a circular reverb.
- **Stingers** (one-shots): fade the end and leave room for the tail.

## Levels

- `level(x)` peaks a one-shot at −3 dBFS.
- `level(x, rms_db=...)` sets loudness for beds and music, with the peak still capped.

| Kind | Target |
|---|---|
| UI blips, small props | peak −6 to −3 |
| Doors, hits, events | peak −3 |
| Music | RMS ≈ −24 |
| Ambience beds | RMS −26 to −32, under the music and the dialogue |

A consistent set matters more than the exact numbers: generate siblings in one script and print their `stats()`.

## Audio types, folders and channels

- **Folder and type defaults.** A clip's **type** decides its channel pool and default behaviour. New clips take the folder's `DefaultType`/`DefaultBundlingType`, so import into the folder that matches (in the default templates: `Sounds` → Sound, `Music` → Music, the root → Ambient Sound), or pass `type`.
- **Folders set runtime defaults too.** A clip whose volume, priority or repeat is `Inherit` plays with its folder's `DefaultVolume`/`DefaultPriority`/`DefaultRepeat`. Group clips that should share these:
  - `create_entity audiofolder {"Name": "Footsteps", "Parent": "Sounds", "DefaultVolume": 60}`, then `import_audio ... folder=Footsteps`.
  - To change an existing folder: `set_properties audiofolder Music {"DefaultBundlingType": "InGameEXE"}`.
  - Check a folder's defaults before importing into it. The templates' `Music` folder repeats and goes into `audio.vox`.
- **Channels.** `MaxChannels` limits how many clips of a type play at once (0 = unlimited). Music is 1, so a new track replaces the old one. To layer several ambience loops, raise it: `set_properties audiocliptype "Ambient Sound" {"MaxChannels": 3}`. New types come from `create_entity audiocliptype {"Name": "Voice"}` (scripts see `eAudioTypeVoice`).
- **Looping.** `aBed.Play(eAudioPriorityNormal, eRepeat)` loops. Folder or clip `DefaultRepeat` is the default when `Play()` gets no repeat argument.
- **Bundling.** `InGameEXE` packs the clip into the game data. `InSeparateVOX` puts it in `audio.vox`, which is common for music.

## Changing a clip later

- Regenerate the WAV over the same file. The next build re-copies a source whose timestamp changed into the AudioCache (what the build packs).
- `replace_audio clip=aKnock` copies it now.
- To switch to another file, use `replace_audio clip=aKnock path=...`. The script name, ID and index stay, so scripts and view frames keep working.
- `delete_audio` refuses while a view frame or script still uses the clip. Use `find_usages` first.

## Formats

WAV (16-bit mono, 22050 Hz from `write_wav`) is the simplest and always works. OGG is much smaller for long music and ambience. If `ffmpeg` or `oggenc` is installed and the game's size matters, convert (`ffmpeg -i bed.wav -c:a libvorbis -q:a 4 bed.ogg`) and import the `.ogg` instead. Keep the WAV-producing script either way.
