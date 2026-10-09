using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using AGS.Types;

namespace AgsMcp.Editor
{
    /// <summary>
    /// The only place that touches AGSEditor.exe internals (public classes that are not part of the
    /// AGS.Types plugin interface). Keeping them here contains breakage when the editor version changes.
    /// </summary>
    internal static class EditorInternals
    {
        public static string EditorDirectory => AGS.Editor.AGSEditor.Instance.EditorDirectory;

        public static Game CurrentGame => AGS.Editor.AGSEditor.Instance.CurrentGame;

        /// <summary>Saves Game.agf, scripts and any modified loaded room (same as File > Save).</summary>
        public static bool SaveGameFiles() => AGS.Editor.AGSEditor.Instance.SaveGameFiles();

        /// <summary>Returns a script name of the form prefix+N that is not yet used anywhere in the game.</summary>
        public static string GetFirstAvailableScriptName(string prefix) =>
            AGS.Editor.AGSEditor.Instance.GetFirstAvailableScriptName(prefix);

        /// <summary>As above, also avoiding the names of a loaded room's objects and hotspots.</summary>
        public static string GetFirstAvailableScriptName(string prefix, int startIndex, Room room) =>
            AGS.Editor.AGSEditor.Instance.GetFirstAvailableScriptName(prefix, startIndex, room);

        public static void SetMenuItemEnabled(IEditorComponent component, string commandId, bool enabled)
        {
            AGS.Editor.GUIController.Instance.SetMenuItemEnabled(component, commandId, enabled);
        }

        /// <summary>
        /// Compiles a single script module with the headers that precede it (and its own header),
        /// using the embedded script compiler only. This is the lightweight per-module check the
        /// editor uses; it does not package assets or build the whole game. Never throws on a
        /// compile error: errors and warnings are collected into the returned messages.
        /// </summary>
        public static CompileMessages CompileScript(Script script)
        {
            var editor = AGS.Editor.AGSEditor.Instance;
            var messages = new CompileMessages();
            // The auto-generated header (character/inventory/view/dialog/GUI declarations) is only
            // rebuilt by the editor on save, so a module compiled right after create_entity would
            // report "undefined symbol" for the new name. Rebuild it first (cheap, no UI).
            editor.RegenerateScriptHeader(null);
            List<Script> headers = editor.GetImportedScriptHeaders(script);
            editor.CompileScript(script, headers, messages);
            return messages;
        }

        /// <summary>
        /// Tells the editor a script was changed in memory so any open editor tab reloads its text.
        /// GUIController raises OnScriptChanged internally; a plugin cannot invoke an event directly,
        /// so we reach the backing delegate by reflection. If no tab is open for the script this is a
        /// no-op (ScriptsComponent only reacts for scripts it already has editors for). Best-effort:
        /// the write has already been saved to disk, so a refresh failure must not fail the tool.
        /// </summary>
        public static void NotifyScriptChanged(Script script)
        {
            try
            {
                var gui = AGS.Editor.GUIController.Instance;
                FieldInfo field = typeof(AGS.Editor.GUIController).GetField("OnScriptChanged", BindingFlags.Instance | BindingFlags.NonPublic);
                var handler = field?.GetValue(gui) as Delegate;
                handler?.DynamicInvoke(script);
            }
            catch (Exception e)
            {
                Logger.Write("Could not refresh open script tab: " + e.Message);
            }
        }

        /// <summary>Appends a stub function (if absent) to script text; wraps the public editor helper.</summary>
        public static string InsertFunction(string scriptText, string functionName, string paramList) =>
            AGS.Editor.ScriptGeneration.InsertFunction(scriptText ?? string.Empty, functionName, paramList ?? string.Empty);

        // --- Rooms (through the editor's RoomsComponent) ---
        // The native editor keeps exactly one room in memory: agsnative.cpp's global `RoomStruct thisroom`.
        // NativeProxy.LoadRoom loads into that struct in place and points the returned Room's _roomStructPtr at
        // it, so loading a room behind the RoomsComponent's back leaves the editor's open room (and its pane,
        // which paints from the same struct) pointing at another room's data; doing it off the main thread
        // races the pane's painting and corrupted the heap (M9). So rooms are only ever loaded, saved and
        // unloaded through the RoomsComponent (which implements IRoomController), on the main-window thread.

