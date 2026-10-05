using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Room tools. Every room is accessed as the editor's loaded room (see RoomSession), so tools and the
    /// editor's own room tab never disagree about which room the single native room struct holds. Reading
    /// loads the room in the editor; edits are saved at once through the editor's save path.
    /// </summary>
    internal static class RoomTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ListRooms(ctx);
            yield return GetRoom(ctx);
            yield return CreateRoom(ctx);
            yield return RenderRoom(ctx);
            yield return GetMaskPixel(ctx);
            yield return SetRoomBackground(ctx);
            yield return DrawRoomMask(ctx);
            yield return ImportRoomMask(ctx);
            yield return GetRoomProperties(ctx);
            yield return SetRoomProperties(ctx);
            yield return CreateRoomObject(ctx);
            yield return DeleteRoomObject(ctx);
            yield return SetRoomEvent(ctx);
        }

        private const string EntityHelp =
            "\"room\", \"hotspot:N\", \"object:N\", \"region:N\", \"walkablearea:N\" or \"walkbehind:N\" (N = the ID), " +
            "or a hotspot/object script name such as \"hDoor\"";

        private static JObject NumberProp() => Schema.Integer("The room number.");

        private static JObject MaskProp(string description) =>
            Schema.String(description, "hotspots", "walkableareas", "walkbehinds", "regions");

        private static Tool ListRooms(ToolContext ctx) => new Tool
        {
            Name = "list_rooms",
            Title = "List rooms",
            Description = "List the rooms in the project with their number, description, .crm and script file names, " +
                          "and which one (if any) is loaded in the editor.",
            InputSchema = Schema.Object().Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                Room loaded = EditorInternals.LoadedRoom(ctx.Editor);
                var rooms = game.Rooms
                    .OrderBy(r => r.Number)
                    .Select(r => new { number = r.Number, description = r.Description, file = r.FileName, script = ((UnloadedRoom)r).ScriptFileName })
                    .ToList();
                return ToolResult.Json(new { count = rooms.Count, loadedInEditor = loaded?.Number, rooms });
            },
        };

        private static Tool GetRoom(ToolContext ctx) => new Tool
        {
            Name = "get_room",
            Title = "Get room",
            Description = "Return a room's size, background count, colour depth, mask resolution, edges, hotspots, objects, " +
                          "regions, walkable areas, walk-behinds, custom properties and the room-level event bindings. " +
                          "Hotspots, regions, walkable areas and walk-behinds are fixed slots (ID 0 means 'none'); " +
                          "'painted' lists the IDs that cover at least one mask pixel. Loads the room in the editor " +
                          "(the editor holds one room at a time); does not change it. 'show' also opens the room's editor tab.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Optional("show", Schema.Boolean("Also open the room's editor tab in the AGS editor (default false)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Room room = RoomSession.Open(ctx, args.Int("number"));
                if (args.Bool("show", false)) EditorInternals.ShowRoomPane(ctx.Editor, room.Number);
                return ToolResult.Json(new
                {
                    number = room.Number,
                    description = room.Description,
                    size = new { width = room.Width, height = room.Height },
                    backgroundCount = room.BackgroundCount,
                    colorDepth = room.ColorDepth,
                    maskResolution = room.MaskResolution,
                    edges = new { left = room.LeftEdgeX, right = room.RightEdgeX, top = room.TopEdgeY, bottom = room.BottomEdgeY },
                    painted = new
                    {
                        hotspots = PaintedAreas(room, RoomAreaMaskType.Hotspots),
                        walkableAreas = PaintedAreas(room, RoomAreaMaskType.WalkableAreas),
                        walkBehinds = PaintedAreas(room, RoomAreaMaskType.WalkBehinds),
                        regions = PaintedAreas(room, RoomAreaMaskType.Regions),
                    },
                    hotspots = room.Hotspots.Select(h => new { id = h.ID, name = h.Name, description = h.Description, walkTo = new { x = h.WalkToPoint.X, y = h.WalkToPoint.Y }, events = BoundEvents(h.Interactions) }).ToList(),
                    objects = room.Objects.Select(o => new { id = o.ID, name = o.Name, description = o.Description, image = o.Image, x = o.StartX, y = o.StartY, visible = o.Visible, clickable = o.Clickable, baseline = o.Baseline, events = BoundEvents(o.Interactions) }).ToList(),
                    regions = room.Regions.Select(r => new { id = r.ID, useColourTint = r.UseColourTint, lightLevel = r.LightLevel, events = BoundEvents(r.Interactions) }).ToList(),
                    walkableAreas = room.WalkableAreas.Select(w => new { id = w.ID, scalingLevel = w.ScalingLevel, useContinuousScaling = w.UseContinuousScaling, areaSpecificView = w.AreaSpecificView }).ToList(),
                    walkBehinds = room.WalkBehinds.Select(w => new { id = w.ID, baseline = w.Baseline }).ToList(),
                    properties = room.Properties.PropertyValues.ToDictionary(p => p.Key, p => p.Value.Value),
                    events = BoundEvents(room.Interactions),
                    allRoomEvents = room.Interactions.FunctionSuffixes,
                });
            },
        };

        private static Tool CreateRoom(ToolContext ctx) => new Tool
        {
            Name = "create_room",
            Title = "Create room",
            Description = "Create a new blank room (black background at the game's resolution, empty masks, empty script) " +
                          "and add it to the project, like the editor's 'New room'. 'number' defaults to the lowest unused " +
                          "number from 1. Then use set_room_background, draw_room_mask and the other room tools. " +
                          "Call save_project to record the new room in Game.agf.",
            InputSchema = Schema.Object()
                .Optional("number", Schema.Integer("Room number (0-999; rooms above 299 are non-state-saving). Default: lowest unused from 1."))
                .Optional("description", Schema.String("Room description shown in the project tree."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                int number = args.Has("number") ? args.Int("number") : Enumerable.Range(1, 998).First(n => game.Rooms.All(r => r.Number != n));
                if (number < 0 || number > 999) throw new ToolException("'number' must be between 0 and 999.");
                if (game.Rooms.Any(r => r.Number == number)) throw new ToolException($"Room {number} already exists.");
                var probe = new UnloadedRoom(number);
                string crm = Path.Combine(game.DirectoryPath, probe.FileName);
                if (File.Exists(crm) || File.Exists(Path.Combine(game.DirectoryPath, probe.ScriptFileName)))
                    throw new ToolException($"'{probe.FileName}' or '{probe.ScriptFileName}' already exists in the game folder but is not in the project. Delete or rename it first.");

                // Creating a room through the editor unloads nothing, but refuse if the loaded room has unsaved
                // changes, as with any other room switch, so nothing gets lost later.
                Room loaded = EditorInternals.LoadedRoom(ctx.Editor);
                if (loaded != null && loaded.Modified)
                    throw new ToolException($"Room {loaded.Number} is loaded in the editor with unsaved changes. Call save_project first.");

                EditorInternals.CreateBlankRoom(ctx.Editor, number);
                UnloadedRoom created = game.Rooms.Cast<UnloadedRoom>().FirstOrDefault(r => r.Number == number)
                    ?? throw new ToolException($"The editor did not create room {number}.");

                object saved = null;
                if (args.Has("description"))
                {
                    Room room = RoomSession.Open(ctx, number);
                    room.Description = args.String("description");
                    created.Description = room.Description;
                    saved = RoomSession.Save(ctx, room);
                }
                return ToolResult.Json(new { number, file = created.FileName, script = created.ScriptFileName, created = true, save = saved });
            },
        };

        private static Tool RenderRoom(ToolContext ctx) => new Tool
        {
            Name = "render_room",
            Title = "Render room",
            Description = "Render a room background to a PNG image. Optionally overlay an area mask: one of " +
                          "none, hotspots, walkbehinds, walkableareas, regions. 'background' selects which background frame (default 0). " +
                          "Loads the room in the editor.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Optional("mask", Schema.String("Area mask to overlay.", "none", "hotspots", "walkbehinds", "walkableareas", "regions"))
                .Optional("background", Schema.Integer("Background frame index (default 0)."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Room room = RoomSession.Open(ctx, args.Int("number"));
                int bg = args.Int("background", 0);
                if (bg < 0 || bg >= room.BackgroundCount)
                    throw new ToolException($"Room {room.Number} has {room.BackgroundCount} background(s); valid 'background' is 0..{room.BackgroundCount - 1}.");
                RoomAreaMaskType mask = ParseMask(args.String("mask", "none"));

                byte[] png;
                List<int> areas = null;
                using (Bitmap bmp = EditorInternals.GetRoomBackgroundForPreview(room, bg))
                {
                    if (mask != RoomAreaMaskType.None)
                        using (Bitmap maskBmp = EditorInternals.ExportAreaMask(room, mask))
                            areas = OverlayMask(bmp, ReadMask(maskBmp));
                    png = ToPng(bmp);
                }

                var result = new ToolResult();
                string legend = areas == null ? "" : areas.Count == 0 ? " The mask is empty."
                    : " Areas: " + string.Join(", ", areas.Select(a => a + "=" + ColorName(AreaColor(a)))) + ".";
                result.AddText($"Room {room.Number} background {bg}{(mask == RoomAreaMaskType.None ? "" : " with " + mask + " mask")} ({room.Width}x{room.Height}).{legend}");
                result.AddImage(png, "image/png");
                return result;
            },
        };

        private static Tool GetMaskPixel(ToolContext ctx) => new Tool
        {
            Name = "get_mask_pixel",
            Title = "Get mask pixel",
            Description = "Return the area number at a pixel of a room's area mask (0 means no area). 'mask' is one of " +
                          "hotspots, walkbehinds, walkableareas, regions. Coordinates are room pixels. Loads the room in the editor.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("mask", MaskProp("Which mask."))
                .Required("x", Schema.Integer("X pixel coordinate."))
                .Required("y", Schema.Integer("Y pixel coordinate."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Room room = RoomSession.Open(ctx, args.Int("number"));
                RoomAreaMaskType mask = ParseMask(args.String("mask"));
                if (mask == RoomAreaMaskType.None) throw new ToolException("'mask' must be hotspots, walkbehinds, walkableareas or regions.");
                int x = args.Int("x"), y = args.Int("y");
                RequireInRoom(room, x, y);
                int area = EditorInternals.GetAreaMaskPixel(room, mask, x, y);
                return ToolResult.Json(new { number = room.Number, mask = mask.ToString(), x, y, area });
            },
        };

        private static Tool SetRoomBackground(ToolContext ctx) => new Tool
        {
            Name = "set_room_background",
            Title = "Set room background",
            Description = "Import an image (file 'path' or 'base64') as a room background. 'background' 0 (default) is the main " +
                          "background: if its size differs from the room's, the room is resized and all its masks (hotspots, " +
                          "walkable areas, regions, walk-behinds) are cleared and edges reset. 'background' 1..4 replaces or " +
                          "adds (index = current count) an animation frame, which must match the room size. Images smaller than " +
                          "the game resolution are padded. Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Optional("path", Schema.String("Absolute path to an image file (PNG/BMP/JPG/GIF). Provide this OR 'base64'."))
                .Optional("base64", Schema.String("Image bytes as base64 (an optional data: URL prefix is stripped). Provide this OR 'path'."))
                .Optional("background", Schema.Integer("Background frame index (default 0)."))
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                int bg = args.Int("background", 0);
                using (Bitmap loaded = AssetTools.LoadBitmap(args))
                using (Bitmap bmp = PadToScreen(loaded, game.Settings.CustomResolution))
                {
                    if (game.Settings.ColorDepth == GameColorDepth.Palette)
                        throw new ToolException("This is a 256-colour game; import 8-bit backgrounds in the editor instead.");

                    Room room = RoomSession.Open(ctx, args.Int("number"));
                    if (bg < 0 || bg > room.BackgroundCount || bg >= Room.MAX_BACKGROUNDS)
                        throw new ToolException($"'background' must be 0..{Math.Min(room.BackgroundCount, Room.MAX_BACKGROUNDS - 1)} (the room has {room.BackgroundCount}).");

                    bool sizeChanged = bmp.Width != room.Width || bmp.Height != room.Height;
                    if (sizeChanged && bg > 0)
                        throw new ToolException($"Background frames must match the main background size {room.Width}x{room.Height}; this image is {bmp.Width}x{bmp.Height}.");

                    int framesDeleted = 0;
                    if (bg == 0) room.Resolution = RoomResolution.Real;
                    room.Width = bmp.Width;
                    room.Height = bmp.Height;
                    EditorInternals.ImportBackground(room, bg, bmp, !EditorInternals.RemapPalettizedBackgrounds, false);
                    if (sizeChanged)
                    {
                        while (room.BackgroundCount > 1) { EditorInternals.DeleteBackground(room, 1); framesDeleted++; }
                        room.LeftEdgeX = 0;
                        room.RightEdgeX = room.Width - 1;
                        room.TopEdgeY = 0;
                        room.BottomEdgeY = room.Height - 1;
                    }
                    int width = room.Width, height = room.Height, count = room.BackgroundCount;
                    var save = RoomSession.Save(ctx, room);
                    return ToolResult.Json(new
                    {
                        number = room.Number,
                        background = bg,
                        size = new { width, height },
                        backgroundCount = count,
                        masksCleared = sizeChanged,
                        framesDeleted,
                        save,
                    });
                }
            },
        };

        private static Tool DrawRoomMask(ToolContext ctx) => new Tool
        {
            Name = "draw_room_mask",
            Title = "Draw room mask",
            Description = "Paint shapes onto one of a room's area masks with area number 'area' (0 erases). Shapes are drawn " +
                          "in order, in room pixel coordinates (the mask's resolution is handled for you): " +
                          "{type:\"rect\", x, y, width, height}, {type:\"polygon\", points:[[x,y],...]}, " +
                          "{type:\"ellipse\", x, y, rx, ry} (centre and radii), {type:\"line\", x1, y1, x2, y2}, " +
                          "{type:\"fill\", x, y} (flood-fill the contiguous region under the point). 'clear' wipes the " +
                          "whole mask to 0 first. Areas: hotspots 1-49, walkable areas/regions/walk-behinds 1-15. " +
                          "Walk-behinds also need a Baseline (set_room_properties walkbehind:N). Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("mask", MaskProp("Which mask to paint."))
                .Required("area", Schema.Integer("Area number to paint with (0 = erase)."))
                .Required("shapes", Schema.ArrayOf(Schema.AnyObject("A shape: rect, polygon, ellipse, line or fill."), "Shapes to draw, in order."))
                .Optional("clear", Schema.Boolean("Clear the whole mask to 0 before drawing (default false)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                RoomAreaMaskType mask = ParseMask(args.String("mask"));
                if (mask == RoomAreaMaskType.None) throw new ToolException("'mask' must be hotspots, walkbehinds, walkableareas or regions.");
                List<MaskShape> shapes = ParseShapes(args.Array("shapes"));
                Room room = RoomSession.Open(ctx, args.Int("number"));
                int area = args.Int("area");
                int maxArea = AreaSlots(room, mask) - 1;
                if (area < 0 || area > maxArea) throw new ToolException($"'area' must be 0..{maxArea} for the {mask} mask.");

                if (args.Bool("clear", false))
                    EditorInternals.DrawFilledRectOntoMask(room, mask, 0, 0, room.Width - 1, room.Height - 1, 0);
                foreach (MaskShape shape in shapes)
                    DrawShape(room, mask, area, shape);

                List<int> painted = PaintedAreas(room, mask);
                var save = RoomSession.Save(ctx, room);
                return ToolResult.Json(new { number = room.Number, mask = mask.ToString(), area, shapesDrawn = shapes.Count, paintedAreas = painted, save });
            },
        };

        private static Tool ImportRoomMask(ToolContext ctx) => new Tool
        {
            Name = "import_room_mask",
            Title = "Import room mask",
            Description = "Replace a room's area mask with an indexed-colour image (1, 4 or 8-bit PNG/BMP/GIF, file 'path' or " +
                          "'base64') whose palette index is the area number (0 = none). The image must be the room size, or the " +
                          "room size divided by the room's mask resolution (walk-behinds are always full size). Use " +
                          "draw_room_mask instead when you do not have an indexed image. Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("mask", MaskProp("Which mask to replace."))
                .Optional("path", Schema.String("Absolute path to an indexed image file. Provide this OR 'base64'."))
                .Optional("base64", Schema.String("Indexed image bytes as base64. Provide this OR 'path'."))
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                RoomAreaMaskType mask = ParseMask(args.String("mask"));
                if (mask == RoomAreaMaskType.None) throw new ToolException("'mask' must be hotspots, walkbehinds, walkableareas or regions.");
                using (Bitmap bmp = AssetTools.LoadBitmap(args, keepFormat: true))
                {
                    if ((bmp.PixelFormat & PixelFormat.Indexed) == 0)
                        throw new ToolException($"The image is {bmp.PixelFormat}, not indexed colour. Masks use palette indexes as area numbers; save it as an 8-bit indexed image, or use draw_room_mask.");
                    Room room = RoomSession.Open(ctx, args.Int("number"));
                    int factor = mask == RoomAreaMaskType.WalkBehinds ? 1 : room.MaskResolution;
                    bool full = bmp.Width == room.Width && bmp.Height == room.Height;
                    bool scaled = bmp.Width == room.Width / factor && bmp.Height == room.Height / factor;
                    if (!full && !scaled)
                        throw new ToolException($"The image is {bmp.Width}x{bmp.Height}; the room is {room.Width}x{room.Height} (mask size {room.Width / factor}x{room.Height / factor}).");
                    EditorInternals.ImportAreaMask(room, mask, bmp);
                    List<int> painted = PaintedAreas(room, mask);
                    var save = RoomSession.Save(ctx, room);
                    return ToolResult.Json(new { number = room.Number, mask = mask.ToString(), paintedAreas = painted, save });
                }
            },
        };

        private static Tool GetRoomProperties(ToolContext ctx) => new Tool
        {
            Name = "get_room_properties",
            Title = "Get room properties",
            Description = "Get the editable properties of a room or one of its parts: " + EntityHelp + ". Returns name, " +
                          "category, description, type, value, read-only flag and enum values, as get_properties does. " +
                          "Loads the room in the editor.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("entity", Schema.String("Which part: " + EntityHelp + "."))
                .Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Room room = RoomSession.Open(ctx, args.Int("number"));
                object target = ResolveEntity(room, ParseEntity(args.String("entity")), out string label);
                return ToolResult.Json(new { number = room.Number, entity = label, properties = PropertyReflection.Describe(target) });
            },
        };

        private static Tool SetRoomProperties(ToolContext ctx) => new Tool
        {
            Name = "set_room_properties",
            Title = "Set room properties",
            Description = "Set properties of a room or one of its parts: " + EntityHelp + ". 'properties' maps property " +
                          "name to value, e.g. hotspot {Name:\"hDoor\", Description:\"Door\", WalkToPoint:\"160,140\"}, " +
                          "object {Image:5, StartX:100, StartY:150, Visible:true}, walkbehind {Baseline:120}, room " +
                          "{Description:\"Forest\", LeftEdgeX:10}. Script names are checked for uniqueness. Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("entity", Schema.String("Which part: " + EntityHelp + "."))
                .Required("properties", Schema.AnyObject("Map of property name to new value."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                Room room = RoomSession.Open(ctx, args.Int("number"));
                object target = ResolveEntity(room, ParseEntity(args.String("entity")), out string label);
                List<string> changed = ApplyRoomProperties(game, room, target, args.Object("properties"));
                SyncDescription(game, room);
                var save = RoomSession.Save(ctx, room);
                return ToolResult.Json(new { number = room.Number, entity = label, changed, save });
            },
        };

        private static Tool CreateRoomObject(ToolContext ctx) => new Tool
        {
            Name = "create_room_object",
            Title = "Create room object",
            Description = "Add an object to a room (up to " + Room.MAX_OBJECTS + "), like the editor's 'New object here'. 'properties' " +
                          "sets its initial values, e.g. {Name:\"oKey\", Image:12, StartX:150, StartY:160, Description:\"Key\"} " +
                          "(StartX/StartY are the bottom-left corner of the sprite; Name defaults to oObjectN). Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Optional("properties", Schema.AnyObject("Initial property values (Name, Image, StartX, StartY, Description, Visible, Clickable, Baseline, ...)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                Room room = RoomSession.Open(ctx, args.Int("number"));
                if (room.Objects.Count >= Room.MAX_OBJECTS)
                    throw new ToolException($"Room {room.Number} already has the maximum {Room.MAX_OBJECTS} objects.");

                var obj = new RoomObject(room)
                {
                    ID = room.Objects.Count,
                    Name = EditorInternals.GetFirstAvailableScriptName("oObject", 0, room),
                };
                obj.Interactions.ScriptModule = room.Interactions.ScriptModule;
                room.Objects.Add(obj);
                List<string> applied;
                try
                {
                    applied = args.Has("properties") ? ApplyRoomProperties(game, room, obj, args.Object("properties")) : new List<string>();
                }
                catch
                {
                    room.Objects.Remove(obj);
                    throw;
                }
                var save = RoomSession.Save(ctx, room);
                return ToolResult.Json(new { number = room.Number, id = obj.ID, name = obj.Name, applied, created = true, save });
            },
        };

        private static Tool DeleteRoomObject(ToolContext ctx) => new Tool
        {
            Name = "delete_room_object",
            Title = "Delete room object",
            Description = "Delete an object from a room by ID or script name. Higher object IDs shift down by one, as in the " +
                          "editor. Script references to the object (oName or object[N]) are not updated. Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("object", Schema.String("Object ID or script name."))
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                Room room = RoomSession.Open(ctx, args.Int("number"));
                string want = args.String("object").Trim();
                RoomObject obj = room.Objects.FirstOrDefault(o => o.ID.ToString() == want || string.Equals(o.Name, want, StringComparison.OrdinalIgnoreCase))
                    ?? throw new ToolException($"Room {room.Number} has no object '{want}'. Objects: {string.Join(", ", room.Objects.Select(o => o.ID + "=" + o.Name))}.");
                int id = obj.ID;
                string name = obj.Name;
                room.Objects.Remove(obj);
                foreach (RoomObject o in room.Objects)
                    if (o.ID > id) o.ID--;
                var save = RoomSession.Save(ctx, room);
                return ToolResult.Json(new { number = room.Number, id, name, deleted = true, save });
            },
        };

        private static Tool SetRoomEvent(ToolContext ctx) => new Tool
        {
            Name = "set_room_event",
            Title = "Set room event",
            Description = "Bind a room event to a script function and add an empty stub for it to the room script (fill it in " +
                          "with edit_script name=\"roomN\"). 'entity' is \"room\", \"hotspot:N\", \"object:N\" or \"region:N\" " +
                          "(or a hotspot/object script name). 'event' is the event suffix or display name, e.g. room Load/" +
                          "AfterFadeIn/LeaveLeft, hotspot/object Look/Interact/Talk/UseInv/PickUp/AnyClick/WalkOn/MouseMove, " +
                          "region WalksOnto/WalksOff/Standing. 'function' defaults to <item>_<event>. Saves the room.",
            InputSchema = Schema.Object()
                .Required("number", NumberProp())
                .Required("entity", Schema.String("\"room\", or \"hotspot:N\" / \"object:N\" / \"region:N\", or a hotspot/object script name."))
                .Required("event", Schema.String("Event suffix or display name, e.g. \"Load\", \"Look\", \"AnyClick\"."))
                .Optional("function", Schema.String("Script function name to bind (defaults to <item>_<event>)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(120),
            Handler = args =>
            {
                Room room = RoomSession.Open(ctx, args.Int("number"));
                if (room.Script == null) room.LoadScript();

                EntityRef target = ParseEntity(args.String("entity"));
                Interactions interactions = ResolveInteractions(room, target, out string itemName);

                int index = FindEventIndex(interactions, args.String("event"));
                string suffix = interactions.FunctionSuffixes[index];
                string paramList = interactions.FunctionParameterLists[index];
                string funcName = args.Has("function") ? args.String("function").Trim() : itemName + "_" + suffix;
                if (!Regex.IsMatch(funcName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                    throw new ToolException($"'{funcName}' is not a valid script function name.");

                interactions.SetScriptFunctionNameForInteractionSuffix(suffix, funcName);
                string newText = EditorInternals.InsertFunction(room.Script.Text, funcName, paramList);
                bool stubAdded = newText != room.Script.Text;
                if (stubAdded)
                {
                    room.Script.Text = newText;
                    room.Script.Modified = true;
                    room.Script.SaveToDisk();
                    EditorInternals.NotifyScriptChanged(room.Script);
                }
                // The editor's post-save check for missing handlers reads the script's autocomplete data.
                ctx.Editor.RebuildAutocompleteCache(room.Script);
                var save = RoomSession.Save(ctx, room);

                return ToolResult.Json(new
                {
                    number = room.Number,
                    entity = args.String("entity"),
                    @event = EventName(interactions, index),
                    function = funcName,
                    parameters = paramList,
                    stubAdded,
                    script = room.ScriptFileName,
                    save,
                });
            },
        };

        // --- entities ---

        internal struct EntityRef
        {
            public string Type;   // "room", "hotspot", "object", "region", "walkablearea", "walkbehind", or "name"
            public int Index;     // area/object ID, or -1
            public string Name;   // script name when Type == "name"
        }

        private static readonly string[][] TypeAliases =
        {
            new[] { "walkablearea", "walkablearea", "walkable" },
            new[] { "walkbehind", "walkbehind" },
            new[] { "hotspot", "hotspot" },
            new[] { "object", "object" },
            new[] { "region", "region" },
        };

        /// <summary>
        /// Parses "room", "hotspot:3", "hotspot 3", "hotspot3", "object:2", "region:1", "walkablearea:1",
        /// "walkable:1", "walkbehind:1" (case-insensitive), or a bare script name such as "hDoor".
        /// </summary>
        internal static EntityRef ParseEntity(string entity)
        {
            if (string.IsNullOrWhiteSpace(entity)) throw new ToolException("'entity' is required.");
            string s = entity.Trim();
            if (string.Equals(s, "room", StringComparison.OrdinalIgnoreCase))
                return new EntityRef { Type = "room", Index = -1 };

            foreach (string[] alias in TypeAliases)
            {
                for (int i = 1; i < alias.Length; i++)
                {
                    if (!s.StartsWith(alias[i], StringComparison.OrdinalIgnoreCase)) continue;
                    string rest = s.Substring(alias[i].Length);
                    string trimmed = rest.TrimStart(':', ' ', '\t');
                    if (int.TryParse(trimmed, out int index) && index >= 0)
                        return new EntityRef { Type = alias[0], Index = index };
                    // "hotspotDoor" style names fall through to the script-name case; "hotspot:x" is an error.
                    if (trimmed.Length != rest.Length || rest.Length == 0)
                        throw new ToolException($"Could not read an ID from '{entity}'. Use e.g. \"{alias[0]}:3\".");
                }
            }
            if (Regex.IsMatch(s, "^[A-Za-z_][A-Za-z0-9_]*$"))
                return new EntityRef { Type = "name", Index = -1, Name = s };
            throw new ToolException($"Unknown entity '{entity}'. Use {EntityHelp}.");
        }

        private static object ResolveEntity(Room room, EntityRef target, out string label)
        {
            label = target.Type == "room" ? "room" : target.Type + ":" + target.Index;
            switch (target.Type)
            {
                case "room": return room;
                case "hotspot": return Slot(room.Hotspots, h => h.ID, target.Index, room, "hotspot");
                case "object": return Slot(room.Objects, o => o.ID, target.Index, room, "object");
                case "region": return Slot(room.Regions, r => r.ID, target.Index, room, "region");
                case "walkablearea": return Slot(room.WalkableAreas, w => w.ID, target.Index, room, "walkable area");
                case "walkbehind": return Slot(room.WalkBehinds, w => w.ID, target.Index, room, "walk-behind");
                case "name":
                    RoomHotspot hs = room.Hotspots.FirstOrDefault(h => string.Equals(h.Name, target.Name, StringComparison.OrdinalIgnoreCase));
                    if (hs != null) { label = "hotspot:" + hs.ID; return hs; }
                    RoomObject obj = room.Objects.FirstOrDefault(o => string.Equals(o.Name, target.Name, StringComparison.OrdinalIgnoreCase));
                    if (obj != null) { label = "object:" + obj.ID; return obj; }
                    throw new ToolException($"Room {room.Number} has no hotspot or object named '{target.Name}'.");
                default:
                    throw new ToolException($"Unsupported entity type '{target.Type}'.");
            }
        }

        private static T Slot<T>(IEnumerable<T> items, Func<T, int> id, int index, Room room, string what) where T : class =>
            items.FirstOrDefault(x => id(x) == index) ?? throw new ToolException($"Room {room.Number} has no {what} {index}.");

        private static Interactions ResolveInteractions(Room room, EntityRef target, out string itemName)
        {
            object entity = ResolveEntity(room, target, out _);
            switch (entity)
            {
                case Room r: itemName = "room"; return r.Interactions;
                case RoomHotspot h: itemName = h.Name; return h.Interactions;
                case RoomObject o: itemName = o.Name; return o.Interactions;
                case RoomRegion g: itemName = "region" + g.ID; return g.Interactions;
                default: throw new ToolException("Walkable areas and walk-behinds have no events.");
            }
        }

        /// <summary>Applies properties to a room part, checking hotspot/object script names and refusing a room renumber.</summary>
        private static List<string> ApplyRoomProperties(Game game, Room room, object target, JObject props)
        {
            var descriptors = TypeDescriptor.GetProperties(target);
            var toApply = new JObject();
            foreach (var pair in props)
            {
                PropertyDescriptor pd = PropertyReflection.Find(descriptors, pair.Key);
                if (pd != null && target is Room && pd.Name == "Number")
                    throw new ToolException("Renumbering a room is not supported here; use the editor.");
                if (pd != null && pd.Name == "Name" && (target is RoomHotspot || target is RoomObject))
                {
                    string newName = pair.Value?.ToString() ?? string.Empty;
                    string oldName = target is RoomHotspot h ? h.Name : ((RoomObject)target).Name;
                    if (!string.Equals(newName, oldName, StringComparison.Ordinal))
                    {
                        if (!Regex.IsMatch(newName, "^[A-Za-z_][A-Za-z0-9_]*$"))
                            throw new ToolException($"'{newName}' is not a valid script name.");
                        bool clash = game.IsScriptNameAlreadyUsed(newName, target)
                            || room.Hotspots.Any(x => x != target && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase))
                            || room.Objects.Any(x => x != target && string.Equals(x.Name, newName, StringComparison.OrdinalIgnoreCase));
                        if (clash) throw new ToolException($"The script name '{newName}' is already in use.");
                    }
                }
                toApply[pd?.Name ?? pair.Key] = pair.Value;
            }
            return PropertyReflection.Apply(target, toApply);
        }

        /// <summary>The project tree shows the UnloadedRoom's description; keep it in step with the loaded room.</summary>
        private static void SyncDescription(Game game, Room room)
        {
            var unloaded = game.Rooms.Cast<UnloadedRoom>().FirstOrDefault(r => r.Number == room.Number);
            if (unloaded != null && !ReferenceEquals(unloaded, room) && unloaded.Description != room.Description)
                unloaded.Description = room.Description;
        }

        private static List<object> BoundEvents(Interactions interactions)
        {
            var list = new List<object>();
            if (interactions == null) return list;
            string[] names = interactions.DisplayNames, suffixes = interactions.FunctionSuffixes, fns = interactions.ScriptFunctionNames;
            for (int i = 0; i < fns.Length; i++)
                if (!string.IsNullOrEmpty(fns[i]))
                    list.Add(new { @event = EventName(interactions, i), function = fns[i] });
            return list;
        }

        /// <summary>
        /// An event's name for tool output: its display name, or its function suffix when the display name is a
        /// cursor-mode placeholder such as "$$01 hotspot" (the editor substitutes the game's cursor names there).
        /// </summary>
        internal static string EventName(Interactions interactions, int index)
        {
            string display = interactions.DisplayNames[index];
            return string.IsNullOrEmpty(display) || display.StartsWith("$$") ? interactions.FunctionSuffixes[index] : display;
        }

        internal static int FindEventIndex(Interactions interactions, string eventName)
        {
            string[] suffixes = interactions.FunctionSuffixes, names = interactions.DisplayNames;
            for (int i = 0; i < suffixes.Length; i++)
                if (string.Equals(suffixes[i], eventName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(names[i], eventName, StringComparison.OrdinalIgnoreCase))
                    return i;
            throw new ToolException($"Unknown event '{eventName}'. Valid events: {string.Join(", ", suffixes)}.");
        }

        // --- masks ---

        internal static RoomAreaMaskType ParseMask(string mask)
        {
            switch ((mask ?? "none").Trim().ToLowerInvariant())
            {
                case "none": return RoomAreaMaskType.None;
                case "hotspot":
                case "hotspots": return RoomAreaMaskType.Hotspots;
                case "walkbehind":
                case "walkbehinds": return RoomAreaMaskType.WalkBehinds;
                case "walkable":
                case "walkableareas":
                case "walkablearea": return RoomAreaMaskType.WalkableAreas;
                case "region":
                case "regions": return RoomAreaMaskType.Regions;
                default: throw new ToolException($"Unknown mask '{mask}'. Use none, hotspots, walkbehinds, walkableareas or regions.");
            }
        }

        private static int AreaSlots(Room room, RoomAreaMaskType mask)
        {
            switch (mask)
            {
                case RoomAreaMaskType.Hotspots: return room.Hotspots.Count;
                case RoomAreaMaskType.WalkableAreas: return room.WalkableAreas.Count;
                case RoomAreaMaskType.WalkBehinds: return room.WalkBehinds.Count;
                case RoomAreaMaskType.Regions: return room.Regions.Count;
                default: return 0;
            }
        }

        /// <summary>Area IDs (excluding 0) that cover at least one pixel of the mask.</summary>
        private static List<int> PaintedAreas(Room room, RoomAreaMaskType mask)
        {
            using (Bitmap bmp = EditorInternals.ExportAreaMask(room, mask))
            {
                byte[,] areas = ReadMask(bmp);
                var seen = new SortedSet<int>();
                foreach (byte b in areas) if (b > 0) seen.Add(b);
                return seen.ToList();
            }
        }

        /// <summary>A mask bitmap's area numbers as [y, x]. Masks export as 8-bit indexed (pixel = area).</summary>
        private static byte[,] ReadMask(Bitmap bmp)
        {
            var result = new byte[bmp.Height, bmp.Width];
            if (bmp.PixelFormat == PixelFormat.Format8bppIndexed)
            {
                BitmapData data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, bmp.PixelFormat);
                try
                {
                    var row = new byte[bmp.Width];
                    for (int y = 0; y < bmp.Height; y++)
                    {
                        System.Runtime.InteropServices.Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                        for (int x = 0; x < row.Length; x++) result[y, x] = row[x];
                    }
                }
                finally { bmp.UnlockBits(data); }
                return result;
            }
            if ((bmp.PixelFormat & PixelFormat.Indexed) != 0)
            {
                // Map other indexed formats through their palette back to indexes.
                Color[] entries = bmp.Palette.Entries;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        result[y, x] = (byte)Math.Max(0, Array.IndexOf(entries, bmp.GetPixel(x, y)));
                return result;
            }
            throw new ToolException($"Unexpected mask format {bmp.PixelFormat}.");
        }

        /// <summary>Blends each painted area of a mask (any resolution) over a room image in a distinct colour.</summary>
        private static List<int> OverlayMask(Bitmap image, byte[,] mask)
        {
            int mh = mask.GetLength(0), mw = mask.GetLength(1);
            var seen = new SortedSet<int>();
            for (int y = 0; y < image.Height; y++)
            {
                int my = Math.Min(mh - 1, y * mh / image.Height);
                for (int x = 0; x < image.Width; x++)
                {
                    int area = mask[my, Math.Min(mw - 1, x * mw / image.Width)];
                    if (area == 0) continue;
                    seen.Add(area);
                    Color c = AreaColor(area), p = image.GetPixel(x, y);
                    image.SetPixel(x, y, Color.FromArgb(255, (p.R + c.R * 2) / 3, (p.G + c.G * 2) / 3, (p.B + c.B * 2) / 3));
                }
            }
            return seen.ToList();
        }

        private static readonly Color[] AreaColors =
        {
            Color.Red, Color.Lime, Color.Blue, Color.Yellow, Color.Magenta, Color.Cyan, Color.Orange, Color.White,
            Color.DeepPink, Color.Chartreuse, Color.DodgerBlue, Color.Gold, Color.Purple, Color.Teal, Color.Brown, Color.Gray,
        };

        private static Color AreaColor(int area) => AreaColors[(area - 1) % AreaColors.Length];

        private static string ColorName(Color c) => c.Name.ToLowerInvariant();

        internal sealed class MaskShape
        {
            public string Type;
            public int X, Y, Width, Height, X2, Y2, Rx, Ry;
            public List<Point> Points;
        }

        internal static List<MaskShape> ParseShapes(JArray shapes)
        {
            if (shapes.Count == 0) throw new ToolException("'shapes' must contain at least one shape.");
            var list = new List<MaskShape>();
            for (int i = 0; i < shapes.Count; i++)
            {
                if (!(shapes[i] is JObject o)) throw new ToolException($"shapes[{i}] must be an object.");
                string type = ((string)o["type"] ?? "").Trim().ToLowerInvariant();
                int Get(string key)
                {
                    JToken t = o[key];
                    if (t == null || t.Type == JTokenType.Null) throw new ToolException($"shapes[{i}] ({type}) needs '{key}'.");
                    try { return (int)Math.Round(t.Value<double>()); }
                    catch (Exception) { throw new ToolException($"shapes[{i}].{key} must be a number."); }
                }
                var s = new MaskShape { Type = type };
                switch (type)
                {
                    case "rect":
                        if (o["width"] != null) { s.X = Get("x"); s.Y = Get("y"); s.Width = Get("width"); s.Height = Get("height"); }
                        else { s.X = Get("x1"); s.Y = Get("y1"); s.Width = Get("x2") - s.X + 1; s.Height = Get("y2") - s.Y + 1; }
                        if (s.Width <= 0 || s.Height <= 0) throw new ToolException($"shapes[{i}] (rect) must have a positive width and height.");
                        break;
                    case "ellipse":
                        s.X = Get("x"); s.Y = Get("y"); s.Rx = Get("rx"); s.Ry = Get("ry");
                        if (s.Rx <= 0 || s.Ry <= 0) throw new ToolException($"shapes[{i}] (ellipse) needs positive rx and ry.");
                        break;
                    case "line":
                        s.X = Get("x1"); s.Y = Get("y1"); s.X2 = Get("x2"); s.Y2 = Get("y2");
                        break;
                    case "fill":
                        s.X = Get("x"); s.Y = Get("y");
                        break;
                    case "polygon":
                        if (!(o["points"] is JArray pts) || pts.Count < 3)
                            throw new ToolException($"shapes[{i}] (polygon) needs 'points' with at least 3 [x,y] pairs.");
                        s.Points = new List<Point>();
                        foreach (JToken p in pts)
                        {
                            if (p is JArray xy && xy.Count == 2)
                                s.Points.Add(new Point((int)Math.Round(xy[0].Value<double>()), (int)Math.Round(xy[1].Value<double>())));
                            else if (p is JObject po && po["x"] != null && po["y"] != null)
                                s.Points.Add(new Point((int)Math.Round(po["x"].Value<double>()), (int)Math.Round(po["y"].Value<double>())));
                            else
                                throw new ToolException($"shapes[{i}].points entries must be [x,y] or {{x,y}}.");
                        }
                        break;
                    default:
                        throw new ToolException($"shapes[{i}] has unknown type '{type}'. Use rect, polygon, ellipse, line or fill.");
                }
                list.Add(s);
            }
            return list;
        }

        /// <summary>Horizontal spans [x1,x2] (inclusive) for each row y that a polygon covers, sampled at pixel centres.</summary>
        internal static List<int[]> PolygonSpans(IList<Point> pts)
        {
            var spans = new List<int[]>();
            int minY = pts.Min(p => p.Y), maxY = pts.Max(p => p.Y);
            var xs = new List<double>();
            for (int y = minY; y <= maxY; y++)
            {
                double sy = y + 0.5;
                xs.Clear();
                for (int i = 0; i < pts.Count; i++)
                {
                    Point a = pts[i], b = pts[(i + 1) % pts.Count];
                    if ((a.Y <= sy && b.Y > sy) || (b.Y <= sy && a.Y > sy))
                        xs.Add(a.X + (sy - a.Y) * (b.X - a.X) / (double)(b.Y - a.Y));
                }
                xs.Sort();
                for (int k = 0; k + 1 < xs.Count; k += 2)
                {
                    int x1 = (int)Math.Ceiling(xs[k] - 0.5), x2 = (int)Math.Floor(xs[k + 1] - 0.5);
                    if (x2 >= x1) spans.Add(new[] { y, x1, x2 });
                }
            }
            return spans;
        }

        private static void DrawShape(Room room, RoomAreaMaskType mask, int area, MaskShape s)
        {
            int maxX = room.Width - 1, maxY = room.Height - 1;
            int Cx(int x) => Math.Max(0, Math.Min(maxX, x));
            int Cy(int y) => Math.Max(0, Math.Min(maxY, y));
            void Span(int y, int x1, int x2)
            {
                if (y < 0 || y > maxY || x2 < 0 || x1 > maxX) return;
                EditorInternals.DrawFilledRectOntoMask(room, mask, Cx(x1), y, Cx(x2), y, area);
            }

            switch (s.Type)
            {
                case "rect":
                    if (s.X > maxX || s.Y > maxY || s.X + s.Width - 1 < 0 || s.Y + s.Height - 1 < 0) return;
                    EditorInternals.DrawFilledRectOntoMask(room, mask, Cx(s.X), Cy(s.Y), Cx(s.X + s.Width - 1), Cy(s.Y + s.Height - 1), area);
                    break;
                case "ellipse":
                    for (int y = s.Y - s.Ry; y <= s.Y + s.Ry; y++)
                    {
                        double dy = (y - s.Y) / (double)s.Ry;
                        if (Math.Abs(dy) > 1) continue;
                        int dx = (int)Math.Round(s.Rx * Math.Sqrt(1 - dy * dy));
                        Span(y, s.X - dx, s.X + dx);
                    }
                    break;
                case "polygon":
                    foreach (int[] span in PolygonSpans(s.Points)) Span(span[0], span[1], span[2]);
                    for (int i = 0; i < s.Points.Count; i++)
                    {
                        Point a = s.Points[i], b = s.Points[(i + 1) % s.Points.Count];
                        EditorInternals.DrawLineOntoMask(room, mask, Cx(a.X), Cy(a.Y), Cx(b.X), Cy(b.Y), area);
                    }
                    break;
                case "line":
                    EditorInternals.DrawLineOntoMask(room, mask, Cx(s.X), Cy(s.Y), Cx(s.X2), Cy(s.Y2), area);
                    break;
                case "fill":
                    RequireInRoom(room, s.X, s.Y);
                    EditorInternals.DrawFillOntoMask(room, mask, s.X, s.Y, area);
                    break;
            }
        }

        private static void RequireInRoom(Room room, int x, int y)
        {
            if (x < 0 || y < 0 || x >= room.Width || y >= room.Height)
                throw new ToolException($"({x},{y}) is outside room {room.Number} ({room.Width}x{room.Height}).");
        }

        // --- images ---

        /// <summary>Pads an image smaller than the game resolution up to it (as the editor does), else returns a copy.</summary>
        private static Bitmap PadToScreen(Bitmap source, Size screen)
        {
            int w = Math.Max(source.Width, screen.Width), h = Math.Max(source.Height, screen.Height);
            var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(result))
            {
                g.Clear(Color.Black);
                g.DrawImageUnscaled(source, 0, 0);
            }
            return result;
        }

        private static byte[] ToPng(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }
}
