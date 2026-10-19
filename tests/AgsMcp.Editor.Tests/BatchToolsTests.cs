using System.Linq;
using AgsMcp.Editor.Mcp;
using AgsMcp.Editor.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgsMcp.Editor.Tests
{
    public class BatchToolsTests
    {
        private static McpServer CreateServer()
        {
            var server = new McpServer("test", "1.0.0", null, new DirectExecutor());
            server.AddTool(new Tool
            {
                Name = "echo",
                Description = "Echo",
                InputSchema = Schema.Object().Required("text", Schema.String("Text")).Build(),
                Handler = a => ToolResult.Text(a.String("text")),
            });
            server.AddTool(new Tool { Name = "boom", Description = "Throws", Handler = a => throw new ToolException("bad input") });
            server.AddTool(new Tool
            {
                Name = "picture",
                Description = "Returns JSON text and an image",
                Handler = a => ToolResult.Json(new { width = 2 }).AddImage(new byte[] { 1, 2, 3, 4, 5 }, "image/png"),
            });
            server.AddTools(BatchTools.Create(server));
            return server;
        }

        private static JObject Echo(string text) => new JObject { ["name"] = "echo", ["arguments"] = new JObject { ["text"] = text } };
        private static JObject Named(string name) => new JObject { ["name"] = name };

        /// <summary>Calls the batch tool over tools/call, as a client would, and returns its parsed JSON reply.</summary>
        private static JObject CallBatch(McpServer server, JArray calls, bool? stopOnError = null)
        {
            var args = new JObject { ["calls"] = calls };
            if (stopOnError.HasValue) args["stopOnError"] = stopOnError.Value;
            ToolResult r = server.InvokeTool("batch", args);
            Assert.False(r.IsError, (string)r.Content[0]["text"]);
            return JObject.Parse((string)r.Content[0]["text"]);
        }

        [Fact]
        public void StopOnError_StopsAtTheFirstFailure()
        {
            JObject r = CallBatch(CreateServer(), new JArray(Echo("a"), Named("boom"), Echo("b")));
            var results = (JArray)r["results"];
            Assert.True((bool)r["stopped"]);
            Assert.Equal(2, results.Count);
            Assert.True((bool)results[0]["ok"]);
            Assert.Equal("a", (string)results[0]["content"][0]);
            Assert.False((bool)results[1]["ok"]);
            Assert.Equal("bad input", (string)results[1]["error"]);
        }

        [Fact]
        public void WithoutStopOnError_RunsEveryCall()
        {
            JObject r = CallBatch(CreateServer(), new JArray(Echo("a"), Named("boom"), Echo("b")), stopOnError: false);
            var results = (JArray)r["results"];
            Assert.False((bool)r["stopped"]);
            Assert.Equal(new[] { true, false, true }, results.Select(x => (bool)x["ok"]));
            Assert.Equal("b", (string)results[2]["content"][0]);
        }

        [Fact]
        public void AFailingLastCall_IsNotReportedAsStopped()
        {
            JObject r = CallBatch(CreateServer(), new JArray(Echo("a"), Named("boom")));
            Assert.False((bool)r["stopped"]);
            Assert.Equal(2, ((JArray)r["results"]).Count);
        }

        [Fact]
        public void UnknownToolsAndArguments_FailThatCallOnly()
        {
            var badArgs = new JObject { ["name"] = "echo", ["arguments"] = new JObject { ["text"] = "x", ["extra"] = 1 } };
            JObject r = CallBatch(CreateServer(), new JArray(Named("nope"), badArgs, Echo("ok")), stopOnError: false);
            var results = (JArray)r["results"];
            Assert.Contains("Unknown tool: nope", (string)results[0]["error"]);
            Assert.Contains("'extra'", (string)results[1]["error"]);
            Assert.True((bool)results[2]["ok"]);
        }

        [Fact]
        public void NestedBatchAndMalformedCalls_AreRefused()
        {
            var nested = new JObject { ["name"] = "batch", ["arguments"] = new JObject { ["calls"] = new JArray(Echo("x")) } };
            var badArguments = new JObject { ["name"] = "echo", ["arguments"] = "text=x" };
            JObject r = CallBatch(CreateServer(), new JArray(nested, "not an object", badArguments), stopOnError: false);
            var results = (JArray)r["results"];
            Assert.Contains("cannot be nested", (string)results[0]["error"]);
            Assert.Contains("not an object", (string)results[1]["error"]);
            Assert.Contains("must be an object", (string)results[2]["error"]);
        }

        [Fact]
        public void Images_AreReplacedAndJsonTextIsParsed()
        {
            JObject r = CallBatch(CreateServer(), new JArray(Named("picture")));
            var content = (JArray)r["results"][0]["content"];
            Assert.Equal(2, (int)content[0]["width"]);
            Assert.Equal("<image omitted, 5 bytes; use outPath>", (string)content[1]);
        }

        [Fact]
        public void EmptyCalls_IsAToolError()
        {
            ToolResult r = CreateServer().InvokeTool("batch", new JObject { ["calls"] = new JArray() });
            Assert.True(r.IsError);
        }

        [Theory]
        [InlineData("AQID", 3)]
        [InlineData("AQIDBA==", 4)]
        [InlineData("AQIDBAU=", 5)]
        [InlineData("", 0)]
        public void Base64Length(string data, int expected)
        {
            Assert.Equal(expected, BatchTools.Base64Length(data));
        }

        [Theory]
        [InlineData(@"C:\temp\a.png", true)]
        [InlineData("C:/temp/a.png", true)]
        [InlineData(@"\\server\share\a.png", true)]
        [InlineData("a.png", false)]
        [InlineData(@"\temp\a.png", false)]
        [InlineData("C:a.png", false)]
        [InlineData("", false)]
        public void IsAbsolutePath(string path, bool expected)
        {
            Assert.Equal(expected, ImageOutput.IsAbsolutePath(path));
        }
    }
}
