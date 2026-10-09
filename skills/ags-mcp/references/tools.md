# ags MCP tool reference

Argument shapes and quirks, grouped by area. `N` is a room number. Where a tool "saves", it writes the file immediately; everything else lives in memory until `save_project`.

## Project

| Tool | Notes |
|---|---|
| `project_info` | Always first. Returns `resolution`, `colorDepth`, `playerCharacter`, counts and the enabled `plugins`. |
| `save_project` | Writes `Game.agf`, `acsprset.spr` (sprites), scripts and the loaded room. Cheap; call it after any entity/sprite/view change and before `run_game`. |
| `list_entities type` | `character`, `inventory`, `dialog`, `gui`, `view`, `cursor`, `font`, `audioclip`, `audiocliptype`, `globalvariable`, `customproperty`. Returns `{id, name}`; `id` strings. |
| `find_usages name` | Whole-word search across modules, room scripts and dialog scripts. Use before renames/deletes. |
| `create_project folder [template gameName fileName]` / `open_project path` | Replace the open game. Use them only when the user asks for a new or different game; everything else happens in the open one. Both save the current one first unless `saveCurrent=false`. `open_project` cannot reopen the game that is already open (the editor's lock file), so "reload from disk" means restarting the editor. |

## Game data (entities)

| Tool | Notes |
|---|---|
| `get_properties type id` | `id` is the numeric ID or script name. Returns every property with `type`, `readOnly`, `value` and, for characters and inventory, the `events` with bound functions. |
| `set_properties type id properties` | Enums by name or number. Script names are uniqueness-checked (including derived macros). Needs `save_project`. |
| `create_entity type [properties]` | Auto-names (`cChar3`, `iInvItem4`) unless `Name`/`ScriptName` is given. Inventory: `{Name, Description, Image, CursorImage, HotspotX, HotspotY}`. Character: `{ScriptName, RealName, NormalView, IdleView, IdleDelay, StartingRoom, StartX, StartY, SpeechColor, Solid, Clickable, DiagonalLoops}`. `globalvariable`, `customproperty` and `audiocliptype` need `Name`. Audio clips come from `import_audio`, not here. Returns the new `id`. |
| `delete_entity type id` | Renumbers higher IDs like the editor does, so re-read IDs afterwards. |
| `set_event type id event [function]` | `type` is `character` or `inventory`. Events: characters `Look, Interact, Talk, UseInv, AnyClick, PickUp`; inventory `Look, Interact, Talk, UseInv, OtherClick`. Adds a stub to `GlobalScript.asc` named `<scriptName>_<event>` and binds it. Then `edit_script GlobalScript` to fill the stub. |
| `get_dialog_script id` / `set_dialog_script id [script] [options]` | `options` is `[{text, show, say}]` (numbered from 1 in order). See scripting.md for the dialog-script format. |

## Scripts

| Tool | Notes |
|---|---|
| `list_scripts` | Modules (with line counts) and room scripts (`room1`, …). |
| `read_script name [header] [startLine endLine]` | `name` is a module (`GlobalScript`, `KeyboardMovement`) or `roomN`. `header=true` reads the `.ash`. |
| `write_script name text [header]` | Replaces the whole file and saves. Use for new room scripts after `set_room_event` has added the stubs (keep the stub names). |
| `edit_script name find replace [header] [all]` | Exact, newline-agnostic find/replace; `find` must occur exactly once unless `all=true` (include a comment or the function header to make it unique). Saves. |
| `create_script_module name [headerText scriptText]` | New `.asc`/`.ash` pair. Needs `save_project` to be recorded in `Game.agf`. |
| `compile [name]` | No name: all modules. `name=roomN` compiles the room script with its generated header (hotspot/object names). Regenerates the auto-generated header first, so names from a `create_entity`/`create_view` made moments ago resolve. Returns `{ok, errors, warnings, messages:[{severity, script, line, message}]}`. |
| `script_api_lookup query` | Signatures and docs from the built-in header and project headers. It can list functions the project's Script API level hides, so a listed name is not a guarantee it compiles. |

## Rooms

All room tools load the room in the editor (switching away from whatever was loaded) and, when they change something, save it. If the room script fails to compile, they return `save.saved=false` plus the messages and leave the change pending in the editor.

| Tool | Notes |
|---|---|
| `list_rooms` | Numbers, descriptions, file names, which room is loaded. |
| `get_room N [show]` | Size, edges, painted area IDs per mask (`painted`), all hotspot/object/region/walkable/walk-behind slots with names and bound events, room events. `show=true` opens the editor tab. Slots exist whether used or not: 50 hotspots, 16 of the others; ID 0 is "none". |
| `create_room [number] [description]` | Blank black room at game resolution. Lowest free number by default. Rooms cannot be deleted. |
| `set_room_background N path\|base64 [background]` | Frame 0 = main background. A different size resizes the room and clears all masks, so import the background **before** painting masks. Smaller images are padded. |
| `draw_room_mask N mask area shapes [clear]` | `mask`: `hotspots`, `walkableareas`, `walkbehinds`, `regions`. `area` 0 erases. Shapes in room pixels: `{type:"rect", x, y, width, height}`, `{type:"polygon", points:[[x,y],…]}`, `{type:"ellipse", x, y, rx, ry}` (centre + radii), `{type:"line", x1,y1,x2,y2}`, `{type:"fill", x, y}`. Later shapes overwrite earlier ones, so paint big areas first. Returns `paintedAreas`. |
| `import_room_mask N mask path\|base64` | Indexed image, pixel value = area number. |
| `get_room_properties N entity` / `set_room_properties N entity properties` | `entity`: `room`, `hotspot:K`, `object:K`, `region:K`, `walkablearea:K`, `walkbehind:K`, or a script name (`hDoor`, `oKey`). Hotspot: `{Name, Description, WalkToPoint:"x,y"}`. Walk-behind: `{Baseline}`. Region: `{LightLevel, UseColourTint}`. Room: `{Description, LeftEdgeX, RightEdgeX, TopEdgeY, BottomEdgeY}`. |
| `create_room_object N [properties]` | `{Name, Description, Image, StartX, StartY, Visible, Clickable, Baseline}`. `StartX/StartY` = bottom-left of the sprite. IDs are sequential; returns the `id`. |
| `delete_room_object N object` | By ID or name; renumbers higher IDs. |
| `set_room_event N entity event [function]` | `entity`: `room`, `hotspot:K`/`hDoor`, `object:K`/`oKey`, `region:K`. Room events: `Load`, `AfterFadeIn`, `FirstLoad`, `RepExec`, `Leave`, `Unload`, `LeaveLeft/Right/Top/Bottom`. Hotspot/object: `Look`, `Interact`, `Talk`, `UseInv`, `PickUp`, `AnyClick`, `WalkOn`, `MouseMove`. Region: `WalksOnto`, `WalksOff`, `Standing`. Default function `<item>_<event>` (`hDoor_Look`, `oKey_Interact`, `region1_WalksOnto`, `room_Load`). Adds the stub with the correct parameter list to `roomN.asc`. Pass `function=` to bind a second event to an existing handler (e.g. `oKey PickUp function=oKey_Interact`) instead of adding a stub. |
| `render_room N [mask] [background]` | PNG of the background; with `mask`, each area in a distinct colour with a legend in the text. Use it to check masks and object placement. |
| `get_mask_pixel N mask x y` | Area number at a pixel, 0 = none. Good for spot-checking a walkable floor or a region. |

## Assets

| Tool | Notes |
|---|---|
| `import_sprite path\|base64 [folder] [transparency] [alpha]` | Returns `{number, width, height}`. Default transparency `LeaveAsIs` keeps PNG alpha in 32-bit games. Write the returned numbers down; they are what `Image`, views and `Graphic` refer to. |
| `get_sprite number` | PNG; use it to see what an existing sprite looks like. |
| `replace_sprite number path\|base64` | Keeps the number, so references stay valid. Use it to iterate on art. |
| `delete_sprite number` | Refuses with a usage report if a view/object/character still uses it. |
| `create_view [name] loops` | `loops` is an array of `{frames, runNextLoop?}`; a frame is `{sprite, delay?, flipped?, sound?}`. Every sprite must exist. `sound` is an audio clip's **index** (from `import_audio`), not its ID. Loop 0 is "down" for walking views (0 down, 1 left, 2 right, 3 up; 4–7 diagonals). Script constant = upper-cased name. |

## Audio

See audio.md for making the sounds. Clips live in `Game.agf`, so `save_project` after these.

| Tool | Notes |
|---|---|
| `import_audio path\|base64+fileName [name] [folder] [type] [bundling] [volume] [priority] [repeat]` | Formats `.ogg .mp3 .wav .voc .mid .mod .xm .s3m .it`. The source stays where it is (`base64` is saved to `Audio\<fileName>`) and is copied to `AudioCache\auNNNNNN.ext`, which is what the build packs. Default name `a` + file name; `type` (name or ID) and `bundling` default to the folder's. Returns `{scriptName, id, index, type, folder, cacheFile}`. The editor's Audio tree updates at once. |
| `replace_audio clip [path\|base64+fileName]` | Keeps the script name, ID and index. With no file, re-copies the current source into the cache (after regenerating it in place; a build also does this when the source's timestamp changed). |
| `delete_audio clip [force]` | Refuses, listing the uses, while a view frame, the score sound or a script names it. Later clips shift down one ID; indexes stay. Deletes the cache copy, keeps the source. |
| `audiocliptype` entities | `get_properties`/`set_properties audiocliptype "Ambient Sound" {MaxChannels: 3}` (also `VolumeReductionWhileSpeechPlaying`, `CrossfadeClips`, `Name`). `create_entity audiocliptype {Name}`; `delete_entity` refuses while a clip or folder uses the type. `TypeID` is read-only. |

## Build and run

| Tool | Notes |
|---|---|
| `build_game [rebuild]` | Full build to `Compiled\Windows\<name>.exe`; structured messages like `compile`. |
| `run_game [startRoom] [rebuild]` | Builds incrementally from **disk** (so `save_project` first), launches windowed, stops a previous instance. `startRoom` skips the intro rooms. |
| `stop_game` / `game_status` / `get_game_log [sinceLine] [limit]` | The engine log shows script errors with line numbers when the game crashes or quits unexpectedly. |
| `runtime_enable_plugin` | Once per project, then `save_project`; required for the `game_*` tools. |

## Player simulation (`game_*`)

See playtesting.md for the loop. Quick shapes:

| Tool | Notes |
|---|---|
| `game_state` | `room`, `interfaceEnabled`, `player{x,y,room,walking,inventory:[{item,count}],activeInv}`, `characters[]`, `objects[{id,x,y,sprite,visible,view,frame}]`, `recentEvents` (`enter:N`/`leave:N`). `animating` reflects `Animate()` only, not idle views. |
| `game_process_click x y [mode] [inventory]` | Room coordinates, cursor mode number (templates: 0 Walk, 1 Look, 2 Interact, 3 Talk, 4 UseInv). Queued; returns at once. |
| `game_click x y [button]` | Raw screen click; this is what dismisses a `Display()` box or presses a GUI button. |
| `game_hover_name x y` | What a click there would hit. Check before clicking near the player. |
| `game_wait_until condition [room] [timeoutMs] [stableMs]` | `ready` (interface enabled and player idle, stable for `stableMs`), `room`, `idle`, `interfaceEnabled`. Returns `met` and the final `state`. |
| `game_screenshot` | PNG of the window. |
| `game_key key` | `"Tab"`, `"Enter"`, `"Escape"`, `"Space"`, `"F5"`, arrows, letters, digits. In the standard templates Tab opens the inventory and Escape the control panel. |
| `game_call_function name [args]` | Runs a GlobalScript function with up to two int args; handy for test hooks. |
| `game_get_global_int` / `game_set_global_int` | Legacy GlobalInts only; exported script variables are not reachable this way. |
