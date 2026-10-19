using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Text tools: measure_text (a WFN font's metrics computed here, as the engine computes them, so text can be
    /// fitted without running the game) and list_strings (the game's player-visible text, walked as the editor's
    /// translation source walks it: TextProcessingHelper.ProcessAllGameText and RoomsComponent's room pass).
    /// </summary>
    internal static class TextTools
    {
        private static readonly string[] TextTypes = { "Script", "DialogScript", "Message", "DialogOption", "ItemDescription" };

        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return MeasureText(ctx);
            yield return ListStrings(ctx);
        }

        // ---------------- measure_text ----------------

        private static Tool MeasureText(ToolContext ctx) => new Tool
        {
            Name = "measure_text",
            Title = "Measure text",
            Description = "Measure text in a WFN (bitmap) font as the engine draws it, without running the game: the font file's " +
                          "glyph widths times SizeMultiplier, plus an automatic outline (2x AutoOutlineThickness) or the wider of an " +
                          "outline font. '[' is a line break and '\\[' a literal '['. With maxWidth the text is wrapped as the engine " +
                          "wraps it (at maxWidth-1, at the last space, else inside a word), e.g. maxWidth = a label's width. Returns " +
                          "{width (widest line), height (all lines), fontHeight, lineSpacing, lines:[{text, width}]}. TTF fonts are " +
                          "refused: use game_text_extent with the game running.",
            InputSchema = Schema.Object()
                .Required("font", Schema.String("Font ID (\"0\") or script name (\"eFontNormal\")."))
                .Required("text", Schema.String("The text, as a script string would hold it."))
                .Optional("maxWidth", Schema.Integer("Wrap width in pixels (game resolution). Omit to break only at '['."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                var font = (Font)EntityRegistry.Resolve(EntityRegistry.Get("font"), game, args.Raw["font"]);
                int maxWidth = args.Int("maxWidth", 0);
                if (args.Has("maxWidth") && maxWidth < 1) throw new ToolException("'maxWidth' must be 1 or greater.");

                var notes = new List<string>();
                var metrics = new WfnTextMetrics
                {
                    Font = LoadWfn(game, font),
                    SizeMultiplier = Math.Max(1, font.SizeMultiplier),
                    AutoOutlineThickness = font.OutlineStyle == FontOutlineStyle.Automatic ? Math.Max(1, font.AutoOutlineThickness) : 0,
                    LineSpacing = font.LineSpacing,
                };
                if (font.OutlineStyle == FontOutlineStyle.UseOutlineFont)
                {
                    Font outline = game.Fonts.FirstOrDefault(f => f.ID == font.OutlineFont);
                    if (outline == null || IsTtf(game, outline))
                        notes.Add($"Outline font {font.OutlineFont} is not a WFN font here; its size is not included.");
                    else
                    {
                        metrics.OutlineFont = LoadWfn(game, outline);
                        metrics.OutlineSizeMultiplier = Math.Max(1, outline.SizeMultiplier);
                    }
                }

                Encoding encoding = game.UnicodeMode ? null : game.TextEncoding;
                List<string> lines = metrics.Wrap(ToCodes(args.String("text"), encoding), maxWidth);
                if (lines == null)
                {
                    notes.Add($"Not even one character fits in maxWidth {maxWidth}; the engine draws nothing.");
                    lines = new List<string>();
                }
                var measured = lines.Select(l => new { text = FromCodes(l, encoding), width = metrics.Width(l) }).ToList();
                return ToolResult.Json(new
                {
                    font = font.ID,
                    width = measured.Count == 0 ? 0 : measured.Max(l => l.width),
                    height = metrics.LinesHeight(measured.Count),
                    fontHeight = metrics.FontHeightOutlined,
                    lineSpacing = metrics.LineStep,
                    maxWidth = args.Has("maxWidth") ? (int?)maxWidth : null,
                    lineCount = measured.Count,
                    lines = measured,
                    notes,
                });
            },
        };

        private static bool IsTtf(Game game, Font font) =>
            FontImport.Classify(font.ProjectFilename) == FontFileKind.Ttf ||
            (!File.Exists(Path.Combine(game.DirectoryPath, font.WFNFileName)) && File.Exists(Path.Combine(game.DirectoryPath, font.TTFFileName)));

        private static WfnFont LoadWfn(Game game, Font font)
        {
            if (IsTtf(game, font))
                throw new ToolException($"Font {font.ID} is a TTF font, which only the engine can measure: use game_text_extent with the game running.");
            string path = Path.Combine(game.DirectoryPath, font.WFNFileName);
            if (!File.Exists(path)) throw new ToolException($"Font {font.ID}'s file {font.WFNFileName} is missing from the game folder.");
            try { return WfnFont.Parse(File.ReadAllBytes(path)); }
            catch (FormatException e) { throw new ToolException($"{font.WFNFileName}: {e.Message}"); }
        }

        /// <summary>
        /// The text as the engine's character codes: code points in a Unicode (UTF-8) game, the encoding's bytes
        /// otherwise (one char per byte).
        /// </summary>
        private static string ToCodes(string text, Encoding ansi) =>
            ansi == null ? text : new string(ansi.GetBytes(text ?? string.Empty).Select(b => (char)b).ToArray());

        private static string FromCodes(string codes, Encoding ansi) =>
            ansi == null ? codes : ansi.GetString(codes.Select(c => (byte)c).ToArray());

        // ---------------- list_strings ----------------

        private sealed class GameString
        {
            public string text;
            public string type;
            public string source;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public int? line;
            [JsonProperty(NullValueHandling = NullValueHandling.Ignore)] public string speaker;
        }

        private static Tool ListStrings(ToolContext ctx) => new Tool
        {
            Name = "list_strings",
            Title = "List game strings",
            Description = "List the game's player-visible text, as the editor's translation source collects it: the game name, " +
                          "dialog options and dialog scripts (speech lines with their speaker, and string literals in script lines), " +
                          "string literals in every module and room script, GUI label and button texts, character names, inventory " +
                          "descriptions, text custom properties and global messages. rooms=true adds room messages and hotspot, " +
                          "object and text-property values; that loads every room in the editor in turn (the editor holds one), so " +
                          "it refuses while the loaded room has unsaved changes and reopens the originally loaded room at the end. " +
                          "Script text is as written (escapes not decoded). Each item: {text, type, source, line?, speaker?}. " +
                          "Pair with measure_text to check that lines fit.",
            InputSchema = Schema.Object()
                .Optional("type", Schema.String("Only this kind of text.", TextTypes))
                .Optional("contains", Schema.String("Only texts containing this (case-insensitive)."))
                .Optional("rooms", Schema.Boolean("Also open every room for its messages and descriptions (default false)."))
                .Optional("limit", Schema.Integer("Maximum items to return (default 200)."))
                .Optional("offset", Schema.Integer("Items to skip (default 0)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Timeout = TimeSpan.FromMinutes(5),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                string type = args.String("type", null);
                if (type != null)
                {
                    type = TextTypes.FirstOrDefault(t => string.Equals(t, type, StringComparison.OrdinalIgnoreCase))
                        ?? throw new ToolException($"Unknown type '{type}'. Use one of: {string.Join(", ", TextTypes)}.");
                }
                string contains = args.String("contains", null);
                int limit = args.Int("limit", 200), offset = args.Int("offset", 0);
                if (limit < 1 || offset < 0) throw new ToolException("'limit' must be 1 or greater and 'offset' 0 or greater.");

                List<GameString> all = CollectGameStrings(game);
                if (args.Bool("rooms", false)) all.AddRange(CollectRoomStrings(ctx, game));

                List<GameString> matching = all
                    .Where(s => type == null || s.type == type)
                    .Where(s => string.IsNullOrEmpty(contains) || s.text.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                List<GameString> page = matching.Skip(offset).Take(limit).ToList();
                return ToolResult.Json(new
                {
                    total = matching.Count,
                    offset,
                    count = page.Count,
                    truncated = offset + page.Count < matching.Count,
                    strings = page,
                });
            },
        };

        private static List<GameString> CollectGameStrings(Game game)
        {
            var result = new List<GameString>();
            Add(result, game.Settings.GameName, "ItemDescription", "settings GameName");

            foreach (Dialog dialog in game.RootDialogFolder.AllItemsFlat)
            {
                foreach (DialogOption option in dialog.Options)
                    Add(result, option.Text, "DialogOption", $"dialog {dialog.Name} option {option.ID}");
                AddDialogScript(result, game, dialog);
            }

            foreach (ScriptAndHeader sh in game.RootScriptFolder.AllItemsFlat)
                AddLiterals(result, sh.Script.Text, sh.Script.FileName);
            foreach (UnloadedRoom room in game.Rooms)
            {
                string text;
                try { text = ScriptTools.RoomScriptText(game, room); }
                catch (IOException) { continue; } // no script file
                AddLiterals(result, text, room.ScriptFileName);
            }

            foreach (CustomPropertySchemaItem def in TextProperties(game))
                Add(result, def.DefaultValue, "ItemDescription", $"customproperty {def.Name} default");

            foreach (GUI gui in game.RootGUIFolder.AllItemsFlat)
            {
                foreach (GUIControl control in gui.Controls)
                {
                    string text = (control as GUILabel)?.Text ?? (control as GUIButton)?.Text;
                    Add(result, text, "ItemDescription", $"gui {gui.Name}:{control.Name}");
                }
            }

            foreach (Character c in game.RootCharacterFolder.AllItemsFlat)
            {
                Add(result, c.RealName, "ItemDescription", $"character {c.ScriptName} RealName");
                AddProperties(result, game, c.Properties, $"character {c.ScriptName}");
            }
            foreach (InventoryItem item in game.RootInventoryItemFolder.AllItemsFlat)
            {
                Add(result, item.Description, "ItemDescription", $"inventory {item.Name} Description");
                AddProperties(result, game, item.Properties, $"inventory {item.Name}");
            }

            for (int i = 0; i < game.GlobalMessages.Length; i++)
                Add(result, game.GlobalMessages[i], "Message", $"global message {500 + i}");
            return result;
        }

        /// <summary>RoomsComponent's ProcessAllGameTexts pass, minus the script (already read from disk or memory).</summary>
        private static List<GameString> CollectRoomStrings(ToolContext ctx, Game game)
        {
            var result = new List<GameString>();
            Room original = EditorInternals.LoadedRoom(ctx.Editor);
            foreach (UnloadedRoom unloaded in game.Rooms.ToList())
            {
                Room room = RoomSession.Open(ctx, unloaded.Number);
                string prefix = "room " + room.Number;
                for (int i = 0; i < room.Messages.Count; i++)
                    Add(result, room.Messages[i].Text, "Message", $"{prefix} message {i}");
                AddProperties(result, game, room.Properties, prefix);
                foreach (RoomHotspot h in room.Hotspots)
                {
                    Add(result, h.Description, "ItemDescription", $"{prefix} hotspot {h.Name} Description");
                    AddProperties(result, game, h.Properties, $"{prefix} hotspot {h.Name}");
                }
                foreach (RoomObject o in room.Objects)
                {
                    Add(result, o.Description, "ItemDescription", $"{prefix} object {o.Name} Description");
                    AddProperties(result, game, o.Properties, $"{prefix} object {o.Name}");
                }
            }
            if (original != null)
            {
                try { RoomSession.Open(ctx, original.Number); }
                catch (Exception e) { Logger.Write($"list_strings could not reopen room {original.Number}: {e.Message}"); }
            }
            return result;
        }

        /// <summary>
        /// GameSpeechProcessor.ProcessDialogScript: an indented line is script (its string literals count); otherwise
        /// "name: text" is a speech line when the name is a character, "player" or "narrator".
        /// </summary>
        private static void AddDialogScript(List<GameString> result, Game game, Dialog dialog)
        {
            string source = "dialog " + dialog.Name;
            string[] lines = ScriptText.NormalizeNewlines(dialog.Script ?? string.Empty, "\n").Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string raw = lines[i];
                if ((raw.StartsWith(" ") || raw.StartsWith("\t")) && raw.Trim().Length > 0)
                {
                    foreach (ScriptText.StringLiteral lit in ScriptText.ExtractStringLiterals(raw))
                        Add(result, lit.Text, "DialogScript", source, i + 1);
                    continue;
                }
                string line = raw.Trim();
                if (line.Contains("//")) continue; // as the editor: a commented line is not read for speech
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                int nameLength = 0;
                while (nameLength < line.Length && (char.IsLetterOrDigit(line[nameLength]) || line[nameLength] == '_')) nameLength++;
                string speaker = line.Substring(0, nameLength);
                if (!IsSpeaker(game, speaker)) continue;
                Add(result, line.Substring(colon + 1).Trim(), "DialogScript", source, i + 1, speaker);
            }
        }

        private static bool IsSpeaker(Game game, string name)
        {
            if (name.Length == 0) return false;
            if (name.Equals("player", StringComparison.OrdinalIgnoreCase) || name.Equals("narrator", StringComparison.OrdinalIgnoreCase)) return true;
            return game.RootCharacterFolder.AllItemsFlat.Any(c =>
                c.ScriptName.Equals(name, StringComparison.OrdinalIgnoreCase) || c.ScriptName.Equals("c" + name, StringComparison.OrdinalIgnoreCase));
        }

        private static void AddLiterals(List<GameString> result, string script, string source)
        {
            foreach (ScriptText.StringLiteral lit in ScriptText.ExtractStringLiterals(script))
                Add(result, lit.Text, "Script", source, lit.Line);
        }

        private static IEnumerable<CustomPropertySchemaItem> TextProperties(Game game) =>
            game.PropertySchema.PropertyDefinitions.Where(d => d.Type == CustomPropertyType.Text);

        private static void AddProperties(List<GameString> result, Game game, CustomProperties props, string owner)
        {
            if (props == null) return;
            foreach (CustomPropertySchemaItem def in TextProperties(game))
            {
                if (props.PropertyValues.TryGetValue(def.Name, out CustomProperty prop))
                    Add(result, prop.Value, "ItemDescription", $"{owner} property {def.Name}");
            }
        }

        private static void Add(List<GameString> result, string text, string type, string source, int? line = null, string speaker = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            result.Add(new GameString { text = text, type = type, source = source, line = line, speaker = speaker });
        }
    }
}