        private const string RoomSettingsNodePrefix = "Roe"; // RoomsComponent.TREE_PREFIX_ROOM_SETTINGS

        /// <summary>The room the editor currently has loaded, or null.</summary>
        public static Room LoadedRoom(IAGSEditor editor) => editor.RoomController.CurrentRoom as Room;

        /// <summary>Loads a room through the editor (it becomes the editor's loaded room). Unloads the current one first.</summary>
        public static Room LoadRoomInEditor(IAGSEditor editor, UnloadedRoom room)
        {
            // Unload first so LoadDifferentRoom never asks "save changes?" for a pane with design-only changes.
            if (LoadedRoom(editor) != null) UnloadRoomInEditor(editor);
            editor.RoomController.LoadRoom(room);
            return LoadedRoom(editor);
        }

        /// <summary>Unloads the editor's current room and closes its pane, without saving or prompting.</summary>
        public static void UnloadRoomInEditor(IAGSEditor editor) =>
            InvokePrivate(editor.RoomController, "UnloadCurrentRoomAndGreyOutTree", Type.EmptyTypes, new object[0]);

        /// <summary>Whether the loaded room's settings pane (the room editor tab) is open.</summary>
        public static bool IsRoomPaneOpen(IAGSEditor editor)
        {
            var doc = GetPrivateField(editor.RoomController, "_roomSettings") as ContentDocument;
            return doc != null && doc.Control != null && !doc.Control.IsDisposed && doc.Visible;
        }

        /// <summary>Loads (if needed) and shows the room editor tab for a room, as double-clicking it in the tree does.</summary>
        public static void ShowRoomPane(IAGSEditor editor, int number) =>
            InvokePrivate(editor.RoomController, "LoadRoom", new[] { typeof(string) }, new object[] { RoomSettingsNodePrefix + number });

        /// <summary>Repaints the room editor tab, if open.</summary>
        public static void InvalidateRoomPane(IAGSEditor editor)
        {
            var doc = GetPrivateField(editor.RoomController, "_roomSettings") as ContentDocument;
            if (doc?.Control != null && !doc.Control.IsDisposed) doc.Control.Invalidate(true);
        }

        /// <summary>
        /// Saves the editor's loaded room the way the editor does (RoomsComponent.SaveRoomButDoNotShowAnyErrors):
        /// regenerates the room header, compiles the room script into the room and writes the .crm. If the room
        /// script does not compile, nothing is written and the errors come back. Never shows a message box.
        /// </summary>
        public static CompileMessages SaveLoadedRoom(IAGSEditor editor, Room room)
        {
            var errors = new CompileMessages();
            InvokePrivate(editor.RoomController, "SaveRoomButDoNotShowAnyErrors",
                new[] { typeof(Room), typeof(CompileMessages), typeof(string) },
                new object[] { room, errors, "Please wait while room " + room.Number + " is saved..." });
            return errors;
        }

        /// <summary>Compiles a loaded room's script with the room's generated header, as saving the room does.</summary>
        public static CompileMessages CompileRoomScript(Room room)
        {
            var editor = AGS.Editor.AGSEditor.Instance;
            var messages = new CompileMessages();
            editor.RegenerateScriptHeader(room);
            editor.CompileScript(room.Script, new List<Script>(editor.GetAllScriptHeaders()), messages);
            return messages;
        }

        /// <summary>
        /// Creates a new blank room file pair (roomN.crm/.asc) and adds it to the project tree (the editor's "New
        /// room"). CreateNewRoom adds the room to the folder last right-clicked in the tree (_rightClickedID), which
        /// is null until the user right-clicks something, so point it at the top-level rooms folder for the call.
        /// </summary>
        public static void CreateBlankRoom(IAGSEditor editor, int number)
        {
            object rooms = editor.RoomController;
            FieldInfo clicked = FindField(rooms.GetType(), "_rightClickedID");
            FieldInfo topLevel = FindField(rooms.GetType(), "TOP_LEVEL_COMMAND_ID");
            if (clicked == null || topLevel == null)
                throw new InvalidOperationException("RoomsComponent folder fields were not found.");
            object previous = clicked.GetValue(rooms);
            clicked.SetValue(rooms, topLevel.GetValue(rooms));
            try
            {
                InvokePrivate(rooms, "CreateNewRoom", new[] { typeof(int), typeof(RoomTemplate) },
                    new object[] { number, new RoomTemplate(null, null, "Blank Room") });
            }
            finally { clicked.SetValue(rooms, previous); }
        }

