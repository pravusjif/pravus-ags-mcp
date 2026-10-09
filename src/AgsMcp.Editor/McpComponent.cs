using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Sockets;
using System.Windows.Forms;
using System.Xml;
using AGS.Types;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using AgsMcp.Editor.Ui;

namespace AgsMcp.Editor
{
    /// <summary>Editor component: owns the MCP server, its HTTP transport and the "MCP" menu.</summary>
    public sealed class McpComponent : IEditorComponent
    {
        private const string MenuId = "AgsMcpMenu";
        private const string CmdStart = "AgsMcpStart";
        private const string CmdStop = "AgsMcpStop";
        private const string CmdCopyUrl = "AgsMcpCopyUrl";
        private const string CmdClientSetup = "AgsMcpClientSetup";
        private const string CmdStatus = "AgsMcpStatus";
        private const string CmdPort = "AgsMcpPort";
        private const string CmdLog = "AgsMcpLog";

        private const string Instructions =
            "This server controls the Adventure Game Studio (AGS) 3.6 editor and the project currently open in it. " +
            "Call project_info first to see which game is loaded. Changes made through tools are applied to the live " +
            "editor; call save_project to write them to disk.";

        private readonly IAGSEditor _editor;
        private readonly PluginSettings _settings;
        private readonly UiDispatcher _dispatcher;
        private readonly McpServer _mcp;
        private HttpServer _http;
        private string _lastError;

        public McpComponent(IAGSEditor editor)
        {
            _editor = editor;
            _settings = PluginSettings.Load();
            _dispatcher = new UiDispatcher(() => EditorInternals.MainForm);

            string version = typeof(McpComponent).Assembly.GetName().Version.ToString(3);
            _mcp = new McpServer("ags-mcp", version, Instructions, _dispatcher) { Log = Logger.Write };
            var context = new ToolContext(editor);
            _mcp.AddTools(ProjectTools.Create(context));
            _mcp.AddTools(ScriptTools.Create(context));
            _mcp.AddTools(GameDataTools.Create(context));
            _mcp.AddTools(RoomTools.Create(context));
            _mcp.AddTools(AssetTools.Create(context));
            _mcp.AddTools(AudioTools.Create(context));
            _mcp.AddTools(BuildRunTools.Create(context));
            _mcp.AddTools(GameTools.Create(context));

            CreateMenu();
            if (_settings.AutoStart) StartServer(showErrors: false);
        }

        public string ComponentID => "AgsMcp";

        public string Url => $"http://127.0.0.1:{_settings.Port}{McpHttpEndpoint.Path}";

        private void CreateMenu()
        {
            _editor.GUIController.AddMenu(this, MenuId, "&MCP", "DebugMenuHeader");
            var commands = new MenuCommands(MenuId, null);
            commands.Commands.Add(new MenuCommand(CmdStart, "&Start MCP server"));
            commands.Commands.Add(new MenuCommand(CmdStop, "S&top MCP server"));
            commands.Commands.Add(MenuCommand.Separator);
            commands.Commands.Add(new MenuCommand(CmdCopyUrl, "&Copy endpoint URL"));
            commands.Commands.Add(new MenuCommand(CmdClientSetup, "&Client setup..."));
            commands.Commands.Add(new MenuCommand(CmdStatus, "Server s&tatus..."));
            commands.Commands.Add(new MenuCommand(CmdPort, "Change &port..."));
            commands.Commands.Add(new MenuCommand(CmdLog, "Open plugin &log"));
            _editor.GUIController.AddMenuItems(this, commands);
        }

        private void UpdateMenuState()
        {
            try
            {
                bool running = _http != null;
                EditorInternals.SetMenuItemEnabled(this, CmdStart, !running);
                EditorInternals.SetMenuItemEnabled(this, CmdStop, running);
            }
            catch (Exception e)
            {
                Logger.Write("Could not update menu state: " + e.Message);
            }
        }

