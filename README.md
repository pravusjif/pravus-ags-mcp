# AGS MCP Server

An [MCP](https://modelcontextprotocol.io) server that runs **inside the Adventure Game Studio 3.6 editor**
as a plugin. It lets any MCP client (Claude Code, Codex, Gemini CLI, …) work with the project you have open in the editor:
read and write scripts, edit game data, rooms and assets, build and run the game, and **drive the running
game** the way a player would.

It ships as two DLLs that you copy into the AGS folder — nothing else to install:

- `AGS.Plugin.Mcp.dll` — the editor plugin. Hosts the MCP server over localhost HTTP.
- `agsmcp.dll` — an optional native engine plugin. Only needed for the player-simulation (`game_*`) tools;
  inert unless the editor plugin launches the game.

## Status

The server covers project creation, scripts, game data, room authoring (backgrounds, masks, objects, events),
assets, build/run and player simulation. The architecture, design decisions, known gaps and verified AGS facts are
in [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Platform support

**Windows only.** The AGS editor is a Windows (.NET Framework) application, and AGS publishes no editor for
macOS or Linux, only engine builds. The editor plugin runs inside the editor, and the engine plugin is a Windows
x86 DLL loaded by the Windows build of your game, so neither runs natively on macOS. Your MCP client can run
anywhere that can reach `127.0.0.1:7471` on the machine running the editor; in practice, that same Windows machine.

## Install

You need AGS 3.6.x. The portable zip from the [AGS releases page](https://github.com/adventuregamestudio/ags/releases)
works without admin rights. **Close the AGS editor before installing**: it locks the plugin DLL while it runs.

### One-line installer (PowerShell)

```powershell
irm https://raw.githubusercontent.com/pravusjif/pravus-ags-mcp/main/install.ps1 | iex
```

It downloads the latest release, finds your AGS 3.6 editor, copies both DLLs next to `AGSEditor.exe` and prints
the client setup. It finds an editor installed with the AGS installer, or one that is running. For a portable AGS,
or to pass any other option, run it like this:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/pravusjif/pravus-ags-mcp/main/install.ps1))) -AgsDir C:\AGS-3.6.2
```

| Option | What it does |
|---|---|
| `-AgsDir <folder>` | The AGS folder (the one with `AGSEditor.exe`). |
| `-Version 0.1.0` | Install that release instead of the latest. |
| `-Source <zip or folder>` | Install from a release zip or unpacked release you already have. |
| `-InstallSkill [-SkillDir <folder>]` | Also install the agent skill (below) into a skills folder; default `~\.claude\skills` (Claude Code). |
| `-Uninstall` | Remove the plugin DLLs from the AGS folder. |

An AGS folder under `Program Files` needs an elevated (Administrator) PowerShell.

### Manual install

1. Download `ags-mcp-<version>.zip` from this repo's [Releases](https://github.com/pravusjif/pravus-ags-mcp/releases) and unpack it.
2. Copy `AGS.Plugin.Mcp.dll` into the folder that contains `AGSEditor.exe`. For the `game_*` player-simulation
   tools, also copy `agsmcp.dll` into the same folder. (Or, from the unpacked zip, run
   `powershell -ExecutionPolicy Bypass -File .\install.ps1 -AgsDir <folder>`, which does the same.)
3. If Windows marked the files as downloaded from the internet, unblock them (`Unblock-File <dll>`, or file
   **Properties > Unblock**); otherwise .NET may refuse to load the plugin.

### Connect your client

Start the editor and open your game. A new **MCP** menu appears, and the server starts on `http://127.0.0.1:7471/mcp`.

The server works with any MCP client; it uses only plain MCP. **MCP > Client setup** in the editor shows the setup
below with your current port, and **MCP > Copy endpoint URL** copies the URL.

- **Clients with Streamable HTTP support:** use the URL `http://127.0.0.1:7471/mcp`.
- **Claude Code:** run `claude mcp add --transport http ags http://127.0.0.1:7471/mcp`. This repo's `.mcp.json`
  already configures it for sessions started here.
- **Codex CLI:** add to `~/.codex/config.toml`:

  ```toml
  [mcp_servers.ags]
  url = "http://127.0.0.1:7471/mcp"
  ```
- **Gemini CLI:** add to `~/.gemini/settings.json` (or `.gemini/settings.json` in the game folder):

  ```json
  { "mcpServers": { "ags": { "httpUrl": "http://127.0.0.1:7471/mcp" } } }
  ```

  The Codex and Gemini configurations follow those tools' documented formats but have not been tested against this
  server yet.
