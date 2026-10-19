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
    /// run_game, over loopback TCP, so they run off the UI dispatcher. runtime_enable_plugin and
    /// runtime_disable_plugin are the exceptions: they edit the editor's plugin state and must run on the UI thread.
    /// </summary>
    internal static class GameTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            var client = new EngineClient(BuildRunTools.EnginePort);
            yield return RuntimeEnablePlugin(ctx);
            yield return RuntimeDisablePlugin(ctx);
            yield return GameState(ctx, client);
            yield return GameScreenshot(ctx, client);
            yield return GameClick(ctx, client);
            yield return GameProcessClick(ctx, client);
            yield return GameKey(ctx, client);
            yield return GameType(ctx, client);
            yield return GameHoverName(ctx, client);
            yield return GameWaitUntil(ctx, client);
            yield return GameGetGlobalInt(ctx, client);
            yield return GameSetGlobalInt(ctx, client);
            yield return GameCallFunction(ctx, client);
            yield return GameTextExtent(ctx, client);
            yield return GameRunInteraction(ctx, client);
        }

        internal const string PluginFileName = "agsmcp.dll";

        private static Tool RuntimeEnablePlugin(ToolContext ctx) => new Tool
        {
            Name = "runtime_enable_plugin",
            Title = "Enable the engine plugin",
            Description = "Enable the native 'agsmcp' engine plugin for this project so the game, once built and run, " +
                          "can be driven by the game_* tools. The plugin DLL must already be in the AGS editor folder " +
                          "(build & deploy the engine plugin, then restart the editor). Call save_project afterwards. " +
                          "Do not ship it: build_game mode=release disables it (or use runtime_disable_plugin).",
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
                    note = "Call save_project to persist this, then build_game/run_game. Use build_game mode=release for release builds.",
                });
            },
        };

        private static Tool RuntimeDisablePlugin(ToolContext ctx) => new Tool
        {
            Name = "runtime_disable_plugin",
            Title = "Disable the engine plugin",
            Description = "Disable the native 'agsmcp' engine plugin for this project (untick it in the Plugins node), so " +
                          "builds no longer load it and the game_* tools cannot reach the game. Call save_project afterwards " +
                          "(build_game and run_game also save). An agsmcp.dll copied into Compiled\\Windows by an earlier " +
                          "build stays there; build_game mode=release disables the plugin and deletes that copy.",
            InputSchema = Schema.Object().Build(),
            Annotations = new ToolAnnotations { Idempotent = true },
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                bool wasEnabled = EditorInternals.DisableEnginePlugin(ctx.Editor, game, PluginFileName);
                return ToolResult.Json(new
                {
                    ok = true,
                    enabled = false,
                    wasEnabled,
                    note = "Call save_project to persist this.",
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
            Description = "Capture a screenshot of the running game and return it as a PNG image, or write it to outPath.",
            InputSchema = Schema.Object().Optional(ImageOutput.Arg, ImageOutput.Prop()).Build(),
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
                int width, height;
                try
                {
                    using (var bmp = new Bitmap(path))
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        png = ms.ToArray();
                        width = bmp.Width;
                        height = bmp.Height;
                    }
                }
                finally
                {
                    // Each shot is a fresh agsmcp-<n>.bmp in the game's save folder; do not let them pile up.
                    try { File.Delete(path); } catch (Exception e) { Logger.Write("Could not delete " + path + ": " + e.Message); }
                }
                return ImageOutput.Result(args, png, width, height, "Screenshot of the running game.");
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
                          "Up/Down/Left/Right, Home/End/PageUp/PageDown, Insert/Delete (or a raw AGS key code number). " +
                          "Backspace/Tab/Enter, F1..F12 and the navigation keys are posted to the game window as real keys " +
                          "(via=window); the rest go through Game.SimulateKeyPress. on_key_press receives the AGS code " +
                          "(Up = 372, F7 = 365). To type text into a GUI text box use game_type.",
            InputSchema = Schema.Object()
                .Required("key", Schema.String("Key name, letter, digit, or raw AGS key code."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                int code = KeyMap.Resolve(args.String("key"));
                JObject resp = Ok(client.Send("key", new JObject { ["code"] = code }));
                resp["code"] = code;
                return ToolResult.Json(resp);
            },
        };

        private static Tool GameType(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_type",
            Title = "Type text in the game",
            Description = "Type text into the running game as character input (WM_CHAR per character, which the engine " +
                          "receives as text input): it fills a focused GUI text box and reaches on_text_input. It does not " +
                          "press keys, so on_key_press does not run; use game_key for Enter, Backspace and other keys.",
            InputSchema = Schema.Object()
                .Required("text", Schema.String("The text to type."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                string text = args.String("text");
                if (string.IsNullOrEmpty(text)) throw new ToolException("'text' must not be empty.");
                return ToolResult.Json(Ok(client.Send("type", new JObject { ["text"] = text })));
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
            Description = "Queue a script function to run in the game as soon as the engine is able to (script " +
                          "QueueGameScriptFunction). Pass 0-2 integer arguments. By default the function must be defined in " +
                          "GlobalScript.asc; with roomScript=true it runs in the current room's script instead and must be " +
                          "defined in that room's roomN.asc on disk. Functions in other modules are not reachable (refused; " +
                          "add a GlobalScript wrapper). Keep the function non-blocking (no Say, Display, Wait or eBlock walks): " +
                          "it runs inside the engine's frame hook. To read values back, have it System.Log a line and read it " +
                          "with get_game_log.",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("Function name, e.g. \"my_function\"."))
                .Optional("args", Schema.ArrayOf(Schema.Integer("An integer argument."), "Up to 2 integer arguments."))
                .Optional("roomScript", Schema.Boolean("Run it in the current room's script instead of GlobalScript (default false)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                string name = args.String("name").Trim();
                bool roomScript = args.Bool("roomScript", false);
                // The engine drops a call to a missing function silently, so check the script first. The running game
                // was built from an earlier save, so this is a guard, not a guarantee.
                Game game = ctx.RequireGame();
                int? room = null;
                if (roomScript)
                {
                    room = (int?)Ok(client.Send("state", null))["room"];
                    if (!room.HasValue) throw new ToolException("The game did not report its current room.");
                    string file = Path.Combine(game.DirectoryPath, "room" + room.Value + ".asc");
                    if (!File.Exists(file))
                        throw new ToolException($"The game is in room {room.Value}, but '{file}' does not exist.");
                    if (!ScriptText.DefinesFunction(File.ReadAllText(file), name))
                        throw new ToolException($"room{room.Value}.asc defines no function '{name}'. With roomScript=true " +
                                                "game_call_function only reaches functions of the room the game is in.");
                }
                else
                {
                    Script global = game.ScriptsAndHeaders
                        .Select(sh => sh.Script)
                        .FirstOrDefault(s => s != null && s.FileName == Script.GLOBAL_SCRIPT_FILE_NAME);
                    if (global != null && !ScriptText.DefinesFunction(global.Text, name))
                        throw new ToolException($"GlobalScript.asc defines no function '{name}'. game_call_function only reaches " +
                                                "GlobalScript functions (or, with roomScript=true, the current room's); define one " +
                                                "there (it may forward to a module) and rebuild.");
                }
                var a = new JObject { ["name"] = name, ["globalScript"] = roomScript ? 0 : 1 };
                if (args.Has("args")) a["args"] = args.Array("args");
                JObject resp = Ok(client.Send("call_function", a));
                if (room.HasValue) resp["room"] = room.Value;
                return ToolResult.Json(resp);
            },
        };

        private static Tool GameTextExtent(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_text_extent",
            Title = "Measure text in the game",
            Description = "Measure one line of text in a font as the running game draws it (engine GetTextExtent): width " +
                          "and height in game pixels, outline included. No wrapping; '[' is not a line break here. Works for " +
                          "TTF fonts too, unlike measure_text, but needs the game running with the engine plugin.",
            InputSchema = Schema.Object()
                .Required("font", Schema.Integer("Font number (0-based)."))
                .Required("text", Schema.String("The line to measure."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                RequireRunning(ctx);
                Game game = ctx.RequireGame();
                string text = args.String("text");
                var a = new JObject { ["font"] = args.Int("font") };
                // A non-Unicode game reads text as codepage bytes; the bridge carries JSON (UTF-8), so send the bytes.
                if (game.UnicodeMode) a["text"] = text;
                else a["bytes"] = new JArray(game.TextEncoding.GetBytes(text).Select(b => (int)b));
                JObject resp = Ok(client.Send("text_extent", a));
                return ToolResult.Json(new { font = args.Int("font"), text, width = (int)resp["width"], height = (int)resp["height"] });
            },
        };

        private static Tool GameRunInteraction(ToolContext ctx, EngineClient client) => new Tool
        {
            Name = "game_run_interaction",
            Title = "Run an interaction in the game",
            Description = "Click a hotspot, object or character of the current room in a cursor mode, as a player would, " +
                          "without knowing its coordinates. target is \"hotspot:N\", \"object:N\", \"character:N\" (N = ID or " +
                          "script name) or a bare script name. The point comes from editor data: a hotspot's mask centroid, an " +
                          "object's or character's current sprite box. Up to 8 points are checked with hover_name until one " +
                          "reports the target's name, then a click in 'mode' is queued there (as game_process_click). Refused " +
                          "when no point reports the target (covered by a character or GUI, off-screen, hidden) or while the " +
                          "interface is disabled. Returns at once; follow with game_wait_until ready. Loads the room in the editor " +
                          "for hotspots and objects.",
            InputSchema = Schema.Object()
                .Required("target", Schema.String("\"hotspot:N\", \"object:N\", \"character:N\" (ID or script name), or a script name."))
                .Required("mode", Schema.Integer("Cursor mode number as defined by the game (templates: 0 Walk, 1 Look, 2 Interact, 3 Talk, 4 UseInv)."))
                .Optional("inventory", Schema.Integer("Inventory item ID to make active before clicking (for UseInv)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                RequireRunning(ctx);
                Game game = ctx.RequireGame();
                InteractionTarget target = InteractionTarget.Parse(args.String("target"));
                int mode = args.Int("mode");
                JObject state = Ok(client.Send("state", null));
                int room = (int)state["room"];
                if (state["interfaceEnabled"] != null && (int)state["interfaceEnabled"] == 0)
                    throw new ToolException("The game's interface is disabled (a blocking action or cutscene is running), so a click " +
                                            "would be ignored. Wait with game_wait_until condition=ready, then retry.");

                string label, expected;
                List<MaskPoint> points = InteractionPoints(ctx, game, state, room, target, out label, out expected);
                if (string.IsNullOrEmpty(expected))
                    throw new ToolException($"{label} has no name, so hover_name cannot confirm a point on it. Give it a description.");
                if (points.Count == 0)
                    throw new ToolException($"{label} has no visible pixels in room {room} to click.");

                var tried = new List<object>();
                foreach (MaskPoint p in points)
                {
                    JObject hover = Ok(client.Send("hover_name", new JObject { ["x"] = p.X, ["y"] = p.Y, ["room"] = 1 }));
                    string name = (string)hover["name"] ?? "";
                    tried.Add(new { x = p.X, y = p.Y, hoverName = name });
                    if (name != expected) continue;

                    var click = new JObject { ["x"] = p.X, ["y"] = p.Y, ["mode"] = mode, ["inventory"] = args.Int("inventory", 0) };
                    JObject resp = Ok(client.Send("process_click", click));
                    return ToolResult.Json(new
                    {
                        target = label,
                        room,
                        mode,
                        point = new { x = p.X, y = p.Y },
                        screen = new { x = (int?)hover["screenX"], y = (int?)hover["screenY"] },
                        hoverName = name,
                        queued = (bool?)resp["queued"] ?? false,
                        tried = tried.Count,
                    });
                }
                throw new ToolException($"No sampled point on {label} reports '{expected}' (tried {Newtonsoft.Json.JsonConvert.SerializeObject(tried)}). " +
                                        "It may be covered by a character, object or GUI, off-screen, hidden, or renamed at run time; " +
                                        "click it yourself with game_process_click.");
            },
        };

        /// <summary>Candidate room points on the target, best first, and the name hover_name must report there.</summary>
        private static List<MaskPoint> InteractionPoints(ToolContext ctx, Game game, JObject state, int roomNumber, InteractionTarget target,
                                                         out string label, out string expected)
        {
            const int MaxSamples = 8;
            string kind = target.Kind;
            Room room = null;
            if (kind != "character")
            {
                room = RoomSession.Open(ctx, roomNumber);
                if (kind == null)
                {
                    // A bare script name: a hotspot or object of this room, else a character.
                    if (room.Hotspots.Any(h => NameIs(h.Name, target.Name))) kind = "hotspot";
                    else if (room.Objects.Any(o => NameIs(o.Name, target.Name))) kind = "object";
                    else kind = "character";
                }
            }

            if (kind == "hotspot")
            {
                RoomHotspot hs = room.Hotspots.FirstOrDefault(h => target.Id.HasValue ? h.ID == target.Id.Value : NameIs(h.Name, target.Name))
                                 ?? throw new ToolException($"Room {roomNumber} has no hotspot {target.Text}.");
                if (hs.ID == 0) throw new ToolException("Hotspot 0 is 'no hotspot' and cannot be clicked.");
                label = "hotspot:" + hs.ID + " (" + hs.Name + ")";
                expected = hs.Description;
                using (Bitmap bmp = EditorInternals.ExportAreaMask(room, RoomAreaMaskType.Hotspots))
                {
                    byte[,] mask = RoomTools.ReadMask(bmp);
                    int factor = Math.Max(1, room.Width / Math.Max(1, mask.GetLength(1)));
                    return MaskGeometry.SamplePoints(mask, hs.ID, MaxSamples)
                        .Select(p => new MaskPoint(p.X * factor + factor / 2, p.Y * factor + factor / 2)).ToList();
                }
            }

            if (kind == "object")
            {
                RoomObject obj = room.Objects.FirstOrDefault(o => target.Id.HasValue ? o.ID == target.Id.Value : NameIs(o.Name, target.Name))
                                 ?? throw new ToolException($"Room {roomNumber} has no object {target.Text}.");
                label = "object:" + obj.ID + " (" + obj.Name + ")";
                expected = obj.Description;
                JObject live = (state["objects"] as JArray)?.OfType<JObject>().FirstOrDefault(o => (int)o["id"] == obj.ID)
                               ?? throw new ToolException($"The running game reports no object {obj.ID} in room {roomNumber}; rebuild the game.");
                if ((int)live["visible"] == 0) throw new ToolException($"{label} is not visible in the running game.");
                Sprite sprite = SpriteOf(game, (int)live["sprite"], label);
                int x = (int)live["x"], y = (int)live["y"];
                // An object's x/y is its bottom-left corner.
                return MaskGeometry.RectSamplePoints(x, y - sprite.Height, sprite.Width, sprite.Height, MaxSamples);
            }

            JObject ch = (state["characters"] as JArray)?.OfType<JObject>().FirstOrDefault(c =>
                             target.Id.HasValue ? (int)c["id"] == target.Id.Value : NameIs((string)c["scriptName"], target.Name))
                         ?? throw new ToolException($"No character {target.Text} in the running game.");
            label = "character:" + (int)ch["id"] + " (" + (string)ch["scriptName"] + ")";
            expected = (string)ch["name"];
            if ((int)ch["room"] != roomNumber) throw new ToolException($"{label} is in room {(int)ch["room"]}, not in the current room {roomNumber}.");
            if ((int)ch["on"] == 0) throw new ToolException($"{label} is hidden (Character.on is 0).");
            int view = (int)ch["view"], loop = (int)ch["loop"], frame = (int)ch["frame"];
            View v = game.RootViewFolder.AllItemsFlat.FirstOrDefault(i => i.ID == view)
                     ?? throw new ToolException($"{label} shows view {view}, which the editor does not have.");
            ViewFrame f = v.Loops.FirstOrDefault(l => l.ID == loop)?.Frames.FirstOrDefault(fr => fr.ID == frame)
                          ?? throw new ToolException($"{label} shows view {view} loop {loop} frame {frame}, which the editor does not have.");
            Sprite s = SpriteOf(game, f.Image, label);
            int cx = (int)ch["x"], cy = (int)ch["y"];
            // A character's x/y is the middle of its feet; character scaling is not taken into account.
            return MaskGeometry.RectSamplePoints(cx - s.Width / 2, cy - s.Height, s.Width, s.Height, MaxSamples);
        }

        private static Sprite SpriteOf(Game game, int number, string label) =>
            game.RootSpriteFolder.FindSpriteByID(number, true)
            ?? throw new ToolException($"{label} shows sprite {number}, which the editor does not have.");

        private static bool NameIs(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        /// <summary>A game_run_interaction target: Kind is hotspot/object/character or null (a bare script name).</summary>
        internal sealed class InteractionTarget
        {
            public string Kind;
            public int? Id;
            public string Name;
            public string Text => Id.HasValue ? Id.Value.ToString() : "'" + Name + "'";

            internal static InteractionTarget Parse(string target)
            {
                string s = (target ?? "").Trim();
                if (s.Length == 0) throw new ToolException("'target' is required, e.g. \"hotspot:1\", \"object:oKey\" or \"cGuard\".");
                int colon = s.IndexOf(':');
                string kind = null, rest = s;
                if (colon >= 0)
                {
                    kind = s.Substring(0, colon).Trim().ToLowerInvariant();
                    rest = s.Substring(colon + 1).Trim();
                    if (kind == "char") kind = "character";
                    if (kind != "hotspot" && kind != "object" && kind != "character")
                        throw new ToolException($"Unknown target kind '{kind}'. Use hotspot:N, object:N or character:N (ID or script name).");
                }
                if (int.TryParse(rest, out int id) && id >= 0)
                {
                    if (kind == null) throw new ToolException($"A bare number is ambiguous; use \"hotspot:{id}\", \"object:{id}\" or \"character:{id}\".");
                    return new InteractionTarget { Kind = kind, Id = id };
                }
                if (!System.Text.RegularExpressions.Regex.IsMatch(rest, "^[A-Za-z_][A-Za-z0-9_]*$"))
                    throw new ToolException($"Could not read a target from '{target}'. Use hotspot:N, object:N, character:N or a script name.");
                return new InteractionTarget { Kind = kind, Name = rest };
            }
        }

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
