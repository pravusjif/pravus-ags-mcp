using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace AgsMcp.Editor.Mcp
{
    public sealed class HttpRequest
    {
        public string Method { get; set; }
        public string Path { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body { get; set; } = new byte[0];

        public string Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public string BodyText => Encoding.UTF8.GetString(Body);
    }

    public sealed class HttpResponse
    {
        public int Status { get; set; } = 200;
        public string ContentType { get; set; } = "application/json";
        public byte[] Body { get; set; } = new byte[0];
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HttpResponse Text(int status, string text, string contentType = "text/plain; charset=utf-8") =>
            new HttpResponse { Status = status, ContentType = contentType, Body = Encoding.UTF8.GetBytes(text ?? "") };

        public static HttpResponse Empty(int status) => new HttpResponse { Status = status, ContentType = null };
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on the loopback interface (IPv4 and IPv6). Built on TcpListener so it needs
    /// no http.sys URL ACL / admin rights. Supports keep-alive and Content-Length or chunked request bodies.
    /// </summary>
    public sealed class HttpServer : IDisposable
    {
        private const int MaxHeaderBytes = 64 * 1024;
        private const int MaxBodyBytes = 64 * 1024 * 1024;

        private readonly int _port;
        private readonly Func<HttpRequest, HttpResponse> _handler;
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private volatile bool _running;

        public HttpServer(int port, Func<HttpRequest, HttpResponse> handler)
        {
            _port = port;
            _handler = handler;
        }

        public Action<string> Log { get; set; } = _ => { };
        public bool IsRunning => _running;

        public void Start()
        {
            var v4 = new TcpListener(IPAddress.Loopback, _port);
            v4.Start(); // throws SocketException if the port is in use: let the caller report it
            _listeners.Add(v4);
            try
            {
                var v6 = new TcpListener(IPAddress.IPv6Loopback, _port);
                v6.Start();
                _listeners.Add(v6);
            }
            catch (SocketException)
            {
                // IPv6 disabled or port taken on ::1 only; IPv4 loopback is enough.
            }

            _running = true;
            foreach (var l in _listeners)
            {
                var listener = l;
                new Thread(() => AcceptLoop(listener)) { IsBackground = true, Name = "AgsMcp HTTP accept" }.Start();
            }
        }

        public void Dispose()
        {
            _running = false;
            foreach (var l in _listeners)
            {
                try { l.Stop(); } catch (Exception) { }
            }
            _listeners.Clear();
        }

        private void AcceptLoop(TcpListener listener)
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (!_running) return;
                    continue;
                }
                ThreadPool.QueueUserWorkItem(_ => HandleConnection(client));
            }
        }

        private void HandleConnection(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.NoDelay = true;
                    var stream = client.GetStream();
                    stream.ReadTimeout = (int)TimeSpan.FromMinutes(10).TotalMilliseconds;
                    var reader = new ByteReader(stream);
                    while (_running)
                    {
                        HttpRequest request = ReadRequest(reader);
                        if (request == null) return; // connection closed
                        HttpResponse response;
                        try
                        {
                            response = _handler(request);
                        }
                        catch (Exception e)
                        {
                            Log("HTTP handler error: " + e);
                            response = HttpResponse.Text(500, "Internal server error: " + e.Message);
                        }
                        bool close = string.Equals(request.Header("Connection"), "close", StringComparison.OrdinalIgnoreCase);
                        WriteResponse(stream, response, close);
                        if (close) return;
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (BadRequestException e)
                {
                    try { WriteResponse(client.GetStream(), HttpResponse.Text(400, e.Message), true); } catch (Exception) { }
                }
                catch (Exception e)
                {
                    Log("HTTP connection error: " + e);
                }
            }
        }

        private sealed class BadRequestException : Exception
        {
            public BadRequestException(string message) : base(message) { }
        }

        private static HttpRequest ReadRequest(ByteReader reader)
        {
            string requestLine = reader.ReadLine(MaxHeaderBytes);
            while (requestLine != null && requestLine.Length == 0) requestLine = reader.ReadLine(MaxHeaderBytes);
            if (requestLine == null) return null;

            string[] parts = requestLine.Split(' ');
            if (parts.Length < 3) throw new BadRequestException("Malformed request line");
            var request = new HttpRequest { Method = parts[0].ToUpperInvariant(), Path = parts[1] };

            int headerBytes = 0;
            while (true)
            {
                string line = reader.ReadLine(MaxHeaderBytes);
                if (line == null) throw new IOException("Connection closed in headers");
                if (line.Length == 0) break;
                headerBytes += line.Length;
                if (headerBytes > MaxHeaderBytes) throw new BadRequestException("Headers too large");
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                request.Headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
            }

            if (string.Equals(request.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
            {
                request.Body = ReadChunked(reader);
            }
            else if (request.Header("Content-Length") != null)
            {
                if (!int.TryParse(request.Header("Content-Length"), out int length) || length < 0 || length > MaxBodyBytes)
                    throw new BadRequestException("Invalid Content-Length");
                request.Body = reader.ReadExact(length);
            }
            return request;
        }

        private static byte[] ReadChunked(ByteReader reader)
        {
            var body = new MemoryStream();
            while (true)
            {
                string sizeLine = reader.ReadLine(1024) ?? throw new IOException("Connection closed in chunked body");
                int semi = sizeLine.IndexOf(';');
                if (semi >= 0) sizeLine = sizeLine.Substring(0, semi);
                int size = Convert.ToInt32(sizeLine.Trim(), 16);
                if (size == 0)
                {
                    // Skip trailers.
                    while (!string.IsNullOrEmpty(reader.ReadLine(MaxHeaderBytes))) { }
                    return body.ToArray();
                }
                if (body.Length + size > MaxBodyBytes) throw new BadRequestException("Body too large");
                byte[] chunk = reader.ReadExact(size);
                body.Write(chunk, 0, chunk.Length);
                reader.ReadLine(2); // CRLF after the chunk
            }
        }

        private static void WriteResponse(Stream stream, HttpResponse response, bool close)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(response.Status).Append(' ').Append(ReasonPhrase(response.Status)).Append("\r\n");
            if (response.ContentType != null) sb.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            if (close) sb.Append("Connection: close\r\n");
            foreach (var h in response.Headers) sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
            sb.Append("\r\n");
            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            stream.Write(head, 0, head.Length);
            if (response.Body.Length > 0) stream.Write(response.Body, 0, response.Body.Length);
            stream.Flush();
        }

        private static string ReasonPhrase(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 202: return "Accepted";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 415: return "Unsupported Media Type";
                case 500: return "Internal Server Error";
                case 503: return "Service Unavailable";
                default: return "Status";
            }
        }

        /// <summary>Buffered reader that can read CRLF-terminated ASCII lines and exact byte counts.</summary>
        private sealed class ByteReader
        {
            private readonly Stream _stream;
            private readonly byte[] _buffer = new byte[16 * 1024];
            private int _pos;
            private int _len;

            public ByteReader(Stream stream)
            {
                _stream = stream;
            }

            private bool Fill()
            {
                _pos = 0;
                _len = _stream.Read(_buffer, 0, _buffer.Length);
                return _len > 0;
            }

            /// <summary>Returns the line without CRLF, or null on EOF before any byte.</summary>
            public string ReadLine(int maxLength)
            {
                var sb = new StringBuilder();
                while (true)
                {
                    if (_pos >= _len && !Fill()) return sb.Length == 0 ? null : sb.ToString();
                    byte b = _buffer[_pos++];
                    if (b == (byte)'\n')
                    {
                        if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                        return sb.ToString();
                    }
                    sb.Append((char)b);
                    if (sb.Length > maxLength) throw new BadRequestException("Line too long");
                }
            }

            public byte[] ReadExact(int count)
            {
                var result = new byte[count];
                int copied = 0;
                while (copied < count)
                {
                    if (_pos >= _len && !Fill()) throw new IOException("Connection closed in body");
                    int n = Math.Min(count - copied, _len - _pos);
                    Buffer.BlockCopy(_buffer, _pos, result, copied, n);
                    _pos += n;
                    copied += n;
                }
                return result;
            }
        }
    }
}
