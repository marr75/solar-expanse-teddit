using System;
using System.Collections.Generic;
using System.Reflection;
using Data.ScriptableObject;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;

namespace MyMod
{
    /// <summary>
    /// Patches fields on ScriptableObject types at runtime via reflection.
    /// Changes are global (shared objects) — affect all companies.
    ///
    /// Field names match private C# field names from the decompiled source.
    /// Supported value types: float, double, int, long, bool, float?, double?.
    /// Dot notation patches one level of nesting: "energyProductionData.energyProduction".
    ///
    /// ── FACILITY FIELDS (facilities.json) ────────────────────────────────────
    ///   maintenanceCostPerDay                  float   daily upkeep cost
    ///   energyConsumption                      double  energy draw per enabled unit per day
    ///   needWorkersToWork                      int     crew required per enabled unit
    ///   specialAbilityParameter                float   context-dependent: mining rate, crew capacity, etc.
    ///   upkeepStackingValue                    float   additional upkeep per stacked unit
    ///   energyProductionData.energyProduction  double  energy output per day
    ///   resourceExplorationBonus               float   (GroundFacilityDescriptor only) exploration power bonus
    ///
    /// ── SPACECRAFT FIELDS (spacecraft.json) ──────────────────────────────────
    ///   All fields below apply to both flat SpacecraftType and Hull-based spacecraft.
    ///   For Hull-based spacecraft (isCompletedDesign == true), these field names are
    ///   automatically remapped to the corresponding Hull base fields before patching:
    ///
    ///   spacecraft.json name   → Hull field         description
    ///   cargoCapacity          → cargoCapacityBase   cargo capacity in tonnes
    ///   fuelCapacity           → fuelCapacityBase    fuel tank size in tonnes
    ///   mass                   → massBase            dry mass in tonnes
    ///   thrust                 → thrustBase          engine thrust (kN)
    ///   exhaustV               → exhaustVBase        exhaust velocity (km/s)
    ///   maxLifeSupport         → lifeSupportMaxBase  max life-support crew-days
    ///
    ///   Fields NOT remapped (apply directly to SpacecraftType regardless of hull):
    ///   availableDeltaV        float   total delta-V budget (km/s)
    ///   maintenanceCostPerDay  float   daily upkeep cost
    ///   maxPayload             float?  max payload to surface; null = unlimited
    ///
    ///   BACKGROUND — SpacecraftType vs Hull:
    ///   A SpacecraftType is the base blueprint with flat numeric fields. A Hull is an
    ///   optional struct embedded in SpacecraftType (used by the ship designer) that
    ///   builds stats from typed SpaceComponent slots (engine, tank, utility, crew) plus
    ///   per-category base values (cargoCapacityBase, etc.). When isCompletedDesign is
    ///   true, every property getter (CargoCapacity, Mass, Thrust, …) bypasses the flat
    ///   field entirely and reads from the hull. Patching the flat field has no effect in
    ///   that case, so the patcher detects the hull and writes to the base field instead.
    ///
    /// ── LAUNCH VEHICLE FIELDS (launch_vehicles.json) ─────────────────────────
    ///   maxPayload             float   max payload to surface (tonnes)
    ///   maxFuelLoad            float   max fuel load (tonnes)
    ///   costLaunch             float   fuel cost per launch
    ///   exhaustV               float   exhaust velocity (km/s)
    ///   reusability            float   0 = expendable, 1 = fully reusable
    ///   maintenanceCostPerDay  float   daily upkeep cost
    ///   canSendHuman           bool    whether humans can be launched
    ///
    /// ── RESEARCH FIELDS (research.json) ──────────────────────────────────────
    ///   workHourToComplete     float   research work-hours needed to complete
    ///   isLocked               bool    whether the research is locked (hidden/unavailable)
    ///   isLockedForUI          bool    whether the research is hidden in the UI tree
    /// </summary>
    internal static class ScriptableObjectPatcher
    {
        public static void RunFacilities(Dictionary<string, Dictionary<string, JToken>> config)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[FacilityPatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, skipped = 0;
            foreach (var kv in config)
            {
                var descriptor = allSO.AllFacility.GetByID(kv.Key);
                if (descriptor == null)
                {
                    Plugin.Log.LogWarning($"[FacilityPatcher] Unknown facility ID '{kv.Key}' — skipping.");
                    skipped++; continue;
                }
                ApplyFields(descriptor, kv.Value, "[FacilityPatcher]", kv.Key);
                patched++;
            }
            Plugin.Log.LogInfo($"[FacilityPatcher] Done — patched: {patched}, skipped: {skipped}");
        }