        private static FieldInfo FindField(Type type, string name)
        {
            for (Type t = type; t != null; t = t.BaseType)
            {
                FieldInfo f = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (f != null) return f;
            }
            return null;
        }

        public static bool RemapPalettizedBackgrounds => AGS.Editor.AGSEditor.Instance.Settings.RemapPalettizedBackgrounds;

        private static object InvokePrivate(object target, string name, Type[] types, object[] args)
        {
            MethodInfo mi = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null, types, null)
                ?? throw new InvalidOperationException(target.GetType().Name + "." + name + " was not found.");
            try { return mi.Invoke(target, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        private static object GetPrivateField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target);

        // --- Room native access ---
        // Native calls on an already-loaded Room (its _roomStructPtr is the editor's single native room).
        // NativeProxy is an internal class, so it is reached by reflection and isolated here.

        private static object _nativeProxy;
        private static object NativeProxy
        {
            get
            {
                if (_nativeProxy == null)
                {
                    Type t = typeof(AGS.Editor.Factory).Assembly.GetType("AGS.Editor.NativeProxy");
                    _nativeProxy = t.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static).GetValue(null);
                }
                return _nativeProxy;
            }
        }

        private static object InvokeNative(string name, Type[] types, object[] args)
        {
            MethodInfo mi = NativeProxy.GetType().GetMethod(name, types)
                ?? throw new InvalidOperationException("NativeProxy." + name + " was not found.");
            try { return mi.Invoke(NativeProxy, args); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        public static void ImportBackground(Room room, int background, Bitmap bmp, bool useExactPalette, bool sharePalette) =>
            InvokeNative("ImportBackground", new[] { typeof(Room), typeof(int), typeof(Bitmap), typeof(bool), typeof(bool) },
                new object[] { room, background, bmp, useExactPalette, sharePalette });

        public static void DeleteBackground(Room room, int background) =>
            InvokeNative("DeleteBackground", new[] { typeof(Room), typeof(int) }, new object[] { room, background });

        public static void DrawFilledRectOntoMask(Room room, RoomAreaMaskType mask, int x1, int y1, int x2, int y2, int area) =>
            InvokeNative("DrawFilledRectOntoMask", new[] { typeof(Room), typeof(RoomAreaMaskType), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) },
                new object[] { room, mask, x1, y1, x2, y2, area });

        public static void DrawLineOntoMask(Room room, RoomAreaMaskType mask, int x1, int y1, int x2, int y2, int area) =>
            InvokeNative("DrawLineOntoMask", new[] { typeof(Room), typeof(RoomAreaMaskType), typeof(int), typeof(int), typeof(int), typeof(int), typeof(int) },
                new object[] { room, mask, x1, y1, x2, y2, area });

        public static void DrawFillOntoMask(Room room, RoomAreaMaskType mask, int x, int y, int area) =>
            InvokeNative("DrawFillOntoMask", new[] { typeof(Room), typeof(RoomAreaMaskType), typeof(int), typeof(int), typeof(int) },
                new object[] { room, mask, x, y, area });

        /// <summary>Copies an indexed (1/4/8-bit) image into a mask; palette index = area number. Size must match the mask.</summary>
        public static void ImportAreaMask(Room room, RoomAreaMaskType mask, Bitmap bmp) =>
            InvokeNative("ImportAreaMask", new[] { typeof(Room), typeof(RoomAreaMaskType), typeof(Bitmap) }, new object[] { room, mask, bmp });

