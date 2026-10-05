using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Asset tools: sprites and views. Sprite image data goes through the editor's native sprite set
    /// (see EditorInternals) without triggering the Sprite Manager's UI refresh; model mutation (folders,
    /// the view collection) is done here. Changes persist on save_project (which writes Game.agf and
    /// acsprset.spr). Open editor panes refresh on reload.
    /// </summary>
    internal static class AssetTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ImportSprite(ctx);
            yield return GetSprite(ctx);
            yield return ReplaceSprite(ctx);
            yield return DeleteSprite(ctx);
            yield return CreateView(ctx);
        }

        private static readonly string[] TransparencyNames = Enum.GetNames(typeof(SpriteImportTransparency));

        private static JObject PathProp() => Schema.String("Absolute path to an image file (PNG/BMP/JPG/GIF). Provide this OR 'base64'.");
        private static JObject Base64Prop() => Schema.String("Image bytes as base64 (an optional data: URL prefix is stripped). Provide this OR 'path'.");
        private static JObject TransparencyProp() => Schema.String("How to pick the transparent colour (default LeaveAsIs = keep the image's own transparency).", TransparencyNames);
        private static JObject AlphaProp() => Schema.Boolean("Import the image's alpha channel (default true; forced off for sprites under 32-bit).");

        private static Tool ImportSprite(ToolContext ctx) => new Tool
        {
            Name = "import_sprite",
            Title = "Import sprite",
            Description = "Create a new sprite from an image (file 'path' or 'base64'). Optionally place it in a named sprite " +
                          "folder (default: the root folder). Returns the new sprite number. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Optional("path", PathProp())
                .Optional("base64", Base64Prop())
                .Optional("folder", Schema.String("Name of the sprite folder to add it to (default: the root folder)."))
                .Optional("transparency", TransparencyProp())
                .Optional("alpha", AlphaProp())
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                SpriteFolder folder = ResolveFolder(game, args.String("folder", null));
                SpriteImportTransparency transparency = ParseTransparency(args.String("transparency", "LeaveAsIs"));
                bool alpha = args.Bool("alpha", true);
                using (Bitmap bmp = LoadBitmap(args))
                {
                    Sprite sprite = EditorInternals.CreateSpriteFromBitmap(bmp, transparency, 0, true, false, alpha);
                    if (sprite == null) throw new ToolException("The editor could not create a sprite from that image.");
                    if (sprite.ColorDepth < 32) sprite.AlphaChannel = false;
                    sprite.SourceFile = string.Empty;
                    folder.Sprites.Add(sprite);
                    return ToolResult.Json(new
                    {
                        number = sprite.Number,
                        width = sprite.Width,
                        height = sprite.Height,
                        colorDepth = sprite.ColorDepth,
                        alphaChannel = sprite.AlphaChannel,
                        folder = folder.Name,
                        created = true,
                    });
                }
            },
        };

        private static Tool GetSprite(ToolContext ctx) => new Tool
        {
            Name = "get_sprite",
            Title = "Get sprite",
            Description = "Return a sprite's image as a PNG, by sprite number. Reads only.",
            InputSchema = Schema.Object().Required("number", Schema.Integer("The sprite number.")).Build(),
            Annotations = ToolAnnotations.ReadOnlyTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                int n = args.Int("number");
                Sprite sprite = game.RootSpriteFolder.FindSpriteByID(n, true)
                    ?? throw new ToolException($"No sprite numbered {n}.");
                using (Bitmap bmp = EditorInternals.GetSpriteBitmap(n))
                {
                    byte[] png = ToPng(bmp);
                    var result = new ToolResult();
                    result.AddText($"Sprite {n} ({bmp.Width}x{bmp.Height}, {sprite.ColorDepth}-bit{(sprite.AlphaChannel ? ", alpha" : "")}).");
                    result.AddImage(png, "image/png");
                    return result;
                }
            },
        };

        private static Tool ReplaceSprite(ToolContext ctx) => new Tool
        {
            Name = "replace_sprite",
            Title = "Replace sprite",
            Description = "Replace an existing sprite's image (keeping its number) with a new image (file 'path' or 'base64'). " +
                          "Everything that references the sprite number keeps working. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Required("number", Schema.Integer("The sprite number to replace."))
                .Optional("path", PathProp())
                .Optional("base64", Base64Prop())
                .Optional("transparency", TransparencyProp())
                .Optional("alpha", AlphaProp())
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                int n = args.Int("number");
                Sprite sprite = game.RootSpriteFolder.FindSpriteByID(n, true)
                    ?? throw new ToolException($"No sprite numbered {n}.");
                SpriteImportTransparency transparency = ParseTransparency(args.String("transparency", "LeaveAsIs"));
                bool alpha = args.Bool("alpha", true);
                using (Bitmap bmp = LoadBitmap(args))
                {
                    EditorInternals.ReplaceSpriteWithBitmap(sprite, bmp, transparency, 0, true, false, alpha);
                    if (sprite.ColorDepth < 32) sprite.AlphaChannel = false;
                    sprite.SourceFile = string.Empty;
                    return ToolResult.Json(new
                    {
                        number = sprite.Number,
                        width = sprite.Width,
                        height = sprite.Height,
                        colorDepth = sprite.ColorDepth,
                        alphaChannel = sprite.AlphaChannel,
                        replaced = true,
                    });
                }
            },
        };

        private static Tool DeleteSprite(ToolContext ctx) => new Tool
        {
            Name = "delete_sprite",
            Title = "Delete sprite",
            Description = "Delete a sprite by number. Refuses (with a usage report) if the sprite is still used by any view, " +
                          "character, GUI, inventory item, etc. Call save_project to persist.",
            InputSchema = Schema.Object().Required("number", Schema.Integer("The sprite number to delete.")).Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                int n = args.Int("number");
                Sprite sprite = game.RootSpriteFolder.FindSpriteByID(n, true)
                    ?? throw new ToolException($"No sprite numbered {n}.");

                string usage = EditorInternals.GetSpriteUsageReport(n, game);
                if (usage != null)
                    throw new ToolException($"Cannot delete sprite {n} because it is in use:" + Environment.NewLine + usage);

                SpriteFolder parent = game.RootSpriteFolder.FindFolderThatContainsSprite(n)
                    ?? throw new ToolException($"Sprite {n} is not in any sprite folder.");
                parent.Sprites.Remove(sprite);
                EditorInternals.DeleteSpriteFromNative(sprite);
                return ToolResult.Json(new { number = n, folder = parent.Name, deleted = true });
            },
        };

        private static Tool CreateView(ToolContext ctx) => new Tool
        {
            Name = "create_view",
            Title = "Create view",
            Description = "Create an animation view from loops of frames. 'loops' is an array of {frames, runNextLoop?}; " +
                          "each frame is {sprite, delay?, flipped?, sound?}. Every sprite must already exist. 'name' is optional " +
                          "(auto-named if omitted). Call save_project to persist.",
            InputSchema = CreateViewSchema(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                List<LoopSpec> loops = ParseViewLoops(args.Array("loops"));

                // Reject frames that point at sprites which do not exist, so the view is never left broken.
                var missing = loops.SelectMany(l => l.Frames).Select(f => f.Image)
                    .Distinct().Where(img => !EditorInternals.DoesSpriteExist(img)).ToList();
                if (missing.Count > 0)
                    throw new ToolException($"These sprite numbers do not exist: {string.Join(", ", missing)}. Import them first.");

                var view = new View { ID = game.FindAndAllocateAvailableViewID() };
                string name = args.String("name", null);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    name = name.Trim();
                    if (game.IsScriptNameAlreadyUsed(name, null))
                        throw new ToolException($"The script name '{name}' is already in use.");
                    view.Name = name; // setter validates it is a legal identifier
                }
                else
                {
                    view.Name = "View" + view.ID;
                }

                foreach (LoopSpec ls in loops)
                {
                    ViewLoop loop = view.AddNewLoop();
                    loop.RunNextLoop = ls.RunNextLoop;
                    int frameId = 0;
                    foreach (FrameSpec fs in ls.Frames)
                    {
                        var frame = new ViewFrame(frameId++) { Image = fs.Image, Delay = fs.Delay, Flipped = fs.Flipped };
                        if (fs.Sound.HasValue) frame.Sound = fs.Sound.Value;
                        loop.Frames.Add(frame);
                    }
                }
                game.RootViewFolder.Items.Add(view);

                return ToolResult.Json(new
                {
                    id = view.ID,
                    name = view.Name,
                    loops = view.Loops.Select(l => new
                    {
                        id = l.ID,
                        runNextLoop = l.RunNextLoop,
                        frames = l.Frames.Select(f => new { id = f.ID, image = f.Image, delay = f.Delay, flipped = f.Flipped, sound = f.Sound }).ToList(),
                    }).ToList(),
                    created = true,
                });
            },
        };

        /// <summary>
        /// The create_view input schema. It declares one form per level (loop and frame objects), with no
        /// anyOf/oneOf, so clients that validate arguments or restrict schema features can follow it exactly.
        /// </summary>
        internal static JObject CreateViewSchema() => Schema.Object()
            .Optional("name", Schema.String("Script name for the view (auto-generated if omitted)."))
            .Required("loops", Schema.ArrayOf(
                Schema.Object()
                    .Required("frames", Schema.ArrayOf(
                        Schema.Object()
                            .Required("sprite", Schema.Integer("Sprite number."))
                            .Optional("delay", Schema.Integer("Extra delay for this frame, in game loops (default 0)."))
                            .Optional("flipped", Schema.Boolean("Draw the frame mirrored (default false)."))
                            .Optional("sound", Schema.Integer("Index of an audio clip to play on this frame."))
                            .Build("A frame."),
                        "The loop's frames, in order."))
                    .Optional("runNextLoop", Schema.Boolean("Carry on into the next loop when this one ends (default false)."))
                    .Build("A loop."),
                "The view's loops, in order. For walking views loop 0 faces down, 1 left, 2 right, 3 up."))
            .Build();

        // --- parsing helpers (pure, unit-tested) ---

        internal sealed class FrameSpec
        {
            public int Image;
            public int Delay;
            public bool Flipped;
            public int? Sound;
        }

        internal sealed class LoopSpec
        {
            public bool RunNextLoop;
            public List<FrameSpec> Frames = new List<FrameSpec>();
        }

        internal static SpriteImportTransparency ParseTransparency(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return SpriteImportTransparency.LeaveAsIs;
            foreach (SpriteImportTransparency v in Enum.GetValues(typeof(SpriteImportTransparency)))
                if (string.Equals(v.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                    return v;
            throw new ToolException($"Unknown transparency '{value}'. Valid: {string.Join(", ", TransparencyNames)}.");
        }

        /// <summary>
        /// Parses the loops of CreateViewSchema. Also accepts the older shorthand (a loop as a bare frame array, a
        /// frame as a bare sprite number), which the schema no longer advertises, so existing callers keep working.
        /// </summary>
        internal static List<LoopSpec> ParseViewLoops(JArray loops)
        {
            if (loops == null || loops.Count == 0)
                throw new ToolException("'loops' must contain at least one loop.");

            var result = new List<LoopSpec>();
            foreach (JToken loopTok in loops)
            {
                var ls = new LoopSpec();
                JArray frames;
                if (loopTok is JArray arr)
                {
                    frames = arr;
                }
                else if (loopTok is JObject obj)
                {
                    ls.RunNextLoop = obj["runNextLoop"]?.Type == JTokenType.Boolean && obj["runNextLoop"].Value<bool>();
                    frames = obj["frames"] as JArray
                        ?? throw new ToolException("A loop object must have a 'frames' array.");
                }
                else
                {
                    throw new ToolException("Each loop must be an array of frames or an object with a 'frames' array.");
                }

                foreach (JToken frameTok in frames)
                    ls.Frames.Add(ParseFrame(frameTok));
                result.Add(ls);
            }
            return result;
        }

        internal static FrameSpec ParseFrame(JToken token)
        {
            if (token != null && token.Type == JTokenType.Integer)
                return new FrameSpec { Image = (int)token };

            if (token is JObject o)
            {
                JToken spr = o["sprite"] ?? o["image"];
                if (spr == null || spr.Type != JTokenType.Integer)
                    throw new ToolException("A frame object must have an integer 'sprite' (the sprite number).");
                return new FrameSpec
                {
                    Image = (int)spr,
                    Delay = o["delay"]?.Type == JTokenType.Integer ? o["delay"].Value<int>() : 0,
                    Flipped = o["flipped"]?.Type == JTokenType.Boolean && o["flipped"].Value<bool>(),
                    Sound = o["sound"]?.Type == JTokenType.Integer ? (int?)o["sound"].Value<int>() : null,
                };
            }

            throw new ToolException("Each frame must be a sprite number, or an object {sprite, delay?, flipped?, sound?}.");
        }

        // --- image / folder helpers ---

        /// <summary>
        /// Loads the image given as 'path' or 'base64' into a detached bitmap. By default it is converted to 32-bit;
        /// keepFormat preserves the source pixel format (e.g. an indexed mask image).
        /// </summary>
        internal static Bitmap LoadBitmap(ToolArgs args, bool keepFormat = false)
        {
            bool hasPath = args.Has("path"), hasBase64 = args.Has("base64");
            if (hasPath == hasBase64)
                throw new ToolException("Provide exactly one of 'path' or 'base64'.");

            if (hasPath)
            {
                string path = args.String("path");
                if (!File.Exists(path)) throw new ToolException($"Image file not found: {path}");
                try
                {
                    // Copy into a detached bitmap so the file handle is released immediately.
                    using (var loaded = new Bitmap(path)) return Detach(loaded, keepFormat);
                }
                catch (ToolException) { throw; }
                catch (Exception e) { throw new ToolException($"Could not read image '{path}': {e.Message}"); }
            }

            byte[] data;
            try { data = Convert.FromBase64String(StripDataUrl(args.String("base64"))); }
            catch (Exception) { throw new ToolException("'base64' is not valid base64 data."); }
            try
            {
                using (var ms = new MemoryStream(data))
                using (var loaded = new Bitmap(ms))
                    return Detach(loaded, keepFormat);
            }
            catch (Exception e) { throw new ToolException($"Could not decode an image from the base64 data: {e.Message}"); }
        }

        private static Bitmap Detach(Bitmap loaded, bool keepFormat) =>
            keepFormat ? loaded.Clone(new Rectangle(0, 0, loaded.Width, loaded.Height), loaded.PixelFormat) : new Bitmap(loaded);

        private static string StripDataUrl(string value)
        {
            string s = (value ?? string.Empty).Trim();
            if (s.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = s.IndexOf(',');
                if (comma >= 0) return s.Substring(comma + 1);
            }
            return s;
        }

        private static SpriteFolder ResolveFolder(Game game, string name)
        {
            SpriteFolder root = game.RootSpriteFolder;
            if (string.IsNullOrWhiteSpace(name)) return root;
            SpriteFolder found = FindFolder(root, name.Trim());
            if (found == null)
                throw new ToolException($"No sprite folder named '{name}'. The root folder is '{root.Name}'.");
            return found;
        }

        private static SpriteFolder FindFolder(SpriteFolder folder, string name)
        {
            if (string.Equals(folder.Name, name, StringComparison.OrdinalIgnoreCase)) return folder;
            foreach (ISpriteFolder sub in folder.SubFolders)
            {
                SpriteFolder found = FindFolder((SpriteFolder)sub, name);
                if (found != null) return found;
            }
            return null;
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
