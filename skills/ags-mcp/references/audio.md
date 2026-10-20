# Sound in an AGS game

There are no sound assets to fetch, so synthesise them: the `chiptune` skill (installed beside this one; `skills/chiptune/` in the repo) makes music, stingers, ambience loops and effects with numpy, from a seeded generator script, and `import_audio` registers each file as an audio clip. Never edit `Game.agf` by hand to add clips.

## The loop

1. **Look at what exists:**
   - `list_entities audioclip`, plus `get_properties audioclip <name>` on one clip, to see the naming, folders and types in use.
   - `list_entities audiocliptype` (usually 1 Ambient Sound, 2 Music, 3 Sound) and `list_entities audiofolder`, plus `get_properties audiofolder <name>` for each folder's defaults.
   - `find_usages` on a clip, to see how the scripts play it.
2. **Generate** with the `chiptune` skill: read its `SKILL.md`, copy its `scripts/chip.py` and `scripts/sfx.py` into `<game>/Audio/`, and write the generators there (`Audio/music.py` for the cues, `Audio/effects.py` for the one-shots, with `.mid` files beside them). They write `Audio/mus_*.wav`, `Audio/fx_*.wav`, `Audio/amb_*.wav`. AGS packs only the clips it imported, so the scripts in `Audio/` ship nothing.
3. **Check every file** as that skill says: the generators enforce seamless loops and clean one-shot ends; `python chip.py info Audio/*.wav` prints levels, and `python chip.py preview Audio/sheet.png Audio/mus_*.wav` draws spectrograms you can read. You cannot listen; these are your ears.
4. **Import:** `import_audio path=<game>\Audio\fx_knock.wav name=aKnock folder=Sounds type=Sound`.
   - Without `name`, the clip is named `a` + the file name (`fx_knock.wav` → `aFx_knock`).
   - `type` and `bundling` default to the folder's defaults.
   - The result gives `scriptName` (for scripts) and `index` (for view frames' `sound`).
5. **Persist:** `save_project` (clips live in `Game.agf`).
6. **Play it in script** (`aKnock.Play();`, `aMusTitle.Play(eAudioPriorityNormal, eRepeat);`), then `compile`, then play-test.

## Levels

**Render loud; the engine can only turn a clip down.** Clip and channel volume top out at 100, so a quiet file stays quiet however high the script sets it. Bake the loudness into the file (the chiptune skill's targets: music RMS −18 to −14 dBFS, one-shots peak −3, beds RMS −26 to −20), then trim in the game: a folder `DefaultVolume` around 70 leaves room to go up as well as down.

The numbers were tuned by ear in another engine and have not been checked by ear in an AGS game: generate, play the game, and adjust. The relations between them (beds under music, one-shots heard over it) matter more than the exact values. Generate siblings in one script and compare their printed stats; a clip 4 dB louder than its family is a bug.

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

WAV (16-bit, 44100 Hz from the generators; music stereo, effects mono) is the simplest and always works. OGG is much smaller for long music and ambience: `save_ogg` in `chip.py` (needs the `soundfile` package) or `ffmpeg -i bed.wav -c:a libvorbis -q:a 4 bed.ogg`, then import the `.ogg` instead. Never MP3 for a loop (encoder padding breaks the seam). Keep the WAV-producing script either way.
