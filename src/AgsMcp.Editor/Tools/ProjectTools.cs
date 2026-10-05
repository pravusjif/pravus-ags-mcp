using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Tools
{
    internal static class ProjectTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return new Tool
            {
                Name = "project_info",
                Title = "Project info",
                Description = "Summary of the game project open in the AGS editor: name, folder, resolution, colour depth, " +
                              "editor version, and how many rooms, characters, inventory items, dialogs, GUIs, views, sprites and scripts it has.",
                InputSchema = Schema.Object().Build(),
                Annotations = ToolAnnotations.ReadOnlyTool,
                Handler = args =>
                {
                    Game game = ctx.RequireGame();
                    var s = game.Settings;
                    return ToolResult.Json(new
                    {
                        gameName = s.GameName,
                        gameFileName = s.GameFileName,
                        projectFolder = game.DirectoryPath,
                        editorVersion = ctx.Editor.Version,
                        resolution = new { width = s.CustomResolution.Width, height = s.CustomResolution.Height },
                        colorDepth = s.ColorDepth.ToString(),
                        playerCharacter = game.PlayerCharacter?.ScriptName,
                        counts = new
                        {
                            rooms = game.Rooms.Count,
                            characters = game.CharacterFlatList.Count,
                            inventoryItems = game.InventoryFlatList.Count,
                            dialogs = game.DialogFlatList.Count,
                            guis = game.GUIFlatList.Count,
                            views = game.ViewFlatList.Count,
                            sprites = game.RootSpriteFolder.CountSpritesInAllSubFolders(),
                            cursors = game.Cursors.Count,
                            fonts = game.Fonts.Count,
                            audioClips = game.AudioClipFlatList.Count,
                            scriptModules = game.ScriptsAndHeaders.Count,
                        },
                        plugins = game.Plugins.Select(p => p.FileName).ToList(),
                    });
                },
            };

            yield return new Tool
            {
                Name = "save_project",
                Title = "Save project",
                Description = "Save the open AGS project to disk (same as File > Save): Game.agf, scripts and the currently loaded room.",
                InputSchema = Schema.Object().Build(),
                Annotations = new ToolAnnotations { Idempotent = true },
                Handler = args =>
                {
                    ctx.RequireGame();
                    bool ok = EditorInternals.SaveGameFiles();
                    return ok ? ToolResult.Text("Project saved.") : ToolResult.Error("The editor reported that saving failed. Check the editor for an error message.");
                },
            };

            yield return CreateProject(ctx);
            yield return OpenProject(ctx);
        }

        private static Tool CreateProject(ToolContext ctx) => new Tool
        {
            Name = "create_project",
            Title = "Create project",
            Description = "Create a new AGS game from a template in a new (or empty) folder and open it in the editor, as " +
                          "File > New Game does. The open game is saved first unless saveCurrent is false. Templates are " +
                          "the editor's .agt files: " + string.Join(", ", SafeTemplateNames()) + " (default Sierra-style), " +
                          "or an absolute path to an .agt. The new game is built once so its rooms belong to it.",
            InputSchema = Schema.Object()
                .Required("folder", Schema.String("Absolute path of the folder for the new game. It must not exist or be empty."))
                .Optional("template", Schema.String("Template name (e.g. \"Sierra-style\", \"Empty Game\") or an absolute .agt path."))
                .Optional("gameName", Schema.String("The game's title (default: the folder name)."))
                .Optional("fileName", Schema.String("Base name of the built .exe (default: the folder name, letters/digits only)."))
                .Optional("saveCurrent", Schema.Boolean("Save the currently open game first (default true)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromMinutes(5),
            Handler = args =>
            {
                string folder = args.String("folder").Trim();
                if (!Path.IsPathRooted(folder)) throw new ToolException("'folder' must be an absolute path.");
                folder = Path.GetFullPath(folder).TrimEnd('\\', '/');
                if (File.Exists(folder)) throw new ToolException($"'{folder}' is a file.");
                if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any())
                    throw new ToolException($"'{folder}' is not empty. Use an empty or new folder (the editor deletes the folder if creation fails).");

                string template = ResolveTemplate(args.String("template", "Sierra-style"));
                string folderName = Path.GetFileName(folder);
                string gameName = args.String("gameName", folderName);
                string fileName = args.String("fileName", new string(folderName.Where(char.IsLetterOrDigit).ToArray()));
                if (string.IsNullOrEmpty(fileName) || fileName.Any(c => !char.IsLetterOrDigit(c) && c != '_' && c != '-'))
                    throw new ToolException("'fileName' must be letters, digits, '_' or '-'.");

                SaveCurrentIfAsked(ctx, args);
                if (!EditorInternals.CreateNewGame(folder, fileName, gameName, template))
                    throw new ToolException("The editor could not create the game; see its error message.");
                Game game = ctx.RequireGame();
                return ToolResult.Json(new { created = true, projectFolder = game.DirectoryPath, gameName = game.Settings.GameName, template = Path.GetFileNameWithoutExtension(template), rooms = game.Rooms.Count });
            },
        };

        private static Tool OpenProject(ToolContext ctx) => new Tool
        {
            Name = "open_project",
            Title = "Open project",
            Description = "Open another AGS game (its Game.agf, or the folder containing it) in the editor, replacing the open " +
                          "one, as File > Open does. The open game is saved first unless saveCurrent is false.",
            InputSchema = Schema.Object()
                .Required("path", Schema.String("Absolute path to Game.agf or to the game folder."))
                .Optional("saveCurrent", Schema.Boolean("Save the currently open game first (default true)."))
                .Build(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromMinutes(2),
            Handler = args =>
            {
                string path = args.String("path").Trim();
                if (!Path.IsPathRooted(path)) throw new ToolException("'path' must be an absolute path.");
                string agf = Directory.Exists(path) ? Path.Combine(path, "Game.agf") : path;
                if (!File.Exists(agf) || !agf.EndsWith(".agf", StringComparison.OrdinalIgnoreCase))
                    throw new ToolException($"No Game.agf found at '{path}'.");

                SaveCurrentIfAsked(ctx, args);
                if (!EditorInternals.LoadGame(Path.GetFullPath(agf)))
                    throw new ToolException("The editor could not open the game; see its error message.");
                Game game = ctx.RequireGame();
                return ToolResult.Json(new { opened = true, projectFolder = game.DirectoryPath, gameName = game.Settings.GameName, rooms = game.Rooms.Count });
            },
        };

        private static void SaveCurrentIfAsked(ToolContext ctx, ToolArgs args)
        {
            if (!args.Bool("saveCurrent", true)) return;
            if (!(ctx.Editor.CurrentGame is Game current) || string.IsNullOrEmpty(current.DirectoryPath)) return;
            if (!EditorInternals.SaveGameFiles())
                throw new ToolException("Saving the currently open game failed; nothing was changed. Fix it, or pass saveCurrent=false to discard its unsaved changes.");
        }

        private static IEnumerable<string> SafeTemplateNames()
        {
            try { return EditorInternals.GameTemplateFiles().Select(Path.GetFileNameWithoutExtension).ToList(); }
            catch (Exception) { return new[] { "Sierra-style", "Empty Game" }; }
        }

        private static string ResolveTemplate(string template)
        {
            if (Path.IsPathRooted(template))
            {
                if (!File.Exists(template)) throw new ToolException($"Template file not found: {template}");
                return template;
            }
            string[] files = EditorInternals.GameTemplateFiles();
            string match = files.FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), template.Trim(), StringComparison.OrdinalIgnoreCase));
            return match ?? throw new ToolException($"No template named '{template}'. Available: {string.Join(", ", files.Select(Path.GetFileNameWithoutExtension))}.");
        }
    }
}
