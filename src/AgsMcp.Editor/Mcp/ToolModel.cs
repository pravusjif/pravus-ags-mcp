using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Mcp
{
    /// <summary>Thrown by tool handlers for bad input or unmet preconditions; reported to the client as a tool error.</summary>
    public class ToolException : Exception
    {
        public ToolException(string message) : base(message) { }
    }

    public sealed class ToolAnnotations
    {
        public bool ReadOnly { get; set; }
        public bool Destructive { get; set; }
        public bool Idempotent { get; set; }

        public static ToolAnnotations ReadOnlyTool => new ToolAnnotations { ReadOnly = true, Idempotent = true };
        public static ToolAnnotations Mutating => new ToolAnnotations();
        public static ToolAnnotations DestructiveTool => new ToolAnnotations { Destructive = true };

        public JObject ToJson()
        {
            return new JObject
            {
                ["readOnlyHint"] = ReadOnly,
                ["destructiveHint"] = Destructive,
                ["idempotentHint"] = Idempotent,
                ["openWorldHint"] = false,
            };
        }
    }

    public delegate ToolResult ToolHandler(ToolArgs args);

    public sealed class Tool
    {
        public string Name { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public JObject InputSchema { get; set; }
        public ToolAnnotations Annotations { get; set; } = ToolAnnotations.ReadOnlyTool;
        /// <summary>Most tools touch editor state and must run on the WinForms UI thread.</summary>
        public bool RunOnUiThread { get; set; } = true;
        /// <summary>How long the caller waits before reporting a timeout.</summary>
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
        public ToolHandler Handler { get; set; }

        public JObject ToJson()
        {
            var json = new JObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["inputSchema"] = InputSchema ?? Schema.Object().Build(),
                ["annotations"] = Annotations.ToJson(),
            };
            if (Title != null) json["title"] = Title;
            return json;
        }
    }

    public sealed class ToolResult
    {
        public List<JObject> Content { get; } = new List<JObject>();
        public bool IsError { get; set; }

        public static ToolResult Text(string text)
        {
            var r = new ToolResult();
            r.AddText(text);
            return r;
        }

        /// <summary>Serializes a value as indented JSON text content.</summary>
        public static ToolResult Json(object value)
        {
            return Text(JToken.FromObject(value, JsonSerializer.Create(JsonSettings.Default)).ToString(Formatting.Indented));
        }

        public static ToolResult Error(string message)
        {
            var r = Text(message);
            r.IsError = true;
            return r;
        }

        public ToolResult AddText(string text)
        {
            Content.Add(new JObject { ["type"] = "text", ["text"] = text });
            return this;
        }

        public ToolResult AddImage(byte[] data, string mimeType)
        {
            Content.Add(new JObject { ["type"] = "image", ["data"] = Convert.ToBase64String(data), ["mimeType"] = mimeType });
            return this;
        }

        public JObject ToJson()
        {
            return new JObject { ["content"] = new JArray(Content), ["isError"] = IsError };
        }
    }

    public static class JsonSettings
    {
        public static readonly JsonSerializerSettings Default = new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Ignore,
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Converters = { new Newtonsoft.Json.Converters.StringEnumConverter() },
        };
    }

    /// <summary>Typed accessors over a tools/call "arguments" object that raise readable errors.</summary>
    public sealed class ToolArgs
    {
        private readonly JObject _args;

        public ToolArgs(JObject args)
        {
            _args = args ?? new JObject();
        }

        public JObject Raw => _args;

        public bool Has(string name) => _args[name] != null && _args[name].Type != JTokenType.Null;

        public string String(string name)
        {
            if (!Has(name)) throw new ToolException($"Missing required argument '{name}'.");
            return _args[name].Type == JTokenType.String ? (string)_args[name] : _args[name].ToString(Formatting.None);
        }

        public string String(string name, string defaultValue) => Has(name) ? String(name) : defaultValue;

        public int Int(string name)
        {
            if (!Has(name)) throw new ToolException($"Missing required argument '{name}'.");
            try { return _args[name].Value<int>(); }
            catch (Exception) { throw new ToolException($"Argument '{name}' must be an integer."); }
        }

        public int Int(string name, int defaultValue) => Has(name) ? Int(name) : defaultValue;

        public bool Bool(string name, bool defaultValue)
        {
            if (!Has(name)) return defaultValue;
            try { return _args[name].Value<bool>(); }
            catch (Exception) { throw new ToolException($"Argument '{name}' must be a boolean."); }
        }

        public JObject Object(string name)
        {
            if (!Has(name)) throw new ToolException($"Missing required argument '{name}'.");
            if (!(_args[name] is JObject obj)) throw new ToolException($"Argument '{name}' must be an object.");
            return obj;
        }

        public JArray Array(string name)
        {
            if (!Has(name)) throw new ToolException($"Missing required argument '{name}'.");
            if (!(_args[name] is JArray arr)) throw new ToolException($"Argument '{name}' must be an array.");
            return arr;
        }
    }
}
