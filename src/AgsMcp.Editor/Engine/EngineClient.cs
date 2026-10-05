using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Engine
{
    /// <summary>
    /// Talks to the agsmcp engine plugin inside the running game over a loopback TCP connection,
    /// line-delimited JSON. One request/response at a time, guarded by a lock; reconnects on failure.
    /// </summary>
    internal sealed class EngineClient : IDisposable
    {
        private readonly object _lock = new object();
        private readonly int _port;
        private TcpClient _client;
        private NetworkStream _stream;
        private readonly List<byte> _recv = new List<byte>();
        private int _nextId;

        public EngineClient(int port)
        {
            _port = port;
        }

        /// <summary>Send a command and return the engine's response object. Throws ToolException on failure.</summary>
        public JObject Send(string cmd, JObject args, int timeoutMs = 12000)
        {
            lock (_lock)
            {
                var req = new JObject { ["id"] = Interlocked.Increment(ref _nextId), ["cmd"] = cmd };
                if (args != null) req["args"] = args;
                string line = req.ToString(Formatting.None) + "\n";

                try
                {
                    return Exchange(line, timeoutMs);
                }
                catch (Exception first)
                {
                    // The game may have dropped the connection; reconnect once.
                    CloseConnection();
                    try
                    {
                        return Exchange(line, timeoutMs);
                    }
                    catch (ToolException)
                    {
                        throw;
                    }
                    catch (Exception second)
                    {
                        throw new ToolException("Could not reach the running game's MCP engine plugin on port " + _port +
                            ". Make sure a game started with run_game is running and the 'agsmcp' plugin is enabled " +
                            "(runtime_enable_plugin). Details: " + second.Message + " / " + first.Message);
                    }
                }
            }
        }

        private JObject Exchange(string line, int timeoutMs)
        {
            EnsureConnected(timeoutMs);
            byte[] bytes = Encoding.UTF8.GetBytes(line);
            _stream.Write(bytes, 0, bytes.Length);
            _stream.Flush();
            string response = ReadLine(timeoutMs);
            if (response == null)
                throw new Exception("connection closed before a response arrived");
            return JObject.Parse(response);
        }

        private void EnsureConnected(int timeoutMs)
        {
            if (_client != null && _client.Connected && _stream != null) return;
            CloseConnection();

            var client = new TcpClient();
            IAsyncResult ar = client.BeginConnect(IPAddress.Loopback, _port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(Math.Max(1000, timeoutMs)))
            {
                try { client.Close(); } catch { }
                throw new ToolException("No game is listening on port " + _port + ". Start one with run_game first.");
            }
            client.EndConnect(ar);
            client.NoDelay = true;
            client.ReceiveTimeout = timeoutMs;
            client.SendTimeout = timeoutMs;
            _client = client;
            _stream = client.GetStream();
            _stream.ReadTimeout = timeoutMs;
            _stream.WriteTimeout = timeoutMs;
            _recv.Clear();
        }

        private string ReadLine(int timeoutMs)
        {
            var buf = new byte[4096];
            while (true)
            {
                int nl = _recv.IndexOf((byte)'\n');
                if (nl >= 0)
                {
                    string line = Encoding.UTF8.GetString(_recv.ToArray(), 0, nl);
                    _recv.RemoveRange(0, nl + 1);
                    return line;
                }
                int read = _stream.Read(buf, 0, buf.Length);
                if (read <= 0) return null;
                for (int i = 0; i < read; i++) _recv.Add(buf[i]);
            }
        }

        private void CloseConnection()
        {
            try { _stream?.Dispose(); } catch { }
            try { _client?.Close(); } catch { }
            _stream = null;
            _client = null;
            _recv.Clear();
        }

        public void Dispose()
        {
            lock (_lock) CloseConnection();
        }
    }
}
