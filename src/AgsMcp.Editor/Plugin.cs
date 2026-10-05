using System;
using AGS.Types;

namespace AgsMcp.Editor
{
    /// <summary>
    /// Entry point found by the AGS editor (it requires exactly one public IAGSEditorPlugin class with an
    /// (IAGSEditor) constructor). Runs on the UI thread while the splash screen is up, before any game loads.
    /// </summary>
    [RequiredAGSVersion("3.6.0.0")]
    public class McpPlugin : IAGSEditorPlugin
    {
        private readonly McpComponent _component;

        public McpPlugin(IAGSEditor editor)
        {
            Logger.Write("Loading AGS MCP plugin " + typeof(McpPlugin).Assembly.GetName().Version + " into AGS " + editor.Version);
            try
            {
                _component = new McpComponent(editor);
                editor.AddComponent(_component);
            }
            catch (Exception e)
            {
                Logger.Write("Plugin failed to load: " + e);
                throw;
            }
        }

        public void Dispose()
        {
            _component?.Shutdown();
        }
    }
}
