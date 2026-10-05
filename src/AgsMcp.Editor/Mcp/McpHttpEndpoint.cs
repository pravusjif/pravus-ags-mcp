using System;
using System.Text;

namespace AgsMcp.Editor.Mcp
{
    /// <summary>
    /// Maps HTTP requests onto the MCP Streamable HTTP transport. Only POST /mcp carries JSON-RPC; we always
    /// answer with plain application/json (no SSE streams), which the spec permits.
    /// </summary>
    public sealed class McpHttpEndpoint
    {
        public const string Path = "/mcp";

        private readonly McpServer _server;
        private readonly Func<string> _statusText;
        private readonly string _sessionId = Guid.NewGuid().ToString("N");

        public McpHttpEndpoint(McpServer server, Func<string> statusText)
        {
            _server = server;
            _statusText = statusText;
        }

        public HttpResponse Handle(HttpRequest request)
        {
            if (!IsAllowedOrigin(request.Header("Origin")))
            {
                return HttpResponse.Text(403, "Forbidden: requests from non-local origins are rejected.");
            }

            string path = request.Path;
            int q = path.IndexOf('?');
            if (q >= 0) path = path.Substring(0, q);
            path = path.TrimEnd('/');

            if (path.Length == 0 && request.Method == "GET")
            {
                return HttpResponse.Text(200, _statusText());
            }

            if (!string.Equals(path, Path, StringComparison.OrdinalIgnoreCase))
            {
                return HttpResponse.Text(404, "Not found. The MCP endpoint is " + Path);
            }

            switch (request.Method)
            {
                case "POST":
                    return HandlePost(request);
                case "DELETE":
                    // Session termination: we keep no per-session state, so just acknowledge.
                    return HttpResponse.Empty(200);
                default:
                    var r = HttpResponse.Text(405, "This server does not offer an SSE stream; use POST.");
                    r.Headers["Allow"] = "POST, DELETE";
                    return r;
            }
        }

        private HttpResponse HandlePost(HttpRequest request)
        {
            string body = request.BodyText;
            string json = _server.HandleBody(body, out bool wasInitialize);
            if (json == null) return HttpResponse.Empty(202);

            var response = new HttpResponse { Status = 200, ContentType = "application/json", Body = Encoding.UTF8.GetBytes(json) };
            if (wasInitialize) response.Headers["Mcp-Session-Id"] = _sessionId;
            return response;
        }

        /// <summary>DNS-rebinding protection: browsers send Origin; only accept local ones. Non-browser clients send none.</summary>
        public static bool IsAllowedOrigin(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri uri)) return false;
            string host = uri.Host.Trim('[', ']');
            return host == "localhost" || host == "127.0.0.1" || host == "::1";
        }
    }
}
