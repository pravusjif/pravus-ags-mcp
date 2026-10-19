using System.IO;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// The optional outPath argument of the image tools (render_room, get_sprite, game_screenshot): write the PNG to
    /// a file and answer with text only, which keeps large images out of a conversation and out of batch replies.
    /// </summary>
    internal static class ImageOutput
    {
        public const string Arg = "outPath";

        public static JObject Prop() =>
            Schema.String("Absolute file path. When given, the PNG is written there (folders are created) and the result is " +
                          "{path, width, height, note} text instead of an image.");

        /// <summary>True for a full path with a root: "C:\x.png" or "\\server\share\x.png", not "x.png", "\x.png" or "C:x.png".</summary>
        public static bool IsAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return false;
            if (path.StartsWith(@"\\") || path.StartsWith("//")) return path.Length > 2;
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }

        /// <summary>
        /// Adds the image to the result, or, when the call passed outPath, writes it there and adds
        /// {path, width, height, note} text instead. <paramref name="note"/> is the text the tool returns with the image.
        /// </summary>
        public static ToolResult Result(ToolArgs args, byte[] png, int width, int height, string note)
        {
            if (!args.Has(Arg)) return new ToolResult().AddText(note).AddImage(png, "image/png");

            string path = args.String(Arg);
            if (!IsAbsolutePath(path)) throw new ToolException($"'{Arg}' must be an absolute file path, e.g. C:\\temp\\image.png; got '{path}'.");
            path = Path.GetFullPath(path);
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllBytes(path, png);
            return ToolResult.Json(new { path, width, height, note });
        }
    }
}
