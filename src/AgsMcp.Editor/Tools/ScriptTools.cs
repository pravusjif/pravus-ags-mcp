using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AGS.Types;
using AGS.Types.AutoComplete;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Tools
{
    /// <summary>Reading, writing, compiling and looking up scripts in the open project.</summary>
    internal static class ScriptTools
    {
        private const string DefaultHeaderText = "// new module header\r\n";
        private const string DefaultScriptText = "// new module script\r\n";

        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ListScripts(ctx);
            yield return ReadScript(ctx);
            yield return WriteScript(ctx);
            yield return EditScript(ctx);
            yield return CreateScriptModule(ctx);
            yield return Compile(ctx);
            yield return ScriptApiLookup(ctx);
        }

        private static Tool ListScripts(ToolContext ctx) => new Tool
        {
            Name = "list_scripts",
            Title = "List scripts",
            Description = "List the script modules in the project, in compile order, and the room scripts. Each module has a " +
                          "header (.ash) and a body (.asc); a room script is a body only, named \"roomN\". Use these names with " +
                          "read_script, write_script, edit_script and compile.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                var modules = game.ScriptsAndHeaders.Select(sh => new
                {
                    name = ModuleName(sh),
                    isGlobalScript = sh.Script != null && sh.Script.FileName == Script.GLOBAL_SCRIPT_FILE_NAME,
                    header = sh.Header == null ? null : new { file = sh.Header.FileName, lines = ScriptText.CountLines(sh.Header.Text) },
                    script = sh.Script == null ? null : new { file = sh.Script.FileName, lines = ScriptText.CountLines(sh.Script.Text) },
                }).ToList();
                var rooms = game.Rooms.Cast<UnloadedRoom>().OrderBy(r => r.Number).Select(r => new
                {
                    name = Path.GetFileNameWithoutExtension(r.ScriptFileName),
                    room = r.Number,
                    file = r.ScriptFileName,
                    lines = r.Script != null || File.Exists(Path.Combine(game.DirectoryPath, r.ScriptFileName)) ? ScriptText.CountLines(RoomScriptText(game, r)) : 0,
                }).ToList();
                return ToolResult.Json(new { count = modules.Count, modules, roomScripts = rooms });
            },
        };

        private static Tool ReadScript(ToolContext ctx) => new Tool
        {
            Name = "read_script",
            Title = "Read script",
            Description = "Read a script's text. 'name' is a module name (e.g. \"GlobalScript\"), a room script (\"room1\") " +
                          "or a filename (\"GlobalScript.asc\"/\".ash\", \"room1.asc\"). Reads the body (.asc) by default; set header=true for the header. " +
                          "Optionally restrict to a 1-based inclusive line range with startLine/endLine.",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("Module name or script filename."))
                .Optional("header", Schema.Boolean("Read the header (.ash) instead of the body (.asc). Default false."))
                .Optional("startLine", Schema.Integer("First line to return (1-based, inclusive)."))
                .Optional("endLine", Schema.Integer("Last line to return (1-based, inclusive). Omit for end of file."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                Script script = Resolve(game, args.String("name"), args.Bool("header", false));
                int startLine = args.Int("startLine", 1);
                int endLine = args.Int("endLine", 0);
                string text = ScriptText.ExtractLineRange(script.Text, startLine, endLine, out int totalLines);
                return ToolResult.Json(new
                {
                    file = script.FileName,
                    isHeader = script.IsHeader,
                    totalLines,
                    startLine = Math.Max(1, startLine),
                    endLine = endLine <= 0 ? totalLines : Math.Min(endLine, totalLines),
                    text,
                });
            },
        };

        private static Tool WriteScript(ToolContext ctx) => new Tool
        {
            Name = "write_script",
            Title = "Write script",
            Description = "Replace the entire text of a script module's body (.asc) or header (.ash), or of a room script " +
                          "(\"roomN\"), and save it to disk. Overwrites the whole file; use edit_script for a targeted change. " +
                          "Run compile afterwards to check it (rooms are recompiled on the next build).",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("Module name or script filename."))
                .Required("text", Schema.String("The full new contents of the file."))
                .Optional("header", Schema.Boolean("Write the header (.ash) instead of the body (.asc). Default false."))
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                Script script = Resolve(game, args.String("name"), args.Bool("header", false));
                string text = args.String("text");
                Save(ctx, script, text);
                return ToolResult.Json(new { file = script.FileName, lines = ScriptText.CountLines(text), saved = true });
            },
        };

        private static Tool EditScript(ToolContext ctx) => new Tool
        {
            Name = "edit_script",
            Title = "Edit script",
            Description = "Replace an exact substring in a script module or room script (\"roomN\") and save it. 'find' must appear exactly once " +
                          "unless all=true (then every occurrence is replaced). Newlines in find/replace are matched " +
                          "regardless of how they are encoded. Run compile afterwards to check the result.",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("Module name or script filename."))
                .Required("find", Schema.String("Exact text to find."))
                .Required("replace", Schema.String("Text to replace it with."))
                .Optional("header", Schema.Boolean("Edit the header (.ash) instead of the body (.asc). Default false."))
                .Optional("all", Schema.Boolean("Replace every occurrence instead of requiring exactly one. Default false."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                Script script = Resolve(game, args.String("name"), args.Bool("header", false));
                bool all = args.Bool("all", false);
                var result = ScriptText.ApplyEdit(script.Text, args.String("find"), args.String("replace"), all);
                if (result.Count == 0)
                    throw new ToolException($"The 'find' text was not found in {script.FileName}.");
                if (!all && result.Count > 1)
                    throw new ToolException($"The 'find' text appears {result.Count} times in {script.FileName}. Add surrounding context to make it unique, or pass all=true.");
                Save(ctx, script, result.NewText);
                return ToolResult.Json(new { file = script.FileName, replacements = all ? result.Count : 1, lines = ScriptText.CountLines(result.NewText), saved = true });
            },
        };

        private static Tool CreateScriptModule(ToolContext ctx) => new Tool
        {
            Name = "create_script_module",
            Title = "Create script module",
            Description = "Create a new script module (a .asc body and .ash header pair), write it to disk and add it to " +
                          "the project just above GlobalScript, so GlobalScript and the modules below it can use its header (in a project " +
                          "with script folders it goes at the bottom; 'position' says where it landed). If the name is taken a number is appended; the " +
                          "actual name is returned. The module is registered in the project; call save_project to persist " +
                          "it to Game.agf. (It appears in the editor's project tree after the editor next refreshes it.)",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("Base name for the module, without extension (e.g. \"Utils\")."))
                .Optional("headerText", Schema.String("Initial header (.ash) contents."))
                .Optional("scriptText", Schema.String("Initial body (.asc) contents."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                string name = args.String("name").Trim();
                if (name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("."))
                    throw new ToolException("'name' must be a bare module name with no extension or path separators.");

                ScriptAndHeader created = CreateModule(game, name,
                    args.String("headerText", DefaultHeaderText),
                    args.String("scriptText", DefaultScriptText));
                return ToolResult.Json(new
                {
                    name = ModuleName(created),
                    header = created.Header?.FileName,
                    script = created.Script?.FileName,
                    position = game.ScriptsAndHeaders.ToList().IndexOf(created),
                    created = true,
                });
            },
        };

        private static Tool Compile(ToolContext ctx) => new Tool
        {
            Name = "compile",
            Title = "Compile scripts",
            Description = "Compile the project's script modules and return any errors and warnings with file and line number. " +
                          "Pass 'name' to compile a single module, or a room script (\"roomN\", compiled with the room's hotspot " +
                          "and object names; this loads the room in the editor). Without 'name' every module is compiled, but not " +
                          "room scripts. This checks scripts only (it does not package assets or build a runnable game).",
            InputSchema = Schema.Object()
                .Optional("name", Schema.String("Module name or room script (\"roomN\") to compile. Omit to compile all modules."))
                .Build(),
            Annotations = new ToolAnnotations { Idempotent = true },
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                List<Script> bodies;
                if (args.Has("name") && TryParseRoomScript(args.String("name"), out int roomNumber))
                {
                    Room room = RoomSession.Open(ctx, roomNumber);
                    if (room.Script == null) room.LoadScript();
                    CompileMessages roomMessages = EditorInternals.CompileRoomScript(room);
                    int roomErrors = roomMessages.Count(m => m is CompileError);
                    return ToolResult.Json(new
                    {
                        ok = roomErrors == 0,
                        errors = roomErrors,
                        warnings = roomMessages.Count - roomErrors,
                        modulesCompiled = 1,
                        messages = RoomSession.Describe(roomMessages),
                    });
                }
                if (args.Has("name"))
                {
                    bodies = new List<Script> { Resolve(game, args.String("name"), wantHeader: false) };
                }
                else
                {
                    bodies = game.ScriptsAndHeaders.Where(sh => sh.Script != null).Select(sh => sh.Script).ToList();
                }

                // An error in a shared header surfaces once per module that imports it; dedupe on (file, line, text, severity).
                var seen = new HashSet<string>();
                var messages = new List<object>();
                int errors = 0, warnings = 0;
                foreach (Script body in bodies)
                {
                    foreach (CompileMessage m in EditorInternals.CompileScript(body))
                    {
                        bool isError = m is CompileError;
                        string severity = isError ? "error" : "warning";
                        string key = severity + "|" + m.ScriptName + "|" + m.LineNumber + "|" + m.Message;
                        if (!seen.Add(key)) continue;
                        if (isError) errors++; else warnings++;
                        messages.Add(new { severity, script = m.ScriptName, line = m.LineNumber, message = m.Message });
                    }
                }
                return ToolResult.Json(new { ok = errors == 0, errors, warnings, modulesCompiled = bodies.Count, messages });
            },
        };

        private static Tool ScriptApiLookup(ToolContext ctx) => new Tool
        {
            Name = "script_api_lookup",
            Title = "Script API lookup",
            Description = "Search the AGS script API and the project's own headers for functions, structs, enums, defines and " +
                          "global variables whose name contains the query (case-insensitive). Returns signatures, the source " +
                          "header and any /// documentation. Use it to find the exact name and parameters of script commands.",
            InputSchema = Schema.Object()
                .Required("query", Schema.String("Substring to match against API names, e.g. \"Walk\" or \"Character.Say\"."))
                .Optional("limit", Schema.Integer("Maximum number of results to return (default 40)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                ctx.RequireGame();
                string query = args.String("query").Trim();
                if (query.Length == 0) throw new ToolException("'query' must not be empty.");
                int limit = Math.Max(1, args.Int("limit", 40));

                var entries = new List<ApiEntry>();
                foreach (Script header in ctx.Editor.GetAllScriptHeaders())
                {
                    if (!header.AutoCompleteData.Populated) ctx.Editor.RebuildAutocompleteCache(header);
                    CollectEntries(header, entries);
                }

                var matches = entries
                    .Where(e => e.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .GroupBy(e => e.Kind + "|" + e.Signature)
                    .Select(g => g.First())
                    .OrderBy(e => e.Name.Length)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var results = matches.Take(limit).Select(e => new
                {
                    kind = e.Kind,
                    name = e.Name,
                    signature = e.Signature,
                    source = e.Source,
                    doc = string.IsNullOrEmpty(e.Doc) ? null : e.Doc,
                }).ToList();

                return ToolResult.Json(new
                {
                    query,
                    totalMatches = matches.Count,
                    returned = results.Count,
                    truncated = matches.Count > results.Count,
                    results,
                });
            },
        };

        // --- helpers ---

        private static string ModuleName(ScriptAndHeader sh)
        {
            string file = sh.Script?.FileName ?? sh.Header?.FileName;
            return file == null ? "(unknown)" : Path.GetFileNameWithoutExtension(file);
        }

        /// <summary>Recognises a room script name: "room1", "room1.asc" or "room:1" (case-insensitive).</summary>
        internal static bool TryParseRoomScript(string name, out int number)
        {
            Match m = Regex.Match((name ?? string.Empty).Trim(), @"^room\s*:?\s*(\d+)(\.asc)?$", RegexOptions.IgnoreCase);
            number = m.Success ? int.Parse(m.Groups[1].Value) : -1;
            return m.Success;
        }

        /// <summary>
        /// A room's script object. The loaded Room shares its Script with the project's UnloadedRoom entry
        /// (load_crm_file copies the reference), and the editor's room script tab edits that same object.
        /// </summary>
        private static Script RoomScript(Game game, int number)
        {
            UnloadedRoom room = RoomSession.Find(game, number);
            if (room.Script == null)
            {
                if (!File.Exists(Path.Combine(game.DirectoryPath, room.ScriptFileName)))
                    throw new ToolException($"Room {number}'s script file '{room.ScriptFileName}' is missing.");
                room.LoadScript();
            }
            return room.Script;
        }

        /// <summary>A room script's current text, without loading it into the project if it is not already.</summary>
        private static string RoomScriptText(Game game, UnloadedRoom room) =>
            room.Script != null ? room.Script.Text : File.ReadAllText(Path.Combine(game.DirectoryPath, room.ScriptFileName));

        private static Script Resolve(Game game, string name, bool wantHeader)
        {
            if (TryParseRoomScript(name, out int roomNumber))
            {
                if (wantHeader) throw new ToolException("Room scripts have no header.");
                return RoomScript(game, roomNumber);
            }
            bool headerByExt = name.EndsWith(".ash", StringComparison.OrdinalIgnoreCase);
            bool bodyByExt = name.EndsWith(".asc", StringComparison.OrdinalIgnoreCase);
            bool header = wantHeader || headerByExt;
            string module = (headerByExt || bodyByExt) ? Path.GetFileNameWithoutExtension(name) : name;

            foreach (ScriptAndHeader sh in game.ScriptsAndHeaders)
            {
                if (!string.Equals(ModuleName(sh), module, StringComparison.OrdinalIgnoreCase)) continue;
                Script s = header ? sh.Header : sh.Script;
                if (s == null) throw new ToolException($"Module '{module}' has no {(header ? "header (.ash)" : "body (.asc)")}.");
                return s;
            }
            string available = string.Join(", ", game.ScriptsAndHeaders.Select(ModuleName));
            throw new ToolException($"No script module named '{module}'. Available modules: {available}; room scripts are named roomN.");
        }

        /// <summary>
        /// Creates a module at the model level: writes the .asc/.ash pair to disk and adds it to the
        /// root script folder (so it shows up in ScriptsAndHeaders and compiles), just above GlobalScript:
        /// a script only sees the headers of modules above it, and the editor's own "New script" appends
        /// at the bottom, which leaves the module invisible to GlobalScript. In a project with script
        /// folders it appends, as the editor does. It deliberately does
        /// not refresh the editor's project tree, because that WinForms control is owned by the editor's
        /// main-window thread, not the plugin's dispatcher thread.
        /// </summary>
        private static ScriptAndHeader CreateModule(Game game, string baseName, string headerText, string scriptText)
        {
            string name = FirstAvailableName(game, baseName);
            var header = new Script(name + ".ash", headerText, true) { Modified = true };
            var script = new Script(name + ".asc", scriptText, false) { Modified = true };
            header.SaveToDisk();
            script.SaveToDisk();
            var pair = new ScriptAndHeader(header, script);
            // Insert through the flat list (FolderListHybrid), which updates both it and the root folder at the same
            // index; inserting into RootScriptFolder.Items alone leaves the flat list (the order list_scripts reports)
            // with the module appended. The two indexes only agree when there are no subfolders,
            // so with script folders keep the editor's behaviour and append.
            ScriptsAndHeaders all = game.ScriptsAndHeaders;
            int global = -1;
            for (int i = 0; i < all.Count; i++)
                if (all[i].Script != null && all[i].Script.FileName == Script.GLOBAL_SCRIPT_FILE_NAME) { global = i; break; }
            if (global >= 0 && game.RootScriptFolder.SubFolders.Count == 0) all.AddAt(pair, global);
            else game.RootScriptFolder.Items.Add(pair);
            game.FilesAddedOrRemoved = true;
            return pair;
        }

        /// <summary>Mirrors the editor's own first-free-name search (checks .asc/.ash on disk).</summary>
        private static string FirstAvailableName(Game game, string prefix)
        {
            for (int attempt = 0; ; attempt++)
            {
                string name = prefix + (attempt > 0 ? attempt.ToString() : string.Empty);
                string asc = Path.Combine(game.DirectoryPath, name + ".asc");
                string ash = Path.Combine(game.DirectoryPath, name + ".ash");
                bool inProject = game.ScriptsAndHeaders.Any(sh => string.Equals(ModuleName(sh), name, StringComparison.OrdinalIgnoreCase));
                if (!File.Exists(asc) && !File.Exists(ash) && !inProject) return name;
            }
        }

        private static void Save(ToolContext ctx, Script script, string text)
        {
            // The editor writes CRLF (its stub generator appends CRLF lines); keep files uniform so
            // later multi-line edit_script matches never straddle two newline styles.
            script.Text = ScriptText.NormalizeNewlines(text, "\r\n");
            script.Modified = true;
            script.SaveToDisk();
            ctx.Editor.RebuildAutocompleteCache(script);
            EditorInternals.NotifyScriptChanged(script);
        }

        private sealed class ApiEntry
        {
            public string Kind;
            public string Name;
            public string Signature;
            public string Source;
            public string Doc;
        }

        private static void CollectEntries(Script header, List<ApiEntry> entries)
        {
            ScriptAutoCompleteData data = header.AutoCompleteData;
            string source = header.FileName;

            foreach (ScriptFunction f in data.Functions)
            {
                if (f.FunctionName.IndexOf(':') >= 0) continue; // member functions come from their struct
                entries.Add(new ApiEntry { Kind = "function", Name = f.FunctionName, Signature = FunctionSignature(f, null), Source = source, Doc = f.Description });
            }
            foreach (ScriptVariable v in data.Variables)
            {
                entries.Add(new ApiEntry { Kind = "variable", Name = v.VariableName, Signature = VariableSignature(v, null), Source = source, Doc = v.Description });
            }
            foreach (ScriptStruct s in data.Structs)
            {
                entries.Add(new ApiEntry { Kind = "struct", Name = s.Name, Signature = "struct " + s.Name, Source = source, Doc = s.Description });
                foreach (ScriptFunction f in s.Functions)
                    entries.Add(new ApiEntry { Kind = "method", Name = s.Name + "." + f.FunctionName, Signature = FunctionSignature(f, s.Name), Source = source, Doc = f.Description });
                foreach (ScriptVariable v in s.Variables)
                    entries.Add(new ApiEntry { Kind = "member", Name = s.Name + "." + v.VariableName, Signature = VariableSignature(v, s.Name), Source = source, Doc = v.Description });
            }
            foreach (ScriptEnum e in data.Enums)
            {
                entries.Add(new ApiEntry { Kind = "enum", Name = e.Name, Signature = "enum " + e.Name, Source = source, Doc = e.Description });
                foreach (ScriptEnumValue val in e.EnumValues)
                    entries.Add(new ApiEntry { Kind = "enumvalue", Name = val.Name, Signature = e.Name + "." + val.Name, Source = source, Doc = val.Description });
            }
            foreach (ScriptDefine d in data.Defines)
            {
                entries.Add(new ApiEntry { Kind = "define", Name = d.Name, Signature = "#define " + d.Name, Source = source, Doc = d.Description });
            }
        }

        private static string FunctionSignature(ScriptFunction f, string owner)
        {
            string name = owner == null ? f.FunctionName : owner + "." + f.FunctionName;
            string ret = f.Type + (f.ReturnsPointer ? "*" : "");
            return (f.IsStatic ? "static " : "") + (ret.Length > 0 ? ret + " " : "") + name + "(" + f.ParamList + ")";
        }

        private static string VariableSignature(ScriptVariable v, string owner)
        {
            string name = owner == null ? v.VariableName : owner + "." + v.VariableName;
            string type = v.Type + (v.IsPointer && !v.IsDynamicArray ? "*" : "");
            string suffix = (v.IsArray && !v.IsDynamicArray) || v.IsIndexedAttribute ? "[]" : "";
            return (v.IsStatic ? "static " : "") + (v.IsReadOnly ? "readonly " : "") + type + " " + name + suffix;
        }
    }
}
