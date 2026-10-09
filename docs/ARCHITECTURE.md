# AGS MCP Server: architecture

This document describes how the server is built, why it is built that way, and the verified facts about AGS internals it relies on.
The tool reference is in [README.md](../README.md#tools). When you change behaviour or verify a new AGS fact, update this file.

## Scope

- **Target:** the AGS **3.6.x** editor (developed against 3.6.2.21). AGS 4.0 is out of scope.
- **What it covers:** project creation, scripts, game data, rooms, assets (sprites, views, audio clips), building and running the game, and driving the running game the way a player would.
- **Clients:** any standards-compliant MCP client (Claude Code, Codex, Gemini CLI, …; stdio-only clients through `mcp-remote`). Only plain MCP is used, with no client-specific extensions, and tool schemas avoid `anyOf`/`oneOf`/`$ref` so clients with a restricted schema subset can use every tool.
- **Install:** copying DLLs into the AGS folder. There is no stdio shim and no extra runtime.
- **Deployment:** local use only. The server listens on loopback.

## Overview: two DLLs

```
MCP client ───HTTP (MCP Streamable HTTP, JSON responses)──► AGS.Plugin.Mcp.dll  (inside AGSEditor.exe)
                                                              │  IAGSEditor / AGS.Types / AGSEditor.Instance
                                                              │  launches the game exe with AGSMCP_PORT env var
                                                              ▼
                                                  agsmcp.dll (native engine plugin, inside the running game)
                                                  ◄── loopback TCP, line-delimited JSON ──┘
```

| Component | Source | Language and build | Role |
|---|---|---|---|
| `AGS.Plugin.Mcp.dll` | `src/AgsMcp.Editor` | C# 7.3, .NET Framework 4.6, AnyCPU, SDK-style csproj | Hosts the MCP server inside the editor. Every tool lives here. |
| `agsmcp.dll` | `src/AgsMcp.Engine` | C++14, **x86**, CMake with MSVC (VS 2022) | Runs inside the launched game and executes player-simulation commands. Optional. |

## Editor plugin (`AGS.Plugin.Mcp.dll`)

### Packaging: one DLL, no dependencies
- The editor loads plugins with `Assembly.LoadFile`, so no other DLL next to the plugin would resolve. That rules out the official MCP C# SDK and every other NuGet package.
- The plugin references only what the editor ships: `AGS.Types.dll`, `AGSEditor.exe` and the bundled `Newtonsoft.Json.dll` 13.0.1, all with `Private=false`.
- `System.ValueTuple` is not available on net46, so the code uses small classes or anonymous types in place of tuples.

### Loading
- The editor loads `AGS.Plugin.*.dll` from its install folder (`Editor/AGS.Editor/Components/PluginsComponent.cs`).
- `McpPlugin` (`Plugin.cs`) implements `IAGSEditorPlugin`, carries `[RequiredAGSVersion("3.6.0.0")]`, and its `IAGSEditor` constructor adds `McpComponent`.
- The constructor runs during the splash screen, before any game loads, so `CurrentGame` can be null. `RefreshDataFromGame` fires after a game loads.

### MCP transport (`Mcp/`)
- A hand-written MCP layer on a loopback `TcpListener` HTTP/1.1 server at `http://127.0.0.1:7471/mcp`, on IPv4 and IPv6. `TcpListener` was chosen over `HttpListener` so that http.sys URL ACLs and admin rights never come into play.
- Methods: `initialize`, `ping`, `tools/list`, `tools/call`, and empty `resources/*` and `prompts/*` lists. Notifications get 202.
- Responses are plain `application/json`, with no SSE. `GET /mcp` returns 405 and `GET /` returns a status page.
- Requests with a non-local `Origin` header (including `null`) get 403, which blocks DNS rebinding.
- Every tool carries `readOnlyHint` or `destructiveHint` annotations. Bad input is reported by throwing `ToolException`.
- The `Mcp/` folder has no editor dependency, so it is unit-tested directly.

### Threading (`Ui/UiDispatcher.cs`)
- Tool calls are serialized: one at a time, behind a semaphore.
- Tools with `RunOnUiThread = true` (the default) run on the **editor's main-window thread**. The dispatcher looks up `GUIController._mainForm` on every call, because the form does not exist yet when the plugin is constructed. Until it exists, work falls back to a hidden control created on the constructing thread.
  - **Why:** the editor's model, panes and native room and sprite state all belong to that thread. An earlier design ran tool work on the HTTP worker thread. It raced the editor's own UI, could not touch the project tree or open panes ("called from the wrong thread"), and corrupted the native room (see [Rooms](#rooms)).
- If a modal dialog is open on the UI thread, the call is refused with a "dialog box open" error. That covers a modal `Form` and a visible Win32 dialog (class `#32770`, such as a `MessageBox`, which is not in `Application.OpenForms`). Otherwise the queued work would run re-entrantly inside the dialog's message loop.
- If a call times out, the work stays queued, and the gate is released only once that work actually finishes.
- Every tool call runs with a plain base `SynchronizationContext` installed (restored afterwards). `AGSEditor.SaveGameFiles()` reaches `TaskScheduler.FromCurrentSynchronizationContext()` through the recent-games `Debouncer`, which throws on a null context and aborted saves before any file was written. A `WindowsFormsSynchronizationContext` does not work: WinForms uninstalls it when a form handle is destroyed mid-save (the save progress dialog).
- Tools that only talk to the game process or read its log (`stop_game`, `game_status`, `get_game_log` and every `game_*` tool) set `RunOnUiThread = false`. They can be polled while a build runs and never block the editor.

### Editor UI and settings
- An **MCP** menu: Start/Stop, Copy endpoint URL, Client setup (a dialog with the setup for Claude Code, Codex, Gemini CLI, generic HTTP and `mcp-remote`, built by `Ui/ClientSetup.cs` from the current port), Status, change port, open the plugin log.
- Settings live in `%APPDATA%\AGS-MCP\settings.json` and the log in `%APPDATA%\AGS-MCP\plugin.log`. The server starts automatically by default.

### Code layout and version-specific code
```
src/AgsMcp.Editor/
  Plugin.cs, McpComponent.cs, Settings.cs   plugin entry point, menu, settings
  Mcp/                                       JSON-RPC/MCP protocol and the HTTP server
  Ui/UiDispatcher.cs                         runs tool calls on the editor's main thread
  Tools/                                     the MCP tools, one file per area, plus pure parsing helpers
  Engine/                                    EngineClient (TCP to the game), KeyMap, GameWait
  EditorInternals.cs                         the only code that touches AGSEditor.exe internals
```
- Anything that needs `AGSEditor.exe` internals (non-public members, reflection, the concrete `AGSEditor`/`GUIController` types) goes only in `EditorInternals.cs`. Everything else uses the public `AGS.Types` interfaces. This keeps the code that can break on an editor upgrade in one place.
- Pure logic (script text edits, property conversion, entity-ref and mask-shape parsing, engine arguments, key mapping, wait conditions) is kept out of editor-dependent code so that it is unit-testable.

## Engine plugin (`agsmcp.dll`)

- **Files:** `agsmcp.cpp`, `json_min.h`, and `agsplugin.h` vendored from the 3.6.2 source (plugin API v30).
- **JSON:** a self-contained ~300-line reader/writer (`json_min.h`). The wire protocol is tiny and both ends are ours, so the plugin stays dependency-free.
- **Exports:** `AGS_PluginV2`, `AGS_EngineStartup`, `AGS_EngineOnEvent`, `AGS_EngineShutdown`, `AGS_GetPluginName`, `AGS_EditorStartup`, `AGS_EditorShutdown`. The editor exports are required for the plugin to appear in, and be enabled from, the project's Plugins node. Exports are `extern "C" __declspec(dllexport)` with default `__cdecl`, which keeps them undecorated on x86 (verified with dumpbin); AGS looks them up by exact name.
- **Inactive unless `AGSMCP_PORT` is set.** Only the editor plugin's launcher sets it, so a shipped game that still contains the plugin stays inert. The engine port is 7470; the editor's MCP port is 7471.

### Threading and protocol
- A worker thread runs a loopback TCP server that accepts line-delimited JSON requests (`{id, cmd, args}`) and **only queues** them.
- The main thread drains the queue in `AGSE_PRERENDER`, which fires every frame, even during a blocking `Wait`, walk or dialog. `IAGSEngine` access is only safe on the main thread.
- Each request is a `shared_ptr<Request>` with its own condition variable. The worker waits up to 10 s for the main thread to fill the result, so a timed-out request is never freed while the main thread still uses it.
- Every event handler returns 0, because a non-zero return stops the plugin hook chain.
- `ENTERROOM`, `LEAVEROOM` and `POSTRESTOREGAME` hooks feed a small recent-events log that `game_state` returns.
- Editor side: `Engine/EngineClient.cs` is a reconnecting TCP client.

### Commands
| Command | Implementation |
|---|---|
| `state` | `GetCurrentRoom`, `GetPlayerCharacter`, `GetCharacter(i)`, `GetObject(i)`, `GetMousePosition`, `GetGameOptions` (disabled-UI and cutscene flags), `IsGamePaused`, `IsInterfaceEnabled`, plus the event log. |
| `screenshot` | `SaveScreenShot("$SAVEGAMEDIR$/agsmcp.bmp")`, path resolved with `ResolveFilePath`. The editor converts the BMP to PNG and returns MCP image content. |
| `click` | `SetMousePosition` then `SimulateMouseClick`. Real SDL input: it reaches `on_mouse_click` and the GUIs, but moves the OS cursor. |
| `process_click` | `SetActiveInventory` (optional), `SetCursorMode`, `RoomToViewport`, `SetMousePosition`, `SimulateMouseClick(1)`. See below. |
| `key` | `Game::SimulateKeyPress`, except Backspace, Tab and Enter, which are posted as `WM_KEYDOWN`/`WM_KEYUP` to `GetWindowHandle()`. See below. |
| `hover_name` | `GetLocationName` into a buffer. |
| `get_global_int` / `set_global_int` | `GetGlobalInt` / `SetGlobalInt`. |
| `call` | `QueueGameScriptFunction` (GlobalScript only, 0–2 int args). |

- **`process_click` queues a real click; it never calls `Room.ProcessClick`.** Commands run inside `AGSE_PRERENDER`. Calling `ProcessClick` there ran blocking handlers (`Display`, `Say`, `Walk eBlock`) as a nested game loop inside the render callback, which timed the request out and wedged later interactions. A queued click makes the game's own `on_mouse_click` run the interaction on the next frame, exactly as for a player.
- **Backspace, Tab and Enter bypass `SimulateKeyPress`**, because `Game_SimulateKeyPress` (`Engine/ac/game.cpp`) reinterprets codes 1–26 as Ctrl+A..Z. Those keys arrived as Ctrl+H/I/M.

## Subsystems and design decisions

### Persistence model
- **`Game.agf`-level changes** (game data, sprites, views, plugins, settings, new script modules' registration) are made to the editor's in-memory model and written by `save_project`. `AGSEditor.SaveGameFiles()` writes both `Game.agf` and `acsprset.spr`.
- **Script text** is written to disk at once (`Script.SaveToDisk()`), in CRLF, the editor's convention.
- **Room edits** are saved at once through the editor's own room save path (see [Rooms](#rooms)).
- Unsaved model changes are lost when the editor restarts.

### Scripts
- `write_script`/`edit_script` set `Script.Text`, save to disk, call `RebuildAutocompleteCache`, then refresh any open script tab. Setting `Script.Text` alone does not update an open tab (`Editor/AGS.Editor/Panes/ScriptEditor.cs`). The refresh raises `GUIController.OnScriptChanged` through its backing delegate by reflection, because a plugin cannot invoke an event. It is best-effort and never fails the tool, since the change is already on disk.
- `edit_script` is an exact find/replace that is newline-agnostic: it normalises the file text before matching. The editor's `ScriptGeneration.InsertFunction` (used when binding events) appends CRLF stubs, so files can otherwise end up with mixed line endings.
- **`compile` checks scripts module by module**, not with `CompileGame`. It calls `AGSEditor.Instance.CompileScript(script, GetImportedScriptHeaders(script), messages)`, the embedded compiler, after `RegenerateScriptHeader(null)` so that entities created since the last save are defined. It is fast, has no side effects (no asset packaging, no exe) and matches AGS's own model of compiling each module with its preceding headers. Errors in a shared header surface once per importing module, so results are deduplicated on (severity, file, line, message). A full build is `build_game`.
- Room scripts are addressed as `roomN` by the script tools; `compile name="roomN"` loads the room and compiles it with its generated header.
- `create_script_module` writes the `.asc`/`.ash` pair, adds a `ScriptAndHeader` to `RootScriptFolder.Items` and sets `FilesAddedOrRemoved`. It does not refresh the project tree; the module appears there after the next refresh, and `save_project` registers it in `Game.agf`.
- `script_api_lookup` searches `AutoCompleteData` across the built-in `agsdefns.sh` and the project's headers, including `///` doc comments.
- `find_usages` is a whole-word `\bname\b` text search over script modules, room scripts and dialog scripts. The editor's `FindAllUsages` opens an interactive results pane instead of returning data.

### Game data
- **One generic property mechanism.** `get_properties`/`set_properties` reflect over any `AGS.Types` object with `TypeDescriptor.GetProperties`, so the editor's own `[Browsable]`, `[Category]`, `[Description]`, `[DisplayName]`, `[ReadOnly]` attributes and `ICustomTypeDescriptor` (used by Settings) apply for free, with the property grid's semantics and one code path.
- **An entity registry** (`Tools/EntityRegistry.cs`) maps a `type` string to how to list, identify, create and delete that entity. `id` accepts the numeric ID or the script name. Types: character, inventory, dialog, gui, view, cursor, font, audioclip, audiocliptype, audiofolder, globalvariable, customproperty, setting. Create and delete cover character, inventory, dialog, gui, view, cursor, audiocliptype, audiofolder, globalvariable and customproperty. Audio clips are created and deleted by the audio tools (see [Audio](#audio)), which also need the file copy.
- Create and delete work on the model and renumber IDs exactly as the editor's components do. `set_properties` enforces script-name uniqueness, including the macro AGS derives from character and GUI names (`cGuard` defines `GUARD`). A global variable cannot be renamed through `set_properties`, because its name is its dictionary key; delete and recreate it.
- A kind can add two hooks. `PrepareProperty` checks or normalises a value before it is applied; the audio kinds use it to take a type name for `Type`/`DefaultType` and to keep names unique. `AfterChange` runs after a create, a set or a delete; the audio kinds use it to refresh the Audio tree.
- Apart from those hooks, these tools do not call the components' `PropertyChanged`/`RePopulateTreeView`/`RefreshPropertyGrid`, so open panes show the change after a reload. They were written when tool work could not touch editor UI; now that it runs on the main-window thread, wiring in those refreshes is possible.
- `get_dialog_script`/`set_dialog_script` cover the dialog-language text and the options. `set_event` binds character and inventory events and adds a stub to GlobalScript; `get_properties` lists an entity's events.

### Rooms
- **Rooms are only loaded, saved and unloaded through the editor's `RoomsComponent` (the `IRoomController`), never with a raw `NativeProxy.LoadRoom`.** The native side holds exactly one room (`agsnative.cpp`'s global `RoomStruct thisroom`), and every `LoadRoom` loads into it in place. Raw loads overwrote the struct under the editor's open room pane while it painted, garbling renders and crashing the editor with heap corruption (`0xc0000374` in ntdll, no managed exception).
- `RoomSession.Open` makes the requested room the editor's loaded room, unloading the previous one without a prompt and refusing if it has unsaved changes.
- **Room edits are written immediately.** Every room-mutating tool saves through the editor's private `SaveRoomButDoNotShowAnyErrors`, which regenerates the room header, compiles the room script into the `.crm` and writes it. `RoomSession.Save` then unloads the room and, if its tab was open, reopens it so the tab never shows stale lists. A room is its own file and the editor holds one room at a time, so pending edits would block every later room switch.
- If the room script does not compile, the room is not written: the change stays on the editor's loaded room and the result carries `save.saved=false`, the compile messages and a hint.
- **`render_room` mask overlays are drawn in managed code.** Each area of `ExportAreaMask` is blended over `GetRoomBackgroundForPreview` in a fixed distinct colour, and the text names them ("Areas: 1=red, 2=lime"). The native `DrawRoomBackground` colours areas through the global palette (area 1 was invisible on a true-colour room) and draws into the buffer the editor's room pane also uses.
- `set_room_event` binds `room`/`hotspot:N`/`object:N`/`region:N` events and adds a stub to the room script.

### Assets
- **Sprite image data goes through the native sprite set** (`NativeProxy.CreateSpriteFromBitmap`/`ReplaceSpriteWithBitmap`/`GetSpriteBitmap`/`DeleteSprite`, by reflection in `EditorInternals`), not `IAGSEditor.CreateNewSprite`/`ChangeSpriteImage`. The public methods fire `SpritesUpdated`, whose subscriber (the Sprite Manager pane) refreshes WinForms controls. The tools do the folder mutation themselves and mirror the editor's field fix-ups (`AlphaChannel=false` below 32-bit, `SourceFile=""`). A new sprite shows in the Sprite Manager on its next refresh.
- **`delete_sprite` keeps the editor's in-use check** (`SpriteTools.GetSpriteUsageReport`) and refuses with the report, but skips the `PreDeleteSprite` component event. The usage report covers views, characters and GUIs; uses in text scripts and rooms are not detected, as the editor itself warns.
- **`create_view` checks that every referenced sprite exists** (`NativeProxy.DoesSpriteExist`) before allocating a view ID, so a bad request leaks no half-built view. Its schema declares exactly one form per level, a loop `{frames, runNextLoop?}` and a frame `{sprite, delay?, flipped?, sound?}`, with no `anyOf`/`oneOf`, so clients that validate arguments or restrict schema features (Gemini-based ones, for example) can follow it. The parser also accepts the older shorthand (a loop as a bare frame array, a frame as a bare sprite number) so existing callers keep working. Default transparency is `LeaveAsIs` and default `alpha` is true.

### Audio
- **`import_audio`/`replace_audio`/`delete_audio` (`Tools/AudioTools.cs`) rebuild the editor's own audio import with public `AGS.Types` members** instead of invoking `AudioComponent`'s private `CreateAudioClipForFile`/`ImportAudioFiles`. Those show a modal error box on a bad file and add the clip to `_folders[_rightClickedID]`, which is null until the user right-clicks the tree. The steps are the editor's:
  1. `new AudioClip(name, game.GetNextAudioIndex())` with `ID = RootAudioClipFolder.GetAllItemsCount()`, and `Type`/`BundlingType` from the target folder's defaults unless given.
  2. Copy the source to `AudioCache\au{Index:X6}{ext}`, and set `SourceFileName` (project-relative when inside the game folder), `FileType`, `CacheFileName` and `FileLastModifiedDate`.
  3. `folder.Items.Add`, `FilesAddedOrRemoved = true`, delete `Compiled\Data\audio.vox`, and `AudioClipTypeConverter.SetAudioClipList(...)`.
- **This replaces hand-patching `Game.agf`.** Before these tools, an agent had to add `<AudioClip>` nodes and bump `<AudioIndexer>` by hand, copy the cache file, then reload the project without letting the editor save over the edit.
- **The audio tools refresh the editor's Audio tree** (`IRePopulatableComponent.RePopulateTreeView` on the component with ID `"AudioNew"`) and regenerate the script header, so a new `aName` autocompletes at once. That makes them an exception to the "no tree refresh" gap.
- **Sources stay where they are.** A `path` import does not copy the file into the project. A `base64` upload is written to `<game>\Audio\<fileName>` and never overwrites an existing file, except the clip's own source on `replace_audio`.
- **`replace_audio` keeps `ScriptName`, `ID` and `Index`.** Without a new file, it re-copies the current source into the cache, as the editor's "Force reimport" does.
- **`delete_audio` refuses while the clip is in use**, unless `force=true`. Uses are view frames whose `Sound` is the clip's `Index`, `Settings.PlaySoundOnScore`, and a whole-word script name match in modules, headers and room scripts. Like the editor, it decrements higher clip `ID`s and deletes the cache file. Indexes never change.
- **Audio types are the `audiocliptype` entity kind.** `TypeID` is `[ReadOnly]`, so `set_properties` cannot change it. Create appends `Count + 1`. Delete refuses for the last type, a type used by a clip, or a folder's `DefaultType`; otherwise it renumbers higher `TypeID`s on the types, clips and folders. The editor does not renumber folder defaults.
- **Audio folders are the `audiofolder` entity kind, addressed by name.** Their defaults are not just tree organisation. At build time a clip whose volume, priority or repeat is `Inherit` takes the nearest folder's value, and new clips take `DefaultType`/`DefaultBundlingType`.
  - Create takes `Name` and an optional `Parent` (default: the root). It uses `parent.CreateChildFolder(name)`, so the new folder starts with the parent's `DefaultType` and `DefaultBundlingType`, as in the editor.
  - Names are kept unique across the tree, so a folder stays addressable. The editor itself allows duplicates; with duplicates, the first match depth-first wins.
  - Delete refuses the root and any folder that still has clips or subfolders. `DefaultVolume` must be 0..100.
- **Making sound is the skill's job.** `skills/ags-mcp/scripts/sfx.py` synthesises WAVs (numpy: oscillators, modal synthesis, FFT-shaped noise, reverb, seamless-loop helpers, checks, spectrogram previews), and the agent imports them. The server stays a thin editor bridge.

### Build and run
- **`build_game` runs `CompileGame` on the main-window thread with `MessageBoxOnCompile = Never`**, then restores the setting. `CompileGame` touches the output panel and can pop a modal "compilation errors" box (`ReportErrorsIfAppropriate`) that would hang an unattended session. Messages come back in the same shape as `compile`.
- **`run_game` builds and launches the full standalone exe** (`Compiled\Windows\<name>.exe`), not the editor's `_Debug` mini-exe. The mini-exe needs `--runfromide <Compiled\Windows> <asset-dir maps> <exe>` to find its assets; the standalone exe runs with no path mapping.
- Launch arguments: `--windowed --background --no-message-box --log-file=all:debug --log-file-path=<temp>\ags-mcp-run --user-data-dir <temp>\ags-mcp-run`, plus `--startr <room>` when asked, with `AGSMCP_PORT` in the environment. `get_game_log` tails `<temp>\ags-mcp-run\ags.log`.
- `GameRunner` owns the single launched process; `run_game` stops any previous game first.

### Player simulation
- **Prerequisite:** `agsmcp.dll` deployed next to `AGSEditor.exe`, enabled for the project with `runtime_enable_plugin`, then `save_project`. The Windows build copies the DLL next to the exe. Untick it in the Plugins node for release builds.
- **`runtime_enable_plugin` flips the editor's `NativePlugin.Enabled`**, not just `Game.Plugins`. `PluginsComponent.BeforeSaveGame` rebuilds `Game.Plugins` from the enabled `NativePlugin` wrappers on every save, so a bare `Game.Plugins.Add` would be wiped. The tool reflects the Plugins component's private `_plugins`, enables the `agsmcp.dll` entry (which calls its `AGS_EditorStartup`) and mirrors it into `Game.Plugins` for immediacy. There is no disable tool.
- **`game_wait_until` requires the condition to hold for `stableMs`** (default 400). The interface is briefly re-enabled between consecutive speech lines and dialog steps, so a first-match wait let the next click land mid-conversation. Conditions: `room`, `interfaceEnabled`, `idle`, and `ready` (interface enabled and player idle). The evaluator is pure (`Engine/GameWait.cs`) and sleep-polls `game_state`.
- `game_key` maps names ("Space", "F5", letters, digits, arrows) or raw codes to AGS `eKey` codes (`Engine/KeyMap.cs`).

## Known gaps

- No `delete_room` or room renumbering.
- GUI **controls** cannot be created, deleted, read or written (GUIs themselves can).
- Fonts cannot be created (they are backed by resource files); list, get and set work.
- Audio clips and folders cannot be moved between folders.
- 8-bit backgrounds cannot be imported into 256-colour games.
- No automatic backup of `Game.agf` before the first write of a session.
- Game-data, sprite and new-module changes do not refresh the editor's project tree or open panes until the next reload. The audio tools and the audio entity kinds are the exception.
- `open_project` cannot reopen the game that is already open (`_OpenInEditor.lock`); reloading from disk means restarting the editor.
- The editor-debugger named pipes (`--enabledebugger <token>`) are not hosted, so runtime errors come only from the engine log, without call stacks.

## Testing

- **Unit tests:** `.\build.ps1 -Test` (xUnit, `tests/AgsMcp.Editor.Tests`). They cover JSON-RPC handling, the HTTP transport, script text edits, property reflection and conversion, room entity refs, mask shapes and polygon scanlines, sprite transparency and view parsing, audio file types, cache names, script names and usage matching, engine arguments and log slicing, key mapping and wait conditions.
- **Live check:** run `.\build.ps1 -Deploy -Run`, then call the tools over HTTP:
  ```powershell
  Invoke-WebRequest http://127.0.0.1:7471/mcp -Method Post -ContentType application/json `
    -Body '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"project_info","arguments":{}}}'
  ```
  Or use the MCP Inspector CLI:
  `npx -y @modelcontextprotocol/inspector --cli http://127.0.0.1:7471/mcp --transport http --method tools/list`
  When a call fails, read `%APPDATA%\AGS-MCP\plugin.log`.
- **Smoke test:** `tests/smoke/smoke.ps1`, run after `.\build.ps1 -Engine -Deploy -Run`. It creates a throwaway game from the Sierra-style template in a temp folder (`create_project`) and drives every tool group against it over HTTP: a compile error and its fix, a property round-trip, room authoring on a probe room, `render_room`, `import_sprite`, `create_view` in its schema form, the audio tools (import by path and base64, an `audiocliptype` round-trip, an `audiofolder` with defaults that a clip is imported into and that cannot be deleted while it holds the clip, the in-use refusal, a script that plays the clip, `replace_audio`, `delete_audio`), a character event, dialog options, `build_game`, `run_game`, the `game_*` tools including a queued `game_process_click` and `game_wait_until ready`, and `stop_game`. It reports pass/fail counts and exits non-zero on failure.
  - Because it works on its own game, no existing game is modified. Afterwards it reopens the game that was open (`open_project`, discarding the throwaway game's unsaved state) and deletes the temp folder. The game open at the start is saved first, as File > New Game does. `-Keep` leaves the throwaway game open for inspection.

## AGS reference

Verified facts about AGS 3.6.2 internals. Source paths are relative to the AGS source tree (`ags_3.6.2.21_source`).

### Editor general
- **Command line:** `AGSEditor.exe "<path>\Game.agf"` opens a project directly. `/compile <Game.agf>` builds and exits, but fails if the project is already open (`_OpenInEditor.lock`).
- `CreateScriptEditor` throws if called from a plugin constructor.
- **Menus:** `GUIController.AddMenu(component, id, title, insertAfterMenu)`, then `AddMenuItems`. The Build menu's ID is `"DebugMenuHeader"`. Clicks arrive as `CommandClick(commandId)`. `SetMenuItemEnabled` exists only on the concrete `AGS.Editor.GUIController`.
- **Built-in commands** can be triggered with `CommandClick` on the components in `IAGSEditor.Components`: `FileCommands` `"SaveGame"`; `Rooms` `"SaveRoom"`; `BuildCommands` `"CompileGame"`, `"RebuildGame"`, `"TestGame"`, `"RunGame"`.
- **Main-window thread:** `GUIController.Instance` holds a private `frmMain _mainForm`; reflect it and `Invoke` to run UI-bound work there.
- **Plugin DLL naming:** the editor treats `ags*.dll` files that don't start with `ags.` as native engine plugins. Never name a helper DLL that way.
- **Modal boxes:** `GUIController.ShowMessage` uses `MessageBox.Show`, which is not in `Application.OpenForms`.
- **Projects:** `GUIController.CreateNewGame(path, fileName, gameName, GameTemplate)` (private) extracts the template (`Tasks.CreateNewGameFromTemplate`), loads it, sets the names and a new game ID, saves and force-rebuilds. On failure it shows an error box and **deletes the folder**. `Tasks.LoadGameFromDisk(agf, interactive:false)` opens a game; it may still ask about a missing sprite file.

### Scripts
- `Game.ScriptsAndHeaders` enumerates `ScriptAndHeader` (`.Header`, `.Script`, each an `AGS.Types.Script`; `.Name` is the filename without extension). `Script` has `Text`, `FileName`, `IsHeader`, `Modified`, `SaveToDisk()` (writes `FileName` relative to the editor's working directory, which is the game folder, only if `Modified`), `LoadFromDisk()` and `AutoCompleteData`. Constants `Script.GLOBAL_SCRIPT_FILE_NAME`/`GLOBAL_HEADER_FILE_NAME`.
- `Game.RootScriptFolder.Items` is the folder's `ScriptAndHeader` list; adding there makes a module appear in `ScriptsAndHeaders`. `Game.FilesAddedOrRemoved` flags that the file set changed.
- `IAGSEditor.GetAllScriptHeaders()` returns every header in order (built-in `_BuiltInScriptHeader.ash`, auto-generated, plugin and user). `RebuildAutocompleteCache(Script)` calls `AutoComplete.ConstructCache` synchronously. `BuiltInScriptHeader`, `GetImportedScriptHeaders(script)` and `CompileScript(...)` exist only on the concrete `AGSEditor.Instance`.
- `AGSEditor.Instance.CompileScript(Script, List<Script> headers, CompileMessages)` never throws; errors and warnings go into `CompileMessages` (a `List<CompileMessage>` with `HasErrors`/`Errors`). Severity is `CompileError` vs `CompileWarning`; each has `Message`, `ScriptName`, `LineNumber` (`Editor/AGS.Types/Exceptions/`).
- **The auto-generated header is only rebuilt on save.** Call the public `AGSEditor.RegenerateScriptHeader(null)` before compiling, or new entities are "undefined symbol".
- **Autocomplete tokens (`AGS.Types.AutoComplete`):** `ScriptAutoCompleteData.{Functions, Variables, Structs, Enums, Defines}` plus `Populated`. `ScriptFunction.{FunctionName, ParamList, Type, ReturnsPointer, IsStatic, Description}`. Struct member functions carry `':'` in `FunctionName` and also appear in `ScriptStruct.Functions`, so iterate structs for members and skip `':'` names in the top-level list. `ScriptStruct.{Name, Variables, Functions}`; `ScriptVariable.{VariableName, Type, IsPointer, IsReadOnly, IsArray, IsDynamicArray, IsIndexedAttribute}`; `ScriptEnum.{Name, EnumValues}`; `ScriptDefine.Name`; `ScriptToken.Description` holds the `///` doc comment.
- **Open-tab refresh:** `GUIController` has a public event `OnScriptChanged(Script)`; `ScriptsComponent` handles it by calling `ScriptModifiedExternally()` on the open editor, only if a tab exists for that script.

### Game data
- **Collections on `Game`:** flat lists `CharacterFlatList`, `InventoryFlatList`, `DialogFlatList`, `GUIFlatList`, `ViewFlatList`, `AudioClipFlatList`; folder roots `RootCharacterFolder`/`RootInventoryItemFolder`/`RootDialogFolder`/`RootGUIFolder`/`RootViewFolder` (`BaseFolderCollection<TItem,TFolder>` with `Items`, `SubFolders`, recursive `Remove(item)`, `GetAllItemsCount()`, `AllItemsFlat`); lists `Cursors` (`IList<MouseCursor>`) and `Fonts` (`IList<Font>`); `GlobalVariables` (keyed by `Name`, with `Add`/`Remove`/`VariableRenamed`/`ToList`); `PropertySchema.PropertyDefinitions`; `Plugins` (`List<Plugin>`); `Settings`.
- **IDs and names:** `Character.ScriptName`, `InventoryItem.Name`, `Dialog.Name`, `GUI.Name`, `View.Name`, `MouseCursor.Name`, `AudioClip.ScriptName`, `Font.ScriptID`. Each has an `int ID`; inventory IDs are **1-based**, the others 0-based. ID setters exist (`[ReadOnly(true)]` only hides them from the grid), which is how delete renumbers. `GlobalVariable` and `CustomPropertySchemaItem` have no numeric ID.
- **Create:** public parameterless constructors (except `AudioClip`). Assign `ID = Root*Folder.GetAllItemsCount()` (`+1` for inventory; `FindAndAllocateAvailableViewID()` for views; `Cursors.Count`/`Fonts.Count` for the lists). Auto-name with `AGSEditor.Instance.GetFirstAvailableScriptName(prefix)` (`cChar`/`iInvItem`/`dDialog`/`gGui`). A GUI is `new NormalGUI(w,h)`. Then `Root*Folder.Items.Add` or `collection.Add`.
- **Delete:** `Root*Folder.Remove` (recursive) or `collection.Remove`, then decrement higher IDs; views call `Game.ViewDeleted(id)` instead.
- **Uniqueness:** `Game.IsScriptNameAlreadyUsed(name, ignoreObject)` checks GUIs, inventory, characters, dialogs, audio clips, views and global variables. It catches a new view named after an existing character's macro, but not a character created after such a view. Name setters call `Utilities.ValidateScriptName`.
- **Dialogs:** `Dialog.Script` (`[Browsable(false)]`) is the raw dialog-language text, stored in `Game.agf`. `Dialog.Options` is a `List<DialogOption>` (`ID` from 1, `Text`, `Show`, `Say`); at most `Dialog.MAX_OPTIONS_PER_DIALOG` (30). Speech lines in dialog scripts use the character's script name without the `c`.
- **Events:** display names for hotspots, objects and characters are cursor-mode placeholders (`"$$01 hotspot"`), so tools report the function suffix. Character events: Look, Interact, Talk, UseInv, AnyClick, PickUp, Mode8, Mode9. Inventory: Look, Interact, Talk, UseInv, OtherClick. Both default to `GlobalScript.asc`.

### Rooms
- `Game.Rooms` is `IList<IRoom>` of `UnloadedRoom` (`Number`, `Description`, `FileName`=`roomN.crm`, `ScriptFileName`=`roomN.asc`, `Script`, `LoadScript()`/`UnloadScript()`).
- **One native room:** `load_crm_file` loads into the global `thisroom` and sets `room->_roomStructPtr = &thisroom`; the loaded `Room` shares its `Script` with the project's `UnloadedRoom`, which is also what the room-script tab edits. `RoomsComponent` tracks it as `_loadedRoom`; its pane is the private `ContentDocument _roomSettings`.
- **`RoomsComponent` members used:** `IRoomController.CurrentRoom` and `.LoadRoom(IRoom)` (public; `LoadDifferentRoom` prompts "save changes?" if the room is modified, so unload first); private `UnloadCurrentRoomAndGreyOutTree()`, `LoadRoom(string controlID)` (`"Roe"+N` shows the tab), `SaveRoomButDoNotShowAnyErrors(Room, CompileMessages, string)` (requires `room == _loadedRoom`; shows a BusyDialog), and `CreateNewRoom(int, RoomTemplate)` (blank room: `new RoomTemplate(null, null, "Blank Room")`; it adds to `_folders[_rightClickedID]`, which is null until the user right-clicks the tree, so set it to the inherited `TOP_LEVEL_COMMAND_ID` for the call). `AGSEditor_PreSaveGame` saves the loaded room if modified.
- **Saving a room** runs `ScanAndReportMissingInteractionHandlers`, which reads `room.Script.AutoCompleteData`; rebuild it after inserting a stub or it warns "function not found".
- **`NativeProxy`** (internal; `typeof(AGS.Editor.Factory).Assembly.GetType("AGS.Editor.NativeProxy").GetProperty("Instance")`): `GetRoomBackgroundForPreview(Room, int) → Bitmap` (always 32-bit), `GetAreaMaskPixel(Room, RoomAreaMaskType, x, y) → int` (0 = no area), `ExportAreaMask`, `ImportAreaMask`, `DrawFilledRectOntoMask`/`DrawLineOntoMask`/`DrawFillOntoMask`, `ImportBackground`. It also has `LoadRoom`/`SaveRoom`, which must not be used (see [Rooms](#rooms) above).
- **`Room`:** `Width`, `Height`, `BackgroundCount`, `ColorDepth`, edges `LeftEdgeX`/`RightEdgeX`/`TopEdgeY`/`BottomEdgeY`, lists `Hotspots`/`Objects`/`Regions`/`WalkableAreas`/`WalkBehinds`, `Interactions`, `Properties` (`CustomProperties.PropertyValues`). `RoomHotspot`: `ID`, `Name`, `Description`, `WalkToPoint`, `Interactions`. `RoomObject`: `ID`, `Name`, `Image`, `StartX`, `StartY`, `Visible`, `Baseline`, `Interactions`. `RoomRegion`: `ID`, `UseColourTint`, `LightLevel`, `Interactions`. `RoomWalkableArea`: `ID`, `ScalingLevel`. `RoomWalkBehind`: `ID`, `Baseline`. The lists carry every slot, including ID 0 ("no area").
- **Slots:** 50 hotspots; 16 walkable areas, regions and walk-behinds; ID 0 = none. `RoomAreaMaskType`: `None, Hotspots, WalkBehinds, WalkableAreas, Regions`.
- **Masks:** the `Draw*OntoMask` methods take room coordinates and scale by the mask's resolution (walk-behinds are always 1:1; rects are inclusive). `ExportAreaMask` returns an 8-bit indexed bitmap (pixel = area). `ImportAreaMask` needs an indexed image of room or mask size and calls `validate_mask`.
- **Backgrounds:** `ImportBackground(room, index, bmp, useExactPalette, sharePalette)`; the editor passes `!Settings.RemapPalettizedBackgrounds`, `false`. Set `room.Width/Height` (and `Resolution = Real` for frame 0) first. A size change makes the native side reset all four masks; the editor then deletes extra frames and resets the edges. Images smaller than the game resolution are padded.
- **Objects:** `new RoomObject(room) { ID = Objects.Count, Name = GetFirstAvailableScriptName("oObject", 0, room) }`, with `Interactions.ScriptModule = room.Interactions.ScriptModule`. Deleting renumbers higher IDs. At most `Room.MAX_OBJECTS` (40). `StartY` is the sprite's bottom edge. AGS hit-tests objects per pixel, so thin, mostly transparent sprites are hard to click.
- **Interactions:** parallel arrays `FunctionSuffixes`, `DisplayNames`, `FunctionParameterLists`, `ScriptFunctionNames`, plus `Get`/`SetScriptFunctionNameForInteractionSuffix(suffix, fn)`. Room event suffixes: LeaveLeft, LeaveRight, LeaveBottom, LeaveTop, FirstLoad, Load, RepExec, AfterFadeIn, Leave, Unload. Handlers are named `<item>_<suffix>`, where item is `room`, the hotspot or object `Name`, or `region<ID>`. `AGS.Editor.ScriptGeneration.InsertFunction(text, name, paramList, ...)` appends a stub if absent.

### Assets
- **`NativeProxy` sprite methods:** `CreateSpriteFromBitmap(Bitmap, SpriteImportTransparency, int transColour, bool remapColours, bool useRoomBackgroundColours, bool alphaChannel) → Sprite` (picks a free slot, does **not** add it to a folder); `ReplaceSpriteWithBitmap(Sprite, Bitmap, …)`; `GetSpriteBitmap(int) → Bitmap`; `DeleteSprite(Sprite)`; `DoesSpriteExist(int)`. All lock the native sprite set. `IAGSEditor.CreateNewSprite` passes `transColour=0, remapColours=true, useRoomBackgroundColours=false`. `SpriteTools.GetSpriteUsageReport(int, Game) → string` (null = unused) is public (`AGS.Editor.Utils`).
- **Sprite model:** `Game.RootSpriteFolder` is a `SpriteFolder` with `Name`, `Sprites`, `SubFolders`, `FindSpriteByID(int, bool recursive)`, `FindFolderThatContainsSprite(int)`, `CountSpritesInAllSubFolders()` and a `SpritesUpdated` event. There is no name-to-folder lookup; recurse `SubFolders` by `Name`. `Sprite`: `Number`, read-only `Width`/`Height`, `ColorDepth`, `AlphaChannel`, `SourceFile`, `TransparentColour`. `SpriteImportTransparency`: `PaletteIndex0, TopLeft, BottomLeft, TopRight, BottomRight, LeaveAsIs, NoTransparency, PaletteIndex`.
- **View model:** `new View()`, `View.AddNewLoop() → ViewLoop`, `View.Loops`. `ViewLoop`: `RunNextLoop`, `Frames`. `ViewFrame`: `Image` (sprite number, clamped to ≥0), `Delay`, `Flipped`, `Sound` (default `AudioClip.FixedIndexNoValue`). Allocate the ID with `Game.FindAndAllocateAvailableViewID()`, then `Game.RootViewFolder.Items.Add(view)`.

### Audio
- **`AudioClip`** (`Editor/AGS.Types/AudioClip.cs`): the constructor is `(string scriptName, int fixedIndex)`, with no parameterless one.
  - `Index` is the clip's fixed ID. It never changes, `FixedIndexBase` = 1, and 0 means none. View frames' `Sound` and `Settings.PlaySoundOnScore` store it.
  - `ID` is the 0-based position in the flat list; the build writes clips by it.
  - Other members: `ScriptName` (the setter validates), `SourceFileName`, `Type` (`int`, an `AudioClipType.TypeID`), `BundlingType` (`InGameEXE`/`InSeparateVOX`), `FileType`, `DefaultVolume` (−1..100, −1 inherits), `DefaultPriority`, `DefaultRepeat` (`InheritableBool`).
  - `CacheFileName`, `FileLastModifiedDate` and the `Actual*` values are `[AGSNoSerialize]`; `ToXml` writes the date by hand.
- **Cache file:** `AudioComponent.GetCacheFileName` = `AudioCache\` + `"au" + Index.ToString("X6") + Path.GetExtension(SourceFileName)`, for example Index 26 with `x.ogg` gives `au00001A.ogg`. It is recomputed on every load (`ApplicationController._events_GamePostLoad`) and never saved. **The build packs only the cache copy** (`BuildTargetDataFile`, `DataFileWriter`).
- **Source re-copy:** before each build, `AudioComponent._agsEditor_PreCompileGame` copies a clip's source over its cache file when the source exists and the write times differ (or on a rebuild), so regenerating a source in place needs no re-import. A missing source with a missing cache stops the build. `File.Copy` keeps the write time.
- **`Game.GetNextAudioIndex()`** is `++Settings.AudioIndexer`, saved in `Game.agf`. `Game.RootAudioClipFolder` is an `AudioClipFolder` (`DefaultType`, default 1; `DefaultBundlingType`, `DefaultVolume`, `DefaultPriority`, `DefaultRepeat`; `GetAllAudioClipsFromAllSubFolders()`). `Game.AudioClips` is a `FolderListHybrid` whose flat list follows `folder.Items.Add`/`Remove` through `OnFolderChange`.
- **Extensions to `AudioClipFileType`:** `.ogg` OGG, `.mp3` MP3, `.wav` WAV, `.voc` VOC, `.mid` MIDI; `.mod`, `.xm`, `.s3m` and `.it` map to MOD.
- **Audio types:** `AudioClipType(typeID, name, maxChannels, volumeReductionWhileSpeechPlaying, backwardsCompatType, CrossfadeSpeed)`; `ScriptID` = `eAudioType` + the name without non-word characters.
  - The defaults are 1 Ambient Sound (`MaxChannels` 1), 2 Music (1, speech reduction 30) and 3 Sound (0 = unlimited).
  - `DataFileWriter` writes types by list position after a hard-coded speech type 0, so `TypeID`s must stay 1..N.
  - After a change, call `AudioClipTypeTypeConverter.RefreshAudioClipTypeList()`.
- **Script names:** `_AutoGenerated.ash` gets `import AudioClip aName;` per clip from `Tasks.RegenerateScriptHeader`. The editor names an imported file with `RemoveInvalidCharactersFromScriptName`, then `"a"` and a capitalised first letter, numbered until `IsScriptNameAlreadyUsed` is false.
- **Plugin API:** `IAGSEditor` has no audio API. The `AudioComponent` is internal; its ID is `"AudioNew"` and it implements the public `IRePopulatableComponent`. `AGS.Editor.ComponentController.Instance.Components` lists it. `AudioComponent`'s private `DeleteAudioClip` decrements higher IDs, deletes the cache file and `audio.vox`, and sets `FilesAddedOrRemoved`.

### Build and run
- `AGSEditor.Instance.CompileGame(bool forceRebuild, bool createMiniExeForDebug) → CompileMessages`. `false` makes a full standalone build; `true` makes the `_Debug` mini-exe. It starts with `ClearOutputPanel()` and ends with `ShowOutputPanel`/`ReportErrorsIfAppropriate`, which shows a modal box unless `AGSEditor.Instance.Settings.MessageBoxOnCompile` (enum in `AGS.Editor.Preferences`) is `Never`. Scripts compile inside a `BusyDialog`.
- **Paths:** `AGSEditor.OUTPUT_DIRECTORY = "Compiled"`, `DEBUG_OUTPUT_DIRECTORY = "_Debug"`, `BuildTargetWindows.WINDOWS_DIRECTORY = "Windows"`. The standalone exe is `<GameDirectory>\Compiled\Windows\<BaseGameFileName>.exe`; `BaseGameFileName` is `Settings.GameFileName` or the folder name.
- **Engine command line** (`Engine/main/main.cpp` `main_process_cmdline`; `OPTIONS.md`): `--windowed`, `--background`, `--no-message-box`; `--log-file=all:debug`; `--log-file-path=<DIR>` is a **directory** and `ags.log` is written inside it (`Engine/debug/debug.cpp` `create_log_output`); `--user-data-dir <DIR>` takes a space-separated argument while `--log-file-path=` uses `=`; `--startr <room>`; `--runfromide <compiledDir> <dir maps> <exe>` is only needed for the mini-exe.
- **Process:** `UseShellExecute=false`, working directory = the exe's folder, `EnableRaisingEvents=true`. Read the log with `FileShare.ReadWrite`, since the game keeps it open. The exit code after `Kill()` is -1.

### Engine plugin API
- **`agsplugin.h`, interface v30:** `AGSIFUNC(type)` is `virtual type __stdcall`; the engine passes `IAGSEngine*` (with `version` = 30) to `AGS_EngineStartup`. Exports are declared under `#define THIS_IS_THE_PLUGIN`.
- **Editor discovery:** `PluginsComponent` scans the editor folder for `ags*.dll` (excluding `ags.*`) **at editor startup** and wraps each as `AGS.Editor.NativePlugin` (`FileName`, `PluginName`, `Enabled`; the setter calls the DLL's `AGS_EditorStartup`/`AGS_EditorShutdown`). A freshly deployed engine plugin only appears after an editor restart. `NativePlugin` requires `AGS_GetPluginName`, `AGS_EditorStartup` and `AGS_EditorShutdown`; `AGS_EditorProperties/SaveGame/LoadGame` are optional (omitted here, so serialized data is empty). `RefreshDataFromGame` re-enables plugins from `Game.Plugins` on load. `BuildTargetWindows.CopyPlugins` copies each `Game.Plugins` DLL into `Compiled\Windows`.
- **Events:** `AGSE_PRERENDER` = 0x10000 (every frame), `AGSE_ENTERROOM` = 0x100, `AGSE_LEAVEROOM` = 0x80, `AGSE_POSTRESTOREGAME` = 0x40000. `data` for ENTER/LEAVEROOM is the room number. Hooks are requested one per call with `engine->RequestEventHook(ev)`.
- **`IAGSEngine` methods used:** `GetCurrentRoom`, `GetPlayerCharacter`, `GetNumCharacters`/`GetCharacter(i)`, `GetNumObjects`/`GetObject(i)`, `GetGameOptions`, `GetMousePosition`, `IsGamePaused`, `GetEngineVersion`, `SetMousePosition`, `SimulateMouseClick` (1 left, 2 right, 3 middle), `RoomToViewport`, `GetWindowHandle`, `GetScriptFunctionAddress`, `QueueGameScriptFunction(name, globalScript=1, numArgs, a1, a2)`, `ResolveFilePath` (v27), `Log(AGSLOG_LEVEL_INFO=5, fmt, …)` (writes into `ags.log`).
- **Structs:** `AGSCharacter` (`room, x, y, view, loop, frame, walking, animating, inv[301], activeinv, name[40], scrname[20], on`), `AGSObject` (`x, y, num, baseline, on, moving, view, loop, frame`), `AGSGameOptions` (`score, disabled_user_interface, in_cutscene, fast_forward, room_width, room_height`).
- **Script functions** (exact registered names, resolved on the first PRERENDER): `Game::SimulateKeyPress` (`void(int)`), `SaveScreenShot` (`int(const char*)`, writes BMP by extension), `SetCursorMode` (`void(int)`), `SetActiveInventory` (`void(int)`), `GetGlobalInt` (`int(int)`), `SetGlobalInt` (`void(int,int)`), `IsInterfaceEnabled` (`int()`), `GetLocationName` (`void(int x, int y, char* buf)`). `GetScriptFunctionAddress` returns the plain `__cdecl` engine function from `simp_for_plugin` (the `(void*)FN_NAME` half of `API_FN_PAIR`), so it can be called directly.
- **eKeyCode values** (`Editor/AGS.Editor/Resources/agsdefns.sh`): `eKeyA`=65..`eKeyZ`=90, `eKey0`=48.., Return=13, Escape=27, Space=32, F1=359..F10=368, F11=433, F12=434, Up=372, Left=375, Right=377, Down=380, Home=371, End=379, PageUp=373, PageDown=381, Insert=382, Delete=383.

### Where to look in the AGS source
- **Editor plugin API:** `Editor/AGS.Types/Interfaces/` (`IGame`, `IRoomController`, `ILoadedRoom`, `IGUIController`) and `Editor/AGS.Types/Plugins/IAGSEditor.cs`.
- **Component IDs:** `Editor/AGS.Editor/Components/ComponentIDs.cs`. Each component's command IDs are the `*_COMMAND` constants in its file.
- **Game launch and debugger:** `Editor/AGS.Editor/Tasks.cs` (`TestGame` shows the engine arguments) and `Editor/AGS.Editor/Debugger/`.
- **Script API and docs:** `Editor/AGS.Editor/Resources/agsdefns.sh`, parsed by `Editor/AGS.Editor/AutoComplete.cs` into `Editor/AGS.Types/EditorFeatures/AutoComplete/`.
- **Engine plugin API:** `Engine/plugin/agsplugin.h`. Script functions that can be looked up by name are registered in `Engine/ac/*.cpp` (`API_FN_PAIR`).
- **Engine command-line options:** `OPTIONS.md`.

## Development environment

- **Machine-specific paths** live in the git-ignored `Local.props` (template: `Local.props.example`): `AgsDir`, `AgsSourceDir`. `Directory.Build.props` imports it and fails the build with a clear message when `AgsDir` has no `AGSEditor.exe`; `build.ps1` reads it through `tools/LocalProps.ps1`. Command-line values override it.
- **Releases:** `build.ps1 -Engine -Package` writes `dist/ags-mcp-<version>.zip` (both DLLs, `install.ps1`, the skill without its evals, README, LICENSE). `.github/workflows/release.yml` does the same on a `v<version>` tag against the official AGS 3.6.2 zip (checksum-verified) and publishes the GitHub release. The editor plugin references `Microsoft.NETFramework.ReferenceAssemblies` (build-time only) so the net46 build does not need the 4.6 targeting pack.
- **Installer:** `install.ps1` runs under Windows PowerShell 5.1 through `irm | iex`, so it keeps all logic in functions, never calls `exit`, and leaves the caller's preferences alone. It finds AGS through a running editor, the uninstall registry keys and `Program Files` (a portable AGS needs `-AgsDir`), refuses while that editor runs, and `Unblock-File`s the copied DLLs.
- AGS release: https://github.com/adventuregamestudio/ags/releases/tag/v3.6.2.21. Use `AGS-3.6.2.21-P11.zip` for the editor and `ags_3.6.2.21_source.zip` for the source.
- **Engine plugin build:** `build.ps1 -Engine` wraps `cmake -S src\AgsMcp.Engine -B build -A Win32` and `cmake --build build --config Release`. VS 2022 bundles CMake at `...\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe`. `-Deploy` copies `agsmcp.dll` into the editor folder next to `AGS.Plugin.Mcp.dll`.
- **Agent skill:** `skills/ags-mcp/` (SKILL.md, references, `scripts/pixelart.py` and `scripts/sfx.py`) teaches working on the user's open game through these tools and checking changes in it with the `game_*` tools. It never creates a separate test game, and its text is client-neutral (tool names without a client prefix). Agents that load `SKILL.md` skills get it from their skills folder (`install.ps1 -InstallSkill [-SkillDir]`); others are pointed at it from their instructions file. Its eval harness lives in `skills/ags-mcp-workspace/` (git-ignored).
