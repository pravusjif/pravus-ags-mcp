using AGS.Types;
using AgsMcp.Editor.Mcp;

namespace AgsMcp.Editor.Tools
{
    /// <summary>Shared state handed to every tool group.</summary>
    public sealed class ToolContext
    {
        public ToolContext(IAGSEditor editor)
        {
            Editor = editor;
            Runner = new GameRunner();
        }

        public IAGSEditor Editor { get; }

        /// <summary>The single game process launched by run_game, shared by the build/run and game_* tools.</summary>
        internal GameRunner Runner { get; }

        /// <summary>The open game as the concrete AGS.Types.Game, or a tool error if none is open.</summary>
        public Game RequireGame()
        {
            if (!(Editor.CurrentGame is Game game) || string.IsNullOrEmpty(game.DirectoryPath))
                throw new ToolException("No game is open in the AGS editor. Open a project (File > Open) and retry.");
            return game;
        }
    }
}
