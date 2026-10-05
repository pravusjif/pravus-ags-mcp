using System;

namespace AgsMcp.Editor.Ui
{
    /// <summary>How to connect common MCP clients to the server. Plain text, shown by MCP > Client setup.</summary>
    internal static class ClientSetup
    {
        public static string Text(string url)
        {
            return string.Join(Environment.NewLine, new[]
            {
                "Any MCP client that supports Streamable HTTP connects to:",
                "  " + url,
                "",
                "Claude Code (run in a terminal; add --scope user to use it in every project):",
                "  claude mcp add --transport http ags " + url,
                "",
                "Codex CLI (~/.codex/config.toml):",
                "  [mcp_servers.ags]",
                "  url = \"" + url + "\"",
                "",
                "Gemini CLI (~/.gemini/settings.json, or .gemini/settings.json in the project):",
                "  { \"mcpServers\": { \"ags\": { \"httpUrl\": \"" + url + "\" } } }",
                "",
                "Clients that only start local servers over stdio (needs Node.js):",
                "  command: npx",
                "  args:    -y mcp-remote " + url,
            });
        }
    }
}
