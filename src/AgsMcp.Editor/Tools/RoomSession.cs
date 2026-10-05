using System.Collections.Generic;
using System.IO;
using System.Linq;
using AGS.Types;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Room access for tools. The editor holds one room in memory at a time (its "loaded" room, which owns the
    /// single native room struct), so every tool works on that room: Open makes the requested room the editor's
    /// loaded room, and Save writes it through the editor's own save path and reopens its editor tab.
    /// </summary>
    internal static class RoomSession
    {
        public static UnloadedRoom Find(Game game, int number)
        {
            IRoom room = game.Rooms.FirstOrDefault(r => r.Number == number);
            if (room == null)
                throw new ToolException($"No room numbered {number}. Available: {string.Join(", ", game.Rooms.Select(r => r.Number))}.");
            return (UnloadedRoom)room;
        }

        /// <summary>Returns room 'number' as the editor's loaded room, loading it in the editor if needed.</summary>
        public static Room Open(ToolContext ctx, int number)
        {
            Game game = ctx.RequireGame();
            UnloadedRoom target = Find(game, number);
            Room current = EditorInternals.LoadedRoom(ctx.Editor);
            if (current != null && current.Number == number) return current;

            if (current != null && current.Modified)
                throw new ToolException($"Room {current.Number} is loaded in the editor with unsaved changes, and the editor holds one room " +
                                        $"at a time. Save it first (save_project, or File > Save); if it will not save, its script has " +
                                        $"errors (compile name=\"room{current.Number}\"). Then retry.");
            if (!File.Exists(target.FileName))
                throw new ToolException($"Room {number}'s file '{target.FileName}' is missing.");

            Room room = EditorInternals.LoadRoomInEditor(ctx.Editor, target);
            if (room == null || room.Number != number)
                throw new ToolException($"The editor could not load room {number}.");
            return room;
        }

        /// <summary>
        /// Saves the loaded room through the editor (compiles its script into the .crm), then reopens its editor tab
        /// so the tab shows the new state. If the room script does not compile, the room is not written; it stays
        /// loaded with its changes and the compile errors are returned.
        /// </summary>
        public static SaveResult Save(ToolContext ctx, Room room)
        {
            room.Modified = true;
            CompileMessages messages = EditorInternals.SaveLoadedRoom(ctx.Editor, room);
            var result = new SaveResult { saved = !messages.HasErrors, messages = Describe(messages) };
            if (result.saved)
            {
                bool paneOpen = EditorInternals.IsRoomPaneOpen(ctx.Editor);
                EditorInternals.UnloadRoomInEditor(ctx.Editor);
                if (paneOpen) EditorInternals.ShowRoomPane(ctx.Editor, room.Number);
            }
            else
            {
                EditorInternals.InvalidateRoomPane(ctx.Editor);
                result.hint = $"Room {room.Number} was changed in the editor but not written, because its script does not compile. " +
                              $"Fix room{room.Number}.asc (see messages), then call save_project.";
            }
            return result;
        }

        public static List<object> Describe(CompileMessages messages) =>
            messages.Cast<CompileMessage>()
                .Select(m => (object)new { severity = m is CompileError ? "error" : "warning", script = m.ScriptName, line = m.LineNumber, message = m.Message })
                .ToList();

        /// <summary>Outcome of a room save, serialized into tool results.</summary>
        internal sealed class SaveResult
        {
            public bool saved;
            public List<object> messages;
            public string hint;
        }
    }
}