        /// <summary>A copy of a mask at its own resolution (8-bit, pixel value = area number).</summary>
        public static Bitmap ExportAreaMask(Room room, RoomAreaMaskType mask) =>
            (Bitmap)InvokeNative("ExportAreaMask", new[] { typeof(Room), typeof(RoomAreaMaskType) }, new object[] { room, mask });

        public static Bitmap GetRoomBackgroundForPreview(Room room, int background) =>
            (Bitmap)InvokeNative("GetRoomBackgroundForPreview", new[] { typeof(Room), typeof(int) }, new object[] { room, background });

        public static int GetAreaMaskPixel(Room room, RoomAreaMaskType mask, int x, int y) =>
            (int)InvokeNative("GetAreaMaskPixel", new[] { typeof(Room), typeof(RoomAreaMaskType), typeof(int), typeof(int) }, new object[] { room, mask, x, y });

        // --- Sprite native access ---
        // Sprite image data lives in the engine's native sprite set, reached through the same NativeProxy.
        // These mirror what IAGSEditor.CreateNewSprite/ChangeSpriteImage/GetSpriteImage and AGSEditor.DeleteSprite
        // do, but deliberately skip SpriteFolder.NotifyClientsOfUpdate: that fires SpritesUpdated, whose only
        // subscriber (the Sprite Manager pane, when it is the active pane) refreshes WinForms controls on the
        // editor's main-window thread and would throw from the plugin's dispatcher thread (the M2 threading
        // caveat). The folder-list mutation and the native set are both safe to touch here; AssetTools adds the
        // returned Sprite to its folder itself. acsprset.spr is written when the game is saved (NativeProxy.SaveGame).

        public static Sprite CreateSpriteFromBitmap(Bitmap bmp, SpriteImportTransparency transparency, int transColour, bool remapColours, bool useRoomBackgroundColours, bool alphaChannel) =>
            (Sprite)InvokeNative("CreateSpriteFromBitmap",
                new[] { typeof(Bitmap), typeof(SpriteImportTransparency), typeof(int), typeof(bool), typeof(bool), typeof(bool) },
                new object[] { bmp, transparency, transColour, remapColours, useRoomBackgroundColours, alphaChannel });

        public static void ReplaceSpriteWithBitmap(Sprite sprite, Bitmap bmp, SpriteImportTransparency transparency, int transColour, bool remapColours, bool useRoomBackgroundColours, bool alphaChannel) =>
            InvokeNative("ReplaceSpriteWithBitmap",
                new[] { typeof(Sprite), typeof(Bitmap), typeof(SpriteImportTransparency), typeof(int), typeof(bool), typeof(bool), typeof(bool) },
                new object[] { sprite, bmp, transparency, transColour, remapColours, useRoomBackgroundColours, alphaChannel });

        public static Bitmap GetSpriteBitmap(int spriteNumber) =>
            (Bitmap)InvokeNative("GetSpriteBitmap", new[] { typeof(int) }, new object[] { spriteNumber });

        /// <summary>Frees the sprite's slot in the native sprite set. The caller removes it from its folder first.</summary>
        public static void DeleteSpriteFromNative(Sprite sprite) =>
            InvokeNative("DeleteSprite", new[] { typeof(Sprite) }, new object[] { sprite });

        public static bool DoesSpriteExist(int spriteNumber) =>
            (bool)InvokeNative("DoesSpriteExist", new[] { typeof(int) }, new object[] { spriteNumber });

        /// <summary>A human-readable report of where a sprite is used, or null if it is unused. Public editor helper.</summary>
        public static string GetSpriteUsageReport(int spriteNumber, Game game) =>
            AGS.Editor.Utils.SpriteTools.GetSpriteUsageReport(spriteNumber, game);

        // --- Audio ---
        // AudioClip model edits are plain AGS.Types; only the tree refresh, the script header and the build-output
        // paths are editor knowledge. The audio component (internal AudioComponent, ID "AudioNew") implements the
        // public IRePopulatableComponent, so it is found by ID rather than by type.

        private const string AudioComponentId = "AudioNew"; // ComponentIDs.Audio

