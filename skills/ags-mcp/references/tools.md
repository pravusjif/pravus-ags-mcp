# ags MCP tool reference

Argument shapes and quirks, grouped by area. Where a tool "saves", it writes the file immediately; everything else lives in memory until `save_project`.

The rows use a short form; the argument keys are:

- `N` is the room number, passed as **`number`**, on every room tool (`get_room number=3`).
- Room entities go in `entity` (`hotspot:2`, `hDoor`), on both `get_room_properties` and `set_room_properties`.
- Game entities are `type` plus `id` (numeric ID or script name).
- Scripts are named with `name`. `write_script` takes the body as `text`; `edit_script` takes `find` and `replace`.

A key a tool does not take is refused with the list of the ones it does.

## Project

| Tool | Notes |
|---|---|
| `project_info` | Always first. Returns `resolution`, `colorDepth`, `playerCharacter`, counts, the enabled `plugins`, `debugMode` and `enginePlugin{available, enabled}` (whether the `game_*` tools can work). |
| `save_project` | Writes `Game.agf`, `acsprset.spr` (sprites), scripts and the loaded room. Cheap; call it after any entity/sprite/view change and before `run_game`. |
| `list_entities type` | `character`, `inventory`, `dialog`, `gui`, `guicontrol`, `view`, `cursor`, `font`, `audioclip`, `audiocliptype`, `audiofolder`, `globalvariable`, `customproperty`. Returns `{id, name}`; `id` strings. |
| `find_usages name` | Whole-word search across modules, room scripts and dialog scripts. Use before renames/deletes. |
| `batch calls [stopOnError]` | `calls` is `[{name, arguments}]`, run in order in one request: use it for bulk passes (many `set_properties`, `import_sprite` per frame) instead of one call each. Returns `{results:[{name, ok, content|error}], stopped}`; JSON text comes back parsed, images as a note (use `outPath`). Stops at the first failure unless `stopOnError=false`; no nesting. |
| `create_project folder [template gameName fileName]` / `open_project path` | Replace the open game. Use them only when the user asks for a new or different game; everything else happens in the open one. Both save the current one first unless `saveCurrent=false`. `open_project` cannot reopen the game that is already open (the editor's lock file), so "reload from disk" means restarting the editor. |

## Game data (entities)

| Tool | Notes |
|---|---|
| `get_properties type id` | `id` is the numeric ID or script name. Returns every property with `type`, `readOnly`, `value` and, for characters and inventory, the `events` with bound functions. |
| `set_properties type id properties` | Enums by name or number. Script names are uniqueness-checked (including derived macros). Needs `save_project`. |
| `create_entity type [properties]` | Auto-names (`cChar3`, `iInvItem4`, `dDialog1`) unless the script name is in `properties`: `ScriptName` for characters, `Name` for inventory items, dialogs, GUIs and views. Properties that fit nothing come back in `ignored` with the reason; check it. Inventory: `{Name, Description, Image, CursorImage, HotspotX, HotspotY}`. Character: `{ScriptName, RealName, NormalView, IdleView, IdleDelay, StartingRoom, StartX, StartY, SpeechColor, Solid, Clickable, DiagonalLoops}`. `globalvariable`, `customproperty`, `audiocliptype` and `audiofolder` need `Name` (`audiofolder` also takes `Parent`). Audio clips come from `import_audio`, not here. Returns the new `id`. |
| `delete_entity type id` | Renumbers higher IDs like the editor does, so re-read IDs afterwards. |
| `set_event type id event [function]` | `type` is `character`, `inventory`, `gui` or `guicontrol`. Events: characters `Look, Interact, Talk, UseInv, AnyClick, PickUp`; inventory `Look, Interact, Talk, UseInv, OtherClick`; a GUI `OnClick`; buttons `OnClick`, list boxes `OnSelectionChanged`, text boxes `OnActivate`, sliders `OnChange` (labels and inventory windows have none). Adds a stub to `GlobalScript.asc` named `<scriptName>_<event>` (`btnOk_OnClick` for a control) with the right parameters and binds it. Then `edit_script GlobalScript` to fill the stub. |
| `list_gui_controls gui` | `{id, name, type, x, y, width, height, zOrder, visible, events}` per control. Address a control as `type=guicontrol id="gMain:btnOk"` (or `"2:0"`, IDs) in `get_properties`/`set_properties`/`set_event`. |
| `create_gui_control gui type [x y width height] [properties]` | `type`: `button`, `label`, `textbox`, `listbox`, `slider`, `inventory`. Size defaults to 40x20, position to 0,0 (relative to the GUI). Pass `properties` such as `{Name, Text, Image, Font, TextColor}`; without `Name` it is auto-named (`Button1`). Text windows refuse. `save_project` to keep it. |
| `delete_gui_control gui control` | Higher control IDs shift down. The script handler is left in place. |
| `get_dialog_script id` / `set_dialog_script id [script] [options]` | `options` is `[{text, show, say}]` (numbered from 1 in order). Without `options` the existing options stay. See scripting.md for the dialog-script format. |

## Scripts

| Tool | Notes |
|---|---|
| `list_scripts` | Modules (with line counts) and room scripts (`room1`, …). |
| `read_script name [header] [startLine endLine]` | `name` is a module (`GlobalScript`, `KeyboardMovement`) or `roomN`. `header=true` reads the `.ash`. `staleOnDisk=true`: the file was changed outside the editor, and this text (the one compiled and saved) is not it; `reload_script` takes the disk version. |
| `write_script name text [header]` | Replaces the whole file and saves. Use for new room scripts after `set_room_event` has added the stubs (keep the stub names). |
| `edit_script name find replace [header] [all]` | Exact, newline-agnostic find/replace; `find` must occur exactly once unless `all=true` (include a comment or the function header to make it unique). Saves. |
| `reload_script name` | Replaces the editor's copy with the file on disk: a module's header and body, or `roomN`. Discards unsaved in-memory edits to it; refuses a room whose other changes are unsaved. Returns `files:[{file, lines, changed}]`. |
| `create_script_module name [headerText scriptText]` | New `.asc`/`.ash` pair, placed just above GlobalScript so GlobalScript can use its header (a script sees only the headers above it). Needs `save_project` to be recorded in `Game.agf`. |
| `move_script_module name direction [index]` | `direction` is `up`, `down` or `index` (with `index`, 0 = first). Use it when a module needs a header from one below it. Refuses GlobalScript and projects with script folders. Returns the new `order`; `save_project` to keep it. |
| `compile [name]` | No name: all modules. `name=roomN` compiles the room script with its generated header (hotspot/object names). Regenerates the auto-generated header first, so names from a `create_entity`/`create_view` made moments ago resolve. Returns `{ok, errors, warnings, messages:[{severity, script, line, message}], staleOnDisk:[file]}`; a file in `staleOnDisk` was edited on disk and not compiled, so `reload_script` it and compile again. |
| `script_api_lookup query` | Signatures and docs from the built-in header and project headers. It can list functions the project's Script API level hides, so a listed name is not a guarantee it compiles. |

## Rooms

All room tools load the room in the editor (switching away from whatever was loaded) and, when they change something, save it. If the room script fails to compile, they return `save.saved=false` plus the messages and leave the change pending in the editor.

| Tool | Notes |
|---|---|
| `list_rooms` | Numbers, descriptions, file names, which room is loaded. |
| `get_room N [show]` | About 4k tokens a call, because it lists every slot. Size, edges, painted area IDs per mask (`painted`), all hotspot/object/region/walkable/walk-behind slots with names and bound events, room events. `show=true` opens the editor tab. Slots exist whether used or not: 50 hotspots, 16 of the others; ID 0 is "none". |
| `create_room [number] [description]` | Blank black room at game resolution. Lowest free number by default. Rooms cannot be deleted. |
| `set_room_background N path\|base64 [background]` | Frame 0 = main background. A different size resizes the room and clears all masks, so import the background **before** painting masks; re-importing a frame of the same size keeps them. `background=1` (then 2, …) adds the next frame. Smaller images are padded. |
| `draw_room_mask N mask area shapes [clear]` | `mask`: `hotspots`, `walkableareas`, `walkbehinds`, `regions`. `area` 0 erases. Shapes in room pixels: `{type:"rect", x, y, width, height}`, `{type:"polygon", points:[[x,y],…]}`, `{type:"ellipse", x, y, rx, ry}` (centre + radii), `{type:"line", x1,y1,x2,y2}`, `{type:"fill", x, y}`. Later shapes overwrite earlier ones, so paint big areas first. Returns `paintedAreas`. |
| `import_room_mask N mask path\|base64` | Indexed image, pixel value = area number. |
| `get_room_properties N entity` / `set_room_properties N entity properties` | `entity`: `room`, `hotspot:K`, `object:K`, `region:K`, `walkablearea:K`, `walkbehind:K`, or a script name (`hDoor`, `oKey`). Hotspot: `{Name, Description, WalkToPoint:"x,y"}`. Walk-behind: `{Baseline}`. Region: `{LightLevel, UseColourTint}`. Room: `{Description, LeftEdgeX, RightEdgeX, TopEdgeY, BottomEdgeY}`. |
| `create_room_object N [properties]` | `{Name, Description, Image, StartX, StartY, Visible, Clickable, Baseline}`. `StartX/StartY` = bottom-left of the sprite. IDs are sequential; returns the `id`. |
| `delete_room_object N object` | By ID or name; renumbers higher IDs. |
| `set_room_event N entity event [function]` | `entity`: `room`, `hotspot:K`/`hDoor`, `object:K`/`oKey`, `region:K`. Room events: `Load`, `AfterFadeIn`, `FirstLoad`, `RepExec`, `Leave`, `Unload`, `LeaveLeft/Right/Top/Bottom`. Hotspot/object: `Look`, `Interact`, `Talk`, `UseInv`, `PickUp`, `AnyClick`, `WalkOn`, `MouseMove`. Region: `WalksOnto`, `WalksOff`, `Standing`. Default function `<item>_<event>` (`hDoor_Look`, `oKey_Interact`, `region1_WalksOnto`, `room_Load`). Adds the stub with the correct parameter list to `roomN.asc`. Pass `function=` to bind a second event to an existing handler (e.g. `oKey PickUp function=oKey_Interact`) instead of adding a stub. |
| `render_room N [mask] [background] [outPath]` | PNG of the background; with `mask`, each area in a distinct colour with a legend in the text. Use it to check masks and object placement. `outPath` (absolute) writes the file and returns `{path, width, height, note}` (the legend is in `note`). |
| `get_mask_pixel N mask x y` | Area number at a pixel, 0 = none. Good for spot-checking a walkable floor or a region. |

## Assets

| Tool | Notes |
|---|---|
| `import_sprite path\|base64 [folder] [transparency] [alpha]` | Returns `{number, width, height}`. Default transparency `LeaveAsIs` keeps PNG alpha in 32-bit games. Write the returned numbers down; they are what `Image`, views and `Graphic` refer to. |
| `list_sprites [folder] [flat]` | Folder tree `{name, sprites:[{number, width, height, colorDepth, alphaChannel}], folders}`; `flat=true` gives one list sorted by number. Folder names are what `import_sprite folder` takes. |
| `create_sprite_folder name [parent]` | Folder names are unique across the project (a taken name gets a number appended; use the returned `name`). `save_project` to keep it. |
| `get_sprite number [outPath]` | PNG; use it to see what an existing sprite looks like. `outPath` (absolute) writes the file and returns `{path, width, height, note}` only. |
| `replace_sprite number path\|base64` | Keeps the number, so references stay valid. Use it to iterate on art. |
| `delete_sprite number` | Refuses with a usage report if a view/object/character still uses it. |
| `create_view [name] loops` | `loops` is an array of `{frames, runNextLoop?}`; a frame is `{sprite, delay?, flipped?, sound?}`. Every sprite must exist. `sound` is an audio clip's **index** (from `import_audio`), not its ID. Loop 0 is "down" for walking views (0 down, 1 left, 2 right, 3 up; 4–7 diagonals). Script constant = upper-cased name. |
| `create_entity font {CopyFrom?, ...}` | New font slot `N = count`, a copy of font 0 (or `CopyFrom`, an ID or `eFont…` name) including its `agsfnt<N>` file; other properties (`Name`, `LineSpacing`, `OutlineFont`…) are applied. Script name `eFont` + Name without spaces. |
| `import_font id path [height\|pointSize]` | Copies a `.ttf` or `.wfn` (absolute path) to `agsfnt<id>.ttf\|.wfn`, deleting the other kind; other extensions are refused. TTF: `height` is pixels, turned into the closest point size; else `pointSize`, else the current size or 10. WFN is a bitmap font: no size arguments, scale it with `SizeMultiplier` (reset to 1 by an import). Returns `{id, projectFile, pointSize, height}`; `height` is the full drawn height and can exceed the asked one. A bad file is refused and the old one kept. `save_project` to keep the slot. |
| `measure_text font text [maxWidth]` | WFN fonts only (TTF: `game_text_extent` with the game running). Computes what the engine computes: glyph widths × `SizeMultiplier`, +2×`AutoOutlineThickness` for an automatic outline. `[` is a line break, `\[` a literal. `maxWidth` wraps like the engine: at `maxWidth-1`, at the last space, else mid-word, so a label of width W fits a line whose `width` ≤ W-1. Returns `{width, height, fontHeight, lineSpacing, lineCount, lines:[{text, width}], notes}`; `height` = `lineSpacing`×(lines-1) + `fontHeight`. Matches the engine's `GetTextWidth`/`GetTextHeight`. |
| `list_strings [type] [contains] [rooms] [limit=200] [offset]` | Player-visible text `{text, type, source, line?, speaker?}`; `type` is `Script`, `DialogScript`, `Message`, `DialogOption` or `ItemDescription`. Covers dialog options, dialog speech lines (`speaker`) and script-line literals, every double-quoted literal in modules and room scripts (as written, escapes not decoded), GUI label/button texts, character names, inventory descriptions, text custom properties, global messages. `rooms=true` also loads each room for its messages and hotspot/object descriptions (refused while the loaded room has unsaved changes). Use with `measure_text` to find lines that overflow. |

## Audio

See audio.md for making the sounds. Clips live in `Game.agf`, so `save_project` after these.

| Tool | Notes |
|---|---|
| `import_audio path\|base64+fileName [name] [folder] [type] [bundling] [volume] [priority] [repeat]` | Formats `.ogg .mp3 .wav .voc .mid .mod .xm .s3m .it`. The source stays where it is (`base64` is saved to `Audio\<fileName>`) and is copied to `AudioCache\auNNNNNN.ext`, which is what the build packs. Default name `a` + file name; `type` (name or ID) and `bundling` default to the folder's. Returns `{scriptName, id, index, type, folder, cacheFile}`. The editor's Audio tree updates at once. |
| `replace_audio clip [path\|base64+fileName]` | Keeps the script name, ID and index. With no file, re-copies the current source into the cache (after regenerating it in place; a build also does this when the source's timestamp changed). |
| `delete_audio clip [force]` | Refuses, listing the uses, while a view frame, the score sound or a script names it. Later clips shift down one ID; indexes stay. Deletes the cache copy, keeps the source. |
| `audiocliptype` entities | `get_properties`/`set_properties audiocliptype "Ambient Sound" {MaxChannels: 3}` (also `VolumeReductionWhileSpeechPlaying`, `CrossfadeClips`, `Name`). `create_entity audiocliptype {Name}`; `delete_entity` refuses while a clip or folder uses the type. `TypeID` is read-only. |
| `audiofolder` entities | Addressed by name (`list_entities audiofolder`; the root is usually `Main`). Properties `DefaultType` (type name or ID), `DefaultBundlingType`, `DefaultVolume` (0..100), `DefaultPriority`, `DefaultRepeat`, `Name` (unique). Clips set to Inherit play with the nearest folder's volume, priority and repeat. `create_entity audiofolder {Name, Parent?, ...defaults}` starts with the parent's type and bundling; `delete_entity` refuses the root and non-empty folders. |

## Build and run

| Tool | Notes |
|---|---|
| `build_game [rebuild] [mode]` | Saves the project, then a full build to `Compiled\Windows\<name>.exe`; structured messages like `compile`. A Debug-mode change forces a rebuild, as in the editor. `mode=release` turns the `agsmcp` plugin and Debug mode off and deletes the copied `agsmcp.dll` (use it for a build the user will ship); `mode=debug` turns both back on. The mode is saved with the project. |
| `run_game [startRoom] [rebuild] [mode]` | Stops a running game (or a leftover one), saves the project, builds incrementally and launches windowed. `startRoom` skips the intro rooms, and also becomes the room `RestartGame` returns to. |
| `stop_game` / `game_status` / `get_game_log [sinceLine] [limit]` | The engine log shows script errors with line numbers when the game crashes or quits unexpectedly. |
| `runtime_enable_plugin` | Once per project, then `save_project`; required for the `game_*` tools. |
| `runtime_disable_plugin` | Unticks the plugin (then `save_project`). For a release build prefer `build_game mode=release`, which also removes the copied DLL. |

### When the server is unreachable

With the editor closed (or the plugin not loaded), `AGSEditor.exe <Game.agf> /compile` builds the game from disk and exits. It fails while the project is open in an editor (`_OpenInEditor.lock`), and it prints compiler errors only when its output is redirected (`AGSEditor.exe <Game.agf> /compile > build.log 2>&1`, then read the file).
## Player simulation (`game_*`)

See playtesting.md for the loop. Quick shapes:

| Tool | Notes |
|---|---|
| `game_state` | View numbers are the editor's. `room`, `interfaceEnabled`, `player{x,y,room,walking,inventory:[{item,count}],activeInv}`, `characters[]`, `objects[{id,x,y,sprite,visible,view,frame}]`, `recentEvents` (`enter:N`/`leave:N`). `animating` reflects `Animate()` only, not idle views. |
| `game_process_click x y [mode] [inventory]` | Room coordinates, cursor mode number (templates: 0 Walk, 1 Look, 2 Interact, 3 Talk, 4 UseInv). Queued; returns at once. |
| `game_click x y [button]` | Raw screen click; this is what dismisses a `Display()` box or presses a GUI button. |
| `game_hover_name x y` | What a click there would hit. Check before clicking near the player. It does not move the mouse. |
| `game_wait_until condition [room] [pattern] [sinceLine] [timeoutMs] [stableMs]` | `ready` (interface enabled and player idle, stable for `stableMs`), `room`, `idle`, `interfaceEnabled`. Returns `met` and the final `state`. `logLine` waits for a log line matching the regex `pattern` (have the script `System.Log` a marker) and returns `line`, `lineIndex` and `nextLine`; take `get_game_log`'s `nextLine` before the action and pass it as `sinceLine`, since the default only sees lines written after the wait starts. Keep `timeoutMs` at 45000 or less and chain waits: many clients abandon a call after about 60 s. |
| `game_screenshot [outPath] [frames] [everyN]` | PNG of the window; `outPath` (absolute) writes the file instead. The shot is taken at the next frame, so right after `game_process_click` or `game_key` it can still show the moment before the click took effect: wait first. `frames > 1` is a burst (needs `outPath` as a directory): `frame-000.png`… and `frames.json` with each frame's `ms`, one frame every `everyN` game frames (at 40 fps, `everyN=2` is about 50 ms apart), at most 2000 game frames in all. Use it for animations, fades and walk cycles. |
| `game_key key` | `"Tab"`, `"Enter"`, `"Escape"`, `"Space"`, `"F5"`, letters, digits. In the standard templates Tab opens the inventory and Escape the control panel. Arrows, Home/End, PageUp/PageDown, Insert/Delete and F1–F12 are posted to the window as real keys (`via: window`) and reach `on_key_press` with their AGS codes (Up 372, F7 365). Letters sent as keys do not reach a GUI text box: use `game_type`. |
| `game_type text` | Character input (one `WM_CHAR` per character) for a focused GUI text box; without one it calls `on_text_input(ch)`. `on_key_press` does not run. Follow with `game_key Enter` to submit a text box. |
| `game_call_function name [args] [roomScript]` | Runs a function defined in `GlobalScript.asc` with up to two int args; refused if GlobalScript does not define it (a hook in another module needs a GlobalScript wrapper). `roomScript=true` runs a function of the current room's script instead (checked against `roomN.asc` on disk; the result carries `room`). Keep it non-blocking: no `Say`, `Display`, `Wait` or `eBlock`. A function that calls `QuitGame` returns `{ok, gameExiting: true}` and the game exits within a fraction of a second. |
| `game_run_interaction target mode [inventory]` | Clicks a thing of the current room without knowing its coordinates: `hotspot:N`, `object:N`, `character:N` (N = ID or script name) or a bare script name, in cursor `mode`. The point is the hotspot's mask centroid or the middle of the object's or character's current sprite; up to 8 points are checked with the hover name (hotspot/object `Description`, character name) and the first match is clicked (queued, as `game_process_click`). Returns `point`, `hoverName`, `tried`. It refuses when nothing matches (covered by the player or a GUI, off-screen, an open popup such as a save/restore dialog), when the target has no description, or while the interface is disabled. Follow with `game_wait_until ready`. |
| `game_text_extent font text` | One line's `width`/`height` as the running game draws it (outline included, no wrapping). Use it for TTF fonts, which `measure_text` refuses. |
| `game_get_global_int` / `game_set_global_int` | Legacy GlobalInts only; exported script variables are not reachable this way. |
