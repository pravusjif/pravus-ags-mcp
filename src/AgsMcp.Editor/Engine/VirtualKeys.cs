using System;
using System.Collections.Generic;
using System.Globalization;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Engine
{
    /// <summary>A Windows virtual key, and whether it needs the extended-key flag (the navigation block).</summary>
    internal sealed class VirtualKey
    {
        public string Name;
        public ushort Code;
        public bool Extended;
    }

    /// <summary>A key with the modifiers held while it is pressed ("Ctrl+Q").</summary>
    internal sealed class KeyChord
    {
        public VirtualKey Key;
        public List<VirtualKey> Modifiers = new List<VirtualKey>();
    }

    /// <summary>
    /// Pure key-name table for the desktop driver: human names to Windows virtual-key codes for real keyboard input
    /// (SendInput), unlike KeyMap, which maps to AGS key codes for the engine plugin.
    /// </summary>
    internal static class VirtualKeys
    {
        public const ushort Shift = 0x10, Control = 0x11, Menu = 0x12;

        private static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            ["backspace"] = 0x08,
            ["tab"] = 0x09,
            ["enter"] = 0x0D,
            ["return"] = 0x0D,
            ["shift"] = Shift,
            ["ctrl"] = Control,
            ["control"] = Control,
            ["alt"] = Menu,
            ["pause"] = 0x13,
            ["escape"] = 0x1B,
            ["esc"] = 0x1B,
            ["space"] = 0x20,
            ["pageup"] = 0x21,
            ["pagedown"] = 0x22,
            ["end"] = 0x23,
            ["home"] = 0x24,
            ["left"] = 0x25,
            ["leftarrow"] = 0x25,
            ["up"] = 0x26,
            ["uparrow"] = 0x26,
            ["right"] = 0x27,
            ["rightarrow"] = 0x27,
            ["down"] = 0x28,
            ["downarrow"] = 0x28,
            ["printscreen"] = 0x2C,
            ["insert"] = 0x2D,
            ["delete"] = 0x2E,
            ["del"] = 0x2E,
            ["numpad0"] = 0x60,
            ["numpad1"] = 0x61,
            ["numpad2"] = 0x62,
            ["numpad3"] = 0x63,
            ["numpad4"] = 0x64,
            ["numpad5"] = 0x65,
            ["numpad6"] = 0x66,
            ["numpad7"] = 0x67,
            ["numpad8"] = 0x68,
            ["numpad9"] = 0x69,
            ["multiply"] = 0x6A,
            ["add"] = 0x6B,
            ["subtract"] = 0x6D,
            ["decimal"] = 0x6E,
            ["divide"] = 0x6F,
        };

        /// <summary>
        /// PageUp, PageDown, End, Home, the arrows, Insert and Delete: without the extended flag Windows reads them as
        /// the keypad keys, which are digits with NumLock on.
        /// </summary>
        public static bool IsExtended(ushort vk) => (vk >= 0x21 && vk <= 0x28) || vk == 0x2D || vk == 0x2E;

        /// <summary>One key: a name, a letter, a digit, F1..F24, or a raw code as 0xNN. Throws a ToolException otherwise.</summary>
        public static VirtualKey Resolve(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ToolException("A key name is required (e.g. \"Space\", \"Enter\", \"F5\", \"A\", \"Ctrl+Q\").");
            string key = name.Trim();
            ushort vk;
            if (Named.TryGetValue(key, out ushort named)) vk = named;
            else if (key.Length == 1 && char.IsLetterOrDigit(key[0]) && key[0] < 128) vk = char.ToUpperInvariant(key[0]); // VK 'A'..'Z', '0'..'9'
            else if ((key[0] == 'F' || key[0] == 'f') && int.TryParse(key.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out int f) && f >= 1 && f <= 24)
                vk = (ushort)(0x70 + f - 1);
            else if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
                     ushort.TryParse(key.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort raw) && raw > 0 && raw < 0xFF)
                vk = raw;
            else
                throw new ToolException("Unknown key \"" + key + "\". Use a letter, digit, F1..F24, a name like Space/Enter/Escape/Tab/" +
                                        "Backspace/Up/Down/Left/Right/Home/End/PageUp/PageDown/Insert/Delete, or a virtual-key code as 0xNN.");
            return new VirtualKey { Name = key, Code = vk, Extended = IsExtended(vk) };
        }

        /// <summary>A key with optional modifiers joined by '+': "Ctrl+Q", "Shift+Alt+F4". "+" alone is not a key name here.</summary>
        public static KeyChord ResolveChord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return new KeyChord { Key = Resolve(text) };
            string[] parts = text.Split('+');
            var chord = new KeyChord { Key = Resolve(parts[parts.Length - 1]) };
            for (int i = 0; i < parts.Length - 1; i++)
            {
                VirtualKey m = Resolve(parts[i]);
                if (m.Code != Shift && m.Code != Control && m.Code != Menu)
                    throw new ToolException($"\"{parts[i].Trim()}\" in \"{text.Trim()}\" is not a modifier: only Shift, Ctrl and Alt go before '+'.");
                if (chord.Modifiers.Exists(x => x.Code == m.Code))
                    throw new ToolException($"\"{parts[i].Trim()}\" is repeated in \"{text.Trim()}\".");
                chord.Modifiers.Add(m);
            }
            return chord;
        }
    }
}
