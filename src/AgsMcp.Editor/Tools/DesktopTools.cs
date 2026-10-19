using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using AGS.Types;
using AgsMcp.Editor.Engine;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// The desktop driver: launch any AGS game exe (a release build, no engine plugin) and observe it from outside,
    /// through its window. Nothing here touches the editor model beyond reading the open game's resolution, so every
    /// tool runs off the UI thread.
    /// </summary>
    internal static partial class DesktopTools
    {
        internal const string RendererSoftware = "software";
        private const int WindowWaitMs = 15000;
        private const int MaxBurstFrames = 500;
        private const int MaxBurstSpanMs = 60000;

        /// <summary>What desktop_run launched: the game size the window is measured against.</summary>
        private sealed class Session
        {
            public int GameWidth;
            public int GameHeight;
            public string Renderer;
        }

        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            var session = new Session();
            yield return DesktopRun(ctx, session);
            yield return DesktopStatus(ctx, session);
            yield return DesktopStop(ctx);
            yield return DesktopScreenshot(ctx, session);
            yield return DesktopClick(ctx, session);
            yield return DesktopKey(ctx);
            yield return DesktopType(ctx);
        }

        private static Tool DesktopRun(ToolContext ctx, Session session) => new Tool
        {
            Name = "desktop_run",
            Title = "Run a game exe on the desktop",
            Description = "Launch an AGS game exe windowed, without the engine plugin, to test a release build as a player " +
                          "sees it (build_game mode=release first). 'exe' defaults to the open game's Compiled\\Windows exe. " +
                          "The renderer defaults to 'software', because Direct3D and OpenGL frames read back black from the " +
                          "screen. Logs to its own file (get_game_log source=desktop). Waits up to 15 s for the game window. " +
                          "The game size defaults to the open game's resolution; pass gameWidth/gameHeight for another game. " +
                          "Stops a game this tool started before. Observe it with desktop_screenshot and desktop_status.",
            InputSchema = Schema.Object()
                .Optional("exe", Schema.String("Absolute path of the game exe (default: the open game's Compiled\\Windows\\<name>.exe)."))
                .Optional("startRoom", Schema.Integer("Room number to start in (engine --startr)."))
                .Optional("renderer", Schema.String("Graphics driver (default software).", RendererSoftware, "d3d9", "ogl"))
                .Optional("gameWidth", Schema.Integer("The game's native width in pixels (default: the open game's resolution)."))
                .Optional("gameHeight", Schema.Integer("The game's native height in pixels (default: the open game's resolution)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Timeout = TimeSpan.FromSeconds(60),
            Handler = args =>
            {
                Game game = ctx.Editor.CurrentGame as Game;
                bool gameOpen = game != null && !string.IsNullOrEmpty(game.DirectoryPath);
                string exe = args.String("exe", null);
                if (string.IsNullOrEmpty(exe))
                {
                    if (!gameOpen) throw new ToolException("Pass 'exe': no game is open to take the built exe from.");
                    exe = EditorInternals.WindowsBuildExePath();
                }
                if (!ImageOutput.IsAbsolutePath(exe)) throw new ToolException($"'exe' must be an absolute path, not '{exe}'.");
                if (!File.Exists(exe)) throw new ToolException($"'{exe}' does not exist. Build it with build_game first.");

                int width = args.Int("gameWidth", gameOpen ? game.Settings.CustomResolution.Width : 0);
                int height = args.Int("gameHeight", gameOpen ? game.Settings.CustomResolution.Height : 0);
                if (width <= 0 || height <= 0)
                    throw new ToolException("Pass gameWidth and gameHeight (the game's native resolution): no game is open to read it from.");
                string renderer = args.String("renderer", RendererSoftware).Trim().ToLowerInvariant();
                if (renderer != RendererSoftware && renderer != "d3d9" && renderer != "ogl")
                    throw new ToolException("'renderer' must be software, d3d9 or ogl.");
                int? startRoom = args.Has("startRoom") ? (int?)args.Int("startRoom") : null;

                ctx.DesktopRunner.Stop();
                string runDir = Path.Combine(Path.GetTempPath(), "ags-mcp-desktop");
                string userDataDir = Path.Combine(runDir, "userdata");
                Directory.CreateDirectory(userDataDir);
                string logPath = Path.Combine(runDir, "ags.log");
                try { if (File.Exists(logPath)) File.Delete(logPath); } catch { /* the previous run may still hold it briefly */ }

                string cmdArgs = BuildRunTools.BuildEngineArgs(runDir, userDataDir, startRoom) + " --gfxdriver " + renderer;
                var env = new Dictionary<string, string>
                {
                    ["SDL_RENDER_DRIVER"] = "software",
                    ["AGSMCP_PORT"] = null, // never let the engine plugin listen, even if it was left in the build
                };
                int pid = ctx.DesktopRunner.Start(exe, cmdArgs, Path.GetDirectoryName(exe), logPath, env);
                session.GameWidth = width;
                session.GameHeight = height;
                session.Renderer = renderer;

                var sw = Stopwatch.StartNew();
                IntPtr hwnd = IntPtr.Zero;
                while (sw.ElapsedMilliseconds < WindowWaitMs && ctx.DesktopRunner.IsRunning)
                {
                    hwnd = ctx.DesktopRunner.MainWindow();
                    if (hwnd != IntPtr.Zero) break;
                    Thread.Sleep(100);
                }
                if (hwnd == IntPtr.Zero)
                {
                    bool exited = !ctx.DesktopRunner.IsRunning;
                    ctx.DesktopRunner.Stop();
                    throw new ToolException(exited
                        ? "The game exited before opening a window. Read get_game_log source=desktop."
                        : "The game opened no window within 15 s and was stopped. Read get_game_log source=desktop.");
                }
                return ToolResult.Json(new
                {
                    launched = true,
                    pid,
                    exe,
                    logPath,
                    startRoom,
                    renderer,
                    gameWidth = width,
                    gameHeight = height,
                    windowMs = sw.ElapsedMilliseconds,
                    window = DescribeWindow(hwnd, session),
                });
            },
        };

        private static Tool DesktopStatus(ToolContext ctx, Session session) => new Tool
        {
            Name = "desktop_status",
            Title = "Desktop game status",
            Description = "Whether the game desktop_run started is running, its pid and log, and its window: client area on " +
                          "screen, and where the game image sits in it (scale and offset), as desktop_screenshot uses them.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Handler = args =>
            {
                IntPtr hwnd = ctx.DesktopRunner.MainWindow();
                return ToolResult.Json(new
                {
                    status = ctx.DesktopRunner.Status(),
                    running = ctx.DesktopRunner.IsRunning,
                    renderer = session.Renderer,
                    gameWidth = session.GameWidth,
                    gameHeight = session.GameHeight,
                    window = hwnd == IntPtr.Zero ? null : DescribeWindow(hwnd, session),
                });
            },
        };

        private static Tool DesktopStop(ToolContext ctx) => new Tool
        {
            Name = "desktop_stop",
            Title = "Stop the desktop game",
            Description = "Stop the game desktop_run started.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args => ToolResult.Json(ctx.DesktopRunner.Stop()),
        };

        private static Tool DesktopScreenshot(ToolContext ctx, Session session) => new Tool
        {
            Name = "desktop_screenshot",
            Title = "Desktop game screenshot",
            Description = "Capture the desktop_run game's window as the player sees it, downscaled to the game's native size " +
                          "(nearest neighbour), and return a PNG or write it to outPath. 'frames' > 1 captures a burst every " +
                          "'intervalMs' on a fixed schedule from the first frame (no drift) into the outPath directory: " +
                          "frame-000.png, ... and frames.json with each frame's 'ms'. The window is captured with PrintWindow, " +
                          "which works while it is covered; if that reads back black the window is brought to the front and " +
                          "the screen is copied instead ('method' says which).",
            InputSchema = Schema.Object()
                .Optional(ImageOutput.Arg, Schema.String("Absolute path of the PNG to write (one frame), or of the directory for a burst (required when frames > 1)."))
                .Optional("frames", Schema.Integer("Frames to capture (default 1)."))
                .Optional("intervalMs", Schema.Integer("Burst only: milliseconds between frames (default 100)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            RunOnUiThread = false,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                IntPtr hwnd = ctx.DesktopRunner.MainWindow();
                if (hwnd == IntPtr.Zero) throw new ToolException("No desktop game window. Start one with desktop_run.");
                int frames = args.Int("frames", 1), interval = args.Int("intervalMs", 100);
                if (frames < 1 || frames > MaxBurstFrames) throw new ToolException($"'frames' must be 1..{MaxBurstFrames}.");
                if (interval < 0 || (long)interval * (frames - 1) > MaxBurstSpanMs)
                    throw new ToolException($"'intervalMs' must be 0 or more and the burst at most {MaxBurstSpanMs / 1000} s long.");

                if (frames == 1)
                {
                    string method;
                    using (Bitmap shot = Capture(hwnd, session, out method))
                    using (var ms = new MemoryStream())
                    {
                        shot.Save(ms, ImageFormat.Png);
                        return ImageOutput.Result(args, ms.ToArray(), shot.Width, shot.Height,
                            $"Desktop game window ({method}), {shot.Width}x{shot.Height}.");
                    }
                }

                string dir = args.String(ImageOutput.Arg, null);
                if (string.IsNullOrEmpty(dir)) throw new ToolException("A burst (frames > 1) writes files: pass 'outPath', an absolute directory.");
                if (!ImageOutput.IsAbsolutePath(dir)) throw new ToolException($"'outPath' must be an absolute path, not '{dir}'.");
                if (File.Exists(dir)) throw new ToolException($"'{dir}' is a file; a burst needs a directory.");
                Directory.CreateDirectory(dir);

                var written = new List<object>();
                string lastMethod = null;
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < frames; i++)
                {
                    // Each frame is due at i * interval from the start, so a slow capture does not push the rest back.
                    long wait = (long)i * interval - sw.ElapsedMilliseconds;
                    if (wait > 0) Thread.Sleep((int)wait);
                    long at = sw.ElapsedMilliseconds;
                    string file = $"frame-{i:D3}.png";
                    using (Bitmap shot = Capture(hwnd, session, out lastMethod))
                    {
                        shot.Save(Path.Combine(dir, file), ImageFormat.Png);
                        written.Add(new { index = i, file, ms = at, width = shot.Width, height = shot.Height });
                    }
                }
                var summary = new { intervalMs = interval, method = lastMethod, elapsedMs = sw.ElapsedMilliseconds, frames = written };
                string json = Path.Combine(dir, "frames.json");
                File.WriteAllText(json, JsonConvert.SerializeObject(summary, Formatting.Indented));
                return ToolResult.Json(new { directory = dir, count = written.Count, intervalMs = interval, method = lastMethod, elapsedMs = summary.elapsedMs, json, frames = written });
            },
        };

        // ---- window capture -----------------------------------------------------

        private static object DescribeWindow(IntPtr hwnd, Session session)
        {
            using (new DpiAwareScope())
            {
                Rectangle client = ClientOnScreen(hwnd);
                GameArea area = session.GameWidth > 0 && client.Width > 0
                    ? DesktopGeometry.Area(client.Width, client.Height, session.GameWidth, session.GameHeight) : null;
                return new
                {
                    hwnd = hwnd.ToInt64(),
                    minimized = NativeMethods.IsIconic(hwnd),
                    foreground = NativeMethods.GetForegroundWindow() == hwnd,
                    client = new { x = client.X, y = client.Y, width = client.Width, height = client.Height },
                    scale = area?.Scale,
                    offset = area == null ? null : new { x = area.OffsetX, y = area.OffsetY },
                };
            }
        }

        /// <summary>The window's client area in screen pixels (physical, inside a DPI-aware scope).</summary>
        private static Rectangle ClientOnScreen(IntPtr hwnd)
        {
            if (!NativeMethods.GetClientRect(hwnd, out NativeMethods.RECT r)) return Rectangle.Empty;
            var p = new NativeMethods.POINT { X = 0, Y = 0 };
            NativeMethods.ClientToScreen(hwnd, ref p);
            return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
        }

        /// <summary>
        /// The game image at native size. PrintWindow (client only, full content) first, since it reads a covered
        /// window; a black result (a hardware-rendered window) falls back to restoring the window, bringing it to the
        /// front and copying the screen.
        /// </summary>
        private static Bitmap Capture(IntPtr hwnd, Session session, out string method)
        {
            if (session.GameWidth <= 0) throw new ToolException("The game size is unknown; start the game with desktop_run.");
            using (new DpiAwareScope())
            {
                if (NativeMethods.IsIconic(hwnd)) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                Rectangle client = ClientOnScreen(hwnd);
                if (client.Width <= 0 || client.Height <= 0) throw new ToolException("The game window has no client area (minimized or closing).");
                GameArea area = DesktopGeometry.Area(client.Width, client.Height, session.GameWidth, session.GameHeight);

                using (var full = new Bitmap(client.Width, client.Height, PixelFormat.Format32bppRgb))
                {
                    method = "PrintWindow";
                    using (Graphics g = Graphics.FromImage(full))
                    {
                        IntPtr hdc = g.GetHdc();
                        try { NativeMethods.PrintWindow(hwnd, hdc, NativeMethods.PW_CLIENTONLY | NativeMethods.PW_RENDERFULLCONTENT); }
                        finally { g.ReleaseHdc(hdc); }
                    }
                    if (IsBlack(full))
                    {
                        method = "CopyFromScreen";
                        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                        NativeMethods.SetForegroundWindow(hwnd);
                        Thread.Sleep(50);
                        client = ClientOnScreen(hwnd);
                        using (Graphics g = Graphics.FromImage(full))
                            g.CopyFromScreen(client.Location, Point.Empty, full.Size);
                    }
                    return Downscale(full, area);
                }
            }
        }

        private static Bitmap Downscale(Bitmap full, GameArea area)
        {
            var shot = new Bitmap(area.GameWidth, area.GameHeight, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(shot))
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(full, new Rectangle(0, 0, area.GameWidth, area.GameHeight),
                    new Rectangle(area.OffsetX, area.OffsetY, area.Width, area.Height), GraphicsUnit.Pixel);
            }
            return shot;
        }

        /// <summary>True when a sparse grid of pixels is all black (a capture that read nothing).</summary>
        private static bool IsBlack(Bitmap bmp)
        {
            int stepX = Math.Max(1, bmp.Width / 32), stepY = Math.Max(1, bmp.Height / 32);
            for (int y = stepY / 2; y < bmp.Height; y += stepY)
                for (int x = stepX / 2; x < bmp.Width; x += stepX)
                    if ((bmp.GetPixel(x, y).ToArgb() & 0xFFFFFF) != 0) return false;
            return true;
        }

        /// <summary>
        /// Per-monitor DPI awareness for this thread only (Windows 10 1607+), so window rectangles and screen copies
        /// are in physical pixels on a scaled display. SetProcessDPIAware would change the whole editor process.
        /// </summary>
        private sealed class DpiAwareScope : IDisposable
        {
            private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);
            private readonly IntPtr _previous;

            public DpiAwareScope()
            {
                try { _previous = NativeMethods.SetThreadDpiAwarenessContext(PerMonitorAwareV2); }
                catch (EntryPointNotFoundException) { _previous = IntPtr.Zero; }
            }

            public void Dispose()
            {
                if (_previous == IntPtr.Zero) return;
                try { NativeMethods.SetThreadDpiAwarenessContext(_previous); } catch (EntryPointNotFoundException) { }
            }
        }

        private static partial class NativeMethods
        {
            public const int SW_RESTORE = 9;
            public const int SW_SHOWNOACTIVATE = 4;
            public const uint PW_CLIENTONLY = 1;
            public const uint PW_RENDERFULLCONTENT = 2;

            [StructLayout(LayoutKind.Sequential)]
            public struct RECT { public int Left, Top, Right, Bottom; }

            [StructLayout(LayoutKind.Sequential)]
            public struct POINT { public int X, Y; }

            [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
            [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT point);
            [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
            [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
            [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
            [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
            [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        }
    }
}