        // When a SpacecraftType has a completed Hull, these fields on SpacecraftType are
        // bypassed by the property getters in favour of the hull's base fields.
        // We remap them so the patch hits the right target.
        static readonly Dictionary<string, string> _hullFieldMap = new Dictionary<string, string>
        {
            { "cargoCapacity",  "cargoCapacityBase"  },
            { "fuelCapacity",   "fuelCapacityBase"   },
            { "mass",           "massBase"            },
            { "thrust",         "thrustBase"          },
            { "exhaustV",       "exhaustVBase"        },
            { "maxLifeSupport", "lifeSupportMaxBase"  },
        };

        static readonly FieldInfo _hullField = typeof(Data.ScriptableObject.SpacecraftType)
            .GetField("hull", BindingFlags.NonPublic | BindingFlags.Instance);

        public static void RunSpacecraft(Dictionary<string, Dictionary<string, JToken>> config)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[SpacecraftPatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, skipped = 0;
            foreach (var kv in config)
            {
                var scType = allSO.AllSpacecraftType.GetByID(kv.Key);
                if (scType == null)
                {
                    Plugin.Log.LogWarning($"[SpacecraftPatcher] Unknown spacecraft ID '{kv.Key}' — skipping.");
                    skipped++; continue;
                }

                // Check if this spacecraft has a completed hull design.
                // If so, remap hull-overridden fields to the hull's base fields.
                object hullBoxed = _hullField?.GetValue(scType); // null if Hull? has no value
                bool hasCompletedHull = false;
                if (hullBoxed != null)
                {
                    var isCompletedFi = hullBoxed.GetType()
                        .GetField("isCompletedDesign", BindingFlags.NonPublic | BindingFlags.Instance);
                    hasCompletedHull = isCompletedFi != null && (bool)isCompletedFi.GetValue(hullBoxed);
                }

                if (hasCompletedHull)
                {
                    Plugin.Log.LogDebug($"[SpacecraftPatcher] {kv.Key} has completed hull — remapping fields to hull base values.");
                    var scFields  = new Dictionary<string, JToken>();
                    var hullFields = new Dictionary<string, JToken>();

                    foreach (var field in kv.Value)
                    {
                        if (_hullFieldMap.TryGetValue(field.Key, out var hullFieldName))
                            hullFields[hullFieldName] = field.Value;
                        else
                            scFields[field.Key] = field.Value;
                    }

                    if (hullFields.Count > 0)
                    {
                        ApplyFields(hullBoxed, hullFields, "[SpacecraftPatcher]", kv.Key + ".hull");
                        // Write the modified struct back (required for value types)
                        _hullField.SetValue(scType, hullBoxed);
                    }
                    if (scFields.Count > 0)
                        ApplyFields(scType, scFields, "[SpacecraftPatcher]", kv.Key);
                }
                else
                {
                    ApplyFields(scType, kv.Value, "[SpacecraftPatcher]", kv.Key);
                }

                patched++;
            }
            Plugin.Log.LogInfo($"[SpacecraftPatcher] Done — patched: {patched}, skipped: {skipped}");
        }

