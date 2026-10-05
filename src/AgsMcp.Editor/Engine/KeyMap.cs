using System;
using System.Collections.Generic;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Engine
{
    /// <summary>
    /// Maps human key names ("Space", "F5", "Enter", "A", "7") to AGS eKeyCode values
    /// (from agsdefns.sh) for Game.SimulateKeyPress. A bare integer string is accepted as a raw code.
    /// </summary>
    public static class KeyMap
    {
        private static readonly Dictionary<string, int> Named = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 8,
            ["tab"] = 9,
            ["enter"] = 13,
            ["return"] = 13,
            ["escape"] = 27,
            ["esc"] = 27,
            ["space"] = 32,
            ["f1"] = 359,
            ["f2"] = 360,
            ["f3"] = 361,
            ["f4"] = 362,
            ["f5"] = 363,
            ["f6"] = 364,
            ["f7"] = 365,
            ["f8"] = 366,
            ["f9"] = 367,
            ["f10"] = 368,
            ["f11"] = 433,
            ["f12"] = 434,
            ["home"] = 371,
            ["up"] = 372,
            ["uparrow"] = 372,
            ["pageup"] = 373,
            ["left"] = 375,
            ["leftarrow"] = 375,
            ["right"] = 377,
            ["rightarrow"] = 377,
            ["end"] = 379,
            ["down"] = 380,
            ["downarrow"] = 380,
            ["pagedown"] = 381,
            ["insert"] = 382,
            ["delete"] = 383,
            ["del"] = 383,
        };

        /// <summary>Resolve a key name to an AGS key code, or throw a ToolException for an unknown key.</summary>
        public static int Resolve(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new ToolException("A key name is required (e.g. \"Space\", \"Enter\", \"F5\", \"A\", or a number).");
            key = key.Trim();

            if (Named.TryGetValue(key, out int named))
                return named;

            if (key.Length == 1)
            {
                char c = key[0];
                if (c >= 'a' && c <= 'z') return c - 'a' + 65; // eKeyA == 65
                if (c >= 'A' && c <= 'Z') return c;
                if (c >= '0' && c <= '9') return c; // eKey0 == '0' == 48
            }

            if (int.TryParse(key, out int raw) && raw > 0)
                return raw;

            throw new ToolException("Unknown key \"" + key + "\". Use a letter, digit, or a name like Space/Enter/Escape/F1..F12/Up/Down/Left/Right.");
        }
    }
}
