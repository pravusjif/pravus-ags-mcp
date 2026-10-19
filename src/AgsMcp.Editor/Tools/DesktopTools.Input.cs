using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using AgsMcp.Editor.Engine;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// The desktop driver's input: real mouse and keyboard events (SendInput) into the desktop_run game's window, as a
    /// player would give them. Input goes to whatever window is in front, so every tool first brings the game window
    /// to the front and refuses, sending nothing, when it cannot.
    /// </summary>
    internal static partial class DesktopTools
    {
        private const int FocusWaitMs = 1000;
        private const int ClickStepMs = 60;
        private const int TypeStepMs = 15;
        private const int MaxTypeUnits = 1000;
        private const int MaxHoldMs = 10000;

        private static Tool DesktopClick(ToolContext ctx, Session session) => new Tool
        {
            Name = "desktop_click",
            Title = "Click in the desktop game",
            Description = "Click the desktop_run game at a point in game coordinates (the native resolution, as " +
                          "desktop_screenshot shows it) with the real mouse: the window is brought to the front, the cursor " +
                          "moved there, and the button pressed and released. Refuses when the window cannot be brought to " +
                          "the front or another window covers the point. The game reacts on its next frames: confirm with " +
                          "desktop_screenshot or get_game_log source=desktop.",
            InputSchema = Schema.Object()
                .Required("x", Schema.Integer("Game x in pixels, 0 = left edge."))
                .Required("y", Schema.Integer("Game y in pixels, 0 = top edge."))
                .Optional("button", Schema.String("Mouse button (default left).", "left", "right", "middle"))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Handler = args =>
            {
                IntPtr hwnd = GameWindow(ctx);
                if (session.GameWidth <= 0) throw new ToolException("The game size is unknown; start the game with desktop_run.");
                int x = args.Int("x"), y = args.Int("y");
                if (x < 0 || y < 0 || x >= session.GameWidth || y >= session.GameHeight)
                    throw new ToolException($"({x},{y}) is outside the {session.GameWidth}x{session.GameHeight} game.");
                string button = args.String("button", "left").Trim().ToLowerInvariant();
                uint down, up;
                switch (button)
                {
                    case "left": down = NativeMethods.MOUSEEVENTF_LEFTDOWN; up = NativeMethods.MOUSEEVENTF_LEFTUP; break;
                    case "right": down = NativeMethods.MOUSEEVENTF_RIGHTDOWN; up = NativeMethods.MOUSEEVENTF_RIGHTUP; break;
                    case "middle": down = NativeMethods.MOUSEEVENTF_MIDDLEDOWN; up = NativeMethods.MOUSEEVENTF_MIDDLEUP; break;
                    default: throw new ToolException("'button' must be left, right or middle.");
                }

                using (new DpiAwareScope())
                {
                    bool tookFocus = Focus(hwnd);
                    Rectangle client = ClientOnScreen(hwnd);
                    if (client.Width <= 0 || client.Height <= 0) throw new ToolException("The game window has no client area (minimized or closing).");
                    GameArea area = DesktopGeometry.Area(client.Width, client.Height, session.GameWidth, session.GameHeight);
                    DesktopGeometry.GameToScreen(area, client.X, client.Y, x, y, out int sx, out int sy);
                    if (!client.Contains(sx, sy))
                        throw new ToolException($"({x},{y}) falls outside the window's client area (the window is smaller than the game).");
                    IntPtr at = NativeMethods.WindowFromPoint(new NativeMethods.POINT { X = sx, Y = sy });
                    if (NativeMethods.GetAncestor(at, NativeMethods.GA_ROOT) != hwnd)
                        throw new ToolException($"Another window covers the game at screen ({sx},{sy}); nothing was clicked.");

                    NativeMethods.SetCursorPos(sx, sy);
                    Thread.Sleep(ClickStepMs); // the game sees the cursor move before the press
                    SendMouse(down);
                    Thread.Sleep(ClickStepMs);
                    SendMouse(up);
                    return ToolResult.Json(new { clicked = true, x, y, button, screen = new { x = sx, y = sy }, tookFocus });
                }
            },
        };

        private static Tool DesktopKey(ToolContext ctx) => new Tool
        {
            Name = "desktop_key",
            Title = "Press a key in the desktop game",
            Description = "Press and release a key in the desktop_run game with the real keyboard, after bringing its window " +
                          "to the front. 'key' is a letter, digit, F1..F24, a name (Space, Enter, Escape, Tab, Backspace, " +
                          "Up/Down/Left/Right, Home, End, PageUp, PageDown, Insert, Delete, Numpad0..9), or a virtual-key code " +
                          "as 0xNN, with optional modifiers: \"Ctrl+Q\", \"Shift+Alt+F4\". The navigation keys are sent " +
                          "as extended keys, so NumLock does not turn them into digits. 'holdMs' keeps the key down. For " +
                          "text use desktop_type.",
            InputSchema = Schema.Object()
                .Required("key", Schema.String("Key name, optionally with modifiers (\"Ctrl+Q\")."))
                .Optional("holdMs", Schema.Integer("Milliseconds to hold the key down (default 50)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Timeout = TimeSpan.FromSeconds(30),
            Handler = args =>
            {
                KeyChord chord = VirtualKeys.ResolveChord(args.String("key"));
                int hold = args.Int("holdMs", 50);
                if (hold < 0 || hold > MaxHoldMs) throw new ToolException($"'holdMs' must be 0..{MaxHoldMs}.");
                IntPtr hwnd = GameWindow(ctx);
                bool tookFocus;
                using (new DpiAwareScope()) tookFocus = Focus(hwnd);

                foreach (VirtualKey m in chord.Modifiers) SendKey(m, false);
                SendKey(chord.Key, false);
                Thread.Sleep(hold);
                SendKey(chord.Key, true);
                for (int i = chord.Modifiers.Count - 1; i >= 0; i--) SendKey(chord.Modifiers[i], true);
                return ToolResult.Json(new
                {
                    pressed = true,
                    key = chord.Key.Name,
                    vk = "0x" + chord.Key.Code.ToString("X2"),
                    scan = NativeMethods.MapVirtualKey(chord.Key.Code, NativeMethods.MAPVK_VK_TO_VSC),
                    extended = chord.Key.Extended,
                    modifiers = chord.Modifiers.Select(m => m.Name).ToArray(),
                    holdMs = hold,
                    tookFocus,
                });
            },
        };

        private static Tool DesktopType(ToolContext ctx) => new Tool
        {
            Name = "desktop_type",
            Title = "Type text into the desktop game",
            Description = "Type text into the desktop_run game as Unicode keyboard input (one character at a time, after " +
                          "bringing its window to the front), which reaches text boxes and on_text_input like typing does. " +
                          "Text only: press Enter, Tab or Backspace with desktop_key.",
            InputSchema = Schema.Object()
                .Required("text", Schema.String($"Text to type (at most {MaxTypeUnits} characters, no control characters)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            RunOnUiThread = false,
            Timeout = TimeSpan.FromSeconds(60),
            Handler = args =>
            {
                string text = args.String("text");
                if (string.IsNullOrEmpty(text)) throw new ToolException("'text' is empty.");
                if (text.Length > MaxTypeUnits) throw new ToolException($"'text' is longer than {MaxTypeUnits} characters; type it in parts.");
                for (int i = 0; i < text.Length; i++)
                    if (char.IsControl(text[i]))
                        throw new ToolException($"'text' has a control character at {i}: send Enter, Tab or Backspace with desktop_key.");
                IntPtr hwnd = GameWindow(ctx);
                bool tookFocus;
                using (new DpiAwareScope()) tookFocus = Focus(hwnd);

                for (int i = 0; i < text.Length; i++)
                {
                    // A surrogate pair goes as two units, which Windows joins into one character.
                    SendUnicode(text[i], false);
                    SendUnicode(text[i], true);
                    Thread.Sleep(TypeStepMs);
                }
                return ToolResult.Json(new { typed = true, units = text.Length, tookFocus });
            },
        };

        // ---- focus and SendInput --------------------------------------------------

        private static IntPtr GameWindow(ToolContext ctx)
        {
            IntPtr hwnd = ctx.DesktopRunner.MainWindow();
            if (hwnd == IntPtr.Zero) throw new ToolException("No desktop game window. Start one with desktop_run.");
            return hwnd;
        }

        /// <summary>
        /// Bring the game window to the front, or throw. Windows only lets the process that had the last input take
        /// the foreground, so an Alt tap (which a window in front of the game receives, not the game) goes first.
        /// True when the window was minimised or not in front before.
        /// </summary>
        private static bool Focus(IntPtr hwnd)
        {
            // A minimised window can still be the foreground window, so a restore alone also counts as taking focus.
            bool restored = NativeMethods.IsIconic(hwnd);
            if (restored) NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            if (NativeMethods.GetForegroundWindow() == hwnd)
            {
                if (restored) Thread.Sleep(100);
                return restored;
            }

            NativeMethods.keybd_event((byte)VirtualKeys.Menu, 0, 0, UIntPtr.Zero);
            NativeMethods.keybd_event((byte)VirtualKeys.Menu, 0, NativeMethods.KEYEVENTF_KEYUP, UIntPtr.Zero);
            NativeMethods.SetForegroundWindow(hwnd);
            var until = DateTime.UtcNow.AddMilliseconds(FocusWaitMs);
            while (NativeMethods.GetForegroundWindow() != hwnd)
            {
                if (DateTime.UtcNow > until)
                    throw new ToolException("Could not bring the game window to the front (a locked desktop, or another window " +
                                            "holding the foreground); nothing was sent.");
                Thread.Sleep(20);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            Thread.Sleep(100); // the game handles its activation before the input arrives
            return true;
        }

        private static void SendMouse(uint flags) =>
            Send(new NativeMethods.INPUT { type = NativeMethods.INPUT_MOUSE, u = new NativeMethods.InputUnion { mi = new NativeMethods.MOUSEINPUT { dwFlags = flags } } });

        private static void SendKey(VirtualKey key, bool up)
        {
            uint flags = (key.Extended ? NativeMethods.KEYEVENTF_EXTENDEDKEY : 0) | (up ? NativeMethods.KEYEVENTF_KEYUP : 0);
            var ki = new NativeMethods.KEYBDINPUT
            {
                wVk = key.Code,
                wScan = (ushort)NativeMethods.MapVirtualKey(key.Code, NativeMethods.MAPVK_VK_TO_VSC),
                dwFlags = flags,
            };
            Send(new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, u = new NativeMethods.InputUnion { ki = ki } });
        }

        private static void SendUnicode(char unit, bool up)
        {
            var ki = new NativeMethods.KEYBDINPUT { wScan = unit, dwFlags = NativeMethods.KEYEVENTF_UNICODE | (up ? NativeMethods.KEYEVENTF_KEYUP : 0) };
            Send(new NativeMethods.INPUT { type = NativeMethods.INPUT_KEYBOARD, u = new NativeMethods.InputUnion { ki = ki } });
        }

        private static void Send(NativeMethods.INPUT input)
        {
            if (NativeMethods.SendInput(1, new[] { input }, Marshal.SizeOf(typeof(NativeMethods.INPUT))) != 1)
                throw new ToolException("Windows refused the input (SendInput error " + Marshal.GetLastWin32Error() +
                                        "): the desktop may be locked, or the game runs with higher privileges than the editor.");
        }

        private static partial class NativeMethods
        {
            public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
            public const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004;
            public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010;
            public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040;
            public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;
            public const uint MAPVK_VK_TO_VSC = 0;
            public const uint GA_ROOT = 2;

            [StructLayout(LayoutKind.Sequential)]
            public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

            [StructLayout(LayoutKind.Sequential)]
            public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

            [StructLayout(LayoutKind.Explicit)]
            public struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

            [StructLayout(LayoutKind.Sequential)]
            public struct INPUT { public uint type; public InputUnion u; }

            [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
            [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
            [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extraInfo);
            [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
            [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
            [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
        }
    }
}