        public static void RunLaunchVehicles(Dictionary<string, Dictionary<string, JToken>> config)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[LaunchVehiclePatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, skipped = 0;
            foreach (var kv in config)
            {
                var lv = allSO.AllLaunchVehicleType.GetByID(kv.Key);
                if (lv == null)
                {
                    Plugin.Log.LogWarning($"[LaunchVehiclePatcher] Unknown launch vehicle ID '{kv.Key}' — skipping.");
                    skipped++; continue;
                }
                ApplyFields(lv, kv.Value, "[LaunchVehiclePatcher]", kv.Key);
                patched++;
            }
            Plugin.Log.LogInfo($"[LaunchVehiclePatcher] Done — patched: {patched}, skipped: {skipped}");
        }

        public static void RunResearch(Dictionary<string, Dictionary<string, JToken>> config)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[ResearchPatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, skipped = 0;
            foreach (var kv in config)
            {
                var rd = allSO.AllResearchDefinition.GetByID(kv.Key);
                if (rd == null)
                {
                    Plugin.Log.LogWarning($"[ResearchPatcher] Unknown research ID '{kv.Key}' — skipping.");
                    skipped++; continue;
                }
                ApplyFields(rd, kv.Value, "[ResearchPatcher]", kv.Key);
                patched++;
            }
            Plugin.Log.LogInfo($"[ResearchPatcher] Done — patched: {patched}, skipped: {skipped}");
        }

        static void ApplyFields(object target, Dictionary<string, JToken> fields, string prefix, string id)
        {
            foreach (var kv in fields)
            {
                try
                {
                    int dot = kv.Key.IndexOf('.');
                    if (dot >= 0)
                    {
                        // Dot notation: "parentField.childField"
                        string parentName = kv.Key.Substring(0, dot);
                        string childName  = kv.Key.Substring(dot + 1);

                        var parentFi = FindField(target.GetType(), parentName);
                        if (parentFi == null) { Warn(prefix, id, $"field '{parentName}' not found on {target.GetType().Name}"); continue; }

                        var parentObj = parentFi.GetValue(target);
                        if (parentObj == null) { Warn(prefix, id, $"'{parentName}' is null"); continue; }

                        var childFi = FindField(parentObj.GetType(), childName);
                        if (childFi == null) { Warn(prefix, id, $"field '{childName}' not found on {parentObj.GetType().Name}"); continue; }

                        SetField(childFi, parentObj, kv.Value, prefix, id);
                    }
                    else
                    {
                        var fi = FindField(target.GetType(), kv.Key);
                        if (fi == null) { Warn(prefix, id, $"field '{kv.Key}' not found on {target.GetType().Name}"); continue; }
                        SetField(fi, target, kv.Value, prefix, id);
                    }

                    Plugin.Log.LogDebug($"{prefix} {id}.{kv.Key} = {kv.Value}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"{prefix} Error setting '{kv.Key}' on '{id}': {ex.Message}");
                }
            }
        }

        static FieldInfo FindField(Type type, string name)
        {
            while (type != null && type != typeof(object))
            {
                var fi = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) return fi;
                type = type.BaseType;
            }
            return null;
        }

        static void SetField(FieldInfo fi, object target, JToken value, string prefix, string id)
        {
            Type ft = fi.FieldType;
            if      (ft == typeof(float))   fi.SetValue(target, value.ToObject<float>());
            else if (ft == typeof(double))  fi.SetValue(target, value.ToObject<double>());
            else if (ft == typeof(int))     fi.SetValue(target, value.ToObject<int>());
            else if (ft == typeof(long))    fi.SetValue(target, value.ToObject<long>());
            else if (ft == typeof(bool))    fi.SetValue(target, value.ToObject<bool>());
            else if (ft == typeof(float?))  fi.SetValue(target, value.Type == JTokenType.Null ? (float?)null  : value.ToObject<float>());
            else if (ft == typeof(double?)) fi.SetValue(target, value.Type == JTokenType.Null ? (double?)null : value.ToObject<double>());
            else Warn(prefix, id, $"unsupported field type '{ft.Name}' for '{fi.Name}'");
        }

        static void Warn(string prefix, string id, string msg) =>
            Plugin.Log.LogWarning($"{prefix} {id}: {msg}");
    }
}
