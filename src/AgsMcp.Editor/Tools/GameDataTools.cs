using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.RegularExpressions;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Game-data tools: a generic property get/set over the AGS.Types model, create/delete of entities,
    /// dialog scripts, global variables and find-usages. One reflection mechanism serves every type.
    /// </summary>
    internal static class GameDataTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ListEntities(ctx);
            yield return GetProperties(ctx);
            yield return SetProperties(ctx);
            yield return CreateEntity(ctx);
            yield return DeleteEntity(ctx);
            yield return GetDialogScript(ctx);
            yield return SetDialogScript(ctx);
            yield return FindUsages(ctx);
            yield return SetEvent(ctx);
        }

        private static JObject TypeProp() =>
            Schema.String("Entity type: " + string.Join(", ", EntityRegistry.TypeNames) + ".");

        private static JObject IdProp() =>
            Schema.String("The entity's numeric ID or its script name. (For globalvariable/customproperty, the name; for audiocliptype, the TypeID or name.)");

        private static Tool ListEntities(ToolContext ctx) => new Tool
        {
            Name = "list_entities",
            Title = "List entities",
            Description = "List all entities of a given type with their ID and script name. Types: " +
                          string.Join(", ", EntityRegistry.TypeNames) + ".",
            InputSchema = Schema.Object().Required("type", TypeProp()).Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EntityKind kind = EntityRegistry.Get(args.String("type"));
                var entities = kind.Enumerate(game)
                    .Select(e => new { id = kind.IdOf(e), name = kind.NameOf(e) })
                    .ToList();
                return ToolResult.Json(new { type = kind.Name, count = entities.Count, entities });
            },
        };

        private static Tool GetProperties(ToolContext ctx) => new Tool
        {
            Name = "get_properties",
            Title = "Get properties",
            Description = "Get all editable properties of an entity: name, category, description, type, current value, " +
                          "whether it is read-only, and the allowed values for enum properties. Works for " +
                          string.Join(", ", EntityRegistry.TypeNames) + " (use id \"settings\" for the setting singleton).",
            InputSchema = Schema.Object().Required("type", TypeProp()).Required("id", IdProp()).Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EntityKind kind = EntityRegistry.Get(args.String("type"));
                object entity = EntityRegistry.Resolve(kind, game, args.Raw["id"]);
                return ToolResult.Json(new
                {
                    type = kind.Name,
                    id = kind.IdOf(entity),
                    name = kind.NameOf(entity),
                    properties = PropertyReflection.Describe(entity),
                    events = DescribeEvents(InteractionsOf(entity)),
                });
            },
        };

        private static Tool SetProperties(ToolContext ctx) => new Tool
        {
            Name = "set_properties",
            Title = "Set properties",
            Description = "Set one or more properties of an entity. 'properties' is an object of propertyName -> value. " +
                          "Enum values may be given by name or number. Script names are checked for uniqueness. Changes " +
                          "are applied in memory; call save_project to persist them. (Open editor panes refresh on reload.)",
            InputSchema = Schema.Object()
                .Required("type", TypeProp())
                .Required("id", IdProp())
                .Required("properties", Schema.AnyObject("Map of property name to new value."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EntityKind kind = EntityRegistry.Get(args.String("type"));
                object entity = EntityRegistry.Resolve(kind, game, args.Raw["id"]);
                List<string> changed = ApplyProperties(game, kind, entity, args.Object("properties"));
                return ToolResult.Json(new { type = kind.Name, id = kind.IdOf(entity), changed });
            },
        };

        private static Tool CreateEntity(ToolContext ctx) => new Tool
        {
            Name = "create_entity",
            Title = "Create entity",
            Description = "Create a new entity and register it in the project. Creatable types: " +
                          string.Join(", ", EntityRegistry.TypeNames.Where(IsCreatable)) + ". " +
                          "globalvariable, customproperty and audiocliptype require a 'Name' in properties; others are auto-named. " +
                          "Audio clips come from import_audio. " +
                          "Any other given properties are applied to the new entity. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Required("type", TypeProp())
                .Optional("properties", Schema.AnyObject("Initial property values. 'Name' is required for globalvariable/customproperty/audiocliptype."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EntityKind kind = EntityRegistry.Get(args.String("type"));
                if (kind.Create == null)
                    throw new ToolException($"Entities of type '{kind.Name}' cannot be created with this tool." + (kind.Name == "audioclip" ? " Use import_audio." : ""));

                JObject props = args.Has("properties") ? args.Object("properties") : new JObject();
                object entity = kind.Create(game, props);
                // Apply any extra properties the caller supplied beyond what creation consumed.
                List<string> applied = ApplyProperties(game, kind, entity, props, ignoreUnknownAndKeys: true);
                return ToolResult.Json(new
                {
                    type = kind.Name,
                    id = kind.IdOf(entity),
                    name = kind.NameOf(entity),
                    applied,
                    created = true,
                });
            },
        };

        private static Tool DeleteEntity(ToolContext ctx) => new Tool
        {
            Name = "delete_entity",
            Title = "Delete entity",
            Description = "Delete an entity from the project. Deletable types: " +
                          string.Join(", ", EntityRegistry.TypeNames.Where(IsDeletable)) + ". " +
                          "For characters/inventory/dialogs/guis/cursors the higher IDs shift down, as in the editor. " +
                          "Call save_project to persist.",
            InputSchema = Schema.Object().Required("type", TypeProp()).Required("id", IdProp()).Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EntityKind kind = EntityRegistry.Get(args.String("type"));
                if (kind.Delete == null)
                    throw new ToolException($"Entities of type '{kind.Name}' cannot be deleted with this tool." + (kind.Name == "audioclip" ? " Use delete_audio." : ""));
                object entity = EntityRegistry.Resolve(kind, game, args.Raw["id"]);
                string id = kind.IdOf(entity), name = kind.NameOf(entity);
                kind.Delete(game, entity);
                return ToolResult.Json(new { type = kind.Name, id, name, deleted = true });
            },
        };

        private static Tool GetDialogScript(ToolContext ctx) => new Tool
        {
            Name = "get_dialog_script",
            Title = "Get dialog script",
            Description = "Get a dialog's options (number, text, show, say) and its dialog script (the special AGS dialog " +
                          "language: @S is the startup entry, @N runs when option N is chosen), by numeric ID or script name.",
            InputSchema = Schema.Object().Required("id", IdProp()).Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                var dialog = (Dialog)EntityRegistry.Resolve(EntityRegistry.Get("dialog"), game, args.Raw["id"]);
                return ToolResult.Json(new
                {
                    id = dialog.ID.ToString(),
                    name = dialog.Name,
                    options = dialog.Options.Select(o => new { number = o.ID, text = o.Text, show = o.Show, say = o.Say }).ToList(),
                    script = dialog.Script,
                });
            },
        };

        private static Tool SetDialogScript(ToolContext ctx) => new Tool
        {
            Name = "set_dialog_script",
            Title = "Set dialog script",
            Description = "Replace a dialog's script and/or its options. The script uses the AGS dialog language, not " +
                          "regular script: lines are 'Name: text' speech (Name is a character's script name without the c, " +
                          "or 'narrator'), commands like return/stop/goto-dialog/option-off N, and indented regular script " +
                          "lines; @S is the startup entry and @N runs when option N is chosen. 'options' replaces the option " +
                          "list: [{text, show?=true, say?=true}], numbered from 1 in order. Applied in memory; call " +
                          "save_project to persist (it is recompiled on the next build).",
            InputSchema = Schema.Object()
                .Required("id", IdProp())
                .Optional("script", Schema.String("The full dialog script text."))
                .Optional("options", Schema.ArrayOf(Schema.AnyObject("An option: {text, show (default true), say (default true)}."), "The dialog's options, in order (option 1 first)."))
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                var dialog = (Dialog)EntityRegistry.Resolve(EntityRegistry.Get("dialog"), game, args.Raw["id"]);
                if (!args.Has("script") && !args.Has("options"))
                    throw new ToolException("Provide 'script', 'options' or both.");
                if (args.Has("options"))
                {
                    List<DialogOption> options = ParseDialogOptions(args.Array("options"));
                    dialog.Options.Clear();
                    dialog.Options.AddRange(options);
                }
                if (args.Has("script")) dialog.Script = args.String("script");
                return ToolResult.Json(new { id = dialog.ID.ToString(), name = dialog.Name, options = dialog.Options.Count, updated = true });
            },
        };

        private static Tool FindUsages(ToolContext ctx) => new Tool
        {
            Name = "find_usages",
            Title = "Find usages",
            Description = "Find where a name (e.g. a character or function script name) appears, as a whole word, in the " +
                          "script modules, room scripts and dialog scripts. Returns file, line and the line text.",
            InputSchema = Schema.Object()
                .Required("name", Schema.String("The identifier to search for."))
                .Optional("limit", Schema.Integer("Maximum matches to return (default 100)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                string name = args.String("name").Trim();
                if (name.Length == 0) throw new ToolException("'name' must not be empty.");
                int limit = Math.Max(1, args.Int("limit", 100));
                var regex = new Regex(@"\b" + Regex.Escape(name) + @"\b");

                var matches = new List<object>();
                int total = 0;
                void Scan(string file, string text)
                {
                    if (string.IsNullOrEmpty(text)) return;
                    string[] lines = text.Replace("\r\n", "\n").Split('\n');
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (!regex.IsMatch(lines[i])) continue;
                        total++;
                        if (matches.Count < limit)
                            matches.Add(new { file, line = i + 1, text = lines[i].Trim() });
                    }
                }

                foreach (ScriptAndHeader sh in game.ScriptsAndHeaders)
                {
                    if (sh.Header != null) Scan(sh.Header.FileName, sh.Header.Text);
                    if (sh.Script != null) Scan(sh.Script.FileName, sh.Script.Text);
                }
                foreach (UnloadedRoom r in game.Rooms.Cast<UnloadedRoom>().OrderBy(r => r.Number))
                {
                    if (r.Script != null) Scan(r.ScriptFileName, r.Script.Text);
                    else
                    {
                        string file = System.IO.Path.Combine(game.DirectoryPath, r.ScriptFileName);
                        if (System.IO.File.Exists(file)) Scan(r.ScriptFileName, System.IO.File.ReadAllText(file));
                    }
                }
                foreach (Dialog d in game.DialogFlatList)
                    Scan(d.Name + " (dialog script)", d.Script);

                return ToolResult.Json(new { name, totalMatches = total, returned = matches.Count, truncated = total > matches.Count, matches });
            },
        };

        private static Tool SetEvent(ToolContext ctx) => new Tool
        {
            Name = "set_event",
            Title = "Set event",
            Description = "Bind a character or inventory item event to a script function and add an empty stub for it to the " +
                          "script that holds its handlers (GlobalScript), like clicking the event's '...' button in the editor. " +
                          "Fill the stub in with edit_script. 'event' is the event suffix or display name: characters have " +
                          "Look, Interact, Talk, UseInv, AnyClick, PickUp, Mode8, Mode9; inventory items Look, Interact, Talk, " +
                          "UseInv, OtherClick (get_properties lists them with their bound functions). 'function' defaults " +
                          "to <scriptName>_<event>. Call save_project to persist the binding.",
            InputSchema = Schema.Object()
                .Required("type", Schema.String("Entity type.", "character", "inventory"))
                .Required("id", IdProp())
                .Required("event", Schema.String("Event suffix or display name, e.g. \"Look\", \"Interact\", \"Talk\", \"UseInv\"."))
                .Optional("function", Schema.String("Script function name to bind (defaults to <scriptName>_<event>)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                EntityKind kind = EntityRegistry.Get(args.String("type"));
                if (kind.Name != "character" && kind.Name != "inventory")
                    throw new ToolException("set_event supports characters and inventory items; use set_room_event for rooms, hotspots, objects and regions.");
                object entity = EntityRegistry.Resolve(kind, game, args.Raw["id"]);
                Interactions interactions = InteractionsOf(entity);

                int index = RoomTools.FindEventIndex(interactions, args.String("event"));
                string suffix = interactions.FunctionSuffixes[index];
                string paramList = interactions.FunctionParameterLists[index];
                string funcName = args.Has("function") ? args.String("function").Trim() : kind.NameOf(entity) + "_" + suffix;
                if (!Regex.IsMatch(funcName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                    throw new ToolException($"'{funcName}' is not a valid script function name.");

                string module = string.IsNullOrEmpty(interactions.ScriptModule) ? Script.GLOBAL_SCRIPT_FILE_NAME : interactions.ScriptModule;
                Script script = game.ScriptsAndHeaders.Select(sh => sh.Script)
                    .FirstOrDefault(sc => sc != null && string.Equals(sc.FileName, module, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolException($"The event script '{module}' is not in the project.");

                interactions.SetScriptFunctionNameForInteractionSuffix(suffix, funcName);
                string newText = EditorInternals.InsertFunction(script.Text, funcName, paramList);
                bool stubAdded = newText != script.Text;
                if (stubAdded)
                {
                    script.Text = newText;
                    script.Modified = true;
                    script.SaveToDisk();
                    ctx.Editor.RebuildAutocompleteCache(script);
                    EditorInternals.NotifyScriptChanged(script);
                }
                return ToolResult.Json(new
                {
                    type = kind.Name,
                    id = kind.IdOf(entity),
                    @event = RoomTools.EventName(interactions, index),
                    function = funcName,
                    parameters = paramList,
                    script = script.FileName,
                    stubAdded,
                });
            },
        };

        internal static List<DialogOption> ParseDialogOptions(JArray options)
        {
            if (options.Count > Dialog.MAX_OPTIONS_PER_DIALOG)
                throw new ToolException($"A dialog has at most {Dialog.MAX_OPTIONS_PER_DIALOG} options.");
            var list = new List<DialogOption>();
            for (int i = 0; i < options.Count; i++)
            {
                JToken t = options[i];
                string text;
                bool show = true, say = true;
                if (t.Type == JTokenType.String) text = (string)t;
                else if (t is JObject o && o["text"] != null)
                {
                    text = (string)o["text"];
                    if (o["show"] != null) show = o["show"].Value<bool>();
                    if (o["say"] != null) say = o["say"].Value<bool>();
                }
                else throw new ToolException($"options[{i}] must be {{text, show?, say?}} or a string.");
                list.Add(new DialogOption { ID = i + 1, Text = text, Show = show, Say = say });
            }
            return list;
        }

        private static Interactions InteractionsOf(object entity)
        {
            switch (entity)
            {
                case Character c: return c.Interactions;
                case InventoryItem i: return i.Interactions;
                default: return null;
            }
        }

        private static List<object> DescribeEvents(Interactions interactions)
        {
            if (interactions == null) return null;
            var list = new List<object>();
            for (int i = 0; i < interactions.FunctionSuffixes.Length; i++)
            {
                string fn = interactions.ScriptFunctionNames[i];
                list.Add(new { @event = RoomTools.EventName(interactions, i), function = string.IsNullOrEmpty(fn) ? null : fn });
            }
            return list;
        }

        /// <summary>The macro AGS generates from a character (cName) or GUI (gName) script name, or null.</summary>
        internal static string DerivedMacro(string kind, string name)
        {
            char prefix = kind == "character" ? 'c' : kind == "gui" ? 'g' : '\0';
            if (prefix == '\0' || name == null || name.Length < 2 || name[0] != prefix) return null;
            return name.Substring(1).ToUpperInvariant();
        }

        private static bool IsCreatable(string type) => EntityRegistry.Get(type).Create != null;
        private static bool IsDeletable(string type) => EntityRegistry.Get(type).Delete != null;

        private static List<string> ApplyProperties(Game game, EntityKind kind, object entity, JObject props, bool ignoreUnknownAndKeys = false)
        {
            if (props == null || !props.HasValues) return new List<string>();

            var descriptors = TypeDescriptor.GetProperties(entity);
            var toApply = new JObject();
            foreach (var pair in props)
            {
                PropertyDescriptor pd = PropertyReflection.Find(descriptors, pair.Key);
                if (pd == null || !pd.IsBrowsable || pd.IsReadOnly)
                {
                    if (ignoreUnknownAndKeys) continue; // during create, extra/creation-only keys are tolerated
                    // otherwise let Apply raise the precise error
                    toApply[pair.Key] = pair.Value;
                    continue;
                }

                // Guard the script-name property: block unsafe global-variable renames and enforce uniqueness.
                if (kind.NameProperty != null && string.Equals(pd.Name, kind.NameProperty, StringComparison.Ordinal))
                {
                    string newName = pair.Value?.ToString() ?? string.Empty;
                    bool unchanged = string.Equals(kind.NameOf(entity), newName, StringComparison.Ordinal);
                    if (kind.Name == "globalvariable" && !unchanged)
                        throw new ToolException("Rename a global variable by deleting and recreating it (its name is its dictionary key).");
                    if (!unchanged && game.IsScriptNameAlreadyUsed(newName, entity))
                        throw new ToolException($"The script name '{newName}' is already in use.");
                    // Characters and GUIs also define a macro from their name minus the c/g prefix, uppercased
                    // (cOldMan -> OLDMAN); a view or other name equal to it breaks every script compile.
                    string macro = DerivedMacro(kind.Name, newName);
                    if (!unchanged && macro != null && game.IsScriptNameAlreadyUsed(macro, entity))
                        throw new ToolException($"The script name '{newName}' also defines the macro '{macro}', which is already in use (e.g. by a view). Pick another name or rename that.");
                    if (unchanged) continue; // no-op, skip
                }

                toApply[pd.Name] = pair.Value;
            }
            return PropertyReflection.Apply(entity, toApply);
        }
    }
}
