---
name: ags-mcp
description: Work on the Adventure Game Studio (AGS 3.6) game that is open in the AGS editor through the `ags` MCP server's tools (`project_info`, `get_room`, `game_state`, …; Claude Code shows them as `mcp__ags__*`). Use this whenever the user wants to build, change or fix anything in their AGS game — a room, background, hotspot, object, sprite, animation view, character, inventory item, dialog, puzzle, GUI setting, sound effect, music, script or event — or generate pixel art or sound for it, or check that it works in the running game with the `game_*` tools, even if they just say "add a door here", "give him a key", "make the cat talk" or "does it work?". Also use it when the ags server's tools are available in the session and the task touches the game project.
---

# Working on an AGS game through the ags MCP server

The `ags` MCP server lives inside the running AGS 3.6 editor and edits **the project the user has open**. That project is the user's game: build what they ask for inside it, and check it there. Every tool works on the editor's **in-memory** model; some changes are written to disk at once, others only on `save_project`. The `game_*` tools drive a built copy of that same game the way a player would.

## Ground rules

- **Work in the open project.** Do not create or open another project (`create_project`, `open_project`) unless the user asks for one. There is no separate test game: verification happens in the user's game.
- **Change only what the task needs.** Leave rooms, scripts, entities and settings the request does not mention as they are. If a check needs a temporary aid (a debug line, a shortcut), remove it afterwards.
- **Ask before anything you cannot undo.** Rooms cannot be deleted, deleting entities renumbers IDs, and `write_script` replaces a whole file.

## Before anything else

1. Call `project_info`. It tells you the game name, folder, resolution, colour depth, player character and counts. If the tools are missing, either the editor is not running with the plugin or your MCP client is not connected to it: the server is at `http://127.0.0.1:7471/mcp` (Streamable HTTP), and the editor's **MCP > Client setup** shows the setup for common clients.
2. Read what the change touches before changing it: `list_rooms`, `get_room N` for the rooms involved, `read_script roomN` and the parts of `GlobalScript` you will depend on, `list_entities character|inventory|view|dialog`, `get_properties` on the entities you will edit. Names, coordinates, flags, sprite numbers and coding conventions all come from there; match them.
3. Keep the two persistence tiers in mind, because forgetting one loses work:
   - **Written immediately:** every room tool (`set_room_background`, `draw_room_mask`, `set_room_properties`, `create_room_object`, `set_room_event`, …) saves its `.crm`; `write_script` / `edit_script` / `create_script_module` save the script file.
   - **Needs `save_project`:** everything in `Game.agf`: sprites, views, audio clips, characters, inventory items, dialogs, global variables, entity properties, `runtime_enable_plugin`. Call `save_project` at the end of any batch that touched these, and before `run_game` (which builds from disk).
   - A room tool returns `save.saved=false` with compiler messages when the room's script does not compile. The edit is still in the editor; fix the script, then run any room tool or `save_project` to write it.

## The general loop

1. **Plan in the game's own terms.** Work out which rooms, entities, sprites and scripts the request touches, and write down coordinates (in room pixels) for anything you place, so art, masks and script agree.
2. **Make it** with the tools for that area (recipes below; argument shapes in [references/tools.md](references/tools.md)).
3. **Compile:** `compile name=roomN` for each room script you touched, and `compile` (all modules) when GlobalScript or a header changed. Fix errors by file and line.
4. **Save:** `save_project` if anything in `Game.agf` changed.
5. **Verify in the running game** when the change has behaviour a player would notice (see below).

## Recipes

**A new room.** Design the hotspots, objects, exits and stand points first. Then: art as PNGs at the game's resolution ([references/pixel-art.md](references/pixel-art.md)) → `create_room` → `set_room_background` (before any mask; a size change clears masks) → `import_sprite` per sprite (note the numbers) → `draw_room_mask` for the walkable floor (one polygon), one hotspot area per interactive thing, walk-behinds (then their `Baseline`) and regions for exits and triggers → `set_room_properties` to name hotspots (`Name`, `Description`, `WalkToPoint`) → `create_room_object` → `set_room_event` for every handler → fill the stubs (`edit_script roomN` or `write_script roomN` keeping the stub names) → connect it: the neighbouring room's exit `ChangeRoom`s into it and its exit leads back. Spawn the player *outside* the exit region of the room they arrive in, or it fires at once.

**Something new in an existing room** (object, hotspot, trigger). `get_room N` and `render_room N` (plus `mask=hotspots`/`walkableareas`) first, so the new thing fits the existing layout and IDs. Add art and masks only where needed, bind events, add handlers next to the existing ones, and keep existing handlers working.

**Characters, inventory, dialogs.** `create_entity` with the properties the game's other entities use (look at one with `get_properties` first), `create_view` for animations, `set_event` for GlobalScript handlers, `set_dialog_script` for options and the dialog script. See [references/scripting.md](references/scripting.md) for the patterns.