- **Clients that only start local servers over stdio** (for example a `claude_desktop_config.json`-style
  config): bridge to the HTTP endpoint with [`mcp-remote`](https://github.com/geelen/mcp-remote), which needs
  Node.js. No extra flags are needed for a `127.0.0.1` URL.

  ```json
  {
    "mcpServers": {
      "ags": {
        "command": "npx",
        "args": ["-y", "mcp-remote", "http://127.0.0.1:7471/mcp"]
      }
    }
  }
  ```

The server only listens on loopback and rejects browser requests from non-local origins. You can change the port
from the MCP menu; settings are saved in `%APPDATA%\AGS-MCP\settings.json`. The plugin log is
`%APPDATA%\AGS-MCP\plugin.log`.

### Player simulation

To use the `game_*` tools you must, once per project: deploy `agsmcp.dll` (above), call `runtime_enable_plugin`,
then `save_project`. `run_game` then builds the game with the plugin baked in and launches it; the `game_*` tools
talk to it over a private loopback port. Untick the plugin in the project's Plugins node (or don't ship `agsmcp.dll`)
for release builds.

### Agent skill

`skills/ags-mcp/` teaches a coding agent to work on the game you have open through these tools: the persistence
rules, recipes for rooms, objects, characters, dialogs and scripts, the pitfalls worth knowing, AGS 3.6 scripting
patterns, checking changes in the running game with the `game_*` tools, and a small PIL helper for pixel art. It is
a standard `SKILL.md` folder (instructions plus `references/` and `scripts/`), so:

