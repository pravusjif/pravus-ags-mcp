# Getting the files into an engine

1. [Formats](#formats)
2. [Loudness and headroom](#loudness-and-headroom)
3. [Where things go](#where-things-go)
4. [Per engine](#per-engine)
5. [Regenerating](#regenerating)

## Formats

- **WAV** (`write_wav`: 16-bit, 44100 Hz; stereo from a stereo kit, mono from a mono kit or `oneshot()`) always plays and loops gaplessly. A minute of stereo is 10 MB, so use OGG for shipped music. `set_rate(22050)` before rendering halves sizes and render times at the cost of the top octave, which chip voices barely use; effects can stay mono and small either way.
- **OGG Vorbis** (`save_ogg`, needs `pip install soundfile`; or `ffmpeg -i x.wav -c:a libvorbis -q:a 4 x.ogg`) is a tenth of the size and loops gaplessly. Use it for music and beds when size matters (web, Decentraland, mobile). `cli --ogg` writes OGG to the assets folder and `--wav DIR` a WAV copy elsewhere for checking.
- **Never MP3 for loops.** The encoder pads the start and end, so the seam clicks or gaps however good the source. MP3 is fine for a stinger.
- Music is stereo (pans, width, ping-pong echo, a decorrelated room), effects mono: an engine positions a mono effect in the world, but music plays as a bed and gains from width.

## Loudness and headroom

An engine's volume control goes from silent to the file's own level and no further. So the loudness lives in the file (`master=dict(rms_db=...)`, `oneshot(peak_db=...)`), and the engine's volume is for ducking and the user's slider.

- Music RMS −18 to −14 dBFS (peaks soft-limited at −1), beds −26 to −20, one-shots peak −3, UI −6. The relations matter more than the numbers: beds under music, effects heard over it, dialogue over everything.
- Leave the user room to go up as well as down: play music at 0.6–0.7 of full in the engine and render accordingly louder, rather than at 1.0.
- Some engines cap or scale differently (Decentraland caps `AudioSource.volume` at 1 and plays quietly; AGS clip volumes top out at 100). If the whole soundtrack is too quiet in one engine, lift every cue's `rms_db` by the same amount, keeping the mix between them.
- Nobody has listened until the user has. Report the levels you rendered at and ask them to check one loud and one quiet cue in the game, with effects over them.

## Where things go

- **Generators** (`music.py`, `effects.py`, `chip.py`, `sfx.py`) live in the repo outside the deployed assets: `design/audio/`, `tools/audio/`, `<game>/Audio/` for AGS (AGS does not pack its `Audio/` folder, only the clips it imported). They are the source; commit them.
- **MIDI** (`--midi DIR`) beside the generators, not in the assets. It is the composition's record and what a human composer takes into a DAW.
- **Rendered files** go where the engine wants them (below). Name them after the cue (`explore_forest.ogg`, `fx_card_play.wav`) with a prefix per family so a folder listing reads as the cue table.
- Write the cue table (cue, where it plays, loop or stinger, level) into the generator's docstring and into the project's design notes. The next person, or the next pass, starts from it.

## Per engine

**AGS (Adventure Game Studio) through the `ags-mcp` skill.** Render WAVs into `<game>/Audio/`, then `import_audio path=<game>\Audio\mus_title.wav name=aMusTitle folder=Music type=Music` per file, `save_project`, and play from script (`aMusTitle.Play(eAudioPriorityNormal, eRepeat)`). Folder defaults decide type, bundling, volume and repeat; the `Music` folder in the default templates repeats and goes into `audio.vox`. `MaxChannels` on the Music type is 1 (a new track replaces the old); raise the Ambient type's to layer beds. The skill's `references/audio.md` has the full import loop, the type/folder model, and `replace_audio` for regenerated files. A regenerated WAV with a newer timestamp is re-copied into the build's cache on the next build without re-importing.

**Decentraland SDK7.** OGG into `assets/audio/music/` and `assets/audio/sfx/`; `AudioSource` with `loop: true` on a music entity, two entities crossfaded by a system for scene changes (1.5 s), a pool of 4–6 one-shot entities for effects. `volume` caps at 1 and the Explorer plays quietly: render 10–14 dB louder than the table above (the Deck of Destiny generators lifted everything by 13.5 dB and played music at 0.6). Keep the generators in `design/audio/` so `dcl deploy` does not ship them.

**Unity.** WAV or OGG into `Assets/Audio/`. Import settings: music and beds *Streaming* or *Compressed In Memory*, Vorbis, quality 70; effects *Decompress On Load*, PCM or ADPCM. Tick *Loop* on the `AudioSource`, not on the clip. Route through an `AudioMixer` with Music / SFX / Ambience groups so a slider per family exists. 22050 Hz files are fine; do not let Unity resample them up.

**Godot.** OGG for music with the import's *Loop* on; WAV for effects. `AudioStreamPlayer` with a bus per family.

**Web (HTML5).** Gapless looping needs the Web Audio API (`AudioBufferSourceNode.loop = true`); an `<audio>` element gaps at the seam. OGG or WAV; for Safari also AAC (`ffmpeg -c:a aac`), never MP3 for the loop.

**Anything else.** If it plays a WAV, it works. Loop in the engine, not by concatenating.

## Regenerating

- Every score has a seed, every effects script seeds once, so running the generator again gives the same bytes. Diff the output files to prove a change touched only what it should (`git diff --stat` on the assets, or a hash list).
- A second pass that only changes kits leaves every `.mid` byte-identical: check it. If a `.mid` changed, a score changed.
- Keep an old kit around as a function when the user may want to compare ("the graybox version" vs "dressed"); `cli --graybox` always renders the plain one.
- When a cue is replaced by an external recording later, keep its generator and its `.mid`: they document what the recording had to be.
