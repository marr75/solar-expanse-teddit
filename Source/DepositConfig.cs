using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MyMod
{
    /// <summary>
    /// Reads deposits.json from the plugin folder.
    ///
    /// Format:
    /// {
    ///   "MARS": [
    ///     {
    ///       "resourceId":   "id_resource_carbon",
    ///       "state":        "Solid",       // Solid | Liquid | Gas | Underground
    ///       "amount":       50000.0,
    ///       "miningFactor": 0.5,           // 0-1; null = use game default
    ///       "forcePrimary": false,
    ///       "overwrite":    false          // true = replace existing deposit's amount+miningFactor
    ///     }
    ///   ]
    /// }
    ///
    /// Body names must match ObjectInfo.ObjectName (always uppercase, e.g. "MARS", "EARTH").
    /// </summary>
    internal class DepositConfig
    {
        // key = ObjectName (uppercase body name)
        public Dictionary<string, List<DepositEntry>> Bodies { get; private set; }
            = new Dictionary<string, List<DepositEntry>>();

        public static DepositConfig Load(string path)
        {
            if (!File.Exists(path))
            {
                Plugin.Log.LogWarning($"[DepositConfig] Not found at {path} — skipping injection.");
                return new DepositConfig();
            }

            var config = new DepositConfig();
            var raw = JsonConvert.DeserializeObject<Dictionary<string, JToken>>(File.ReadAllText(path));
            if (raw == null) return config;

            foreach (var kv in raw)
            {
                // Skip comment/template keys
                if (kv.Key.StartsWith("_")) continue;
                if (kv.Value.Type != JTokenType.Array) continue;

                var entries = kv.Value.ToObject<List<DepositEntry>>();
                if (entries != null && entries.Count > 0)
                    config.Bodies[kv.Key.ToUpperInvariant()] = entries;
            }

            Plugin.Log.LogInfo($"[DepositConfig] Loaded {config.Bodies.Count} body overrides.");
            return config;
        }

        public List<DepositEntry> GetDepositsFor(string objectName)
        {
            // ObjectName is always uppercase; try exact match first, then case-insensitive
            if (Bodies.TryGetValue(objectName, out var list))
                return list;
            foreach (var kv in Bodies)
                if (kv.Key.Equals(objectName, System.StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }
    }

    internal class DepositEntry
    {
        [JsonProperty("resourceId")]   public string ResourceId   { get; set; }
        [JsonProperty("state")]        public string State        { get; set; } = "Solid";
        [JsonProperty("amount")]       public double Amount       { get; set; }
        [JsonProperty("miningFactor")] public float? MiningFactor { get; set; }
        [JsonProperty("forcePrimary")] public bool   ForcePrimary { get; set; }
        [JsonProperty("overwrite")]    public bool   Overwrite    { get; set; }
    }
}
