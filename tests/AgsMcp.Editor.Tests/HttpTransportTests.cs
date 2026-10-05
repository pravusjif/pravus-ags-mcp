using System;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public sealed class HttpTransportTests : IDisposable
    {
        private readonly HttpServer _http;
        private readonly HttpClient _client = new HttpClient();
        private readonly string _baseUrl;

        public HttpTransportTests()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            var server = new McpServer("test", "1.0.0", null, new DirectExecutor());
            server.AddTool(new Tool { Name = "echo", Description = "Echo", Handler = a => ToolResult.Text(a.String("text")) });
            var endpoint = new McpHttpEndpoint(server, () => "status ok");
            _http = new HttpServer(port, endpoint.Handle);
            _http.Start();
            _baseUrl = $"http://127.0.0.1:{port}";
        }

        public void Dispose()
        {
            _client.Dispose();
            _http.Dispose();
        }

        private HttpResponseMessage Post(string json, string origin = null)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/mcp") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            req.Headers.Accept.ParseAdd("application/json, text/event-stream");
            if (origin != null) req.Headers.Add("Origin", origin);
            return _client.SendAsync(req).Result;
        }

        [Fact]
        public void Initialize_OverHttp_SetsSessionHeader()
        {
            var resp = Post(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2025-06-18""}}");
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            Assert.Equal("application/json", resp.Content.Headers.ContentType.MediaType);
            Assert.True(resp.Headers.Contains("Mcp-Session-Id"));
            var body = JObject.Parse(resp.Content.ReadAsStringAsync().Result);
            Assert.Equal("2025-06-18", (string)body["result"]["protocolVersion"]);
        }

        [Fact]
        public void KeepAlive_MultipleRequestsOnOneConnection()
        {
            for (int i = 0; i < 5; i++)
            {
                var resp = Post($@"{{""jsonrpc"":""2.0"",""id"":{i},""method"":""tools/call"",""params"":{{""name"":""echo"",""arguments"":{{""text"":""n{i}""}}}}}}");
                var body = JObject.Parse(resp.Content.ReadAsStringAsync().Result);
                Assert.Equal("n" + i, (string)body["result"]["content"][0]["text"]);
            }
        }

        [Fact]
        public void Notification_Returns202()
        {
            Assert.Equal(HttpStatusCode.Accepted, Post(@"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}").StatusCode);
        }

        [Fact]
        public void ForeignOrigin_IsRejected_LocalOriginAllowed()
        {
            Assert.Equal(HttpStatusCode.Forbidden, Post(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}", "https://evil.example").StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, Post(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}", "null").StatusCode);
            Assert.Equal(HttpStatusCode.OK, Post(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""ping""}", "http://localhost:6274").StatusCode);
        }

        [Fact]
        public void Get_Mcp_Is405_And_Root_ShowsStatus()
        {
            Assert.Equal(HttpStatusCode.MethodNotAllowed, _client.GetAsync(_baseUrl + "/mcp").Result.StatusCode);
            Assert.Equal("status ok", _client.GetStringAsync(_baseUrl + "/").Result);
        }
    }
}
