using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyMod
{
    /// <summary>
    /// Loads a patch config JSON file of the form:
    ///   { "some_id": { "fieldName": value, ... }, ... }
    /// Keys starting with "_" are treated as comments and skipped.
    /// </summary>
    internal static class PatchConfig
    {
        public static Dictionary<string, Dictionary<string, JToken>> Load(string path)
        {
            var result = new Dictionary<string, Dictionary<string, JToken>>();
            if (!File.Exists(path))
            {
                Plugin.Log.LogInfo($"[PatchConfig] {Path.GetFileName(path)} not found — skipping.");
                return result;
            }

            var raw = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(File.ReadAllText(path));
            if (raw == null) return result;

            foreach (var kv in raw)
            {
                if (kv.Key.StartsWith("_")) continue;
                if (kv.Value.Type != JTokenType.Object) continue;
                var fields = kv.Value.ToObject<Dictionary<string, JToken>>();
                if (fields != null && fields.Count > 0)
                    result[kv.Key] = fields;
            }

            Plugin.Log.LogInfo($"[PatchConfig] {Path.GetFileName(path)}: {result.Count} entries.");
            return result;
        }
    }
}
