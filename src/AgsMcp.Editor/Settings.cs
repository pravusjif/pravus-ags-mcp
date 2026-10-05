using System;
using System.IO;
using Newtonsoft.Json;

namespace AgsMcp.Editor
{
    /// <summary>Per-user plugin settings, stored in %APPDATA%\AGS-MCP\settings.json.</summary>
    public sealed class PluginSettings
    {
        public const int DefaultPort = 7471;

        public int Port { get; set; } = DefaultPort;
        public bool AutoStart { get; set; } = true;

        public static string Directory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AGS-MCP");

        private static string FilePath => Path.Combine(Directory, "settings.json");

        public static PluginSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    return JsonConvert.DeserializeObject<PluginSettings>(File.ReadAllText(FilePath)) ?? new PluginSettings();
            }
            catch (Exception e)
            {
                Logger.Write("Could not read settings, using defaults: " + e.Message);
            }
            return new PluginSettings();
        }

        public void Save()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
        }
    }

    /// <summary>Append-only log in %APPDATA%\AGS-MCP\plugin.log (the editor has no console).</summary>
    public static class Logger
    {
        private static readonly object Lock = new object();

        public static string FilePath => Path.Combine(PluginSettings.Directory, "plugin.log");

        public static void Write(string message)
        {
            try
            {
                lock (Lock)
                {
                    System.IO.Directory.CreateDirectory(PluginSettings.Directory);
                    var info = new FileInfo(FilePath);
                    if (info.Exists && info.Length > 2 * 1024 * 1024) info.Delete();
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + message + Environment.NewLine);
                }
            }
            catch (Exception)
            {
                // Logging must never break the editor.
            }
        }
    }
}
