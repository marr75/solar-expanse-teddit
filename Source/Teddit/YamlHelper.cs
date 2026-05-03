using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using YamlDotNet.Serialization;

namespace Teddit
{
    /// <summary>
    /// Parses a .yaml patch file into the same shape that PatchConfig/DepositConfig consume:
    ///   Dictionary&lt;string, Dictionary&lt;string, JToken&gt;&gt;
    /// YamlDotNet deserializes to Dictionary&lt;object,object&gt; / List&lt;object&gt; when the
    /// target type is object, so we recursively convert those to JToken.
    /// </summary>
    internal static class YamlHelper
    {
        private static readonly IDeserializer _deserializer =
            new DeserializerBuilder().Build();

        /// <summary>
        /// Reads a YAML file and returns a flat string-keyed dictionary of field maps.
        /// Top-level keys that start with '#' or '_' are skipped (comment/template keys).
        /// Top-level entries whose value is not a mapping are also skipped.
        /// </summary>
        public static Dictionary<string, Dictionary<string, JToken>> LoadPatch(string path)
        {
            var result = new Dictionary<string, Dictionary<string, JToken>>();
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"[YamlHelper] {Path.GetFileName(path)} not found — skipping.");
                return result;
            }

            var raw = _deserializer.Deserialize<Dictionary<object, object>>(File.ReadAllText(path));
            if (raw == null) return result;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? "";
                if (key.StartsWith("_")) continue;

                if (!(kv.Value is Dictionary<object, object> inner)) continue;

                var fields = new Dictionary<string, JToken>();
                foreach (var fkv in inner)
                {
                    string fkey = fkv.Key?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(fkey))
                        fields[fkey] = ToJToken(fkv.Value);
                }

                if (fields.Count > 0)
                    result[key] = fields;
            }

            return result;
        }

        /// <summary>
        /// Reads a deposits YAML file.
        /// Top-level keys map to a List&lt;object&gt; (each item is a mapping of deposit fields).
        /// Returns Dictionary&lt;string, JToken&gt; where each value is a JArray.
        /// </summary>
        public static Dictionary<string, JToken> LoadDeposits(string path)
        {
            var result = new Dictionary<string, JToken>();
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"[YamlHelper] {Path.GetFileName(path)} not found — skipping.");
                return result;
            }

            var raw = _deserializer.Deserialize<Dictionary<object, object>>(File.ReadAllText(path));
            if (raw == null) return result;

            foreach (var kv in raw)
            {
                string key = kv.Key?.ToString() ?? "";
                if (key.StartsWith("_")) continue;
                if (!(kv.Value is List<object>)) continue;

                result[key] = ToJToken(kv.Value);
            }

            return result;
        }

        // ── Recursive converter ──────────────────────────────────────────────

        internal static JToken ToJToken(object value)
        {
            if (value == null)
                return JValue.CreateNull();

            if (value is Dictionary<object, object> dict)
            {
                var obj = new JObject();
                foreach (var kv in dict)
                    obj[kv.Key.ToString()] = ToJToken(kv.Value);
                return obj;
            }

            if (value is List<object> list)
            {
                var arr = new JArray();
                foreach (var item in list)
                    arr.Add(ToJToken(item));
                return arr;
            }

            // primitives: bool, int, long, float, double, string
            return JToken.FromObject(value);
        }
    }
}