        private bool StartServer(bool showErrors)
        {
            if (_http != null) return true;
            var endpoint = new McpHttpEndpoint(_mcp, StatusText);
            var http = new HttpServer(_settings.Port, endpoint.Handle) { Log = Logger.Write };
            try
            {
                http.Start();
                _http = http;
                _lastError = null;
                Logger.Write("MCP server listening on " + Url);
            }
            catch (SocketException e)
            {
                http.Dispose();
                _lastError = $"Could not listen on port {_settings.Port}: {e.Message}";
                Logger.Write(_lastError);
                if (showErrors) _editor.GUIController.ShowMessage(_lastError + "\n\nChoose another port from the MCP menu.", MessageBoxIconType.Warning);
            }
            UpdateMenuState();
            SetStatus();
            return _http != null;
        }

        private void StopServer()
        {
            _http?.Dispose();
            _http = null;
            Logger.Write("MCP server stopped");
            UpdateMenuState();
            SetStatus();
        }

        private string StatusText()
        {
            string game = "(no game open)";
            try
            {
                if (_editor.CurrentGame != null) game = _editor.CurrentGame.Settings.GameName + " - " + _editor.CurrentGame.DirectoryPath;
            }
            catch (Exception) { }
            return $"AGS MCP server {_mcp.Tools.Count} tools\nEndpoint: {Url}\nAGS editor: {_editor.Version}\nGame: {game}\n";
        }

        private void SetStatus()
        {
            try
            {
                _editor.GUIController.SetStatusBarText(_http != null ? "MCP server: " + Url : "MCP server stopped" + (_lastError != null ? " (" + _lastError + ")" : ""));
            }
            catch (Exception)
            {
                // The status bar may not exist yet during startup.
            }
        }

        public void CommandClick(string controlID)
        {
            switch (controlID)
            {
                case CmdStart:
                    StartServer(showErrors: true);
                    break;
                case CmdStop:
                    StopServer();
                    break;
                case CmdCopyUrl:
                    Clipboard.SetText(Url);
                    _editor.GUIController.ShowMessage("Copied to the clipboard:\n\n" + Url +
                        "\n\nUse it as the Streamable HTTP URL in your MCP client. MCP > Client setup shows the setup for common clients.",
                        MessageBoxIconType.Information);
                    break;
                case CmdClientSetup:
                    using (var dialog = new ClientSetupDialog(Url))
                        dialog.ShowDialog(EditorInternals.MainForm);
                    break;
                case CmdStatus:
                    _editor.GUIController.ShowMessage((_http != null ? "Running.\n\n" : "Stopped.\n\n" + (_lastError ?? "") + "\n\n") + StatusText(),
                        MessageBoxIconType.Information);
                    break;
                case CmdPort:
                    ChangePort();
                    break;
                case CmdLog:
                    if (System.IO.File.Exists(Logger.FilePath)) Process.Start("notepad.exe", "\"" + Logger.FilePath + "\"");
                    break;
            }
        }

        private void ChangePort()
        {
            string input = Microsoft.VisualBasic.Interaction.InputBox("TCP port for the MCP server (127.0.0.1 only):", "AGS MCP", _settings.Port.ToString());
            if (string.IsNullOrWhiteSpace(input)) return;
            if (!int.TryParse(input, out int port) || port < 1024 || port > 65535)
            {
                _editor.GUIController.ShowMessage("Enter a port between 1024 and 65535.", MessageBoxIconType.Warning);
                return;
            }
            bool wasRunning = _http != null;
            StopServer();
            _settings.Port = port;
            _settings.Save();
            if (wasRunning) StartServer(showErrors: true);
        }

        public void Shutdown()
        {
            _http?.Dispose();
            _http = null;
            _dispatcher.Dispose();
        }

        public IList<MenuCommand> GetContextMenu(string controlID) => null;
        public void PropertyChanged(string propertyName, object oldValue) { }
        public void BeforeSaveGame() { }

        public void RefreshDataFromGame()
        {
            // Called after a game is loaded; by now the main window (and status bar) exists.
            UpdateMenuState();
            SetStatus();
        }

        public void GameSettingsChanged() { }
        public void ToXml(XmlTextWriter writer) { }
        public void FromXml(XmlNode node) { }
        public void EditorShutdown() => Shutdown();
    }
}
