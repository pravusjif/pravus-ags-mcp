using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    internal enum FontFileKind { Other, Ttf, Wfn }

    /// <summary>Pure helpers for font import.</summary>
    internal static class FontImport
    {
        /// <summary>The kind of font file a path names, by its extension (case-insensitive).</summary>
        public static FontFileKind Classify(string path)
        {
            string ext = string.IsNullOrEmpty(path) ? string.Empty : Path.GetExtension(path.Trim()).ToLowerInvariant();
            switch (ext)
            {
                case ".ttf": return FontFileKind.Ttf;
                case ".wfn": return FontFileKind.Wfn;
                default: return FontFileKind.Other;
            }
        }
    }

    /// <summary>
    /// Fonts: the font kind's Create (a new slot copied from an existing font, as the editor's "New Font" does) and
    /// import_font (a TTF or WFN file over a slot, as the font pane's import does). A font is its agsfnt&lt;ID&gt; file in
    /// the game folder plus its native slot, so both keep the file, Game.Fonts and the native slot in step
    /// (FontsComponent.CommandClick / ImportTTFFont / ImportWFNFont).
    /// </summary>
    internal static class FontTools
    {
        private const int DefaultImportedPointSize = 10; // FontsComponent.DEFAULT_IMPORTED_FONT_SIZE

        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ImportFont(ctx);
        }

        /// <summary>
        /// The font kind's Create: a new font with ID = Fonts.Count copied from 'CopyFrom' (an ID or a script name,
        /// default font 0). Refuses before touching anything when the source font has no file.
        /// </summary>
        internal static object CreateFont(Game game, JObject props)
        {
            JToken from = props?.Properties().FirstOrDefault(p => string.Equals(p.Name, "CopyFrom", StringComparison.OrdinalIgnoreCase))?.Value;
            Font copyFrom = from == null || from.Type == JTokenType.Null
                ? game.Fonts.FirstOrDefault()
                : (Font)EntityRegistry.Resolve(EntityRegistry.Get("font"), game, from);
            if (copyFrom == null) throw new ToolException("The game has no font to copy from.");

            int id = game.Fonts.Count;
            string sourceFile = ExistingFontFile(game, copyFrom)
                ?? throw new ToolException($"Font {copyFrom.ID} has no agsfnt{copyFrom.ID}.wfn or .ttf in the game folder to copy.");
            string ext = Path.GetExtension(sourceFile).ToLowerInvariant();

            var font = new Font
            {
                ID = id,
                Name = "Font " + id,
                OutlineStyle = FontOutlineStyle.None,
                PointSize = copyFrom.PointSize,
                SourceFilename = copyFrom.SourceFilename, // also sets ProjectFilename from its extension
                TTFMetricsFixup = game.Settings.TTFMetricsFixup,
            };
            font.ProjectFilename = "agsfnt" + id + ext;

            string target = Path.Combine(game.DirectoryPath, font.ProjectFilename);
            File.Copy(sourceFile, target, true);
            File.SetAttributes(target, FileAttributes.Archive); // a read-only source makes a read-only copy
            // A file of the other kind left in this slot (by a font deleted outside the editor) would shadow the copy.
            DeleteIfExists(Path.Combine(game.DirectoryPath, ext == ".ttf" ? font.WFNFileName : font.TTFFileName));

            game.Fonts.Add(font);
            game.FilesAddedOrRemoved = true;
            EditorInternals.OnFontAdded(game, id);
            return font;
        }

        /// <summary>The font kind's AfterChange: native slots, the Fonts tree and the font drop-down lists.</summary>
        internal static void AfterChange(Game game)
        {
            // set_properties may have changed a size or the line spacing; the font pane's PropertyChanged does this.
            foreach (Font f in game.Fonts)
            {
                try { EditorInternals.OnFontUpdated(game, f.ID, false); }
                catch (Exception e) { Logger.Write($"Could not update font {f.ID}: {e.Message}"); }
            }
            EditorInternals.RefreshFontsTree();
            FontTypeConverter.SetFontList(game.Fonts);
        }

        private static Tool ImportFont(ToolContext ctx) => new Tool
        {
            Name = "import_font",
            Title = "Import font",
            Description = "Import a TTF or WFN file over an existing font slot (create a new slot first with create_entity " +
                          "type=font, which copies font 0 or properties.CopyFrom). The file is copied to agsfnt<id>.ttf|.wfn in " +
                          "the game folder, replacing the slot's old file; Name, outline and spacing are kept, SizeMultiplier is " +
                          "reset to 1. A TTF takes 'height' (pixels, converted to a point size) or 'pointSize' (default: the " +
                          "font's current size, else 10); a WFN is a bitmap font and takes neither. Returns {id, projectFile, " +
                          "pointSize, height}. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Required("id", Schema.String("Font ID (\"3\") or script name (\"eFontSpeech\")."))
                .Required("path", Schema.String("Absolute path to a .ttf or .wfn file."))
                .Optional("height", Schema.Integer("TTF only: the wanted pixel height; the closest point size is used."))
                .Optional("pointSize", Schema.Integer("TTF only: the point size to import at."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(60),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                var font = (Font)EntityRegistry.Resolve(EntityRegistry.Get("font"), game, args.Raw["id"]);
                string path = args.String("path");
                if (!ImageOutput.IsAbsolutePath(path)) throw new ToolException("'path' must be an absolute file path.");
                FontFileKind kind = FontImport.Classify(path);
                if (kind == FontFileKind.Other)
                    throw new ToolException("Only .ttf and .wfn fonts can be imported (convert SCI and other bitmap fonts to WFN first).");
                if (!File.Exists(path)) throw new ToolException($"Font file not found: {path}");
                bool hasHeight = args.Has("height"), hasPointSize = args.Has("pointSize");
                if (kind == FontFileKind.Wfn && (hasHeight || hasPointSize))
                    throw new ToolException("'height' and 'pointSize' apply to TTF fonts only; scale a WFN font with its SizeMultiplier property.");
                if (hasHeight && hasPointSize) throw new ToolException("Give 'height' or 'pointSize', not both.");
                int height = args.Int("height", 0), pointSize = args.Int("pointSize", 0);
                if ((hasHeight && height < 1) || (hasPointSize && pointSize < 1))
                    throw new ToolException("'height' and 'pointSize' must be 1 or greater.");

                string dir = game.DirectoryPath;
                string ttf = Path.Combine(dir, font.TTFFileName), wfn = Path.Combine(dir, font.WFNFileName);
                string target = kind == FontFileKind.Ttf ? ttf : wfn, other = kind == FontFileKind.Ttf ? wfn : ttf;
                // Keep the slot's current files, so a file the native loader rejects leaves the font as it was.
                byte[] oldTtf = File.Exists(ttf) ? File.ReadAllBytes(ttf) : null;
                byte[] oldWfn = File.Exists(wfn) ? File.ReadAllBytes(wfn) : null;
                try
                {
                    if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(path, target, true);
                        File.SetAttributes(target, FileAttributes.Archive);
                    }
                    DeleteIfExists(other);
                    EditorInternals.ReloadFont(font.ID);
                    // The native measure opens the file through the asset manager, which resolves names against
                    // the game folder: an absolute path is "not a TTF font". Pass the slot's file name, as the editor does.
                    if (kind == FontFileKind.Ttf)
                        pointSize = hasHeight ? EditorInternals.FindTTFSizeForHeight(font.TTFFileName, height)
                                  : hasPointSize ? pointSize
                                  : font.PointSize > 0 ? font.PointSize : DefaultImportedPointSize;
                }
                catch (Exception e) when (!(e is ToolException))
                {
                    Restore(ttf, oldTtf);
                    Restore(wfn, oldWfn);
                    try { EditorInternals.ReloadFont(font.ID); } catch (Exception) { /* the restored files load as before */ }
                    throw new ToolException($"Could not import the font: {e.Message} The slot's old file was restored.");
                }

                font.PointSize = kind == FontFileKind.Ttf ? pointSize : 0;
                font.SizeMultiplier = 1;
                font.SourceFilename = AudioTools.ProjectRelativePath(dir, Path.GetFullPath(path));
                font.ProjectFilename = kind == FontFileKind.Ttf ? font.TTFFileName : font.WFNFileName;
                EditorInternals.OnFontUpdated(game, font.ID, true);
                AfterChange(game);
                return ToolResult.Json(new
                {
                    id = font.ID,
                    name = font.ScriptID,
                    projectFile = font.ProjectFilename,
                    sourceFile = font.SourceFilename,
                    pointSize = font.PointSize,
                    height = font.Height,
                    familyName = font.FamilyName,
                    imported = true,
                });
            },
        };

        /// <summary>The font's file in the game folder (agsfnt&lt;ID&gt;.wfn first, as Utilities.CopyFont does), or null.</summary>
        private static string ExistingFontFile(Game game, Font font)
        {
            foreach (string name in new[] { font.WFNFileName, font.TTFFileName })
            {
                string full = Path.Combine(game.DirectoryPath, name);
                if (File.Exists(full)) return full;
            }
            return null;
        }

        private static void DeleteIfExists(string path)
        {
            if (!File.Exists(path)) return;
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }

        private static void Restore(string path, byte[] bytes)
        {
            try
            {
                if (bytes != null) File.WriteAllBytes(path, bytes);
                else DeleteIfExists(path);
            }
            catch (Exception e) { Logger.Write($"Could not restore {path}: {e.Message}"); }
        }
    }
}
