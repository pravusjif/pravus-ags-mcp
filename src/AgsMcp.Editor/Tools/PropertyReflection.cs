using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using AgsMcp.Editor.Mcp;
using Newtonsoft.Json.Linq;

namespace AgsMcp.Editor.Tools
{
    /// <summary>
    /// Generic read/write of an object's properties through <see cref="TypeDescriptor"/>, so the
    /// [Browsable], [Category], [Description], [DisplayName] and [ReadOnly] attributes on the AGS.Types
    /// model are honoured (and ICustomTypeDescriptor types such as Settings work too). The JSON value
    /// conversion is pure and unit-tested.
    /// </summary>
    internal static class PropertyReflection
    {
        /// <summary>One described property, shaped for JSON output (nulls are dropped on serialize).</summary>
        public sealed class PropertyView
        {
            public string name;
            public string displayName;
            public string category;
            public string description;
            public string type;
            public bool readOnly;
            public object value;
            public string[] options;
        }

        public static List<PropertyView> Describe(object target)
        {
            var result = new List<PropertyView>();
            foreach (PropertyDescriptor pd in TypeDescriptor.GetProperties(target))
            {
                if (!pd.IsBrowsable) continue;
                object raw = null;
                try { raw = pd.GetValue(target); } catch { /* some getters throw without a full editor; skip the value */ }
                Type t = Nullable.GetUnderlyingType(pd.PropertyType) ?? pd.PropertyType;
                result.Add(new PropertyView
                {
                    name = pd.Name,
                    displayName = pd.DisplayName != pd.Name ? pd.DisplayName : null,
                    category = string.IsNullOrEmpty(pd.Category) ? null : pd.Category,
                    description = string.IsNullOrEmpty(pd.Description) ? null : pd.Description,
                    type = FriendlyTypeName(t),
                    readOnly = pd.IsReadOnly,
                    value = ToJsonValue(raw, pd.PropertyType),
                    options = t.IsEnum ? Enum.GetNames(t) : null,
                });
            }
            return result;
        }

        /// <summary>Sets the given properties on the target. Returns the property names actually changed.</summary>
        public static List<string> Apply(object target, JObject properties)
        {
            var descriptors = TypeDescriptor.GetProperties(target);
            var changed = new List<string>();
            foreach (var pair in properties)
            {
                PropertyDescriptor pd = Find(descriptors, pair.Key);
                if (pd == null || !pd.IsBrowsable)
                    throw new ToolException($"No settable property '{pair.Key}' on this entity. Call get_properties to see the available names.");
                if (pd.IsReadOnly)
                    throw new ToolException($"Property '{pd.Name}' is read-only.");

                object value = FromJsonValue(pair.Value, pd.PropertyType, pd.Converter, pd.Name);
                try { pd.SetValue(target, value); }
                catch (Exception e) { throw new ToolException($"Could not set '{pd.Name}': {Innermost(e).Message}"); }
                changed.Add(pd.Name);
            }
            return changed;
        }

        public static PropertyDescriptor Find(PropertyDescriptorCollection descriptors, string name)
        {
            foreach (PropertyDescriptor pd in descriptors)
            {
                if (string.Equals(pd.Name, name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(pd.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                    return pd;
            }
            return null;
        }

        public static object ToJsonValue(object value, Type declaredType)
        {
            if (value == null) return null;
            Type t = Nullable.GetUnderlyingType(declaredType) ?? declaredType;
            if (t.IsEnum) return value.ToString();
            if (t == typeof(string)) return value;
            if (t.IsPrimitive || t == typeof(decimal)) return value;
            return value.ToString();
        }

        public static object FromJsonValue(JToken token, Type declaredType, TypeConverter converter, string propName)
        {
            Type t = Nullable.GetUnderlyingType(declaredType) ?? declaredType;

            if (token == null || token.Type == JTokenType.Null)
            {
                if (t.IsValueType) throw new ToolException($"Property '{propName}' cannot be set to null.");
                return null;
            }

            try
            {
                if (t == typeof(string)) return token.Type == JTokenType.String ? (string)token : token.ToString();
                if (t == typeof(bool)) return token.Value<bool>();
                if (t.IsEnum)
                {
                    if (token.Type == JTokenType.Integer) return Enum.ToObject(t, token.Value<long>());
                    string s = token.ToString();
                    try { return Enum.Parse(t, s, true); }
                    catch
                    {
                        throw new ToolException($"'{s}' is not a valid value for '{propName}'. Options: {string.Join(", ", Enum.GetNames(t))}.");
                    }
                }
                if (IsIntegral(t)) return Convert.ChangeType(token.Value<long>(), t, CultureInfo.InvariantCulture);
                if (t == typeof(float) || t == typeof(double) || t == typeof(decimal))
                    return Convert.ChangeType(token.Value<double>(), t, CultureInfo.InvariantCulture);

                if (converter != null && converter.CanConvertFrom(typeof(string)))
                    return converter.ConvertFromInvariantString(token.ToString());
            }
            catch (ToolException) { throw; }
            catch (Exception e)
            {
                throw new ToolException($"Could not convert the value for '{propName}' to {FriendlyTypeName(t)}: {Innermost(e).Message}");
            }

            throw new ToolException($"Setting a property of type {FriendlyTypeName(t)} ('{propName}') is not supported.");
        }

        private static bool IsIntegral(Type t) =>
            t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte) ||
            t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte);

        private static string FriendlyTypeName(Type t)
        {
            if (t == typeof(int) || t == typeof(short) || t == typeof(long) || t == typeof(byte)) return "int";
            if (t == typeof(bool)) return "bool";
            if (t == typeof(float) || t == typeof(double) || t == typeof(decimal)) return "float";
            if (t == typeof(string)) return "string";
            if (t.IsEnum) return "enum " + t.Name;
            return t.Name;
        }

        private static Exception Innermost(Exception e)
        {
            while (e.InnerException != null) e = e.InnerException;
            return e;
        }
    }
}
