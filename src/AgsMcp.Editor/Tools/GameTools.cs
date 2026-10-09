using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using AGS.Types;
using AgsMcp.Editor.Engine;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Player-simulation tools (M7). These talk to the agsmcp engine plugin inside the game launched by
    /// run_game, over loopback TCP, so they run off the UI dispatcher. runtime_enable_plugin is the one
    /// exception: it edits the editor's plugin state and must run on the UI thread.
    /// </summary>
    internal static class GameTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            var client = new EngineClient(BuildRunTools.EnginePort);
            yield return RuntimeEnablePlugin(ctx);
            yield return GameState(ctx, client);
            yield return GameScreenshot(ctx, client);
            yield return GameClick(ctx, client);
            yield return GameProcessClick(ctx, client);
            yield return GameKey(ctx, client);
            yield return GameHoverName(ctx, client);
            yield return GameWaitUntil(ctx, client);
            yield return GameGetGlobalInt(ctx, client);
            yield return GameSetGlobalInt(ctx, client);
            yield return GameCallFunction(ctx, client);
        }

        private const string PluginFileName = "agsmcp.dll";

        private static Tool RuntimeEnablePlugin(ToolContext ctx) => new Tool
        {
            Name = "runtime_enable_plugin",
            Title = "Enable the engine plugin",
            Description = "Enable the native 'agsmcp' engine plugin for this project so the game, once built and run, " +
                          "can be driven by the game_* tools. The plugin DLL must already be in the AGS editor folder " +
                          "(build & deploy the engine plugin, then restart the editor). Call save_project afterwards. " +
                          "Untick the plugin (or do not ship it) for release builds.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EditorInternals.EnablePluginResult r = EditorInternals.EnableEnginePlugin(ctx.Editor, game, PluginFileName);
                if (!r.Found)
                {
                    return ToolResult.Json(new
                    {
                        ok = false,
                        enabled = false,
                        reason = "The '" + PluginFileName + "' engine plugin was not found in the AGS editor folder. " +
                                 "Build and deploy the engine plugin (build.ps1 -Engine -Deploy), then restart the editor.",
                        availablePlugins = r.Available,
                    });
                }
                return ToolResult.Json(new
                {
                    ok = true,
                    enabled = true,
                    pluginName = r.PluginName,
                    alreadyEnabled = r.AlreadyEnabled,
                    note = "Call save_project to persist this, then build_game/run_game. Untick for release builds.",
                });
            },
        };

        private static Tool GameState(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_state",
            Title = "Game state",
            Description = "Snapshot of the running game from the engine plugin: current room, player character " +
                          "(position, room, view/loop/frame, walking/animating, inventory), all characters and room " +
                          "objects, mouse position, score, whether the interface is enabled, cutscene flags and paused flag. " +
                          "View numbers match the editor (1-based, 0 = none). A Display() message box leaves the interface " +
                          "enabled; an open dialog option list disables it.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                return ToolResult.Json(Ok(client.Send("state", null)));
            },
        };

        private static Tool GameScreenshot(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_screenshot",
            Title = "Game screenshot",
            Description = "Capture a screenshot of the running game and return it as a PNG image.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                JObject resp = Ok(client.Send("screenshot", null));
                string path = (string)resp["path"];
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                    throw new ToolException("The game reported a screenshot at '" + path + "' but it could not be read.");

                byte[] png;
                using (var bmp = new Bitmap(path))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, ImageFormat.Png);
                    png = ms.ToArray();
                }
                var result = new ToolResult();
                result.AddText("Screenshot of the running game (" + path + ").");
                result.AddImage(png, "image/png");
                return result;
            },
        };

        private static Tool GameClick(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_click",
            Title = "Click in the game",
            Description = "Move the mouse to screen coordinates (x, y) and click. button is 'left' (default), 'right' or " +
                          "'middle'. This is real simulated input (it moves the OS cursor) and reaches GUIs and on_mouse_click.",
            InputSchema = Schema.Object()
                .Required("x", Schema.Integer("Screen x coordinate."))
                .Required("y", Schema.Integer("Screen y coordinate."))
                .Optional("button", Schema.String("Mouse button.", "left", "right", "middle"))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                var a = new JObject { ["x"] = args.Int("x"), ["y"] = args.Int("y"), ["button"] = ButtonCode(args.String("button", "left")) };
                return ToolResult.Json(Ok(client.Send("click", a)));
            },
        };

        private static Tool GameProcessClick(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_process_click",
            Title = "Process click in the game",
            Description = "Click at room coordinates (x, y) in the given cursor mode, as a player would: sets the cursor " +
                          "mode, moves the mouse there and queues a left click, so the game's on_mouse_click runs the " +
                          "interaction on the next frame (a GUI at that point gets the click instead). mode is the cursor-mode " +
                          "number as defined by the game (templates: 0 Walk, 1 Look, 2 Interact, 3 Talk, 4 UseInv). For " +
                          "UseInv pass 'inventory' (an item ID) to make it the active item first. Returns at once; use " +
                          "game_wait_until idle/interfaceEnabled and game_screenshot to see the outcome.",
            InputSchema = Schema.Object()
                .Required("x", Schema.Integer("Room x coordinate."))
                .Required("y", Schema.Integer("Room y coordinate."))
                .Optional("mode", Schema.Integer("Cursor mode number (default 0 = Walk)."))
                .Optional("inventory", Schema.Integer("Inventory item ID to make active before clicking (for UseInv)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                var a = new JObject { ["x"] = args.Int("x"), ["y"] = args.Int("y"), ["mode"] = args.Int("mode", 0), ["inventory"] = args.Int("inventory", 0) };
                return ToolResult.Json(Ok(client.Send("process_click", a)));
            },
        };

        private static Tool GameKey(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_key",
            Title = "Press a key in the game",
            Description = "Simulate a key press. key is a letter, digit, or a name such as Space, Enter, Escape, F1..F12, " +
                          "Up/Down/Left/Right, Home/End/PageUp/PageDown, Insert/Delete (or a raw AGS key code number).",
            InputSchema = Schema.Object()
                .Required("key", Schema.String("Key name, letter, digit, or raw AGS key code."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                int code = KeyMap.Resolve(args.String("key"));
                return ToolResult.Json(Ok(client.Send("key", new JObject { ["code"] = code })));
            },
        };

        private static Tool GameHoverName(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_hover_name",
            Title = "Name at a point",
            Description = "Return the name of whatever is at screen coordinates (x, y) — the hotspot, object, character or " +
                          "inventory item the engine would report (script GetLocationName). Empty if nothing is there.",
            InputSchema = Schema.Object()
                .Required("x", Schema.Integer("Screen x coordinate."))
                .Required("y", Schema.Integer("Screen y coordinate."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                var a = new JObject { ["x"] = args.Int("x"), ["y"] = args.Int("y") };
                return ToolResult.Json(Ok(client.Send("hover_name", a)));
            },
        };

        private static Tool GameWaitUntil(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_wait_until",
            Title = "Wait for a game condition",
            Description = "Poll the running game until a condition holds continuously for 'stableMs' or a timeout elapses. " +
                          "condition is 'room' (the player is in 'room'), 'interfaceEnabled' (the UI is usable, i.e. no " +
                          "blocking speech/walk/cutscene), 'idle' (the player is not walking or animating) or 'ready' (both: " +
                          "the player can act again; use it after game_process_click). The stability window matters because " +
                          "the interface is briefly enabled between consecutive speech lines and dialog steps. Returns whether " +
                          "the condition was met and the final state. A Display() box leaves the interface enabled, so 'ready' can be " +
                          "met while one waits for a click; an open dialog option list keeps it disabled until an option is " +
                          "clicked. Many MCP clients give up on a call after about 60 s, so keep timeoutMs at 45000 or less and " +
                          "chain waits.",
            InputSchema = Schema.Object()
                .Required("condition", Schema.String("What to wait for.", GameWait.ConditionRoom, GameWait.ConditionInterfaceEnabled, GameWait.ConditionIdle, GameWait.ConditionReady))
                .Optional("room", Schema.Integer("Target room number (required for condition 'room')."))
                .Optional("timeoutMs", Schema.Integer("Maximum time to wait in milliseconds (default 5000)."))
                .Optional("stableMs", Schema.Integer("How long the condition must hold without interruption (default 400; 0 = first match)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Timeout = TimeSpan.FromSeconds(60),
            Handler = args =>
            {
                RequireRunning(ctx);
                string condition = args.String("condition");
                int? room = args.Has("room") ? (int?)args.Int("room") : null;
                int timeoutMs = args.Int("timeoutMs", 5000);
                int stableMs = Math.Max(0, args.Int("stableMs", 400));
                if (condition == GameWait.ConditionRoom && !room.HasValue)
                    throw new ToolException("condition 'room' needs a 'room' number to wait for.");

                var sw = System.Diagnostics.Stopwatch.StartNew();
                JObject state = null;
                bool met = false;
                long since = -1; // when the condition last started holding
                while (true)
                {
                    state = Ok(client.Send("state", null));
                    if (GameWait.IsSatisfied(state, condition, room))
                    {
                        if (since < 0) since = sw.ElapsedMilliseconds;
                        if (sw.ElapsedMilliseconds - since >= stableMs) { met = true; break; }
                    }
                    else since = -1;
                    if (sw.ElapsedMilliseconds >= timeoutMs) break;
                    Thread.Sleep(stableMs > 0 ? Math.Min(100, stableMs) : 150);
                }
                return ToolResult.Json(new { met, condition, room, elapsedMs = sw.ElapsedMilliseconds, state });
            },
        };

        private static Tool GameGetGlobalInt(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_get_global_int",
            Title = "Get a global int",
            Description = "Read a legacy GlobalInt from the running game (script GetGlobalInt).",
            InputSchema = Schema.Object()
                .Required("index", Schema.Integer("GlobalInt index (0-based)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                return ToolResult.Json(Ok(client.Send("get_global_int", new JObject { ["index"] = args.Int("index") })));
            },
        };

        private static Tool GameSetGlobalInt(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_set_global_int",
            Title = "Set a global int",
            Description = "Set a legacy GlobalInt in the running game (script SetGlobalInt).",
            InputSchema = Schema.Object()
                .Required("index", Schema.Integer("GlobalInt index (0-based)."))
                .Required("value", Schema.Integer("Value to set."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                var a = new JObject { ["index"] = args.Int("index"), ["value"] = args.Int("value") };
                return ToolResult.Json(Ok(client.Send("set_global_int", a)));
            },
        };

        private static Tool GameCallFunction(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_call_function",
            Title = "Call a game script function",
            Description = "Queue a global-script function to run in the game as soon as the engine is able to (script " +
                          "QueueGameScriptFunction). Pass 0-2 integer arguments. Only functions defined in GlobalScript.asc " +
                          "are reachable (refused otherwise; a hook in another module needs a GlobalScript wrapper). Keep the " +
                          "function non-blocking (no Say, Display, Wait or eBlock walks): it runs inside the engine's frame " +
                          "hook. To read values back, have it System.Log a line and read it with get_game_log.",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("Global-script function name, e.g. \"my_function\"."))
                .Optional("args", Schema.ArrayOf(Schema.Integer("An integer argument."), "Up to 2 integer arguments."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                string name = args.String("name").Trim();
                // The engine drops a call to a missing function silently, so check the editor's GlobalScript first.
                // The running game was built from an earlier save, so this is a guard, not a guarantee.
                Script global = ctx.RequireGame().ScriptsAndHeaders
                    .Select(sh => sh.Script)
                    .FirstOrDefault(s => s != null && s.FileName == Script.GLOBAL_SCRIPT_FILE_NAME);
                if (global != null && !ScriptText.DefinesFunction(global.Text, name))
                    throw new ToolException($"GlobalScript.asc defines no function '{name}'. game_call_function only reaches " +
                                            "GlobalScript functions; define one there (it may forward to a module) and rebuild.");
                var a = new JObject { ["name"] = name };
                if (args.Has("args")) a["args"] = args.Array("args");
                return ToolResult.Json(Ok(client.Send("call_function", a)));
            },
        };

        // ---- helpers ----

        private static void RequireRunning(ToolContext ctx)
        {
            if (!ctx.Runner.IsRunning)
                throw new ToolException("No game is running. Start one with run_game first (and enable the engine plugin " +
                                        "with runtime_enable_plugin + save_project + build if you have not already).");
        }

        private static JObject Ok(JObject resp)
        {
            if (resp == null) throw new ToolException("The game returned an empty response.");
            if (resp["ok"] != null && resp["ok"].Type == JTokenType.Boolean && !(bool)resp["ok"])
                throw new ToolException("The game rejected the command: " + (string)resp["error"]);
            return resp;
        }

        private static int ButtonCode(string button)
        {
            switch ((button ?? "left").Trim().ToLowerInvariant())
            {
                case "right": return 2;
                case "middle": return 3;
                default: return 1; // left
            }
        }
    }
}
