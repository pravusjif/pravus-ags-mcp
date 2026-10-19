using System;
using System.Collections.Generic;
using System.Linq;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// The batch tool: runs a list of tool calls in order in one request, so a bulk pass (dozens of
    /// set_properties, a sprite per frame) needs neither a client round trip per call nor a raw JSON-RPC client.
    /// Every schema keeps one typed form; batch is what makes any tool bulk-capable.
    /// </summary>
    internal static class BatchTools
    {
        public const string Name = "batch";

        public static IEnumerable<Tool> Create(McpServer server)
        {
            yield return new Tool
            {
                Name = Name,
                Title = "Batch tool calls",
                Description = "Run several tool calls in order, in one request. Each call is {name, arguments} and runs exactly as " +
                              "if called on its own (same argument checks; editor work still runs one call at a time on the " +
                              "editor's thread, so other clients' calls may interleave between them). Returns {results:[{name, ok, " +
                              "content|error}], stopped}: 'content' holds the call's content items, with JSON text parsed into " +
                              "objects and images replaced by a note (pass outPath to the image tools to get files instead). " +
                              "stopOnError (default true) stops at the first failed call; stopped=true then, and later calls are " +
                              "not run. batch cannot be nested.",
                InputSchema = Schema.Object()
                    .Required("calls", Schema.ArrayOf(
                        Schema.Object()
                            .Required("name", Schema.String("Tool name, e.g. \"set_properties\"."))
                            .Optional("arguments", Schema.AnyObject("The tool's arguments, as in a tools/call."))
                            .Build("One tool call."),
                        "The calls to run, in order."))
                    .Optional("stopOnError", Schema.Boolean("Stop at the first call that fails. Default true."))
                    .Build(),
                Annotations = ToolAnnotations.DestructiveTool,
                RunOnUiThread = false,
                Timeout = TimeSpan.FromMinutes(10),
                Handler = args =>
                {
                    JArray calls = args.Array("calls");
                    if (calls.Count == 0) throw new ToolException("'calls' is empty.");
                    return ToolResult.Json(Run(server, calls, args.Bool("stopOnError", true)));
                },
            };
        }

        /// <summary>Runs the calls through <see cref="McpServer.InvokeTool"/> and collects their outcomes.</summary>
        internal static JObject Run(McpServer server, JArray calls, bool stopOnError)
        {
            var results = new JArray();
            bool stopped = false;
            for (int i = 0; i < calls.Count; i++)
            {
                var call = calls[i] as JObject;
                string name = call == null ? null : (string)call["name"];
                ToolResult result;
                if (call == null) result = ToolResult.Error($"Call {i} is not an object {{name, arguments}}.");
                else if (name == Name) result = ToolResult.Error("batch cannot be nested.");
                else if (call["arguments"] != null && call["arguments"].Type != JTokenType.Null && !(call["arguments"] is JObject))
                    result = ToolResult.Error($"Call {i} ({name}): 'arguments' must be an object.");
                else result = server.InvokeTool(name, call["arguments"] as JObject);

                var entry = new JObject { ["name"] = name, ["ok"] = !result.IsError };
                if (result.IsError) entry["error"] = string.Join("\n", result.Content.Select(c => (string)c["text"]).Where(t => t != null));
                else entry["content"] = Summarize(result);
                results.Add(entry);

                if (result.IsError && stopOnError && i < calls.Count - 1)
                {
                    stopped = true;
                    break;
                }
            }
            return new JObject { ["results"] = results, ["stopped"] = stopped };
        }

        /// <summary>
        /// A result's content for the batch reply: JSON object/array text is embedded parsed, other text stays a
        /// string, and an image becomes "&lt;image omitted, N bytes; use outPath&gt;" so a batch never carries images.
        /// </summary>
        internal static JArray Summarize(ToolResult result)
        {
            var items = new JArray();
            foreach (JObject c in result.Content)
            {
                string type = (string)c["type"];
                if (type == "image")
                {
                    string data = (string)c["data"] ?? string.Empty;
                    items.Add($"<image omitted, {Base64Length(data)} bytes; use outPath>");
                }
                else if (type == "text")
                {
                    items.Add(ParseJsonText((string)c["text"]));
                }
                else items.Add(c);
            }
            return items;
        }

        private static JToken ParseJsonText(string text)
        {
            string t = (text ?? string.Empty).TrimStart();
            if (t.StartsWith("{") || t.StartsWith("["))
            {
                try { return JToken.Parse(text); }
                catch (JsonException) { }
            }
            return text;
        }

        /// <summary>Decoded size of a base64 string.</summary>
        internal static int Base64Length(string data)
        {
            int padding = data.EndsWith("==") ? 2 : data.EndsWith("=") ? 1 : 0;
            return data.Length / 4 * 3 - padding;
        }
    }
}
