using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Autoroadmarking_Pro.CadHost.UI
{
    internal static class JsonPayload
    {
        public static bool TryGet(JsonElement root, string name, out JsonElement value)
        {
            value = default;
            if (root.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in root.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
            return false;
        }

        public static string String(JsonElement root, string name, string fallback = "")
        {
            return TryGet(root, name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? fallback)
                : fallback;
        }

        public static double Double(JsonElement root, string name, double fallback = 0.0)
        {
            if (!TryGet(root, name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), out n)) return n;
            return fallback;
        }

        public static int Int(JsonElement root, string name, int fallback = 0)
        {
            if (!TryGet(root, name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
            if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out n)) return n;
            return fallback;
        }

        public static bool Bool(JsonElement root, string name, bool fallback = false)
        {
            if (!TryGet(root, name, out var v)) return fallback;
            if (v.ValueKind == JsonValueKind.True) return true;
            if (v.ValueKind == JsonValueKind.False) return false;
            if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b)) return b;
            return fallback;
        }

        public static T? Object<T>(JsonElement root, string name, JsonSerializerOptions? options = null)
        {
            if (!TryGet(root, name, out var v)) return default;
            try { return JsonSerializer.Deserialize<T>(v.GetRawText(), options); }
            catch { return default; }
        }

        public static List<T> List<T>(JsonElement root, string name, JsonSerializerOptions? options = null)
        {
            return Object<List<T>>(root, name, options) ?? new List<T>();
        }
    }
}