        /// <summary>Rebuilds the Audio node of the project tree. Best-effort: the model change is already made.</summary>
        public static void RefreshAudioTree()
        {
            try
            {
                foreach (IEditorComponent comp in AGS.Editor.ComponentController.Instance.Components)
                {
                    if (comp.ComponentID != AudioComponentId) continue;
                    (comp as IRePopulatableComponent)?.RePopulateTreeView();
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Write("Could not refresh the audio tree: " + e.Message);
            }
        }

        /// <summary>
        /// Rebuilds _AutoGenerated.ash (which declares every audio clip, character, view...) and the autocomplete
        /// cache, so a new name autocompletes at once. Compiling does this anyway. Best-effort.
        /// </summary>
        public static void RegenerateScriptHeader()
        {
            try { AGS.Editor.AGSEditor.Instance.RegenerateScriptHeader(null); }
            catch (Exception e) { Logger.Write("Could not regenerate the script header: " + e.Message); }
        }

        /// <summary>
        /// Deletes Compiled\Data\audio.vox so the next build repacks it, as the editor does whenever a clip is
        /// added, replaced or removed.
        /// </summary>
        public static void DeleteAudioVox(Game game)
        {
            string vox = Path.Combine(game.DirectoryPath, AGS.Editor.AGSEditor.OUTPUT_DIRECTORY,
                AGS.Editor.AGSEditor.DATA_OUTPUT_DIRECTORY, AGS.Editor.AGSEditor.AUDIO_VOX_FILE_NAME);
            try { if (File.Exists(vox)) File.Delete(vox); }
            catch (Exception e) { Logger.Write("Could not delete " + vox + ": " + e.Message); }
        }

        // --- Projects ---

        /// <summary>
        /// Creates a game from a template into a new folder and loads it, exactly as the New Game wizard does after
        /// its pages (GUIController.CreateNewGame): extract the template, load it, set the game/file name and a new
        /// game ID, save, and force a rebuild so the rooms are tied to the new game ID. On failure the editor
        /// deletes the folder and shows an error box, so the caller validates everything first.
        /// </summary>
        public static bool CreateNewGame(string folder, string fileName, string gameName, string templatePath)
        {
            var gui = AGS.Editor.GUIController.Instance;
            var editor = AGS.Editor.AGSEditor.Instance;
            AGS.Editor.Preferences.MessageBoxOnCompile old = editor.Settings.MessageBoxOnCompile;
            editor.Settings.MessageBoxOnCompile = AGS.Editor.Preferences.MessageBoxOnCompile.Never;
            try
            {
                return (bool)InvokePrivate(gui, "CreateNewGame",
                    new[] { typeof(string), typeof(string), typeof(string), typeof(GameTemplate) },
                    new object[] { folder, fileName, gameName, new GameTemplate(templatePath, null, null) });
            }
            finally { editor.Settings.MessageBoxOnCompile = old; }
        }

        /// <summary>Loads a game (Game.agf) into the editor, replacing the current one, without prompting to save it.</summary>
        public static bool LoadGame(string agfPath) => AGS.Editor.AGSEditor.Instance.Tasks.LoadGameFromDisk(agfPath, false);

        /// <summary>The game templates (*.agt) shipped in the editor's Templates folder.</summary>
        public static string[] GameTemplateFiles()
        {
            string dir = Path.Combine(EditorDirectory, "Templates");
            return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.agt") : new string[0];
        }

        // --- Build and run ---

        public static string GameDirectory => AGS.Editor.AGSEditor.Instance.GameDirectory;

        /// <summary>The exe base name (Settings.GameFileName, or the game folder name).</summary>
        public static string BaseGameFileName => AGS.Editor.AGSEditor.Instance.BaseGameFileName;

        /// <summary>Full path to the standalone Windows build: &lt;game&gt;\Compiled\Windows\&lt;name&gt;.exe.</summary>
        public static string WindowsBuildExePath() =>
            Path.Combine(GameDirectory, AGS.Editor.AGSEditor.OUTPUT_DIRECTORY, "Windows", BaseGameFileName + ".exe");

        /// <summary>
        /// Runs a full game build on the editor's main-window thread with the compile message box suppressed,
        /// and returns the compile messages. CompileGame touches the output panel and can pop a modal error
        /// dialog (ReportErrorsIfAppropriate); the panel controls belong to the main-window thread and the modal
        /// dialog would block an unattended session, so we marshal onto that thread and set MessageBoxOnCompile
        /// to Never for the duration (mirroring the editor's own headless/new-game build path), then restore it.
        /// </summary>
        public static CompileMessages CompileGameFull(bool forceRebuild)
        {
            return RunOnMainForm(() =>
            {
                var editor = AGS.Editor.AGSEditor.Instance;
                AGS.Editor.Preferences.MessageBoxOnCompile old = editor.Settings.MessageBoxOnCompile;
                editor.Settings.MessageBoxOnCompile = AGS.Editor.Preferences.MessageBoxOnCompile.Never;
                try { return editor.CompileGame(forceRebuild, false); }
                finally { editor.Settings.MessageBoxOnCompile = old; }
            });
        }

        // --- Engine plugin (M7) ---

        /// <summary>Outcome of trying to enable a native engine plugin.</summary>
        public sealed class EnablePluginResult
        {
            public bool Found;
            public bool AlreadyEnabled;
            public string PluginName;
            public List<string> Available = new List<string>();
        }

        /// <summary>
        /// Enables the native engine plugin with the given DLL file name (e.g. "agsmcp.dll") for the current
        /// game. The editor's PluginsComponent discovers "ags*.dll" (not "ags.*") from the editor folder at
        /// startup and wraps each as a NativePlugin; we flip that NativePlugin.Enabled (StartPlugin calls the
        /// DLL's AGS_EditorStartup) and mirror the plugin into Game.Plugins. On save, PluginsComponent rebuilds
        /// Game.Plugins from the enabled NativePlugins, so save_project persists this; the Windows build then
        /// copies the DLL next to the game exe. Must run where the editor's plugin subsystem lives (main form).
        /// </summary>
        public static EnablePluginResult EnableEnginePlugin(IAGSEditor editor, Game game, string fileName)
        {
            return RunOnMainForm(() =>
            {
                var result = new EnablePluginResult();

                object pluginsComp = null;
                foreach (IEditorComponent comp in editor.Components)
                {
                    if (comp.ComponentID == "Plugins") { pluginsComp = comp; break; }
                }
                if (pluginsComp == null)
                    throw new InvalidOperationException("The editor's Plugins component was not found.");

                FieldInfo field = pluginsComp.GetType().GetField("_plugins", BindingFlags.Instance | BindingFlags.NonPublic);
                var list = field?.GetValue(pluginsComp) as System.Collections.IEnumerable;
                if (list == null)
                    throw new InvalidOperationException("Could not read the editor's native-plugin list.");

                AGS.Editor.NativePlugin target = null;
                foreach (AGS.Editor.NativePlugin np in list)
                {
                    result.Available.Add(np.FileName);
                    if (string.Equals(np.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                        target = np;
                }
                if (target == null)
                    return result; // Found stays false; caller reports the available list.

                result.Found = true;
                result.PluginName = target.PluginName;
                result.AlreadyEnabled = target.Enabled;
                if (!target.Enabled)
                    target.Enabled = true; // StartPlugin() -> our AGS_EditorStartup (returns 0)

                bool inGame = false;
                foreach (Plugin p in game.Plugins)
                    if (string.Equals(p.FileName, fileName, StringComparison.OrdinalIgnoreCase)) { inGame = true; break; }
                if (!inGame)
                    game.Plugins.Add(new Plugin(target.FileName, new byte[0]));

                return result;
            });
        }

        public static Form MainForm
        {
            get
            {
                var gui = AGS.Editor.GUIController.Instance;
                FieldInfo f = typeof(AGS.Editor.GUIController).GetField("_mainForm", BindingFlags.Instance | BindingFlags.NonPublic);
                return f?.GetValue(gui) as Form;
            }
        }

        /// <summary>Invokes work on the editor's main-window (UI) thread, so it may touch editor UI safely.</summary>
        public static T RunOnMainForm<T>(Func<T> work)
        {
            Form form = MainForm;
            if (form == null || form.IsDisposed || !form.InvokeRequired)
                return work();
            return (T)form.Invoke(new Func<object>(() => work()));
        }
    }
}
