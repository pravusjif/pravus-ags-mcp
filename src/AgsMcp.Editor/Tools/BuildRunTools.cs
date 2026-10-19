using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Build-and-run tools: compile the whole game to a standalone Windows exe, launch it with logging into a
    /// file we tail, report status and stop it. The build is marshalled to the editor's main thread (see
    /// EditorInternals.CompileGameFull). Launching, stopping and log reading do not touch the editor, so they run
    /// off the UI dispatcher and can be polled while a build runs.
    /// </summary>
    internal static class BuildRunTools
    {
        // Port the editor plugin will tell a running game's engine plugin to listen on (M7). Set in the launched
        // game's environment now so the launch plumbing is in place; harmless until the engine plugin exists.
        internal const int EnginePort = 7470;

        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            var runner = ctx.Runner;
            yield return BuildGame(ctx, runner);
            yield return RunGame(ctx, runner);
            yield return StopGame(ctx, runner);
            yield return GameStatus(ctx, runner);
            yield return GetGameLog(ctx, runner);
        }

        internal const string SourceEngine = "engine";
        internal const string SourceDesktop = "desktop";
        internal const string ModeDebug = "debug";
        internal const string ModeRelease = "release";

        private const string BuildBehaviour =
            "Like the editor's Build command, it saves the project first (fails if the save fails) and forces a full " +
            "rebuild when the debug-mode setting changed since the last build. 'mode' switches the build configuration " +
            "first and is saved with the project: 'release' disables the agsmcp engine plugin, turns Debug mode off and " +
            "deletes a stale Compiled\\Windows\\agsmcp.dll; 'debug' turns Debug mode on and enables the plugin if it is " +
            "installed. Omit 'mode' to keep the current configuration. A game left running from the build folder is " +
            "stopped first.";

        private static JObject ModeProp() => Schema.String(
            "Build configuration to switch to before building: 'debug' (plugin on, Debug mode on) or 'release' (plugin " +
            "off, Debug mode off). Omit to keep the current one.", ModeDebug, ModeRelease);

        private static Tool BuildGame(ToolContext ctx, GameRunner runner) => new Tool
        {
            Name = "build_game",
            Title = "Build game",
            Description = "Compile the whole game to a standalone Windows executable (Compiled\\Windows\\<name>.exe). " +
                          "Returns structured compile messages (errors and warnings with script and line), the debug mode and " +
                          "the engine plugin state. Set 'rebuild' true to force a full rebuild. Does not launch the game. " +
                          BuildBehaviour,
            InputSchema = Schema.Object()
                .Optional("rebuild", Schema.Boolean("Force a full rebuild instead of an incremental build (default false)."))
                .Optional("mode", ModeProp())
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(300),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                BuildOutcome b = Build(ctx, game, runner, args.Bool("rebuild", false), ParseMode(args));
                var structured = StructureMessages(b.Messages, out int errors, out int warnings);
                string exe = EditorInternals.WindowsBuildExePath();
                bool built = errors == 0 && File.Exists(exe);
                return ToolResult.Json(new
                {
                    ok = errors == 0,
                    errors,
                    warnings,
                    exe = built ? exe : null,
                    exeExists = File.Exists(exe),
                    mode = b.Mode,
                    debugMode = game.Settings.DebugMode,
                    enginePlugin = new { available = b.Plugin.Available, enabled = b.Plugin.Enabled },
                    removedStalePlugin = b.RemovedStalePlugin ? (bool?)true : null,
                    retried = b.Retried ? (bool?)true : null,
                    messages = structured,
                });
            },
        };

        private static Tool RunGame(ToolContext ctx, GameRunner runner) => new Tool
        {
            Name = "run_game",
            Title = "Run game",
            Description = "Build (incrementally) and launch the game windowed, logging everything to a file that get_game_log " +
                          "tails. Optionally pass 'startRoom' to boot straight into a room. If a game is already running it is " +
                          "stopped first. Returns the process id, exe path and log path, or the build errors if the build failed. " +
                          BuildBehaviour,
            InputSchema = Schema.Object()
                .Optional("startRoom", Schema.Integer("Room number to start in (engine --startr; omit for the normal game start)."))
                .Optional("rebuild", Schema.Boolean("Force a full rebuild before launching (default false)."))
                .Optional("mode", ModeProp())
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(300),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                int? startRoom = args.Has("startRoom") ? (int?)args.Int("startRoom") : null;
                bool rebuild = args.Bool("rebuild", false);

                BuildOutcome b = Build(ctx, game, runner, rebuild, ParseMode(args));
                var structured = StructureMessages(b.Messages, out int errors, out int warnings);
                string exe = EditorInternals.WindowsBuildExePath();
                if (errors > 0 || !File.Exists(exe))
                {
                    return ToolResult.Json(new
                    {
                        launched = false,
                        reason = errors > 0 ? "The build has errors; fix them and retry." : "The build produced no Windows executable.",
                        errors,
                        warnings,
                        exeExists = File.Exists(exe),
                        messages = structured,
                    });
                }

                string runDir = Path.Combine(Path.GetTempPath(), "ags-mcp-run");
                Directory.CreateDirectory(runDir);
                string userDataDir = Path.Combine(runDir, "userdata");
                Directory.CreateDirectory(userDataDir);
                // The engine's --log-file-path is a DIRECTORY; it writes "ags.log" inside it (debug.cpp).
                string logDir = runDir;
                string logPath = Path.Combine(logDir, "ags.log");
                try { if (File.Exists(logPath)) File.Delete(logPath); } catch { /* the previous run may still hold it briefly */ }

                string cmdArgs = BuildEngineArgs(logDir, userDataDir, startRoom);
                var env = new Dictionary<string, string> { ["AGSMCP_PORT"] = EnginePort.ToString() };
                int pid = runner.Start(exe, cmdArgs, Path.GetDirectoryName(exe), logPath, env);

                return ToolResult.Json(new
                {
                    launched = true,
                    pid,
                    exe,
                    logPath,
                    startRoom,
                    mode = b.Mode,
                    debugMode = game.Settings.DebugMode,
                    enginePlugin = new { available = b.Plugin.Available, enabled = b.Plugin.Enabled },
                    warnings,
                });
            },
        };

        private static string ParseMode(ToolArgs args)
        {
            if (!args.Has("mode")) return null;
            string mode = args.String("mode").Trim().ToLowerInvariant();
            if (mode != ModeDebug && mode != ModeRelease)
                throw new ToolException("'mode' must be 'debug' or 'release' (or omitted to keep the current configuration).");
            return mode;
        }

        private sealed class BuildOutcome
        {
            public CompileMessages Messages;
            public string Mode;
            public EditorInternals.EnginePluginState Plugin;
            public bool RemovedStalePlugin;
            public bool Retried;
        }

        /// <summary>
        /// The build shared by build_game and run_game: switch the configuration when a mode is given, free the
        /// build output, build (CompileGameFull saves first, as the editor does), and retry once after a second when
        /// the failure looks like a file still locked by an exiting game. In release mode, delete the agsmcp.dll an
        /// earlier debug build copied: BuildTargetWindows.CopyPlugins copies enabled plugins but never deletes one.
        /// </summary>
        private static BuildOutcome Build(ToolContext ctx, Game game, GameRunner runner, bool rebuild, string mode)
        {
            if (mode == ModeRelease)
            {
                EditorInternals.DisableEnginePlugin(ctx.Editor, game, GameTools.PluginFileName);
                game.Settings.DebugMode = false;
            }
            else if (mode == ModeDebug)
            {
                if (EditorInternals.GetEnginePluginState(ctx.Editor, game, GameTools.PluginFileName).Available)
                    EditorInternals.EnableEnginePlugin(ctx.Editor, game, GameTools.PluginFileName);
                game.Settings.DebugMode = true;
            }

            var outcome = new BuildOutcome { Mode = mode };
            FreeBuildOutput(runner);
            outcome.Messages = EditorInternals.CompileGameFull(rebuild);
            // Errors only: "Failed to delete a temporary file" is a warning on a build that succeeded.
            if (ShouldRetryBuild(outcome.Messages?.OfType<CompileError>().Select(m => m.Message)))
            {
                Logger.Write("Build failed on a locked file; retrying once.");
                System.Threading.Thread.Sleep(1000);
                FreeBuildOutput(runner);
                outcome.Messages = EditorInternals.CompileGameFull(rebuild);
                outcome.Retried = true;
            }

            if (mode == ModeRelease)
            {
                string stale = Path.Combine(EditorInternals.WindowsBuildDirectory(), GameTools.PluginFileName);
                try
                {
                    if (File.Exists(stale)) { File.Delete(stale); outcome.RemovedStalePlugin = true; }
                }
                catch (Exception e) { Logger.Write("Could not delete " + stale + ": " + e.Message); }
            }
            outcome.Plugin = EditorInternals.GetEnginePluginState(ctx.Editor, game, GameTools.PluginFileName);
            return outcome;
        }

        /// <summary>
        /// Makes the previous build's output replaceable: stops the game run_game launched, kills any other process
        /// still running the built exe (a game started by hand or left over from an earlier editor session), then
        /// waits up to 5 s until every file in Compiled\Windows and the Compiled\Data .ags file can be opened for
        /// writing. Windows keeps an image locked briefly after its process exits, and a build in that window fails
        /// with "Failed to delete an old file" or EndUpdateResource when it patches the exe's version resource.
        /// </summary>
        private static void FreeBuildOutput(GameRunner runner)
        {
            if (runner.IsRunning) runner.Stop();
            string exe = EditorInternals.WindowsBuildExePath();
            KillProcessesRunning(exe);

            var files = new List<string>();
            string dir = EditorInternals.WindowsBuildDirectory();
            if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir));
            files.Add(EditorInternals.DataFilePath());
            WaitUntilWritable(files, TimeSpan.FromSeconds(5));
        }

        /// <summary>
        /// Kills processes whose main module is the given exe. Reading another process's MainModule can throw
        /// (access denied, a 64-bit process seen from a 32-bit one, or a process that just exited); those are skipped.
        /// </summary>
        private static void KillProcessesRunning(string exePath)
        {
            string fullPath = Path.GetFullPath(exePath);
            foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(fullPath)))
            {
                using (p)
                {
                    try
                    {
                        if (!string.Equals(p.MainModule.FileName, fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                        Logger.Write("Killing a lingering game process " + p.Id + " (" + fullPath + ") before the build.");
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                    catch (Exception) { /* not ours to inspect, or already gone */ }
                }
            }
        }

        private static Tool StopGame(ToolContext ctx, GameRunner runner) => new Tool
        {
            Name = "stop_game",
            Title = "Stop game",
            Description = "Stop the running game launched by run_game. Returns whether a game was running and its exit code.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            RunOnUiThread = false,
            Handler = args => ToolResult.Json(runner.Stop()),
        };

        private static Tool GameStatus(ToolContext ctx, GameRunner runner) => new Tool
        {
            Name = "game_status",
            Title = "Game status",
            Description = "Report whether the game launched by run_game is running, its process id, exit code (if it has " +
                          "exited), uptime and log path. Reads only.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args => ToolResult.Json(runner.Status()),
        };

        private static Tool GetGameLog(ToolContext ctx, GameRunner runner) => new Tool
        {
            Name = "get_game_log",
            Title = "Get game log",
            Description = "Return lines from the running (or last) game's log file. 'sinceLine' is a 0-based line offset to " +
                          "start from (use the returned 'nextLine' to poll incrementally). 'limit' caps the lines returned " +
                          "(default 500). 'source' picks the game started by run_game ('engine') or by desktop_run ('desktop'); " +
                          "by default the one launched last.",
            InputSchema = Schema.Object()
                .Optional("sinceLine", Schema.Integer("0-based line to start from (default 0)."))
                .Optional("limit", Schema.Integer("Maximum lines to return (default 500)."))
                .Optional("source", Schema.String("Which game's log: 'engine' (run_game) or 'desktop' (desktop_run). Default: the last launched.", SourceEngine, SourceDesktop))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                string source = args.String("source", null);
                if (source == null)
                    source = (ctx.DesktopRunner.StartedUtc ?? DateTime.MinValue) > (runner.StartedUtc ?? DateTime.MinValue) ? SourceDesktop : SourceEngine;
                if (source != SourceEngine && source != SourceDesktop)
                    throw new ToolException("'source' must be 'engine' or 'desktop'.");
                string logPath = (source == SourceDesktop ? ctx.DesktopRunner : runner).LogPath;
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                    throw new ToolException(source == SourceDesktop
                        ? "No desktop game log yet. Launch a game with desktop_run first."
                        : "No game log yet. Launch the game with run_game first.");
                int sinceLine = Math.Max(0, args.Int("sinceLine", 0));
                int limit = Math.Max(1, args.Int("limit", 500));
                string text = ReadShared(logPath);
                LogSlice slice = SliceLog(text, sinceLine, limit);
                return ToolResult.Json(new
                {
                    source,
                    logPath,
                    totalLines = slice.TotalLines,
                    fromLine = slice.FromLine,
                    nextLine = slice.NextLine,
                    returned = slice.Lines.Count,
                    truncated = slice.Truncated,
                    lines = slice.Lines,
                });
            },
        };

        // --- pure helpers (unit-tested) ---

        /// <summary>
        /// Builds the engine command line. 'logDir' is a directory: the engine writes "ags.log" inside it
        /// (--log-file-path is a directory, not a file). --log-file-path uses '=', --user-data-dir is a
        /// following argument (space), per the engine's OPTIONS.md.
        /// </summary>
        internal static string BuildEngineArgs(string logDir, string userDataDir, int? startRoom)
        {
            var sb = new StringBuilder();
            sb.Append("--windowed --background --no-message-box");
            sb.Append(" --log-file=all:debug");
            sb.Append(" --log-file-path=\"").Append(logDir).Append("\"");
            sb.Append(" --user-data-dir \"").Append(userDataDir).Append("\"");
            if (startRoom.HasValue) sb.Append(" --startr ").Append(startRoom.Value);
            return sb.ToString();
        }

        internal sealed class LogSlice
        {
            public int TotalLines;
            public int FromLine;
            public int NextLine;
            public bool Truncated;
            public List<string> Lines = new List<string>();
        }

        /// <summary>
        /// Splits log text into lines and returns [sinceLine, sinceLine+maxLines). NextLine lets callers poll. A final
        /// newline ends the last line instead of starting an empty one: the engine writes "message\n" and flushes, so
        /// counting that empty tail made NextLine point past the next line written and a poll skipped it.
        /// </summary>
        internal static LogSlice SliceLog(string text, int sinceLine, int maxLines)
        {
            string normalized = (text ?? string.Empty).Replace("\r\n", "\n");
            string[] lines = normalized.Length == 0 ? new string[0]
                           : (normalized.EndsWith("\n", StringComparison.Ordinal) ? normalized.Substring(0, normalized.Length - 1) : normalized).Split('\n');
            int total = lines.Length;
            int from = Math.Min(Math.Max(0, sinceLine), total);
            int end = Math.Min(total, from + Math.Max(1, maxLines));
            var slice = new LogSlice { TotalLines = total, FromLine = from, NextLine = end, Truncated = end < total };
            for (int i = from; i < end; i++) slice.Lines.Add(lines[i]);
            return slice;
        }

        internal static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                return sr.ReadToEnd();
        }

        /// <summary>
        /// True when a failed build looks like it hit a file still locked by a game that is exiting, so one retry
        /// is worth it: BuildTargetBase/BuildTargetWindows report "Failed to delete an old file ...", and patching
        /// the resources of a locked exe fails with "EndUpdateResource ... failed" (AGS.Native/agsicons.cpp).
        /// The caller passes only the error messages.
        /// </summary>
        internal static bool ShouldRetryBuild(IEnumerable<string> messages)
        {
            if (messages == null) return false;
            foreach (string m in messages)
            {
                if (m == null) continue;
                if (m.IndexOf("Failed to delete", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    m.IndexOf("EndUpdateResource", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Waits until every existing file can be opened for writing, sharing one deadline. Windows can keep a
        /// stopped game's image and DLLs locked for a moment after the process has exited, and a build in that
        /// window fails to replace them. Gives up silently at the deadline; the build then reports the lock.
        /// </summary>
        private static void WaitUntilWritable(IEnumerable<string> paths, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            foreach (string path in paths)
            {
                while (File.Exists(path))
                {
                    try
                    {
                        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) break;
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { break; } // read-only: not a lock we can wait out
                    if (DateTime.UtcNow >= deadline) return;
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        private static List<object> StructureMessages(CompileMessages messages, out int errors, out int warnings)
        {
            errors = 0;
            warnings = 0;
            var seen = new HashSet<string>();
            var list = new List<object>();
            if (messages == null) return list;
            foreach (CompileMessage m in messages)
            {
                bool isError = m is CompileError;
                string severity = isError ? "error" : "warning";
                string key = severity + "|" + m.ScriptName + "|" + m.LineNumber + "|" + m.Message;
                if (!seen.Add(key)) continue;
                if (isError) errors++; else warnings++;
                list.Add(new { severity, script = m.ScriptName, line = m.LineNumber, message = m.Message });
            }
            return list;
        }
    }

    /// <summary>Owns the single game process launched by run_game. All state is guarded by a lock.</summary>
    internal sealed class GameRunner
    {
        private readonly object _lock = new object();
        private Process _process;
        private string _exePath;
        private string _logPath;
        private DateTime _startedUtc;
        private int? _exitCode;
        private DateTime? _exitedUtc;

        public string LogPath { get { lock (_lock) return _logPath; } }

        /// <summary>When the last process was started (null if none), so get_game_log can pick the latest launch.</summary>
        public DateTime? StartedUtc { get { lock (_lock) return _process == null ? (DateTime?)null : _startedUtc; } }

        public string ExePath { get { lock (_lock) return _exePath; } }

        /// <summary>The running process's main window, or IntPtr.Zero (none yet, exited, or never launched).</summary>
        public IntPtr MainWindow()
        {
            lock (_lock)
            {
                if (_process == null || SafeHasExited(_process)) return IntPtr.Zero;
                try { _process.Refresh(); return _process.MainWindowHandle; }
                catch { return IntPtr.Zero; }
            }
        }

        public bool IsRunning
        {
            get { lock (_lock) return _process != null && !SafeHasExited(_process); } }

        public int Start(string exePath, string arguments, string workingDir, string logPath, IDictionary<string, string> env)
        {
            lock (_lock)
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = arguments,
                    WorkingDirectory = workingDir,
                    UseShellExecute = false,
                };
                if (env != null)
                    foreach (var kv in env)
                    {
                        if (kv.Value == null) psi.EnvironmentVariables.Remove(kv.Key); // null removes an inherited variable
                        else psi.EnvironmentVariables[kv.Key] = kv.Value;
                    }

                var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                process.Exited += (s, e) =>
                {
                    lock (_lock)
                    {
                        if (!ReferenceEquals(s, _process)) return;
                        try { _exitCode = _process.ExitCode; } catch { _exitCode = null; }
                        _exitedUtc = DateTime.UtcNow;
                    }
                };
                process.Start();

                _process = process;
                _exePath = exePath;
                _logPath = logPath;
                _startedUtc = DateTime.UtcNow;
                _exitCode = null;
                _exitedUtc = null;
                return process.Id;
            }
        }

        public object Status()
        {
            lock (_lock)
            {
                if (_process == null)
                    return new { running = false, everLaunched = false };
                bool exited = SafeHasExited(_process);
                return new
                {
                    running = !exited,
                    everLaunched = true,
                    pid = SafeId(_process),
                    exe = _exePath,
                    logPath = _logPath,
                    startedUtc = _startedUtc.ToString("o"),
                    uptimeSeconds = Math.Round(((_exitedUtc ?? DateTime.UtcNow) - _startedUtc).TotalSeconds, 1),
                    exitCode = exited ? _exitCode : null,
                    exitedUtc = exited && _exitedUtc.HasValue ? _exitedUtc.Value.ToString("o") : null,
                };
            }
        }

        public object Stop()
        {
            lock (_lock)
            {
                if (_process == null)
                    return new { wasRunning = false, stopped = false };
                if (SafeHasExited(_process))
                    return new { wasRunning = false, stopped = false, exitCode = _exitCode };
                try
                {
                    _process.Kill();
                    _process.WaitForExit(5000);
                }
                catch (Exception e)
                {
                    return new { wasRunning = true, stopped = false, error = e.Message };
                }
                try { _exitCode = _process.ExitCode; } catch { _exitCode = null; }
                _exitedUtc = DateTime.UtcNow;
                return new { wasRunning = true, stopped = true, exitCode = _exitCode };
            }
        }

        private static bool SafeHasExited(Process p)
        {
            try { return p.HasExited; }
            catch { return true; }
        }

        private static int? SafeId(Process p)
        {
            try { return p.Id; }
            catch { return null; }
        }
    }
}
