# Checking behaviour in the running game with the game_* tools

The `game_*` tools talk to a built copy of the user's game through the `agsmcp` engine plugin. They let you play the interaction you just built, with real mouse and keyboard input, and assert on real engine state. That catches wrong coordinates, unreachable hotspots and broken flags that reading the script never would. Always check in the user's own project; never build a separate test game for it.

**This loop always runs in a subagent**, not in the main agent's context, so that `game_state` dumps and screenshots stay out of the main context. The main agent saves the project and gives the subagent the room, the steps and the expected assertions (see "Verifying in the user's game" in SKILL.md). If you are that subagent, play only what you were asked to play, stop the game at the end, change nothing in the project, and report pass or fail for each assertion with the observed values, not raw dumps.

## One-time setup per project

The engine plugin has to be enabled in the project. `project_info` lists `agsmcp.dll` under `plugins` once it is on. If it is not:

1. Tell the user it is needed, and that they should untick it in the editor's Plugins node before a release build.
2. With their agreement: `runtime_enable_plugin` (needs `agsmcp.dll` next to `AGSEditor.exe`), then `save_project`.

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

Cursor modes in the standard templates: `0` Walk, `1` Look, `2` Interact, `3` Talk, `4` UseInv (pass `inventory=<1-based ID>`), `5` Pick up, `6` Pointer. `game_process_click` is a real queued click, so GUIs over that point receive it instead of the room.

GUIs: `game_key Tab` opens the template's inventory window and `game_key Escape` the control panel; `game_click` presses their buttons. If a GUI has no key, call its script function directly with `game_call_function show_inventory_window` (any GlobalScript function with 0–2 int args works).

## Waiting correctly

- `game_wait_until ready` means the interface is enabled and the player is idle, continuously for `stableMs`. Speech re-enables the interface between lines, so after a click that triggers several lines use `stableMs` 2500–4000 and a `timeoutMs` long enough for the whole sequence (roughly 3 s per line plus the walk).
- `met:false` with the state still showing `interfaceEnabled:0` usually means a `Display()` box is open: send `game_click 160 100` (any point) to dismiss it, then wait again.
- Room transitions: `game_wait_until room room=N timeoutMs=10000 stableMs=500`.
- Walking only: `game_wait_until idle`.

## Assertions that catch real bugs

Read them off `game_state` rather than the screenshot:

- The object became visible/hidden: `objects[K].visible`.
- The item was taken: `player.inventory` contains `{item: ID}`; `activeInv` clears after a `UseInv`.
- The sprite swapped: `objects[K].sprite` equals the new sprite number.
- The NPC arrived: `characters[j].room` and `x,y`.
- The flag reached the other room: go there and trigger the interaction that depends on it (for example a look text that changes once a door is open), then `game_screenshot` to read the speech.
- `recentEvents` lists `enter:N`/`leave:N` for every room change.

## Things that look like bugs but are not

- `run_game startRoom=N` spawns the player at the player character's own `StartX`/`StartY`, whatever room you start in. If that point is outside the room's walkable area, every `player.Walk` does nothing and handlers appear to half-run; if it is on top of an object, clicks there hit the player. Paint the floor to include it, or walk away first.
- `game_state.animating` stays `false` for a character playing its **idle** view (only `Animate()` sets it). To prove an idle animation runs, sample `frame`/`view` a few times or read the engine log (`Start anim view …`).
- A `narrator:` line in a dialog script and `Display()` are modal boxes: the wait reports the interface disabled until you `game_click` them away.
- A click at the player's own position (or within a few pixels of them in Walk mode) does nothing; choose a target ≥ 10 px away. The click lands on the character sprite, so `game_hover_name` returns the player's name there.
- A click on an object that the player sprite overlaps hits the player. Click a visible edge of the object, or move the player first.
- A hotspot whose `WalkToPoint` is far away makes the player walk before the handler runs; the wait must cover the walk.
- `game_screenshot` is taken on the next frame; immediately after a click it may still show the previous state. Take it after the wait.
- The mouse cursor appears in screenshots (it is a real input), so hover names and state are the better evidence.

## When the game misbehaves

- Stop the game with `stop_game` when the check is done, and leave the project as the task left it.
- `game_status` tells you if the game exited; `get_game_log` then shows the script error with the room and line.
- `run_game` refuses to launch when the build fails and returns the compiler messages; fix, `save_project`, run again.
- If a wait keeps timing out with the interface disabled and no box is visible, a blocking `Walk` to an unreachable point is the usual cause: the target is outside the walkable area. Check with `get_mask_pixel N walkableareas x y`.