**Sound and music.** Generate WAVs with a seeded Python script using `scripts/sfx.py` ([references/audio.md](references/audio.md)), check them with `sfx.py info`/`preview`, then `import_audio` each one into the right folder and type, `save_project`, and play them from script (`aKnock.Play();`). Never hand-edit `Game.agf` to add clips.

**Script-only changes.** `read_script` the relevant part, `edit_script` with a `find` unique enough to match once, `compile`. `find_usages` before renaming or removing anything.

## Things that cost time when you do not know them

- **`run_game startRoom=N` drops the player at the player character's own `StartX`/`StartY`**, not at a point of your choosing. In the room you are checking, that point must be inside the walkable area (otherwise every `Walk` silently does nothing and the check looks broken) and should not sit on top of an object you want to click. Read it with `get_properties character <player>`, then walk the player somewhere sensible with a Walk-mode click first.
- **Object placement:** `StartX`/`StartY` are the sprite's **bottom-left** corner. `WalkToPoint` is a `"x,y"` string. A walk-behind only works after `set_room_properties N walkbehind:K {Baseline: y}`. Walk-behind and object baselines are the y of the feet line that decides draw order.
- **The player sprite catches clicks.** Whatever is drawn on top gets the click, so a hotspot or object the player stands in front of is unreachable at that point. Pick stand points beside objects, not in front, and call `game_hover_name` on a point before clicking it.
- **Objects are hit-tested per pixel.** Thin or mostly transparent sprites are hard to click; give pick-ups a few solid pixels.
- **Script-name macros:** `create_view name=vFire` gives the script constant `VFIRE`; a character `cGuard` gives `GUARD`. A view and a character must not derive the same macro.
- **Cross-room state:** use an exported global (`bool doorOpen; export doorOpen;` in `GlobalScript.asc`, `import bool doorOpen;` in `GlobalScript.ash`) or `create_entity globalvariable`. `script_api_lookup` lists functions the project's Script API level may have disabled; the compiler, not the lookup, decides what exists.
- **Regions as exits:** bind `region:K WalksOnto` to `player.ChangeRoom(...)`, and give the same spot a hotspot with `Interact` so a click works too.

## Verifying in the user's game

Check against the live project, with the same input a player would give. Static checks come first because they are cheap:

- After painting masks: `render_room N mask=...`. After placing objects: `render_room N` and compare with your plan. `get_mask_pixel` spot-checks a stand point or exit.
- After scripting: `compile name=roomN` **and** `compile` (a GlobalScript change can break every room that uses a renamed symbol).

Then play the changed interaction in the running game with the `game_*` tools ([references/playtesting.md](references/playtesting.md)): `save_project`, `run_game startRoom=N`, then hover, click in the right cursor mode, wait, and assert on `game_state` (object `visible`, `inventory`, character `room`, `x,y`). Screenshots confirm the look; state confirms the behaviour. Play only what the task changed (plus anything it could have broken), stop the game when done, and report what you observed.

**Always run the play-test in a subagent, never in your own context.** The `game_*` loop returns large `game_state` payloads and screenshots, and they would fill your context. Call `save_project` yourself, then start one subagent with everything it needs, because it cannot see your conversation:

- the start room, and the player's start point if you already know it;
- each step to play, with its coordinates, cursor mode and inventory ID;
- the exact assertion after each step (for example "`objects[2].visible` is false", "`player.inventory` contains item 3");
- an instruction to read `references/playtesting.md` first, to call `stop_game` at the end, and not to edit the project. The subagent may only add a temporary aid if you allow it, and must then remove it.

Ask it to report back briefly: pass or fail for each assertion with the observed values, any `get_game_log` errors, and a description of anything unexpected. It should not paste raw state dumps. Fix failures yourself, then start a new subagent to play the test again.

The `game_*` tools need the `agsmcp` engine plugin enabled in the project. If `project_info` does not list `agsmcp.dll` under `plugins`, tell the user that `runtime_enable_plugin` turns it on for this game (and that they should untick it in the Plugins node before a release build), and enable it only with their agreement.

## References

Read each one when you reach that part of the job rather than all up front; they are short but specific.

- [references/tools.md](references/tools.md): every tool with its argument shapes and the quirks that matter. Read it when unsure what a tool takes or returns.
- [references/scripting.md](references/scripting.md): AGS 3.6 script patterns that work (handlers, inventory, state, dialog scripts, NPC entrances, ambient animated characters) and the usual pitfalls. Read it before writing handlers.
- [references/playtesting.md](references/playtesting.md): the `game_*` loop: cursor modes, waiting correctly, dismissing message boxes, exact assertions. The play-test subagent reads it before it checks behaviour in the running game.
- [references/pixel-art.md](references/pixel-art.md): generating crisp pixel-art backgrounds and sprites with Python/PIL, plus `scripts/pixelart.py`. Read it when you are about to draw.
- [references/audio.md](references/audio.md): synthesising sound effects, ambience loops and music with numpy, plus `scripts/sfx.py`, and importing them as audio clips. Read it when the game needs sound.
