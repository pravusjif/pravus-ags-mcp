# Checking behaviour in the running game with the game_* and desktop_* tools

The `game_*` tools talk to a built copy of the user's game through the `agsmcp` engine plugin. They let you play the interaction you just built, with real mouse and keyboard input, and assert on real engine state. That catches wrong coordinates, unreachable hotspots and broken flags that reading the script never would. Always check in the user's own project; never build a separate test game for it.

**This loop always runs in a subagent**, not in the main agent's context, so that `game_state` dumps and screenshots stay out of the main context. The main agent saves the project and gives the subagent the room, the steps and the expected assertions (see "Verifying in the user's game" in SKILL.md). If you are that subagent, play only what you were asked to play, stop the game at the end, change nothing in the project, and report pass or fail for each assertion with the observed values, not raw dumps.

## One-time setup per project

The engine plugin has to be enabled in the project. `project_info` lists `agsmcp.dll` under `plugins` once it is on. If it is not:

1. Tell the user it is needed, and that `build_game mode=release` takes it out again for a release build.
2. With their agreement: `runtime_enable_plugin` (needs `agsmcp.dll` next to `AGSEditor.exe`), then `save_project`. `build_game mode=debug` does the same and also turns Debug mode back on.

## The loop

```
save_project                                   # run_game builds from disk
run_game startRoom=N                           # returns at once with the pid
game_wait_until ready timeoutMs=20000 stableMs=3000   # game booted, intro lines done
<for each step of the interaction you changed>
  game_hover_name x y                          # confirm the click will hit what you think
  game_process_click x y mode=K [inventory=ID] # queued; the game runs it next frame
  game_wait_until ready timeoutMs=25000 stableMs=2500
  game_state                                   # assert flags; game_screenshot when the look matters
stop_game
```

`game_run_interaction target=hotspot:hDoor mode=2` replaces the hover-then-click pair for a hotspot, object or character: it finds a point on the thing from the editor's data, confirms the hover name and queues the click, and refuses rather than clicking the wrong thing.

Cursor modes in the standard templates: `0` Walk, `1` Look, `2` Interact, `3` Talk, `4` UseInv (pass `inventory=<1-based ID>`), `5` Pick up, `6` Pointer. `game_process_click` is a real queued click, so GUIs over that point receive it instead of the room.

GUIs: `game_key Tab` opens the template's inventory window and `game_key Escape` the control panel; `game_click` presses their buttons, and `game_type` types into a text box. If a GUI has no key, call its script function directly with `game_call_function show_inventory_window` (any GlobalScript function with 0–2 int args works; `roomScript=true` reaches the current room's script).

Dialog options: pick one with `game_click` on its text row. Number keys do not choose it, and the rows move up as options are switched off, so take a screenshot first, aim at the row's text, and check which option fired.

Send one action per batch of tool calls. A room click sent in the same batch as several `game_hover_name` calls can be lost, two `game_screenshot`s in one batch overwrite each other's file, and waits sent together can return out of order. Several `game_click`s on GUI buttons in one batch all register.

## Waiting correctly

- `game_wait_until ready` means the interface is enabled and the player is idle, continuously for `stableMs`. Speech re-enables the interface between lines, so after a click that triggers several lines use `stableMs` 2500–4000 and a `timeoutMs` long enough for the whole sequence (roughly 3 s per line plus the walk).
- **A `Display()` box (and a `narrator:` line in a dialog) does not disable the interface**, so `ready` is met while the box still waits for a click, and the next step then seems to do nothing. If a beat looks stalled, take a screenshot; `game_click 160 100` (any point) dismisses the box.
- **An open dialog option list keeps `interfaceEnabled` at 0**, so `ready` runs to its timeout. When a list is expected, screenshot and click the option instead of waiting.
- Keep each `timeoutMs` at 45000 or less and chain waits for longer sequences; many clients abandon a tool call after about 60 s.
- Room transitions: `game_wait_until room room=N timeoutMs=10000 stableMs=500`.
- Walking only: `game_wait_until idle`.

## Assertions that catch real bugs

Read them off `game_state` rather than the screenshot:

- The object became visible/hidden: `objects[K].visible`.
- The item was taken: `player.inventory` contains `{item: ID}`. Whether `activeInv` clears after a `UseInv` depends on the game's script, so assert on it only after reading the handler.
- The sprite swapped: `objects[K].sprite` equals the new sprite number.
- The NPC arrived: `characters[j].room` and `x,y`. View numbers in `game_state` are the editor's.
- A value only the script knows (a flag, a counter): if you were allowed a temporary aid, add a hook to GlobalScript that logs it, `function TEST_Door() { System.Log(eLogInfo, "TEST door %d", doorOpen); }`, call it with `game_call_function TEST_Door`, and read the line with `get_game_log`, or wait for it with `game_wait_until logLine pattern="TEST door"` (pass `get_game_log`'s `nextLine`, taken before the call, as `sinceLine`). A hook must not block (no `Say`, `Display`, `Wait` or `eBlock`), and `game_call_function` only reaches functions defined in GlobalScript.
- The flag reached the other room: go there and trigger the interaction that depends on it (for example a look text that changes once a door is open), then `game_screenshot` to read the speech.
- `recentEvents` lists `enter:N`/`leave:N` for every room change.

## Things that look like bugs but are not

- `run_game startRoom=N` spawns the player at the player character's own `StartX`/`StartY`, whatever room you start in. If that point is outside the room's walkable area, every `player.Walk` does nothing and handlers appear to half-run; if it is on top of an object, clicks there hit the player. Paint the floor to include it, or walk away first.
- `run_game startRoom=N` also makes room N the restart point, so `RestartGame` returns there. To check a restart, boot without `startRoom`.
- `game_state.animating` stays `false` for a character playing its **idle** view (only `Animate()` sets it). To prove an idle animation runs, sample `frame`/`view` a few times or read the engine log (`Start anim view …`).
- A click at the player's own position (or within a few pixels of them in Walk mode) does nothing; choose a target ≥ 10 px away. The click lands on the character sprite, so `game_hover_name` returns the player's name there.
- A click on an object that the player sprite overlaps hits the player. Click a visible edge of the object, or move the player first.
- A click sent while a line of speech is on screen only skips the line; the click's own action never runs. Wait for `ready` before every room click.
- A hotspot whose `WalkToPoint` is far away makes the player walk before the handler runs; the wait must cover the walk.
- `Say` lines are not written to the log, and a short one can start and end between two tool calls. Take a screenshot straight after the click, or two about a second apart; for anything shorter, assert on state or a logged line.
- `game_screenshot` is taken on the next frame; immediately after a click it may still show the previous state. Take it after the wait. A burst (`frames` > 1) holds the connection to the game until its last frame, so no click can be sent during it: click first, then take the burst.
- The mouse cursor appears in screenshots (it is a real input), so hover names and state are the better evidence. During a blocking action it is the Wait cursor, drawn over a modal overlay or cutscene picture too, unless the game hides it (`mouse.Visible = false`) or gives Wait mode a blank graphic.
- `Hotspot.RunInteraction` (or any `RunInteraction`) called through `game_call_function` is deferred until the calling script has finished, so state read straight after does not show its effect. Drive interactions with `game_run_interaction` or `game_process_click`.
- A loop that runs 150000 iterations within one frame stops the game with "Script appears to be hung (a while loop ran … times)", even when it would finish. Build large caches or tables a slice per frame (in `repeatedly_execute`), or declare that one function `noloopcheck`.
- A save restores GlobalScript variables by position, not by name. A variable added in the middle shifts every later one when an older save is loaded, so add new globals at the end of the file while saves from earlier builds are still in use.
- A save also stores the object positions and states of every room visited so far; loading it shows those rooms as saved, not with later editor changes.
- `RestoredSaveInfo.GUIControlCount` in `validate_restored_save` has been reported to read 0 (not verified here); compare other counts if a check depends on it.
- A build reads fonts from disk but scripts from the editor's memory: a font file replaced on disk is in the next build, a script edited on disk is not until `reload_script`.

## When the game misbehaves

- Stop the game with `stop_game` when the check is done, and leave the project as the task left it.
- `game_status` tells you if the game exited; `get_game_log` then shows the script error with the room and line.
- `run_game` refuses to launch when the build fails and returns the compiler messages; fix, `save_project`, run again.
- If a wait keeps timing out with the interface disabled and no box is visible, a blocking `Walk` to an unreachable point is the usual cause: the target is outside the walkable area. Check with `get_mask_pixel N walkableareas x y`.
- If `mouse.x/y` in `game_state` stops following your clicks, the game window has lost input focus and clicks land at the old position. Do not debug the room script: `stop_game`, `run_game` and replay the short path back.

## Long play-throughs

Saves go to `%TEMP%\ags-mcp-run\userdata` and survive `stop_game` and `run_game`. A play-through too long for one subagent can be split: one run saves the game, and the next loads that save. Copy the save file aside before saving again, since the save dialog may overwrite the same slot, and change no script between the runs.

## Budget and bulk passes

A play-test subagent typically uses 140–260k tokens for one task of 8–14 checks; plan the split of a long test with that in mind. For a bulk pass that is not a play-through (measuring every dialog line, setting a property on forty objects), use `batch` to send many tool calls in one request instead of scripting raw HTTP calls to the server.

## Testing the release build (desktop_*)

The `game_*` tools need the engine plugin and Debug mode, so they never test the build a player gets. The `desktop_*` tools run any built exe with no plugin and drive it with the real mouse and keyboard. Use them as a final check that the release build starts and plays, after the behaviour itself has been checked with `game_*`.

```
build_game mode=release                         # Debug off, engine plugin out; saved with the project
desktop_run startRoom=N                         # windowed, software renderer, its own log
desktop_screenshot outPath=<abs>.png            # the window at the game's native size
desktop_click x y [button=right]                # game pixels, as the screenshot shows them
desktop_key Escape   /   desktop_type "text"
game_wait_until logLine pattern=... source=desktop   # a System.Log marker, if the build has one
desktop_stop
build_game mode=debug                           # back to the game_* tools
```

- **Keep the software renderer** (the default): Direct3D and OpenGL windows read back black.
- **There is no state readout and no hover check.** Evidence is the screenshot and the log (`get_game_log source=desktop`). Take click points from `get_room`/`render_room` or a screenshot, and screenshot after each action to see its effect.
- **Every input tool brings the game window to the front** (`tookFocus` says when it had to), so tell the user before a desktop run: their own typing and clicking would go into the game, and the tools refuse rather than send input to another window.
- The cursor mode changes the way a player changes it (in the Sierra-style template a right-click selects the next mode; read the game's `on_mouse_click`), not by argument.
- Leave the project in the mode it was in: if the user develops with the plugin enabled, finish with `build_game mode=debug`.
