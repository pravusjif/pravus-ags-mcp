# AGS 3.6 scripting patterns

Patterns that compile and behave in the running game, plus the mistakes that cost a compile or a confusing check. The names below (`hDoor`, `oKey`, `cGuard`, …) are placeholders: use the names in the user's project. Signatures below are what `set_room_event` / `set_event` generate, so handlers written by hand match the bindings.

## Handler signatures

```c
function room_Load() { }                                   // before fade-in: set up views, visibility
function room_AfterFadeIn() { }                            // intro lines go here
function hDoor_Look(Hotspot *theHotspot, CursorMode mode) { }
function hDoor_Interact(Hotspot *theHotspot, CursorMode mode) { }
function hDoor_UseInv(Hotspot *theHotspot, CursorMode mode) { }
function oKey_Interact(Object *theObject, CursorMode mode) { }
function region1_WalksOnto(Region *theRegion) { }
// GlobalScript (via set_event):
function cGuard_Talk(Character *theCharacter, CursorMode mode) { }
function iKey_Look(InventoryItem *theItem, CursorMode mode) { }
```

The room script is compiled with a generated header that declares that room's hotspots (`hDoor`), objects (`oKey`) and regions; other rooms' objects are not visible from it.

## Room-local vs cross-room state

Room-local flags are plain file-scope variables in `roomN.asc`:

```c
bool keyFound;   // reset when the game restarts, persists while the room is in memory
```

State another room must read goes in GlobalScript, exported, and imported in the header so every script sees it:

```c
// GlobalScript.asc (top of file)
bool doorOpen;
export doorOpen;

// GlobalScript.ash
import bool doorOpen;
```

`SetGlobalInt`/`GetGlobalInt` show up in `script_api_lookup` but fail to compile in projects with a modern Script API level. The editor-managed alternative is `create_entity globalvariable {Name, Type, DefaultValue}`, which is declared for all scripts automatically.

## Common actions

```c
player.Say("Line of speech.");                       // blocking, one speech bubble
Display("A narrator box the player must click away.");
cGuard.Say("NPC speech.");
player.Walk(x, y, eBlock, eWalkableAreas);           // stand point: beside the thing, not in front
player.FaceObject(oKey, eBlock);
player.FaceCharacter(cGuard, eBlock);
player.FaceLocation(x, y, eBlock);
player.AddInventory(iKey);   player.LoseInventory(iKey);
if (player.ActiveInventory == iKey) { ... }          // in a *_UseInv handler
oKey.Visible = true;                                 // reveal / hide objects
oChest.Graphic = SPR_CHEST_OPEN;                     // swap sprite by number (keep a #define per sprite)
oTorch.SetView(VTORCH); oTorch.Animate(0, 4, eRepeat, eNoBlock, eForwards);  // loop 0, delay 4
player.ChangeRoom(2, 160, 176, eDirectionUp);        // room, x, y, facing
if (Game.DoOnceOnly("intro")) { ... }                // one-time events without a flag
```

Bring an NPC into the current room and move them in one handler:

```c
if (cGuard.Room != player.Room)
  cGuard.ChangeRoom(player.Room, DOOR_X, DOOR_Y, eDirectionUp);   // appears at the door
cGuard.Walk(x, y, eBlock, eWalkableAreas);
cGuard.FaceCharacter(player, eBlock);
```

## An ambient animated character (an animal, a talking statue, …)

Use a character rather than an object when the thing needs `Talk`, speech lines or a dialog. Give it one view with the animation frames and let the idle system play it:

```
create_view name=vCat loops=[{frames:[{sprite:A, delay:40}, {sprite:B, delay:8}]}]   # A, B: imported sprite numbers
create_entity character {ScriptName:"cCat", RealName:"Cat", NormalView:V, IdleView:V, IdleDelay:0,
                         StartingRoom:N, StartX:x, StartY:y, Solid:false, DiagonalLoops:false}
set_event character cCat Look   /   set_event character cCat Talk
```

`IdleDelay 0` loops the idle view continuously; `DiagonalLoops false` because the view has a single loop; `Solid false` so the player can walk past. The Talk handler walks the player beside it, `FaceCharacter`, then `dCat.Start()`.

## Puzzle skeleton that holds up when played

```c
#define SPR_CHEST_OPEN 12       // the sprite number import_sprite returned
#define CHEST_STAND_X 140
#define CHEST_STAND_Y 150

function oChest_Interact(Object *theObject, CursorMode mode)
{
  if (oChest.Graphic == SPR_CHEST_OPEN) { player.Say("Nothing else in there."); return; }
  player.Walk(CHEST_STAND_X, CHEST_STAND_Y, eBlock, eWalkableAreas);
  player.FaceObject(oChest, eBlock);
  oChest.Graphic = SPR_CHEST_OPEN;
  oKey.Visible = true;
  player.Say("There's a key inside!");
}

function hDoor_UseInv(Hotspot *theHotspot, CursorMode mode)
{
  if (player.ActiveInventory != iKey) { player.Say("That won't help."); return; }
  if (doorOpen) { player.Say("It's already open."); return; }
  oDoorOpen.Visible = true;
  player.LoseInventory(iKey);
  doorOpen = true;
}
```

Guard every handler against being run twice (already open, already taken, already used); players click things repeatedly and `unhandled_event` fallbacks are vague.

## Dialog scripts

Options come from `set_dialog_script id options=[{text:"...", show:true, say:true}, …]`; the script addresses them by number:

```
@S  // entry
Guard: Halt! Who goes there?
return
@1
Guard: The castle is closed to visitors.
return
@3
Ego: Goodbye.
stop
```

Speaker names are the character script names without the leading `c`. `return` goes back to the option list, `stop` ends the dialog. Start it from a handler with `dGuard.Start();`. A `narrator: text` line shows a modal box (like `Display`); use a character line when you want it non-blocking when checking in the running game.

## Common pitfalls

- `edit_script` fails when `find` matches twice. Include a comment line or the function header in `find`.
- A GlobalScript header change (`import` line) makes every room script depend on it; run both `compile` (modules) and `compile name=roomN`.
- A room script that does not compile blocks that room's save (`save.saved=false`). The tool result tells you the line.
- Character script names derive a macro (`cGuard` → `GUARD`); naming a view `GUARD` clashes. Prefix views with `v`.
- Walking the player to a spot *in front of* an object hides the object and steals clicks from it. Stand beside.
- `Display()` blocks until clicked; in the running game use `game_click` to dismiss it, and consider `player.Say` for lines that do not need a modal box.
- Exit regions fire the moment a character stands on them, including the spawn position after `ChangeRoom`. Spawn a few pixels away from the exit region.
- Objects default to `Baseline 0` (= use their y). Set `Baseline` on a wall-mounted object (a picture, an open cabinet door) so characters walking below do not draw behind it.
