using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Audio clip tools: import, replace and delete. They mirror the editor's AudioComponent (CreateAudioClipForFile,
    /// SetAudioClipSourceFile, ImportAudioFiles, DeleteAudioClip) with public AGS.Types members instead of invoking its
    /// private methods, which show modal error boxes and add to the folder last right-clicked in the tree. The file the
    /// build packs is the copy in AudioCache\au{Index:X6}{ext}; the source file is kept where it is and recorded
    /// project-relative. Changes persist on save_project (Game.agf); the cache copy is written at once.
    /// </summary>
    internal static class AudioTools
    {
        public static IEnumerable<Tool> Create(ToolContext ctx)
        {
            yield return ImportAudio(ctx);
            yield return ReplaceAudio(ctx);
            yield return DeleteAudio(ctx);
        }

        /// <summary>The audio cache folder in the game directory (AudioComponent.AUDIO_CACHE_DIRECTORY).</summary>
        internal const string AudioCacheDirectory = "AudioCache";

        /// <summary>Where 'base64' uploads are written, relative to the game directory.</summary>
        internal const string UploadDirectory = "Audio";

        // AudioComponent's _fileTypeMappings.
        private static readonly Dictionary<string, AudioClipFileType> FileTypes = new Dictionary<string, AudioClipFileType>(StringComparer.OrdinalIgnoreCase)
        {
            { ".ogg", AudioClipFileType.OGG },
            { ".mp3", AudioClipFileType.MP3 },
            { ".wav", AudioClipFileType.WAV },
            { ".voc", AudioClipFileType.VOC },
            { ".mid", AudioClipFileType.MIDI },
            { ".mod", AudioClipFileType.MOD },
            { ".xm", AudioClipFileType.MOD },
            { ".s3m", AudioClipFileType.MOD },
            { ".it", AudioClipFileType.MOD },
        };

        internal static string SupportedExtensions => string.Join(", ", FileTypes.Keys);

        private static readonly string[] BundlingNames = Enum.GetNames(typeof(AudioFileBundlingType));
        private static readonly string[] PriorityNames = Enum.GetNames(typeof(AudioClipPriority));
        private static readonly string[] RepeatNames = Enum.GetNames(typeof(InheritableBool));

        private static JObject PathProp() => Schema.String("Path to an audio file (" + SupportedExtensions + "): absolute, or relative to the game folder. Provide this OR 'base64'.");
        private static JObject Base64Prop() => Schema.String("Audio file bytes as base64 (an optional data: URL prefix is stripped). Needs 'fileName'. Provide this OR 'path'.");
        private static JObject FileNameProp() => Schema.String("With 'base64': the file name to save it as in the game's Audio folder, e.g. \"door_knock.wav\". The extension sets the format.");
        private static JObject ClipProp() => Schema.String("The audio clip's script name (e.g. \"aDoorKnock\") or numeric ID.");

        private static Tool ImportAudio(ToolContext ctx) => new Tool
        {
            Name = "import_audio",
            Title = "Import audio clip",
            Description = "Create an audio clip from a sound file (file 'path', or 'base64' + 'fileName'). Formats: " + SupportedExtensions + ". " +
                          "The source file stays where it is (base64 uploads are saved to the game's Audio folder) and is copied to the " +
                          "AudioCache, which is what the build packs. 'type' and 'bundling' default to the target folder's defaults. " +
                          "Returns the clip's scriptName (use it in scripts: aDoorKnock.Play();) and its index (the number a view " +
                          "frame's 'sound' takes). Call save_project to persist.",
            InputSchema = ImportAudioSchema(),
            Annotations = ToolAnnotations.Mutating,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                AudioClipFolder folder = ResolveFolder(game, args.String("folder", null));
                AudioClipType type = args.Has("type") ? ResolveClipType(game.AudioClipTypes, args.String("type")) : FolderDefaultType(game, folder);
                AudioFileBundlingType bundling = args.Has("bundling") ? ParseAudioEnum<AudioFileBundlingType>(args.String("bundling"), "bundling") : folder.DefaultBundlingType;
                AudioClipPriority priority = ParseAudioEnum<AudioClipPriority>(args.String("priority", "Inherit"), "priority");
                InheritableBool repeat = ParseAudioEnum<InheritableBool>(args.String("repeat", "Inherit"), "repeat");
                int volume = args.Int("volume", -1);
                if (volume < -1 || volume > 100) throw new ToolException("'volume' must be -1 (inherit from the folder) or 0..100.");

                // Validate every argument before writing an upload, so a bad call leaves nothing behind.
                string explicitName = args.Has("name") ? CheckExplicitName(game, args.String("name"), null) : null;
                string fullSource = ResolveSource(game, args, null);
                string scriptName = explicitName
                    ?? MakeAudioScriptName(Path.GetFileNameWithoutExtension(fullSource), n => game.IsScriptNameAlreadyUsed(n, null));

                var clip = new AudioClip(scriptName, game.GetNextAudioIndex())
                {
                    ID = game.RootAudioClipFolder.GetAllItemsCount(),
                    Type = type.TypeID,
                    BundlingType = bundling,
                    DefaultPriority = priority,
                    DefaultRepeat = repeat,
                    DefaultVolume = volume,
                };
                SetSource(game, clip, fullSource);
                folder.Items.Add(clip);
                AfterClipsChanged(game);

                return ToolResult.Json(new
                {
                    scriptName = clip.ScriptName,
                    id = clip.ID,
                    index = clip.Index,
                    type = clip.Type,
                    typeName = type.Name,
                    folder = folder.Name,
                    fileType = clip.FileType.ToString(),
                    bundling = clip.BundlingType.ToString(),
                    sourceFileName = clip.SourceFileName,
                    cacheFile = clip.CacheFileName,
                    created = true,
                });
            },
        };

        internal static JObject ImportAudioSchema() => Schema.Object()
            .Optional("path", PathProp())
            .Optional("base64", Base64Prop())
            .Optional("fileName", FileNameProp())
            .Optional("name", Schema.String("Script name for the clip (default: 'a' + the file name, e.g. door_knock.wav -> aDoor_knock). Must be unused."))
            .Optional("folder", Schema.String("Name of the audio folder to add it to (default: the root folder), e.g. \"Sounds\" or \"Music\"."))
            .Optional("type", Schema.String("Audio type, by name or TypeID (default: the folder's default type), e.g. \"Sound\", \"Music\", \"Ambient Sound\"."))
            .Optional("bundling", Schema.String("Where the build stores the clip (default: the folder's default).", BundlingNames))
            .Optional("volume", Schema.Integer("Default volume 0..100, or -1 to inherit the folder's (default -1)."))
            .Optional("priority", Schema.String("Default priority (default Inherit).", PriorityNames))
            .Optional("repeat", Schema.String("Default repeat (default Inherit).", RepeatNames))
            .Build();

        private static Tool ReplaceAudio(ToolContext ctx) => new Tool
        {
            Name = "replace_audio",
            Title = "Replace audio clip",
            Description = "Point an audio clip at a new sound file (file 'path', or 'base64' + 'fileName'), keeping its script name, " +
                          "ID and index so scripts and view frames keep working. With no file, re-copies the clip's current source " +
                          "file into the AudioCache (after regenerating it in place). Builds also re-copy a source whose timestamp " +
                          "changed, so this is only needed to switch files or to update the cache now. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Required("clip", ClipProp())
                .Optional("path", PathProp())
                .Optional("base64", Base64Prop())
                .Optional("fileName", FileNameProp())
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                AudioClip clip = ResolveClip(game, args.String("clip"));
                string oldCache = string.IsNullOrEmpty(clip.CacheFileName) ? null : Path.Combine(game.DirectoryPath, clip.CacheFileName);

                string fullSource;
                if (args.Has("path") || args.Has("base64"))
                {
                    string current = string.IsNullOrEmpty(clip.SourceFileName) ? null : FullPath(game, clip.SourceFileName);
                    fullSource = ResolveSource(game, args, current);
                }
                else
                {
                    if (string.IsNullOrEmpty(clip.SourceFileName))
                        throw new ToolException($"{clip.ScriptName} has no source file recorded. Pass 'path' or 'base64'.");
                    fullSource = FullPath(game, clip.SourceFileName);
                    CheckSourceFile(fullSource);
                }

                SetSource(game, clip, fullSource);
                string newCache = Path.Combine(game.DirectoryPath, clip.CacheFileName);
                if (oldCache != null && !string.Equals(Path.GetFullPath(oldCache), Path.GetFullPath(newCache), StringComparison.OrdinalIgnoreCase))
                    TryDelete(oldCache); // the extension changed, so the cache name did too
                game.FilesAddedOrRemoved = true;
                EditorInternals.DeleteAudioVox(game);

                return ToolResult.Json(new
                {
                    scriptName = clip.ScriptName,
                    id = clip.ID,
                    index = clip.Index,
                    fileType = clip.FileType.ToString(),
                    sourceFileName = clip.SourceFileName,
                    cacheFile = clip.CacheFileName,
                    replaced = true,
                });
            },
        };

        private static Tool DeleteAudio(ToolContext ctx) => new Tool
        {
            Name = "delete_audio",
            Title = "Delete audio clip",
            Description = "Delete an audio clip and its AudioCache copy (the source file is kept). Refuses, listing the uses, if a " +
                          "view frame, the score sound or any script (modules or rooms) still refers to it, unless force=true. Clips " +
                          "after it shift down one ID; indexes do not change. Call save_project to persist.",
            InputSchema = Schema.Object()
                .Required("clip", ClipProp())
                .Optional("force", Schema.Boolean("Delete even if it is still used (default false). Scripts that name it will then fail to compile."))
                .Build(),
            Annotations = ToolAnnotations.DestructiveTool,
            Timeout = TimeSpan.FromSeconds(90),
            Handler = args =>
            {
                Game game = ctx.RequireGame();
                AudioClip clip = ResolveClip(game, args.String("clip"));
                bool force = args.Bool("force", false);

                List<string> uses = FindUses(game, clip);
                if (uses.Count > 0 && !force)
                {
                    const int shown = 20;
                    string list = string.Join(Environment.NewLine, uses.Take(shown).Select(u => "  " + u));
                    if (uses.Count > shown) list += Environment.NewLine + $"  ... and {uses.Count - shown} more";
                    throw new ToolException($"Cannot delete {clip.ScriptName} because it is in use:" + Environment.NewLine + list +
                                            Environment.NewLine + "Remove those uses first, or pass force=true.");
                }

                AudioClipFolder parent = FindFolderContaining(game.RootAudioClipFolder, clip);
                if (parent == null) throw new ToolException($"{clip.ScriptName} is not in any audio folder.");
                int removedId = clip.ID;
                parent.Items.Remove(clip);
                int shifted = 0;
                foreach (AudioClip other in game.RootAudioClipFolder.AllItemsFlat)
                {
                    if (other.ID > removedId) { other.ID--; shifted++; }
                }
                if (!string.IsNullOrEmpty(clip.CacheFileName)) TryDelete(Path.Combine(game.DirectoryPath, clip.CacheFileName));
                AfterClipsChanged(game);

                return ToolResult.Json(new
                {
                    scriptName = clip.ScriptName,
                    id = removedId,
                    index = clip.Index,
                    folder = parent.Name,
                    idsShifted = shifted,
                    usesIgnored = uses,
                    deleted = true,
                });
            },
        };

        // --- model helpers ---

        /// <summary>
        /// Records 'fullSource' as the clip's source and copies it to the clip's cache file, as SetAudioClipSourceFile does.
        /// File.Copy keeps the write time, so the build's "source changed" check (write times differ) starts out false.
        /// </summary>
        private static void SetSource(Game game, AudioClip clip, string fullSource)
        {
            string stored = ProjectRelativePath(game.DirectoryPath, fullSource);
            string cacheRelative = Path.Combine(AudioCacheDirectory, AudioCacheFileName(clip.Index, stored));
            string cacheFull = Path.Combine(game.DirectoryPath, cacheRelative);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFull));
                if (File.Exists(cacheFull)) File.SetAttributes(cacheFull, FileAttributes.Normal);
                File.Copy(fullSource, cacheFull, true);
                File.SetAttributes(cacheFull, FileAttributes.Archive);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new ToolException($"Could not copy '{fullSource}' to '{cacheFull}': {e.Message}");
            }
            clip.SourceFileName = stored;
            clip.FileType = AudioFileType(fullSource);
            clip.CacheFileName = cacheRelative;
            clip.FileLastModifiedDate = File.GetLastWriteTimeUtc(fullSource);
        }

        /// <summary>The editor's follow-up to adding or removing a clip.</summary>
        private static void AfterClipsChanged(Game game)
        {
            game.FilesAddedOrRemoved = true;
            EditorInternals.DeleteAudioVox(game);
            AudioClipTypeConverter.SetAudioClipList(game.RootAudioClipFolder.GetAllAudioClipsFromAllSubFolders());
            EditorInternals.RefreshAudioTree();
            EditorInternals.RegenerateScriptHeader();
        }

        /// <summary>
        /// The absolute path of the file to import: 'path' as given (relative paths are under the game folder), or
        /// 'base64' written to Audio\&lt;fileName&gt;. An upload never overwrites a file, except 'allowOverwrite' (the clip's
        /// current source, when replacing it).
        /// </summary>
        private static string ResolveSource(Game game, ToolArgs args, string allowOverwrite)
        {
            bool hasPath = args.Has("path"), hasBase64 = args.Has("base64");
            if (hasPath == hasBase64)
                throw new ToolException("Provide exactly one of 'path' or 'base64'.");

            if (hasPath)
            {
                string full = FullPath(game, args.String("path"));
                CheckSourceFile(full);
                return full;
            }

            string fileName = CheckUploadFileName(args.String("fileName", null));
            byte[] data;
            try { data = Convert.FromBase64String(AssetTools.StripDataUrl(args.String("base64"))); }
            catch (FormatException) { throw new ToolException("'base64' is not valid base64 data."); }
            if (data.Length == 0) throw new ToolException("'base64' decodes to an empty file.");

            string target = Path.Combine(game.DirectoryPath, UploadDirectory, fileName);
            bool mayOverwrite = allowOverwrite != null && string.Equals(Path.GetFullPath(target), Path.GetFullPath(allowOverwrite), StringComparison.OrdinalIgnoreCase);
            if (File.Exists(target) && !mayOverwrite)
                throw new ToolException($"{UploadDirectory}\\{fileName} already exists. Pick another 'fileName', or import the existing file with 'path'.");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.WriteAllBytes(target, data);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new ToolException($"Could not write '{target}': {e.Message}");
            }
            return target;
        }

        private static string FullPath(Game game, string path)
        {
            try
            {
                string p = path.Trim().Trim('"');
                return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(game.DirectoryPath, p));
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e is PathTooLongException)
            {
                throw new ToolException($"Invalid path '{path}': {e.Message}");
            }
        }

        private static void CheckSourceFile(string full)
        {
            AudioFileType(full); // extension first: the clearer error for a wrong file
            if (!File.Exists(full)) throw new ToolException($"Audio file not found: {full}");
            if (new FileInfo(full).Length == 0) throw new ToolException($"Audio file is empty: {full}");
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception e) { Logger.Write("Could not delete " + path + ": " + e.Message); }
        }

        private static string CheckExplicitName(Game game, string name, object ignore)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) throw new ToolException("'name' must not be empty.");
            try { AGS.Types.Utilities.ValidateScriptName(name); }
            catch (AGS.Types.InvalidDataException e) { throw new ToolException(e.Message); }
            if (game.IsScriptNameAlreadyUsed(name, ignore))
                throw new ToolException($"The script name '{name}' is already in use.");
            return name;
        }

        private static AudioClip ResolveClip(Game game, string idOrName)
        {
            string want = (idOrName ?? string.Empty).Trim();
            foreach (AudioClip c in game.AudioClipFlatList)
            {
                if (string.Equals(c.ScriptName, want, StringComparison.OrdinalIgnoreCase) || c.ID.ToString() == want)
                    return c;
            }
            throw new ToolException($"No audio clip with script name or ID '{want}'. Use list_entities type=audioclip to see them.");
        }

        /// <summary>An audio folder by name (case-insensitive, searched depth-first), or the root for an empty name.</summary>
        internal static AudioClipFolder ResolveFolder(Game game, string name)
        {
            List<AudioClipFolder> all = AllFolders(game.RootAudioClipFolder);
            if (string.IsNullOrWhiteSpace(name)) return all[0];
            AudioClipFolder found = all.FirstOrDefault(f => string.Equals(f.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (found == null)
                throw new ToolException($"No audio folder named '{name}'. Folders: {string.Join(", ", all.Select(f => f.Name))}.");
            return found;
        }

        /// <summary>The folder and all its subfolders, depth-first, the folder itself first.</summary>
        internal static List<AudioClipFolder> AllFolders(AudioClipFolder root)
        {
            var all = new List<AudioClipFolder> { root };
            for (int i = 0; i < all.Count; i++)
                all.InsertRange(i + 1, all[i].SubFolders);
            return all;
        }

        /// <summary>The folder whose SubFolders hold 'child', or null for the root (or a folder not in the tree).</summary>
        internal static AudioClipFolder FindParentFolder(AudioClipFolder root, AudioClipFolder child) =>
            AllFolders(root).FirstOrDefault(f => f.SubFolders.Contains(child));

        private static AudioClipFolder FindFolderContaining(AudioClipFolder root, AudioClip clip) =>
            AllFolders(root).FirstOrDefault(f => f.Items.Contains(clip));

        private static AudioClipType FolderDefaultType(Game game, AudioClipFolder folder)
        {
            AudioClipType type = game.AudioClipTypes.FirstOrDefault(t => t.TypeID == folder.DefaultType);
            if (type == null)
                throw new ToolException($"Folder '{folder.Name}' has default type {folder.DefaultType}, which does not exist. Pass 'type'. " + DescribeTypes(game.AudioClipTypes));
            return type;
        }

        /// <summary>Where the clip is referenced: view frames and the score sound (by Index), and scripts (by name).</summary>
        private static List<string> FindUses(Game game, AudioClip clip)
        {
            var uses = new List<string>();
            foreach (View view in game.RootViewFolder.AllItemsFlat)
                foreach (ViewLoop loop in view.Loops)
                    foreach (ViewFrame frame in loop.Frames)
                        if (frame.Sound == clip.Index)
                            uses.Add($"view {view.Name} loop {loop.ID} frame {frame.ID} (sound)");
            if (game.Settings.PlaySoundOnScore == clip.Index)
                uses.Add("General Settings: PlaySoundOnScore");

            foreach (ScriptAndHeader sh in game.ScriptsAndHeaders)
            {
                AddScriptUses(uses, sh.Header?.FileName, sh.Header?.Text, clip.ScriptName);
                AddScriptUses(uses, sh.Script?.FileName, sh.Script?.Text, clip.ScriptName);
            }
            foreach (UnloadedRoom room in game.Rooms.Cast<UnloadedRoom>())
            {
                string text = room.Script?.Text;
                if (text == null)
                {
                    string file = Path.Combine(game.DirectoryPath, room.ScriptFileName);
                    try { if (File.Exists(file)) text = File.ReadAllText(file); }
                    catch (IOException) { }
                }
                AddScriptUses(uses, room.ScriptFileName, text, clip.ScriptName);
            }
            return uses;
        }

        private static void AddScriptUses(List<string> uses, string fileName, string text, string name)
        {
            foreach (int line in FindScriptNameUses(text, name))
                uses.Add($"{fileName}:{line}");
        }

        // --- pure helpers (unit-tested) ---

        /// <summary>The clip file type for a file name's extension, or a tool error listing the supported ones.</summary>
        internal static AudioClipFileType AudioFileType(string fileName)
        {
            string ext = Path.GetExtension(fileName ?? string.Empty);
            if (!string.IsNullOrEmpty(ext) && FileTypes.TryGetValue(ext, out AudioClipFileType type)) return type;
            throw new ToolException($"Unsupported audio file '{Path.GetFileName(fileName)}'. Supported: {SupportedExtensions}.");
        }

        /// <summary>The cache file name the editor derives (AudioComponent.GetCacheFileName): au + 6 hex digits of Index + source extension.</summary>
        internal static string AudioCacheFileName(int index, string sourceFileName) =>
            string.Format("au{0:X6}{1}", index, Path.GetExtension(sourceFileName));

        /// <summary>
        /// The editor's script name for an imported file (AudioComponent.EnsureScriptNameIsUnique): invalid characters
        /// become '_', then 'a' + the first letter capitalised, numbered 1, 2... until unused. An empty stem gives "aClip".
        /// </summary>
        internal static string MakeAudioScriptName(string stem, Func<string, bool> isUsed)
        {
            string s = AGS.Types.Utilities.RemoveInvalidCharactersFromScriptName((stem ?? string.Empty).Trim());
            if (s.Length == 0) s = "Clip";
            string baseName = "a" + char.ToUpperInvariant(s[0]) + s.Substring(1);
            string name = baseName;
            for (int i = 1; isUsed(name); i++) name = baseName + i;
            return name;
        }

        /// <summary>An upload file name: a bare name (no folders) with a supported extension.</summary>
        internal static string CheckUploadFileName(string fileName)
        {
            string name = (fileName ?? string.Empty).Trim();
            if (name.Length == 0) throw new ToolException("'base64' needs 'fileName' (e.g. \"door_knock.wav\"); its extension sets the format.");
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name != Path.GetFileName(name) || name == "." || name == "..")
                throw new ToolException($"'fileName' must be a plain file name without folders: {fileName}");
            AudioFileType(name);
            return name;
        }

        /// <summary>The path as the editor stores it: relative to the game folder when inside it, otherwise absolute.</summary>
        internal static string ProjectRelativePath(string gameDirectory, string fullPath)
        {
            string dir = gameDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(dir, StringComparison.OrdinalIgnoreCase) ? fullPath.Substring(dir.Length) : fullPath;
        }

        internal static T ParseAudioEnum<T>(string value, string argName) where T : struct
        {
            string v = (value ?? string.Empty).Trim();
            foreach (string name in Enum.GetNames(typeof(T)))
                if (string.Equals(name, v, StringComparison.OrdinalIgnoreCase))
                    return (T)Enum.Parse(typeof(T), name);
            throw new ToolException($"Unknown {argName} '{value}'. Valid: {string.Join(", ", Enum.GetNames(typeof(T)))}.");
        }

        /// <summary>An audio type by TypeID, name or script ID (eAudioTypeSound), case-insensitive.</summary>
        internal static AudioClipType ResolveClipType(IList<AudioClipType> types, string value)
        {
            string v = (value ?? string.Empty).Trim();
            foreach (AudioClipType t in types)
            {
                if (t.TypeID.ToString() == v || string.Equals(t.Name, v, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(t.ScriptID, v, StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            throw new ToolException($"Unknown audio type '{value}'. " + DescribeTypes(types));
        }

        private static string DescribeTypes(IList<AudioClipType> types) =>
            "Types: " + string.Join(", ", types.Select(t => $"{t.TypeID} {t.Name}")) + ".";

        /// <summary>1-based line numbers where 'name' appears as a whole word (an identifier) in script text.</summary>
        internal static List<int> FindScriptNameUses(string text, string name)
        {
            var lines = new List<int>();
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(name)) return lines;
            var word = new Regex(@"(?<![A-Za-z0-9_])" + Regex.Escape(name) + @"(?![A-Za-z0-9_])");
            string[] split = text.Split('\n');
            for (int i = 0; i < split.Length; i++)
                if (word.IsMatch(split[i])) lines.Add(i + 1);
            return lines;
        }
    }
}
