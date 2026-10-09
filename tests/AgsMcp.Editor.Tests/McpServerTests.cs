using System.Linq;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class McpServerTests
    {
        private static McpServer CreateServer()
        {
            var server = new McpServer("test", "1.0.0", "instructions", new DirectExecutor());
            server.AddTool(new Tool
            {
                Name = "echo",
                Description = "Echo",
                InputSchema = Schema.Object().Required("text", Schema.String("Text")).Build(),
                Handler = a => ToolResult.Text(a.String("text")),
            });
            server.AddTool(new Tool
            {
                Name = "boom",
                Description = "Throws",
                Handler = a => throw new ToolException("bad input"),
            });
            return server;
        }

        private static JObject Call(McpServer server, string json)
        {
            string response = server.HandleBody(json, out _);
            return response == null ? null : JObject.Parse(response);
        }

        [Fact]
        public void Initialize_EchoesSupportedProtocolVersion()
        {
            var server = CreateServer();
            string body = server.HandleBody(@"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""2025-06-18"",""capabilities"":{},""clientInfo"":{""name"":""t"",""version"":""1""}}}", out bool wasInit);
            var r = JObject.Parse(body);
            Assert.True(wasInit);
            Assert.Equal("2025-06-18", (string)r["result"]["protocolVersion"]);
            Assert.Equal("test", (string)r["result"]["serverInfo"]["name"]);
            Assert.NotNull(r["result"]["capabilities"]["tools"]);
        }

        [Fact]
        public void Initialize_UnknownVersion_FallsBackToLatest()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":1,""method"":""initialize"",""params"":{""protocolVersion"":""1999-01-01""}}");
            Assert.Equal(McpServer.SupportedProtocolVersions[0], (string)r["result"]["protocolVersion"]);
        }

        [Fact]
        public void Notification_ProducesNoResponse()
        {
            Assert.Null(Call(CreateServer(), @"{""jsonrpc"":""2.0"",""method"":""notifications/initialized""}"));
        }

        [Fact]
        public void ToolsList_ReturnsSchemasAndAnnotations()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":""a"",""method"":""tools/list""}");
            var tools = (JArray)r["result"]["tools"];
            Assert.Equal(new[] { "echo", "boom" }, tools.Select(t => (string)t["name"]));
            Assert.Equal("object", (string)tools[0]["inputSchema"]["type"]);
            Assert.Equal("text", (string)tools[0]["inputSchema"]["required"][0]);
            Assert.True((bool)tools[0]["annotations"]["readOnlyHint"]);
            Assert.Equal("a", (string)r["id"]);
        }

        [Fact]
        public void ToolsCall_ReturnsContent()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":2,""method"":""tools/call"",""params"":{""name"":""echo"",""arguments"":{""text"":""hi""}}}");
            Assert.False((bool)r["result"]["isError"]);
            Assert.Equal("hi", (string)r["result"]["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_ToolException_IsReportedAsToolError()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":3,""method"":""tools/call"",""params"":{""name"":""boom"",""arguments"":{}}}");
            Assert.True((bool)r["result"]["isError"]);
            Assert.Equal("bad input", (string)r["result"]["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_MissingArgument_IsToolError()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":4,""method"":""tools/call"",""params"":{""name"":""echo""}}");
            Assert.True((bool)r["result"]["isError"]);
            Assert.Contains("text", (string)r["result"]["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_UnknownArgument_IsToolErrorNamingTheValidOnes()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":7,""method"":""tools/call"",""params"":{""name"":""echo"",""arguments"":{""text"":""hi"",""txt"":""x""}}}");
            Assert.True((bool)r["result"]["isError"]);
            string message = (string)r["result"]["content"][0]["text"];
            Assert.Contains("'txt'", message);
            Assert.Contains("only text", message);
        }

        [Fact]
        public void ToolsCall_UnknownTool_IsProtocolError()
        {
            var r = Call(CreateServer(), @"{""jsonrpc"":""2.0"",""id"":5,""method"":""tools/call"",""params"":{""name"":""nope""}}");
            Assert.Equal(-32602, (int)r["error"]["code"]);
        }

        [Fact]
        public void UnknownMethod_And_ParseError()
        {
            var server = CreateServer();
            Assert.Equal(-32601, (int)Call(server, @"{""jsonrpc"":""2.0"",""id"":6,""method"":""foo/bar""}")["error"]["code"]);
            Assert.Equal(-32700, (int)Call(server, "{not json")["error"]["code"]);
        }
    }
}
