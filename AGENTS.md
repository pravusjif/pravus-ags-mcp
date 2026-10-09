# AGS MCP Server

An MCP server that runs inside the Adventure Game Studio 3.6 editor as a plugin, plus a native engine plugin for driving the running game.

These are the instructions for any coding agent working on this repo. `CLAUDE.md` only imports this file, so keep everything here.

**Before you change behaviour, read [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).** It records the architecture, the design decisions and their reasons, known gaps and verified AGS facts. When you change behaviour or verify a new AGS fact, update it. The tool reference is in [README.md](README.md#tools).

## Workflow
- Commit finished, verified work on `main`. The remote is `origin` (`git@github.com:pravusjif/pravus-ags-mcp.git`). Push only when the user asks. GPG signing is turned off in this repo's local git config.
- The dev loop is `.\build.ps1 -Test -Deploy -Run`. The editor locks `AGS.Plugin.Mcp.dll`, so close it before deploying (`-StopEditor` kills it, losing unsaved work), and restart it after every plugin change.
- Plans can be run unattended with AI Plan Loop (https://github.com/pravusjif/ai-plan-loop), which is not part of this repo. Run it from wherever it is cloned, against a plan file here: `python <ai-plan-loop>\plan_loop.py <this repo>\docs\<plan>.md [--agent ...]`. It finds the repo from the plan's location. A session started by it gets `AI_PLAN_LOOP=1` and must end every turn with a `LOOP_STATUS:` line.
- To verify against the live editor, call `http://127.0.0.1:7471/mcp` with `Invoke-WebRequest` or with the MCP Inspector CLI. Both commands are in the architecture doc's Testing section. When a call fails, read `%APPDATA%\AGS-MCP\plugin.log`.

## Constraints
- The server is client-agnostic: plain MCP only, with no behaviour tied to one client. Setup docs, the editor menu and the installer cover several clients (Claude Code, Codex, Gemini CLI, generic HTTP, stdio via `mcp-remote`). Keep tool schemas simple (one typed form, no `anyOf`/`oneOf`/`$ref`) so strict clients can follow them.
- The editor plugin ships as **one DLL**. Use only what the editor already provides: .NET Framework 4.6, `AGS.Types`, `AGSEditor.exe` and Newtonsoft.Json 13. Keep every reference `Private=false`. Build-time-only packages (`PrivateAssets="all"`, such as the net46 reference assemblies) are fine.
- Write C# 7.3. `System.ValueTuple` is unavailable on net46, so use small classes or anonymous types in place of tuples.
- Code that touches `AGSEditor.exe` internals goes in `src/AgsMcp.Editor/EditorInternals.cs`.
- Tools that touch editor state run on the UI thread through `UiDispatcher`, which is the default for `Tool.RunOnUiThread`. Report bad input by throwing `ToolException`.

## Local environment (outside the repo)
Machine-specific paths live in the git-ignored `Local.props` at the repo root (template: `Local.props.example`). Read it when you need one:
- `AgsDir`: the AGS 3.6.2.21 editor folder. MSBuild and `build.ps1` use it.
- `AgsSourceDir`: the AGS source tree, for API lookups.

The repo depends on no particular game. The smoke test creates its own throwaway game with `create_project`, and the `ags-mcp` skill works on whatever game the user has open. Don't add references to specific games.
