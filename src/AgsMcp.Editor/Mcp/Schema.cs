using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Mcp
{
    /// <summary>Small builder for the JSON Schemas used in tool definitions.</summary>
    public static class Schema
    {
        public static ObjectSchema Object() => new ObjectSchema();

        public static JObject String(string description, params string[] enumValues)
        {
            var s = new JObject { ["type"] = "string", ["description"] = description };
            if (enumValues != null && enumValues.Length > 0) s["enum"] = new JArray(enumValues);
            return s;
        }

        public static JObject Integer(string description) => new JObject { ["type"] = "integer", ["description"] = description };
        public static JObject Number(string description) => new JObject { ["type"] = "number", ["description"] = description };
        public static JObject Boolean(string description) => new JObject { ["type"] = "boolean", ["description"] = description };
        public static JObject AnyObject(string description) => new JObject { ["type"] = "object", ["description"] = description };

        public static JObject ArrayOf(JObject items, string description) =>
            new JObject { ["type"] = "array", ["items"] = items, ["description"] = description };
    }

    public sealed class ObjectSchema
    {
        private readonly JObject _properties = new JObject();
        private readonly JArray _required = new JArray();

        public ObjectSchema Prop(string name, JObject schema, bool required = false)
        {
            _properties[name] = schema;
            if (required) _required.Add(name);
            return this;
        }

        public ObjectSchema Required(string name, JObject schema) => Prop(name, schema, true);
        public ObjectSchema Optional(string name, JObject schema) => Prop(name, schema, false);

        public JObject Build(string description = null)
        {
            var s = new JObject { ["type"] = "object", ["properties"] = _properties };
            if (_required.Count > 0) s["required"] = _required;
            if (description != null) s["description"] = description;
            return s;
        }
    }
}