- **Agents that load `SKILL.md` skills** (Claude Code, and others that support the format): put the folder in the
  agent's skills directory. `install.ps1 -InstallSkill` does this for Claude Code (`~\.claude\skills`); add
  `-SkillDir <folder>` for another agent's skills directory. For one game only, copy it into that game's own skills
  folder instead (for Claude Code, `<game>\.claude\skills\ags-mcp\`).
- **Other agents:** copy the folder anywhere and point the agent's instructions file in your game folder
  (`AGENTS.md`, `GEMINI.md`, …) at it:

  ```markdown
  This is an Adventure Game Studio game, edited through the `ags` MCP server. Before working on it,
  read C:\path\to\ags-mcp\SKILL.md and follow it; read its references/ files when it says to.
  ```

## Build from source

You need the .NET SDK and the .NET Framework 4.6+ reference assemblies (Visual Studio 2022 provides both). The
native engine plugin additionally needs the **C++ CMake tools for Windows** (also from the VS 2022 installer).

First copy `Local.props.example` to `Local.props` and set `AgsDir` to your AGS 3.6 editor folder. `Local.props` is
git-ignored; MSBuild and `build.ps1` read it, and `-AgsDir` on the command line overrides it.

```powershell
.\build.ps1 -Test                    # build the editor plugin + run unit tests
.\build.ps1 -Engine                  # build the native engine plugin (agsmcp.dll, x86) with CMake
.\build.ps1 -Engine -Deploy -Run     # build both, copy into AgsDir, start the editor (-Game <Game.agf> to open one)
.\build.ps1 -Engine -Package         # build both and zip a release into dist\
```

The editor locks plugin DLLs, so close it before you deploy (`-StopEditor` closes it for you, but any unsaved work is lost).

### Releasing

Set `<Version>` in `Directory.Build.props`, commit, then push a matching tag (`git tag v0.1.0`, `git push origin v0.1.0`).
The [Release workflow](.github/workflows/release.yml) builds both plugins against the official AGS 3.6.2 zip, runs
the unit tests, and publishes a GitHub release with `ags-mcp-<version>.zip` (both DLLs, `install.ps1`, the skill,
README and LICENSE). Running the workflow by hand produces the zip as a build artifact without a release.

## Tools

All tools carry `readOnlyHint`/`destructiveHint` annotations. Changes to the project model are in memory until you
call `save_project`. Coordinates and IDs are as AGS uses them; `id` accepts either the numeric ID or the script name.

### Project
| Tool | Description |
|------|-------------|
| `project_info` | Name, folder, resolution, colour depth, editor version and entity counts. |
| `save_project` | Write all in-memory changes to disk (`Game.agf`, scripts, sprites, the loaded room). |
| `create_project(folder, [template], [gameName], [fileName], [saveCurrent])` | Create a game from an editor template (Sierra-style, Empty Game, …) in a new or empty folder and open it. |
| `open_project(path, [saveCurrent])` | Open another game (`Game.agf` or its folder) in the editor. |
| `list_entities(type)` | List entities of a type (character, inventory, dialog, gui, view, cursor, font, audioclip, audiocliptype, globalvariable, customproperty). |
| `find_usages(name, [limit])` | Whole-word search for an identifier across script modules, room scripts and dialog scripts. |

### Game data
| Tool | Description |
|------|-------------|
| `get_properties(type, id)` | All properties of an entity (honours the editor's property-grid metadata), plus its events for characters and inventory items. |
| `set_properties(type, id, properties)` | Set one or more properties (enum by name or number; script-name uniqueness enforced). |
| `create_entity(type, [properties])` | Create a character, inventory item, dialog, gui, view, cursor, audio type, global variable or custom-property definition. |
| `delete_entity(type, id)` | Delete an entity and renumber IDs as the editor does. |
| `get_dialog_script(id)` / `set_dialog_script(id, [script], [options])` | Read/write a dialog's options (`{text, show, say}`) and its dialog-script text. |
| `set_event(type, id, event, [function])` | Bind a character or inventory event (Look, Interact, Talk, UseInv, …) and add a stub to GlobalScript. |
| `setting` (as a `*_properties` type) | The game-wide Settings object is addressable like an entity. |

### Scripts
| Tool | Description |
|------|-------------|
| `list_scripts` | List script modules (name, header/body) and room scripts (`roomN`). |
| `read_script(name, [header], [startLine], [endLine])` | Read a module's body or header, or a room script (`room1`), optionally a line range. |
| `write_script(name, text, [header])` | Replace a module's or room script's full contents. |
| `edit_script(name, find, replace, [header], [all])` | Exact find/replace (newline-agnostic). |
| `create_script_module(name, [headerText], [scriptText])` | Create a new `.asc`/`.ash` module. |
| `compile([name])` | Compile one module (with its preceding headers), a room script (`roomN`, with the room's hotspot/object names) or all modules; returns structured errors/warnings with file and line. |
| `script_api_lookup(query, [limit])` | Search the built-in API and project headers; returns signatures, source header and `///` docs. |

### Rooms
| Tool | Description |
|------|-------------|
| `list_rooms` | List rooms (number, description, files) and which one the editor has loaded. |
| `get_room(n, [show])` | Size, backgrounds, colour depth, edges, hotspots, objects, regions, walkable/walk-behind areas, painted area IDs, properties and event bindings; `show` opens the room's editor tab. |
| `create_room([number], [description])` | Create a blank room. |
| `set_room_background(n, path\|base64, [background])` | Import a background (or an animation frame). |
| `draw_room_mask(n, mask, area, shapes, [clear])` | Paint rect/polygon/ellipse/line/fill shapes onto the hotspot, walkable-area, region or walk-behind mask. |
| `import_room_mask(n, mask, path\|base64)` | Replace a mask with an indexed image (palette index = area). |
| `get_room_properties(n, entity)` / `set_room_properties(n, entity, properties)` | Properties of the room or a hotspot, object, region, walkable area or walk-behind (by ID or script name). |
| `create_room_object(n, [properties])` / `delete_room_object(n, object)` | Add or remove a room object. |
| `render_room(n, [mask], [background])` | Render a background to PNG, optionally overlaying a mask with a colour legend. |
| `get_mask_pixel(n, mask, x, y)` | The area number at a pixel of a room mask (0 = none). |
| `set_room_event(n, entity, event, [function])` | Bind a `room`/`hotspot:N`/`object:N`/`region:N` event to a function and add a stub to the room script. |

The editor holds one room in memory at a time, so room tools load the requested room in the editor; room
edits are saved immediately (like script writes) through the editor's own save path, which also compiles
the room script.

### Assets
| Tool | Description |
|------|-------------|
| `import_sprite(path\|base64, [folder], [transparency], [alpha])` | Import a sprite into a folder (or the root). |
| `get_sprite(number)` | Return a sprite as a PNG. |
| `replace_sprite(number, path\|base64, …)` | Replace a sprite's image, keeping its number. |
| `delete_sprite(number)` | Delete a sprite (refuses with a usage report if still referenced). |
| `create_view([name], loops)` | Create a view; `loops` is an array of `{frames, runNextLoop?}` and each frame is `{sprite, delay?, flipped?, sound?}` (`sound` is an audio clip's index). |

### Audio
| Tool | Description |
|------|-------------|
| `import_audio(path\|base64+fileName, [name], [folder], [type], [bundling], [volume], [priority], [repeat])` | Create an audio clip from an OGG/MP3/WAV/VOC/MIDI/MOD file: registers it in the project and copies it into the `AudioCache` the build packs. Returns its script name and index. |
| `replace_audio(clip, [path\|base64+fileName])` | Point a clip at a new file (or re-copy its current source), keeping its script name, ID and index. |
| `delete_audio(clip, [force])` | Delete a clip and its cache copy (refuses, listing the uses, while views or scripts refer to it). |
| `audiocliptype` (as an entity type) | Audio types (Sound, Music, Ambient Sound…): `MaxChannels`, crossfade, speech volume reduction; create and delete. |

The server does not synthesise sound itself; the agent skill's `scripts/sfx.py` generates WAVs with numpy for
`import_audio` (see [skills/ags-mcp/references/audio.md](skills/ags-mcp/references/audio.md)).

### Build and run
| Tool | Description |
|------|-------------|
| `build_game([rebuild])` | Compile to `Compiled\Windows\<name>.exe`; returns structured messages. |
| `run_game([startRoom], [rebuild])` | Build (incrementally) and launch the game windowed, logging to a file; stops any prior game. |
| `stop_game` | Stop the running game. |
| `game_status` | Running/exited, pid, exit code, uptime, log path. |
| `get_game_log([sinceLine], [limit])` | Tail the engine log with a `nextLine` cursor. |

### Player simulation (needs `agsmcp.dll`)
| Tool | Description |
|------|-------------|
| `runtime_enable_plugin` | Enable the `agsmcp` engine plugin for the project (then `save_project`). |
| `game_state` | Live snapshot: room, player, characters, objects, mouse, score, interface-enabled, cutscene and paused flags, recent events. |
| `game_screenshot` | Capture the running game as a PNG. |
| `game_click(x, y, [button])` | Move the mouse and click (real input; moves the OS cursor). |
| `game_process_click(x, y, [mode], [inventory])` | Click at room coordinates in a cursor mode, as a player would (queued; `inventory` selects the item for UseInv). |
| `game_key(key)` | Simulate a key press (letter/digit/`Space`/`Enter`/`F5`/arrows… or a raw code). |
| `game_hover_name(x, y)` | The name of whatever is at a screen point. |
| `game_wait_until(condition, [room], [timeoutMs], [stableMs])` | Poll until `room`/`interfaceEnabled`/`idle`/`ready` holds for `stableMs`, or time out. |
| `game_get_global_int(index)` / `game_set_global_int(index, value)` | Read/write a legacy GlobalInt. |
| `game_call_function(name, [args])` | Queue a global-script function (0–2 integer args). |

## Testing

- **Unit tests:** `.\build.ps1 -Test` (xUnit; protocol/transport, script-text, property reflection, room/asset/audio/build parsing, key-map and wait conditions).
- **Smoke test:** `tests\smoke\smoke.ps1` drives a live editor through every tool group over HTTP on a throwaway
  game it creates from the Sierra-style template (compile error and fix, a property round-trip, room authoring,
  `render_room`, `import_sprite`, the audio tools, `build_game`, `run_game`, `game_*`), then reopens the game you had open and
  deletes the throwaway one. Run it after `build.ps1 -Engine -Deploy -Run`.

## Layout

```
src/AgsMcp.Editor/       editor plugin (AGS.Plugin.Mcp.dll), .NET Framework 4.6
  Mcp/                   JSON-RPC/MCP protocol and the loopback HTTP server (no editor dependency)
  Ui/                    UiDispatcher (runs tool calls one at a time on the editor's UI thread), client-setup dialog
  Tools/                 MCP tools
  Engine/                client + helpers for talking to the running game (EngineClient, KeyMap, GameWait)
  EditorInternals.cs     the only code that uses AGSEditor.exe internals
src/AgsMcp.Engine/       native engine plugin (agsmcp.dll), C++/x86, CMake
tests/AgsMcp.Editor.Tests/   unit tests
tests/smoke/smoke.ps1        end-to-end smoke test against a live editor
skills/ags-mcp/              agent skill (SKILL.md) for working on a game through these tools, with pixel-art and sound helpers
install.ps1                  installer (downloads a release, or installs from one)
AGENTS.md                    instructions for coding agents working on this repo (CLAUDE.md imports it)
build.ps1                    build, test, deploy and package
Local.props.example          template for the git-ignored Local.props (machine-specific paths)
```

## License

[MIT](LICENSE). `src/AgsMcp.Engine/agsplugin.h` is part of Adventure Game Studio and is under the Artistic License 2.0.
