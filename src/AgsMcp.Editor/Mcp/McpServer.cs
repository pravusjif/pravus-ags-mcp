using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Mcp
{
    /// <summary>Runs a tool's work, e.g. by marshalling it to the UI thread. Tests use a direct executor.</summary>
    public interface IToolExecutor
    {
        ToolResult Execute(Tool tool, Func<ToolResult> work);
    }

    public sealed class DirectExecutor : IToolExecutor
    {
        public ToolResult Execute(Tool tool, Func<ToolResult> work) => work();
    }

    /// <summary>
    /// Transport-independent MCP (JSON-RPC 2.0) message handling: initialize, ping, tools/list, tools/call.
    /// </summary>
    public sealed class McpServer
    {
        public static readonly string[] SupportedProtocolVersions = { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

        private readonly Dictionary<string, Tool> _tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        private readonly List<Tool> _toolOrder = new List<Tool>();
        private readonly IToolExecutor _executor;
        private readonly string _name;
        private readonly string _version;
        private readonly string _instructions;

        public McpServer(string name, string version, string instructions, IToolExecutor executor)
        {
            _name = name;
            _version = version;
            _instructions = instructions;
            _executor = executor;
        }

        public Action<string> Log { get; set; } = _ => { };

        public IReadOnlyList<Tool> Tools => _toolOrder;

        public void AddTool(Tool tool)
        {
            if (_tools.ContainsKey(tool.Name)) throw new InvalidOperationException("Duplicate tool " + tool.Name);
            _tools[tool.Name] = tool;
            _toolOrder.Add(tool);
        }

        public void AddTools(IEnumerable<Tool> tools)
        {
            foreach (var t in tools) AddTool(t);
        }

        /// <summary>
        /// Handles a raw HTTP body. Returns the JSON to send back, or null when the body only held
        /// notifications/responses (the transport then answers 202 Accepted).
        /// </summary>
        public string HandleBody(string body, out bool wasInitialize)
        {
            wasInitialize = false;
            JToken parsed;
            try
            {
                parsed = JToken.Parse(body);
            }
            catch (JsonException e)
            {
                return Serialize(ErrorResponse(null, -32700, "Parse error: " + e.Message));
            }

            if (parsed is JArray batch)
            {
                var responses = new JArray();
                foreach (var item in batch)
                {
                    var r = HandleMessage(item as JObject, ref wasInitialize);
                    if (r != null) responses.Add(r);
                }
                return responses.Count == 0 ? null : Serialize(responses);
            }

            var single = HandleMessage(parsed as JObject, ref wasInitialize);
            return single == null ? null : Serialize(single);
        }

        public JObject HandleMessage(JObject message)
        {
            bool ignored = false;
            return HandleMessage(message, ref ignored);
        }

        private JObject HandleMessage(JObject message, ref bool wasInitialize)
        {
            if (message == null) return ErrorResponse(null, -32600, "Invalid request: expected a JSON object.");

            JToken id = message["id"];
            string method = (string)message["method"];
            bool isNotification = id == null;

            if (method == null)
            {
                // A response to something we never sent (we don't issue server->client requests). Ignore it.
                return isNotification ? null : ErrorResponse(id, -32600, "Invalid request: missing 'method'.");
            }

            if (isNotification)
            {
                // notifications/initialized, notifications/cancelled, ... need no reply.
                return null;
            }

            try
            {
                switch (method)
                {
                    case "initialize":
                        wasInitialize = true;
                        return Result(id, Initialize(message["params"] as JObject));
                    case "ping":
                        return Result(id, new JObject());
                    case "tools/list":
                        return Result(id, new JObject { ["tools"] = new JArray(_toolOrder.Select(t => t.ToJson())) });
                    case "tools/call":
                        return CallTool(id, message["params"] as JObject);
                    case "resources/list":
                        return Result(id, new JObject { ["resources"] = new JArray() });
                    case "resources/templates/list":
                        return Result(id, new JObject { ["resourceTemplates"] = new JArray() });
                    case "prompts/list":
                        return Result(id, new JObject { ["prompts"] = new JArray() });
                    default:
                        return ErrorResponse(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception e)
            {
                Log("Internal error handling " + method + ": " + e);
                return ErrorResponse(id, -32603, "Internal error: " + e.Message);
            }
        }

        private JObject Initialize(JObject p)
        {
            string requested = (string)p?["protocolVersion"];
            string version = SupportedProtocolVersions.Contains(requested) ? requested : SupportedProtocolVersions[0];
            var result = new JObject
            {
                ["protocolVersion"] = version,
                ["capabilities"] = new JObject { ["tools"] = new JObject { ["listChanged"] = false } },
                ["serverInfo"] = new JObject { ["name"] = _name, ["version"] = _version },
            };
            if (!string.IsNullOrEmpty(_instructions)) result["instructions"] = _instructions;
            return result;
        }

        private JObject CallTool(JToken id, JObject p)
        {
            string name = (string)p?["name"];
            if (name == null || !_tools.TryGetValue(name, out var tool))
            {
                return ErrorResponse(id, -32602, "Unknown tool: " + (name ?? "(none)"));
            }

            var args = new ToolArgs(p["arguments"] as JObject);
            ToolResult result;
            try
            {
                result = _executor.Execute(tool, () => tool.Handler(args));
            }
            catch (ToolException e)
            {
                result = ToolResult.Error(e.Message);
            }
            catch (Exception e)
            {
                Log("Tool " + name + " failed: " + e);
                result = ToolResult.Error(e.GetType().Name + ": " + e.Message);
            }
            return Result(id, result.ToJson());
        }

        private static JObject Result(JToken id, JObject result) =>
            new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

        private static JObject ErrorResponse(JToken id, int code, string message) =>
            new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id ?? JValue.CreateNull(),
                ["error"] = new JObject { ["code"] = code, ["message"] = message },
            };

        private static string Serialize(JToken token) => token.ToString(Formatting.None);
    }
}
