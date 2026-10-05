using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using AGS.Types;
using AgsMcp.Editor.Mcp;

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
            yield return BuildGame(ctx);
            yield return RunGame(ctx, runner);
            yield return StopGame(ctx, runner);
            yield return GameStatus(ctx, runner);
            yield return GetGameLog(ctx, runner);
        }

        private static Tool BuildGame(ToolContext ctx) => new Tool
        {
            Name = "build_game",
            Title = "Build game",
            Description = "Compile the whole game to a standalone Windows executable (Compiled\\Windows\\<name>.exe). " +
                          "Returns structured compile messages (errors and warnings with script and line). Set 'rebuild' " +
                          "true to force a full rebuild. Does not launch the game.",
            InputSchema = Schema.Object()
                .Optional("rebuild", Schema.Boolean("Force a full rebuild instead of an incremental build (default false)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(300),
            Handler = args =>
            {
                ctx.RequireGame();
                bool rebuild = args.Bool("rebuild", false);
                CompileMessages result = EditorInternals.CompileGameFull(rebuild);
                var structured = StructureMessages(result, out int errors, out int warnings);
                string exe = EditorInternals.WindowsBuildExePath();
                bool built = errors == 0 && File.Exists(exe);
                return ToolResult.Json(new
                {
                    ok = errors == 0,
                    errors,
                    warnings,
                    exe = built ? exe : null,
                    exeExists = File.Exists(exe),
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
                          "stopped first. Returns the process id, exe path and log path, or the build errors if the build failed.",
            InputSchema = Schema.Object()
                .Optional("startRoom", Schema.Integer("Room number to start in (engine --startr; omit for the normal game start)."))
                .Optional("rebuild", Schema.Boolean("Force a full rebuild before launching (default false)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(300),
            Handler = args =>
            {
                ctx.RequireGame();
                int? startRoom = args.Has("startRoom") ? (int?)args.Int("startRoom") : null;
                bool rebuild = args.Bool("rebuild", false);

                CompileMessages result = EditorInternals.CompileGameFull(rebuild);
                var structured = StructureMessages(result, out int errors, out int warnings);
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

                if (runner.IsRunning) runner.Stop();

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
                    warnings,
                });
            },
        };

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
                          "(default 500).",
            InputSchema = Schema.Object()
                .Optional("sinceLine", Schema.Integer("0-based line to start from (default 0)."))
                .Optional("limit", Schema.Integer("Maximum lines to return (default 500)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                string logPath = runner.LogPath;
                if (string.IsNullOrEmpty(logPath) || !File.Exists(logPath))
                    throw new ToolException("No game log yet. Launch the game with run_game first.");
                int sinceLine = Math.Max(0, args.Int("sinceLine", 0));
                int limit = Math.Max(1, args.Int("limit", 500));
                string text = ReadShared(logPath);
                LogSlice slice = SliceLog(text, sinceLine, limit);
                return ToolResult.Json(new
                {
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

        /// <summary>Splits log text into lines and returns [sinceLine, sinceLine+maxLines). NextLine lets callers poll.</summary>
        internal static LogSlice SliceLog(string text, int sinceLine, int maxLines)
        {
            string[] lines = (text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
            int total = lines.Length;
            int from = Math.Min(Math.Max(0, sinceLine), total);
            int end = Math.Min(total, from + Math.Max(1, maxLines));
            var slice = new LogSlice { TotalLines = total, FromLine = from, NextLine = end, Truncated = end < total };
            for (int i = from; i < end; i++) slice.Lines.Add(lines[i]);
            return slice;
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs))
                return sr.ReadToEnd();
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
                    foreach (var kv in env) psi.EnvironmentVariables[kv.Key] = kv.Value;

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
