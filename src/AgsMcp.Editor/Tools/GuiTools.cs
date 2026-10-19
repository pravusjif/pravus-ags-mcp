using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// GUI controls: listing, creating and deleting them, and binding GUI and control events. Controls are also the
    /// "guicontrol" entity kind (EntityRegistry), so get_properties/set_properties work on them as "gui:control".
    /// Creation mirrors the editor's GUIEditor.CreateNewControl; deletion its delete command.
    /// </summary>
    internal static class GuiTools
    {
        private static readonly string[] ControlTypes = { "button", "label", "textbox", "listbox", "slider", "inventory" };

        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ListGuiControls(ctx);
            yield return CreateGuiControl(ctx);
            yield return DeleteGuiControl(ctx);
        }

        private static JObject GuiProp() => Schema.String("The GUI's numeric ID or script name (e.g. \"gMain\").");
        private static JObject ControlProp() => Schema.String("The control's ID on that GUI or its script name (e.g. \"btnOk\").");

        private static Tool ListGuiControls(ToolContext ctx) => new Tool
        {
            Name = "list_gui_controls",
            Title = "List GUI controls",
            Description = "List a GUI's controls: id, name, type, position, size, z-order, visibility and bound events. Address a " +
                          "control elsewhere as type=guicontrol id=\"<gui>:<control>\" (names or IDs, e.g. \"gMain:btnOk\" or " +
                          "\"2:0\") with get_properties, set_properties and set_event.",
            InputSchema = Schema.Object().Required("gui", GuiProp()).Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                GUI gui = ResolveGui(game, args.String("gui"));
                var controls = gui.Controls.OrderBy(c => c.ID).Select(c => new
                {
                    id = c.ID,
                    name = c.Name,
                    type = c.ControlType,
                    x = c.Left,
                    y = c.Top,
                    width = c.Width,
                    height = c.Height,
                    zOrder = c.ZOrder,
                    visible = c.Visible,
                    events = DescribeEvents(c),
                }).ToList();
                return ToolResult.Json(new { gui = gui.Name, guiId = gui.ID, guiType = gui.GetType().Name, count = controls.Count, controls });
            },
        };

        private static Tool CreateGuiControl(ToolContext ctx) => new Tool
        {
            Name = "create_gui_control",
            Title = "Create GUI control",
            Description = "Add a control to a GUI, as drawing one in the GUI editor does: it gets the next ID and z-order and an " +
                          "auto-generated script name (set 'Name' in properties to choose one). x/y are relative to the GUI; " +
                          "the size defaults to 40x20. 'properties' are then applied as with set_properties (e.g. {Name, Text, " +
                          "Image, Font, TextColor}). Text windows cannot hold controls. Bind its event with set_event " +
                          "type=guicontrol. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Required("gui", GuiProp())
                .Required("type", Schema.String("Control type.", ControlTypes))
                .Optional("x", Schema.Integer("Left edge, relative to the GUI (default 0)."))
                .Optional("y", Schema.Integer("Top edge, relative to the GUI (default 0)."))
                .Optional("width", Schema.Integer("Width (default 40)."))
                .Optional("height", Schema.Integer("Height (default 20)."))
                .Optional("properties", Schema.AnyObject("Property values to apply to the new control, e.g. {\"Name\": \"btnOk\", \"Text\": \"OK\"}."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                GUI gui = ResolveGui(game, args.String("gui"));
                if (!(gui is NormalGUI))
                    throw new ToolException($"GUI '{gui.Name}' is a text window, which cannot hold controls.");
                int x = args.Int("x", 0), y = args.Int("y", 0), width = args.Int("width", 40), height = args.Int("height", 20);
                if (width < 1 || height < 1) throw new ToolException("'width' and 'height' must be at least 1.");

                GUIControl control = NewControl(args.String("type"), x, y, width, height);
                control.Name = EditorInternals.GetFirstAvailableScriptName(control.ControlType);
                control.ZOrder = gui.Controls.Count;
                control.ID = gui.Controls.Count;
                control.Parent = gui;
                gui.Controls.Add(control);
                game.NotifyClientsGUIControlAddedOrRemoved(gui, control);

                var ignored = new List<object>();
                List<string> applied = args.Has("properties")
                    ? GameDataTools.ApplyProperties(game, EntityRegistry.Get("guicontrol"), control, args.Object("properties"), ignored)
                    : new List<string>();
                return ToolResult.Json(new
                {
                    id = gui.ID + ":" + control.ID,
                    name = gui.Name + ":" + control.Name,
                    controlId = control.ID,
                    controlName = control.Name,
                    type = control.ControlType,
                    applied,
                    ignored,
                    created = true,
                });
            },
        };

        private static Tool DeleteGuiControl(ToolContext ctx) => new Tool
        {
            Name = "delete_gui_control",
            Title = "Delete GUI control",
            Description = "Delete a control from a GUI, as the GUI editor's Delete does: the controls with higher IDs move down " +
                          "by one. Script references to the control (and its event handler) are left as they are; check them " +
                          "with find_usages. Call save_project to persist.",
            InputSchema = Schema.Object().Required("gui", GuiProp()).Required("control", ControlProp()).Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                GUI gui = ResolveGui(game, args.String("gui"));
                GUIControl control = FindControl(gui, args.String("control"));
                int id = control.ID;
                string name = control.Name;
                // The editor notifies first, then removes; DeleteControl renumbers the higher IDs.
                game.NotifyClientsGUIControlAddedOrRemoved(gui, control);
                gui.DeleteControl(control);
                control.MemberOf?.RemoveFromGroup(control);
                return ToolResult.Json(new { gui = gui.Name, controlId = id, controlName = name, remaining = gui.Controls.Count, deleted = true });
            },
        };

        private static GUIControl NewControl(string type, int x, int y, int width, int height)
        {
            switch ((type ?? string.Empty).ToLowerInvariant())
            {
                case "button": return new GUIButton(x, y, width, height);
                case "label": return new GUILabel(x, y, width, height);
                case "textbox": return new GUITextBox(x, y, width, height);
                case "listbox": return new GUIListBox(x, y, width, height);
                case "slider": return new GUISlider(x, y, width, height);
                case "inventory": return new GUIInventory(x, y, width, height);
                default: throw new ToolException($"'type' must be one of {string.Join(", ", ControlTypes)}; got '{type}'.");
            }
        }

        // --- addressing ---

        /// <summary>A parsed "gui:control" reference; each side is a name or a numeric ID.</summary>
        internal sealed class GuiControlRef
        {
            public string Gui;
            public string Control;

            public static GuiControlRef Parse(string id)
            {
                string[] parts = (id ?? string.Empty).Split(':');
                if (parts.Length != 2 || parts[0].Trim().Length == 0 || parts[1].Trim().Length == 0)
                    throw new ToolException($"A GUI control id is \"<gui>:<control>\", each a name or ID (e.g. \"gMain:btnOk\" or \"2:0\"); got '{id}'.");
                return new GuiControlRef { Gui = parts[0].Trim(), Control = parts[1].Trim() };
            }
        }

        internal static GUI ResolveGui(Game game, string gui) =>
            (GUI)EntityRegistry.Resolve(EntityRegistry.Get("gui"), game, new JValue(gui));

        internal static GUIControl ResolveControl(Game game, string id)
        {
            GuiControlRef r = GuiControlRef.Parse(id);
            return FindControl(ResolveGui(game, r.Gui), r.Control);
        }

        internal static GUIControl FindControl(GUI gui, string control)
        {
            GUIControl found = gui.Controls.FirstOrDefault(c => string.Equals(c.Name, control, StringComparison.OrdinalIgnoreCase))
                               ?? gui.Controls.FirstOrDefault(c => c.ID.ToString() == control);
            if (found != null) return found;
            string have = gui.Controls.Count == 0 ? "it has none" : "it has " + string.Join(", ", gui.Controls.OrderBy(c => c.ID).Select(c => c.ID + "=" + c.Name));
            throw new ToolException($"GUI '{gui.Name}' has no control '{control}'; {have}.");
        }

        // --- events ---

        /// <summary>The script-function properties of a GUI or control (OnClick, OnSelectionChanged, ...), marked [AGSEventProperty].</summary>
        internal static List<PropertyInfo> EventProperties(object entity) =>
            entity.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(string) && p.CanWrite && p.IsDefined(typeof(AGSEventPropertyAttribute), true))
                .ToList();

        internal static Dictionary<string, string> DescribeEvents(object entity)
        {
            var events = EventProperties(entity);
            if (events.Count == 0) return null;
            return events.ToDictionary(p => p.Name, p => string.IsNullOrEmpty((string)p.GetValue(entity, null)) ? null : (string)p.GetValue(entity, null));
        }

        /// <summary>
        /// set_event for a GUI or control: binds the event property to a function and adds the stub, with the parameter
        /// list from [ScriptFunctionParameters], to the GUI's ScriptModule (GlobalScript by default), as the GUI
        /// editor's events tab does. The default function name is &lt;scriptName&gt;_&lt;property&gt; (btnOk_OnClick).
        /// </summary>
        internal static object BindEvent(ToolContext ctx, Game game, EntityKind kind, object entity, string eventName, string function)
        {
            GUI gui = entity as GUI ?? ((GUIControl)entity).Parent;
            string itemName = entity is GUI g ? g.Name : ((GUIControl)entity).Name;
            List<PropertyInfo> events = EventProperties(entity);
            if (events.Count == 0)
                throw new ToolException($"A {(entity is GUI ? "text window GUI" : ((GUIControl)entity).ControlType)} has no events.");
            string want = (eventName ?? string.Empty).Trim();
            PropertyInfo prop = events.FirstOrDefault(p => string.Equals(p.Name, want, StringComparison.OrdinalIgnoreCase)
                                                        || string.Equals(p.Name, "On" + want, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"No event '{eventName}'. Events: {string.Join(", ", events.Select(p => p.Name))}.");
            if (string.IsNullOrEmpty(itemName))
                throw new ToolException("Give it a script name first (set_properties Name), then bind its event.");

            string funcName = string.IsNullOrWhiteSpace(function) ? itemName + "_" + prop.Name : function.Trim();
            if (!Regex.IsMatch(funcName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                throw new ToolException($"'{funcName}' is not a valid script function name.");
            var paramsAttr = (ScriptFunctionParametersAttribute)prop.GetCustomAttributes(typeof(ScriptFunctionParametersAttribute), true).FirstOrDefault();
            string paramList = paramsAttr?.Parameters ?? string.Empty;

            string module = string.IsNullOrEmpty(gui?.ScriptModule) ? Script.GLOBAL_SCRIPT_FILE_NAME : gui.ScriptModule;
            Script script = game.ScriptsAndHeaders.Select(sh => sh.Script)
                .FirstOrDefault(sc => sc != null && string.Equals(sc.FileName, module, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException($"The GUI's event script '{module}' is not in the project.");

            prop.SetValue(entity, funcName, null);
            string newText = EditorInternals.InsertFunction(script.Text, funcName, paramList);
            bool stubAdded = newText != script.Text;
            if (stubAdded) ScriptTools.Save(ctx, script, newText);
            return new
            {
                type = kind.Name,
                id = kind.IdOf(entity),
                @event = prop.Name,
                function = funcName,
                parameters = paramList,
                script = script.FileName,
                stubAdded,
            };
        }
    }
}
