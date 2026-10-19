using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Plans a move_script_module: how many one-place moves (ScriptsAndHeaders.MoveScriptAndHeaderUp/Down) take
    /// a module from its position to the requested one. Kept free of editor types so it can be unit-tested.
    /// </summary>
    internal static class ModuleOrder
    {
        /// <summary>
        /// The signed number of one-place moves for the module at <paramref name="from"/> in a list of
        /// <paramref name="count"/>: negative moves up (towards compile-first), positive down, 0 stays. "up" and
        /// "down" move one place and stay put at the ends; "index" goes to <paramref name="index"/> (0-based).
        /// </summary>
        public static int Plan(int count, int from, string direction, int index)
        {
            if (count < 1 || from < 0 || from >= count) throw new ToolException($"Module position {from} is outside the {count} modules.");
            switch (direction)
            {
                case "up":
                    return from > 0 ? -1 : 0;
                case "down":
                    return from < count - 1 ? 1 : 0;
                case "index":
                    if (index < 0 || index >= count)
                        throw new ToolException($"'index' must be between 0 and {count - 1}; got {index}.");
                    return index - from;
                default:
                    throw new ToolException($"'direction' must be up, down or index; got '{direction}'.");
            }
        }
    }
}
