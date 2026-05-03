using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Data.ScriptableObject;
using Game.ObjectInfoDataScripts;
using Game.ObjectInfoDataScripts.CustomFacilitiesAndModules;
using Game.UI.Windows.Elements.ObjectInfoElements;
using Game.UI.Windows.Elements.SpaceCraftConstructElements;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Patches fields on ScriptableObject types at runtime via reflection.
    /// Changes are global (shared objects) — affect all companies.
    ///
    /// ── FACILITY FIELDS (facilities.json) ────────────────────────────────────────
    ///   Simple fields (patching existing OR declaring new):
    ///   maintenanceCostPerDay                  float   daily upkeep cost
    ///   energyConsumption                      double  energy draw per enabled unit per day
    ///   needWorkersToWork                      int     crew required per enabled unit
    ///   specialAbilityParameter                float   context-dependent: mining rate, crew capacity, etc.
    ///   upkeepStackingValue                    float   additional upkeep per stacked unit
    ///   energyProductionData.energyProduction  double  energy output per day
    ///   resourceExplorationBonus               float   (GroundFacilityDescriptor only) exploration power bonus
    ///   timeToBuildInDays                      float   construction time in in-game days
    ///   constructionEquipmentCountIsRequired   bool    whether a Construction Equipment facility is required to build
    ///   price.buildCost                        double  money cost to build (dot notation)
    ///   blockStacking                          bool    true = keep separate facility instances, false = merge/stack
    ///   facilityItemClass                      string   runtime facility class name for new facilities (e.g. LabFacility)
    ///
    ///   Complex fields (object graphs, handled separately):
    ///   buildResources   { "resource_id": amount }          physical resources required to build
    ///   resourcesToMine  ["resource_id", ...]               resources a Mining facility can extract
    ///   refinerInput     { "resource_id": ratePerDay }      resources consumed per day (Refiner ability)
    ///   refinerOutput    { "resource_id": ratePerDay }      resources produced per day (Refiner ability)
    ///   byproducts       [{"resource":"id","rate":1.0,"state":"Solid"}]  side outputs (Solid/Liquid/Gas/Underground)
    ///   labBonusToResearchInPerHour  int                    flat research points per hour from a Lab facility
    ///   labResearchSubTypeId         string                 optional ResearchSubType ID filter for the lab
    ///   labIdToBonus                 ["id"|"All", ...]     optional exact research IDs the lab applies to
    ///
    /// ── SPACECRAFT FIELDS (spacecraft.json) ──────────────────────────────────────
    ///   cargoCapacity / fuelCapacity / mass / thrust / exhaustV / maxLifeSupport
    ///   (remapped to hull base fields for completed-design ships)
    ///   availableDeltaV / maximumAcceleration / maintenanceCostPerDay / maxPayload
    ///   reusability / solarParameter / constanceAcceleration
    ///   engineType       EEngineType enum — e.g. Chemical, Nuclear, Ion, Electric, Solar
    ///   orbitSC          bool   — true = orbital only (cannot land), false = can land on surface
    ///   solarSC          bool   — solar sail spacecraft
    ///   isLocked         bool   — hidden from construction UI
    ///   needLaunchVehicleToGoToMoon / buildOnlyLowOrbit / canByBuildByUser
    ///   lowOrbitContainer / magneticCatapult / isInterstellarShip / asteroidPullingShip
    ///   timeToBuildInDays float  — construction time in in-game days
    ///   buildCost        double — money cost to build
    ///   buildResources   { "resource_id": amount } — physical resources required
    ///
    /// ── LAUNCH VEHICLE FIELDS (launch_vehicles.json) ─────────────────────────────
    ///   maxPayload / maxFuelLoad / costLaunch / exhaustV / reusability / maintenanceCostPerDay / canSendHuman
    ///   isLocked / forCycleMission / fakeForFacility / canBuyMaxPayload
    ///   special  ESpecial enum — None or SeaDragon
    ///
    /// ── RESEARCH FIELDS (research.json) ──────────────────────────────────────────
    ///   workHourToComplete / isLocked / isLockedForUI
    ///   requirementsResearch  ["research_id", ...]          prerequisite research that must be completed first
    ///   unlocks               [{action, id}, ...]           items this research unlocks on completion
    ///   replaceUnlocks        bool                          true = replace the entire unlock set, false = append to existing
    ///                           action: UnlockFacility | UnlockSpacecraftType | UnlockVehicleType | UnlockResearch
    ///                           id: the string ID of the item to unlock
    /// </summary>
    internal static class ScriptableObjectPatcher
    {
        // These keys need special handling for spacecraft/LV — skip in generic patcher.
        static readonly HashSet<string> _complexPriceKeys = new HashSet<string>
        {
            "buildCost", "buildResources",
        };

        // Creation-only keys that have no matching field on SpacecraftType/LaunchVehicleType.
        // Filtered out when patching existing entries so ApplyFields doesn't log spurious warnings.
        static readonly HashSet<string> _complexVehicleKeys = new HashSet<string>
        {
            "buildCost", "buildResources",
            "name", "description", "icon", "iconRef", "fuelType",
        };

        // These keys are handled by ApplyFacilityComplexFields — skip them in the generic patcher
        // to avoid "unsupported type" warnings.
        static readonly HashSet<string> _complexFacilityKeys = new HashSet<string>
        {
            "buildResources", "resourcesToMine", "refinerInput", "refinerOutput", "byproducts",
            "labBonusToResearchInPerHour", "labResearchSubTypeId", "labIdToBonus",
            // Creation-only keys that aren't real field names on the descriptor
            "icon", "iconRef", "name", "description", "capabilities", "facilityType", "possiblePlacement",
            "specialAbility", "energyProduction", "solarPanels", "windPower", "geothermal", "buildCost", "facilityItemClass",
        };

        static readonly HashSet<string> _complexResourceKeys = new HashSet<string>
        {
            "icon", "iconRef", "name", "description", "showInMarket", "cloneFrom",
            "terraformationInfo", "toxicityCurve",
        };

        public static void RunResources(Dictionary<string, Dictionary<string, JToken>> config, string modDir)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[ResourcePatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, created = 0, skipped = 0;
            foreach (var kv in config)
            {
                if (kv.Key.StartsWith("_")) continue;

                var resource = allSO.AllResourceDefinitions.GetByID(kv.Key);
                if (resource == null)
                {
                    try
                    {
                        ResourceCreator.CreateAndInjectResource(kv.Key, kv.Value, modDir);
                        created++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[ResourcePatcher] Failed to create '{kv.Key}': {ex}");
                        skipped++;
                    }
                    continue;
                }

                var simpleFields = kv.Value
                    .Where(f => !_complexResourceKeys.Contains(f.Key))
                    .ToDictionary(f => f.Key, f => f.Value);

                ApplyFields(resource, simpleFields, "[ResourcePatcher]", kv.Key);
                ResourceCreator.ApplyComplexFields(resource, kv.Value, kv.Key, modDir);
                patched++;
            }

            ResourceCreator.RefreshAllResourceLists(allSO.AllResourceDefinitions);
            if (patched > 0 || created > 0)
                Plugin.Log.LogInfo($"[ResourcePatcher] Done — patched: {patched}, created: {created}, skipped: {skipped}");
        }

        public static void RunFacilities(Dictionary<string, Dictionary<string, JToken>> config, string modDir)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[FacilityPatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, created = 0, skipped = 0;
            foreach (var kv in config)
            {
                if (kv.Key.StartsWith("_")) continue;

                var descriptor = allSO.AllFacility.GetByID(kv.Key);
                if (descriptor == null)
                {
                    try
                    {
                        FacilityCreator.CreateAndInjectFacility(kv.Key, kv.Value, modDir);
                        created++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[FacilityPatcher] Failed to create '{kv.Key}': {ex}");
                        skipped++;
                    }
                    continue;
                }

                // Patch existing descriptor
                var simpleFields = kv.Value
                    .Where(f => !_complexFacilityKeys.Contains(f.Key))
                    .ToDictionary(f => f.Key, f => f.Value);

                ApplyFields(descriptor, simpleFields, "[FacilityPatcher]", kv.Key);
                ApplyFacilityComplexFields(descriptor, kv.Value, kv.Key);
                patched++;
            }
            if (patched > 0 || created > 0)
                Plugin.Log.LogInfo($"[FacilityPatcher] Done — patched: {patched}, created: {created}, skipped: {skipped}");
        }

        // ── Complex facility field handler ────────────────────────────────────────

        internal static void ApplyFacilityComplexFields(FacilityBaseDescriptor descriptor, Dictionary<string, JToken> fields, string id)
        {
            const string prefix = "[FacilityPatcher]";
            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            JToken tok;

            if (fields.ContainsKey("icon") || fields.ContainsKey("iconRef"))
            {
                var sprite = FacilityCreator.ResolveConfiguredSprite(fields, null, id, descriptor.Sprite);
                if (sprite != null)
                {
                    var spriteFi = FindField(descriptor.GetType(), "sprite");
                    spriteFi?.SetValue(descriptor, sprite);
                }
            }

            if (fields.ContainsKey("name") || fields.ContainsKey("description") || fields.ContainsKey("capabilities"))
            {
                string name = FacilityCreator.GetVal<string>(fields, "name", null);
                string description = FacilityCreator.GetVal<string>(fields, "description", null);
                string capabilities = FacilityCreator.GetVal<string>(fields, "capabilities", null);
                if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(description) || !string.IsNullOrEmpty(capabilities))
                    FacilityCreator.InjectTranslations(id, name, description, capabilities);
            }

            // ── buildResources: { "resource_id": amount } ─────────────────────────
            if (fields.TryGetValue("buildResources", out tok) && tok.Type == JTokenType.Object)
            {
                var priceFi = FindField(descriptor.GetType(), "price");
                if (priceFi != null)
                {
                    double buildCost = ((ResourcePrice)priceFi.GetValue(descriptor))?.BuildCost ?? 0.0;
                    var list = new List<ResourcePriceOne>();
                    foreach (var kv in (JObject)tok)
                    {
                        var res = allSO.AllResourceDefinitions.GetByID(kv.Key);
                        if (res == null) { Warn(prefix, id, $"unknown resource '{kv.Key}' in buildResources"); continue; }
                        list.Add(new ResourcePriceOne(res, kv.Value.Value<double>()));
                    }
                    priceFi.SetValue(descriptor, new ResourcePrice(list, buildCost));
                    Plugin.Log.LogDebug($"{prefix} {id}.buildResources = {list.Count} resources");
                }
            }

            // ── resourcesToMine: ["resource_id", ...] ─────────────────────────────
            if (fields.TryGetValue("resourcesToMine", out tok) && tok.Type == JTokenType.Array)
            {
                var set = new HashSet<ResourceDefinition>();
                foreach (var item in (JArray)tok)
                {
                    var res = allSO.AllResourceDefinitions.GetByID(item.Value<string>());
                    if (res == null) { Warn(prefix, id, $"unknown resource '{item}' in resourcesToMine"); continue; }
                    set.Add(res);
                }
                var fi = FindField(descriptor.GetType(), "resourcesToMine");
                if (fi != null) fi.SetValue(descriptor, set);
                Plugin.Log.LogDebug($"{prefix} {id}.resourcesToMine = {set.Count} resources");
            }

            // ── refinerInput / refinerOutput: { "resource_id": ratePerDay } ───────
            if (fields.ContainsKey("refinerInput") || fields.ContainsKey("refinerOutput"))
            {
                try { ApplyRefinerData(descriptor, fields, id, allSO, prefix); }
                catch (Exception ex) { Plugin.Log.LogError($"{prefix} {id}: refiner setup failed — {ex.Message}"); }
            }

            // ── byproducts: [{"resource":"id","rate":1.0,"state":"Solid"}, ...] ───
            // state values: Solid=0, Liquid=1, Gas=2, Underground=4
            if (fields.TryGetValue("byproducts", out tok) && tok.Type == JTokenType.Array)
            {
                var byproductFi = FindField(descriptor.GetType(), "byproducts");
                if (byproductFi != null)
                {
                    var stateFi = typeof(FacilityBaseDescriptor.Byproduct).GetField("state");
                    var bpList = new List<FacilityBaseDescriptor.Byproduct>();
                    foreach (var item in (JArray)tok)
                    {
                        string resId = item["resource"]?.Value<string>();
                        var res = allSO.AllResourceDefinitions.GetByID(resId);
                        if (res == null) { Warn(prefix, id, $"unknown resource '{resId}' in byproducts"); continue; }
                        var bp = new FacilityBaseDescriptor.Byproduct();
                        bp.resource = res;
                        bp.rate = item["rate"]?.Value<double>() ?? 0.0;
                        if (stateFi != null)
                        {
                            string stateStr = item["state"]?.Value<string>() ?? "Solid";
                            try { stateFi.SetValue(bp, Enum.Parse(stateFi.FieldType, stateStr)); }
                            catch { Warn(prefix, id, $"unknown byproduct state '{stateStr}' (use Solid/Liquid/Gas/Underground)"); }
                        }
                        bpList.Add(bp);
                    }
                    byproductFi.SetValue(descriptor, bpList.ToArray());
                    Plugin.Log.LogDebug($"{prefix} {id}.byproducts = {bpList.Count} entries");
                }
            }

            // labData / research output
            if (descriptor is GroundFacilityDescriptor groundDescriptor &&
                (fields.ContainsKey("labBonusToResearchInPerHour") || fields.ContainsKey("labResearchSubTypeId") || fields.ContainsKey("labIdToBonus")))
            {
                if (groundDescriptor.labData == null)
                    groundDescriptor.labData = new LabData();

                if (groundDescriptor.labData.idResearchSubType == null)
                    groundDescriptor.labData.idResearchSubType = string.Empty;
                if (groundDescriptor.labData.idToBonus == null)
                    groundDescriptor.labData.idToBonus = Array.Empty<string>();

                if (fields.TryGetValue("labBonusToResearchInPerHour", out tok) && tok.Type != JTokenType.Null)
                    groundDescriptor.labData.bonusToResearchInPerHour = tok.Value<int>();

                if (fields.TryGetValue("labResearchSubTypeId", out tok))
                    groundDescriptor.labData.idResearchSubType = tok.Type == JTokenType.Null ? string.Empty : (tok.Value<string>() ?? string.Empty);

                if (fields.TryGetValue("labIdToBonus", out tok) && tok.Type == JTokenType.Array)
                    groundDescriptor.labData.idToBonus = ((JArray)tok).Select(x => x.Value<string>()).Where(x => !string.IsNullOrEmpty(x)).ToArray();

                Plugin.Log.LogDebug($"{prefix} {id}.labData set");
            }

            if (fields.TryGetValue("facilityItemClass", out tok) && tok.Type != JTokenType.Null)
            {
                string className = tok.Value<string>();
                if (string.Equals(className, "LabFacility", StringComparison.OrdinalIgnoreCase))
                {
                    var fi = FindField(descriptor.GetType(), "facilityItemClass");
                    fi?.SetValue(descriptor, typeof(LabFacility));
                }
                else
                {
                    Warn(prefix, id, $"unsupported facilityItemClass '{className}'");
                }
            }
            else if ((descriptor.specialAbilityFacilityNew == ESpecialAbilityFacilityNew.Mining || fields.ContainsKey("resourcesToMine"))
                && descriptor is GroundFacilityDescriptor)
            {
                var fi = FindField(descriptor.GetType(), "facilityItemClass");
                fi?.SetValue(descriptor, typeof(MiningFacility));
            }
        }

        static void ApplyVehicleComplexFields(object vehicle, Dictionary<string, JToken> fields, string id, string modDir, string prefix)
        {
            if (fields.ContainsKey("icon") || fields.ContainsKey("iconRef"))
            {
                var currentSprite = FindField(vehicle.GetType(), "rocketBackGround")?.GetValue(vehicle) as Sprite;
                var sprite = FacilityCreator.ResolveConfiguredSprite(fields, modDir, id, currentSprite);
                if (sprite != null)
                    FindField(vehicle.GetType(), "rocketBackGround")?.SetValue(vehicle, sprite);
            }

            JToken tok;
            if (fields.TryGetValue("fuelType", out tok) && tok.Type != JTokenType.Null)
            {
                var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
                var fuel = allSO?.AllResourceDefinitions?.GetByID(tok.Value<string>());
                if (fuel == null)
                {
                    Warn(prefix, id, $"unknown resource '{tok.Value<string>()}' in fuelType");
                    return;
                }

                if (vehicle is SpacecraftType)
                {
                    FindField(vehicle.GetType(), "fuelType")?.SetValue(vehicle, fuel);
                }
                else if (vehicle is LaunchVehicleType)
                {
                    FindField(vehicle.GetType(), "fuelTypeOnStart")?.SetValue(vehicle, fuel);
                }
            }
        }

        static void ApplyRefinerData(FacilityBaseDescriptor descriptor, Dictionary<string, JToken> fields,
                                      string id, AllScriptableObjectManager allSO, string prefix)
        {
            var refinerFi = FindField(descriptor.GetType(), "refinerData");
            if (refinerFi == null) { Warn(prefix, id, "refinerData field not found"); return; }

            Type refinerType = refinerFi.FieldType;
            object refinerInst = refinerFi.GetValue(descriptor) ?? Activator.CreateInstance(refinerType);

            // RefineryDataItem is a nested type — find it
            Type itemType = refinerType.GetNestedType("RefineryDataItem")
                         ?? refinerType.GetNestedType("RefineryDataItem", BindingFlags.NonPublic);
            if (itemType == null)
            {
                foreach (var t in refinerType.Assembly.GetTypes())
                    if (t.Name == "RefineryDataItem") { itemType = t; break; }
            }
            if (itemType == null) { Warn(prefix, id, "could not find RefineryDataItem type"); return; }

            var resourceFi = itemType.GetField("resource");
            var rateFi     = itemType.GetField("ratePerDay");
            if (resourceFi == null || rateFi == null)
            {
                Warn(prefix, id, "RefineryDataItem missing 'resource' or 'ratePerDay' fields");
                return;
            }

            // Process Input and Output
            var pairs = new string[][] {
                new[] { "refinerInput",  "Input"  },
                new[] { "refinerOutput", "Output" },
            };
            foreach (var pair in pairs)
            {
                string jsonKey = pair[0], memberName = pair[1];
                JToken tok;
                if (!fields.TryGetValue(jsonKey, out tok) || tok.Type != JTokenType.Object) continue;

                MemberInfo storageMember = FindRefinerStorageMember(refinerType, memberName);
                if (storageMember == null)
                {
                    Warn(prefix, id, $"could not find storage member for RefinerData.{memberName}");
                    continue;
                }

                Type targetCollectionType = GetRefinerMemberType(storageMember);
                object collection = BuildRefinerCollection(targetCollectionType, itemType, (JObject)tok, allSO, jsonKey, prefix, id, resourceFi, rateFi);
                if (collection == null)
                {
                    Warn(prefix, id, $"could not build collection for RefinerData.{memberName}");
                    continue;
                }

                SetRefinerMemberValue(storageMember, refinerInst, collection, prefix, id, memberName);
                Plugin.Log.LogDebug($"{prefix} {id}.{jsonKey} set");
            }

            refinerFi.SetValue(descriptor, refinerInst);
        }

        static MemberInfo FindRefinerStorageMember(Type refinerType, string memberName)
        {
            string fieldName = char.ToLowerInvariant(memberName[0]) + memberName.Substring(1);
            return (MemberInfo)refinerType.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? refinerType.GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?? (MemberInfo)refinerType.GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        }

        static Type GetRefinerMemberType(MemberInfo member)
        {
            if (member is FieldInfo field) return field.FieldType;
            if (member is PropertyInfo prop) return prop.PropertyType;
            return null;
        }

        static object BuildRefinerCollection(Type targetCollectionType, Type itemType, JObject entries,
                                             AllScriptableObjectManager allSO, string jsonKey, string prefix, string id,
                                             FieldInfo resourceFi, FieldInfo rateFi)
        {
            var items = new List<object>();
            foreach (var kv in entries)
            {
                var res = allSO.AllResourceDefinitions.GetByID(kv.Key);
                if (res == null) { Warn(prefix, id, $"unknown resource '{kv.Key}' in {jsonKey}"); continue; }
                object item = Activator.CreateInstance(itemType);
                resourceFi.SetValue(item, res);
                rateFi.SetValue(item, kv.Value.Value<double>());
                items.Add(item);
            }

            if (targetCollectionType == null) return null;
            if (targetCollectionType.IsArray)
            {
                Array array = Array.CreateInstance(itemType, items.Count);
                for (int i = 0; i < items.Count; i++) array.SetValue(items[i], i);
                return array;
            }

            if (typeof(IList).IsAssignableFrom(targetCollectionType))
            {
                object list = Activator.CreateInstance(targetCollectionType);
                var addMethod = targetCollectionType.GetMethod("Add");
                if (addMethod == null) return list;
                foreach (object item in items) addMethod.Invoke(list, new[] { item });
                return list;
            }

            return null;
        }

        static void SetRefinerMemberValue(MemberInfo member, object instance, object value, string prefix, string id, string memberName)
        {
            if (member is FieldInfo field)
            {
                field.SetValue(instance, value);
                return;
            }

            if (member is PropertyInfo prop)
            {
                var setter = prop.GetSetMethod(nonPublic: true);
                if (setter != null)
                {
                    setter.Invoke(instance, new[] { value });
                    return;
                }
            }

            Warn(prefix, id, $"could not set RefinerData.{memberName}");
        }

        // ── Spacecraft ────────────────────────────────────────────────────────────

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

        public static void RunSpacecraft(Dictionary<string, Dictionary<string, JToken>> config, string modDir = null)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[SpacecraftPatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, created = 0, skipped = 0;
            foreach (var kv in config)
            {
                if (kv.Key.StartsWith("_")) continue;

                var scType = allSO.AllSpacecraftType.GetByID(kv.Key);
                if (scType == null)
                {
                    try
                    {
                        VehicleCreator.CreateAndInjectSpacecraft(kv.Key, kv.Value, modDir);
                        created++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[SpacecraftPatcher] Failed to create '{kv.Key}': {ex}");
                        skipped++;
                    }
                    continue;
                }

                object hullBoxed = _hullField?.GetValue(scType);
                bool hasCompletedHull = false;
                if (hullBoxed != null)
                {
                    var isCompletedFi = hullBoxed.GetType()
                        .GetField("isCompletedDesign", BindingFlags.NonPublic | BindingFlags.Instance);
                    hasCompletedHull = isCompletedFi != null && (bool)isCompletedFi.GetValue(hullBoxed);
                }

                var simpleFields = kv.Value.Where(f => !_complexVehicleKeys.Contains(f.Key))
                                          .ToDictionary(f => f.Key, f => f.Value);

                if (hasCompletedHull)
                {
                    Plugin.Log.LogDebug($"[SpacecraftPatcher] {kv.Key} has completed hull — remapping fields.");
                    var scFields   = new Dictionary<string, JToken>();
                    var hullFields = new Dictionary<string, JToken>();
                    foreach (var field in simpleFields)
                    {
                        if (_hullFieldMap.TryGetValue(field.Key, out var hullFieldName))
                            hullFields[hullFieldName] = field.Value;
                        else
                            scFields[field.Key] = field.Value;
                    }

                    bool priceOnHull = FindField(hullBoxed.GetType(), "priceBase") != null;
                    if (priceOnHull)
                        ApplyPriceFields(hullBoxed, kv.Value, "[SpacecraftPatcher]", kv.Key, allSO);
                    else
                        ApplyPriceFields(scType, kv.Value, "[SpacecraftPatcher]", kv.Key, allSO);

                    if (hullFields.Count > 0 || priceOnHull)
                    {
                        if (hullFields.Count > 0)
                            ApplyFields(hullBoxed, hullFields, "[SpacecraftPatcher]", kv.Key + ".hull");
                        _hullField.SetValue(scType, hullBoxed);
                    }
                    if (scFields.Count > 0)
                        ApplyFields(scType, scFields, "[SpacecraftPatcher]", kv.Key);
                }
                else
                {
                    ApplyPriceFields(scType, kv.Value, "[SpacecraftPatcher]", kv.Key, allSO);
                    ApplyFields(scType, simpleFields, "[SpacecraftPatcher]", kv.Key);
                }
                ApplyVehicleComplexFields(scType, kv.Value, kv.Key, modDir, "[SpacecraftPatcher]");
                patched++;
            }
            if (patched > 0 || created > 0)
                Plugin.Log.LogInfo($"[SpacecraftPatcher] Done — patched: {patched}, created: {created}, skipped: {skipped}");
        }

        // ── Launch vehicles ───────────────────────────────────────────────────────

        public static void RunLaunchVehicles(Dictionary<string, Dictionary<string, JToken>> config, string modDir = null)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[LaunchVehiclePatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, created = 0, skipped = 0;
            foreach (var kv in config)
            {
                if (kv.Key.StartsWith("_")) continue;

                var lv = allSO.AllLaunchVehicleType.GetByID(kv.Key);
                if (lv == null)
                {
                    try
                    {
                        VehicleCreator.CreateAndInjectLaunchVehicle(kv.Key, kv.Value, modDir);
                        created++;
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogError($"[LaunchVehiclePatcher] Failed to create '{kv.Key}': {ex}");
                        skipped++;
                    }
                    continue;
                }
                ApplyPriceFields(lv, kv.Value, "[LaunchVehiclePatcher]", kv.Key, allSO);
                var simpleFields = kv.Value.Where(f => !_complexVehicleKeys.Contains(f.Key))
                                          .ToDictionary(f => f.Key, f => f.Value);
                ApplyFields(lv, simpleFields, "[LaunchVehiclePatcher]", kv.Key);
                ApplyVehicleComplexFields(lv, kv.Value, kv.Key, modDir, "[LaunchVehiclePatcher]");
                patched++;
            }
            if (patched > 0 || created > 0)
                Plugin.Log.LogInfo($"[LaunchVehiclePatcher] Done — patched: {patched}, created: {created}, skipped: {skipped}");
        }

        // ── Research ──────────────────────────────────────────────────────────────

        static readonly HashSet<string> _complexResearchKeys = new HashSet<string>
        {
            "requirementsResearch", "unlocks", "newViewResearchTreeParent", "replaceUnlocks",
            // creation-only / sprite keys that aren't plain fields on ResearchDefinition
            "researchType", "researchSubType", "name", "description", "icon",
        };

        public static void RunResearch(Dictionary<string, Dictionary<string, JToken>> config, string modDir)
        {
            if (config.Count == 0) return;

            var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
            if (allSO == null) { Plugin.Log.LogError("[ResearchPatcher] AllScriptableObjectManager null"); return; }

            int patched = 0, created = 0, skipped = 0;
            foreach (var kv in config)
            {
                if (kv.Key.StartsWith("_")) continue;

                var rd = allSO.AllResearchDefinition.GetByID(kv.Key);
                if (rd == null)
                {
                    // New-research injection is temporarily disabled — skip unknown IDs.
                    Plugin.Log.LogDebug($"[ResearchPatcher] '{kv.Key}' not found in game data — new-entry injection is disabled, skipping.");
                    skipped++;
                    continue;
                }

                var simpleFields = kv.Value
                    .Where(f => !_complexResearchKeys.Contains(f.Key))
                    .ToDictionary(f => f.Key, f => f.Value);
                ApplyFields(rd, simpleFields, "[ResearchPatcher]", kv.Key);
                ApplyResearchComplexFields(rd, kv.Value, kv.Key, allSO, modDir);
                patched++;
            }
            if (patched > 0 || created > 0)
                Plugin.Log.LogInfo($"[ResearchPatcher] Done — patched: {patched}, created: {created}, skipped: {skipped}");

            if (created > 0)
                ResearchCreator.RebuildResearchTree();
        }

        internal static void ApplyResearchComplexFields(ResearchDefinition rd, Dictionary<string, JToken> fields, string id, AllScriptableObjectManager allSO, string modDir = null)
        {
            const string prefix = "[ResearchPatcher]";
            JToken tok;

            // ── requirementsResearch: ["research_id", ...] ────────────────────────
            if (fields.TryGetValue("requirementsResearch", out tok) && tok.Type == JTokenType.Array)
            {
                var list = new List<ResearchDefinition>();
                foreach (var item in (JArray)tok)
                {
                    var req = allSO.AllResearchDefinition.GetByID(item.Value<string>());
                    if (req == null) { Warn(prefix, id, $"unknown research '{item}' in requirementsResearch"); continue; }
                    list.Add(req);
                }
                var fi = FindField(rd.GetType(), "requirementsResearch");
                fi?.SetValue(rd, list.ToArray());
                Plugin.Log.LogDebug($"{prefix} {id}.requirementsResearch = {list.Count} entries");
            }

            // ── newViewResearchTreeParent: "research_id" ──────────────────────────
            if (fields.TryGetValue("newViewResearchTreeParent", out tok))
            {
                string parentId = tok.Type == JTokenType.Null ? null : tok.Value<string>();
                ResearchDefinition parent = null;
                if (!string.IsNullOrEmpty(parentId))
                {
                    parent = allSO.AllResearchDefinition.GetByID(parentId);
                    if (parent == null) Warn(prefix, id, $"unknown newViewResearchTreeParent '{parentId}'");
                }
                FindField(rd.GetType(), "newViewResearchTreeParent")?.SetValue(rd, parent);
                Plugin.Log.LogDebug($"{prefix} {id}.newViewResearchTreeParent = {parentId ?? "null"}");
            }

            // ── name / description → translations ────────────────────────────────
            JToken nameTok, descTok;
            fields.TryGetValue("name",        out nameTok);
            fields.TryGetValue("description", out descTok);
            string nameStr = nameTok?.Type == JTokenType.Null ? null : nameTok?.Value<string>();
            string descStr = descTok?.Type == JTokenType.Null ? null : descTok?.Value<string>();
            if (!string.IsNullOrEmpty(nameStr) || !string.IsNullOrEmpty(descStr))
                ResearchCreator.InjectResearchTranslations(id, nameStr, descStr);

            // ── icon: load sprite and assign ─────────────────────────────────────
            if (fields.TryGetValue("icon", out tok) && tok.Type != JTokenType.Null && !string.IsNullOrEmpty(modDir))
            {
                string iconRelPath = tok.Value<string>();
                if (!string.IsNullOrEmpty(iconRelPath))
                {
                    string iconFull = System.IO.Path.IsPathRooted(iconRelPath)
                        ? iconRelPath
                        : System.IO.Path.Combine(modDir, iconRelPath);
                    var sprite = FacilityCreator.LoadSprite(iconFull, id);
                    if (sprite != null) rd.Sprite = sprite;
                }
            }

            // ── unlocks: [{action, id}, ...] ──────────────────────────────────────
            if (fields.TryGetValue("unlocks", out tok) && tok.Type == JTokenType.Array)
            {
                Type udType = typeof(ResearchDefinition).Assembly.GetType("Game.CompanyScripts.UnlockData");
                if (udType == null) { Warn(prefix, id, "UnlockData type not found in assembly"); return; }

                var actionFi = udType.GetField("actionUnlock", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var param1Fi = udType.GetField("parameter1",   BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                Type actionEnumType = actionFi?.FieldType;

                var udObjects = new List<object>();
                foreach (var item in (JArray)tok)
                {
                    string actionStr = item["action"]?.Value<string>() ?? "UnlockFacility";
                    string targetId  = item["id"]?.Value<string>();
                    if (string.IsNullOrEmpty(targetId)) { Warn(prefix, id, "unlock entry missing 'id'"); continue; }

                    object ud = Activator.CreateInstance(udType);
                    param1Fi?.SetValue(ud, targetId);
                    if (actionEnumType != null && actionFi != null)
                    {
                        try { actionFi.SetValue(ud, Enum.Parse(actionEnumType, actionStr)); }
                        catch { Warn(prefix, id, $"unknown unlock action '{actionStr}'"); }
                    }
                    udObjects.Add(ud);
                }

                bool replaceUnlocks = fields.TryGetValue("replaceUnlocks", out var replaceTok)
                    && replaceTok.Type != JTokenType.Null
                    && replaceTok.Value<bool>();

                var finalUnlocks = replaceUnlocks
                    ? udObjects
                    : MergeResearchUnlocks(rd, udObjects, udType);

                SetResearchUnlocks(rd, finalUnlocks, udType, actionFi);
                Plugin.Log.LogDebug($"{prefix} {id}.unlocks = {finalUnlocks.Count} entries (replace={replaceUnlocks})");
            }
        }

        static List<object> MergeResearchUnlocks(ResearchDefinition rd, List<object> additions, Type udType)
        {
            var merged = new List<object>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var existing in EnumerateResearchUnlocks(rd))
            {
                if (existing == null || !udType.IsInstanceOfType(existing)) continue;
                string key = BuildResearchUnlockKey(existing);
                if (seen.Add(key))
                    merged.Add(existing);
            }

            foreach (var added in additions)
            {
                if (added == null) continue;
                string key = BuildResearchUnlockKey(added);
                if (seen.Add(key))
                    merged.Add(added);
            }

            return merged;
        }

        static IEnumerable<object> EnumerateResearchUnlocks(ResearchDefinition rd)
        {
            if (rd.unlockData != null)
                yield return rd.unlockData;

            if (rd.unlockDataList == null)
                yield break;

            foreach (var unlock in rd.unlockDataList)
                if (unlock != null)
                    yield return unlock;
        }

        static void SetResearchUnlocks(ResearchDefinition rd, List<object> unlocks, Type udType, FieldInfo actionFi)
        {
            var unlockDataFi = FindField(rd.GetType(), "unlockData");
            var unlockDataListFi = FindField(rd.GetType(), "unlockDataList");

            if (unlocks == null || unlocks.Count == 0)
            {
                object emptyUnlock = Activator.CreateInstance(udType);
                if (actionFi != null)
                    actionFi.SetValue(emptyUnlock, Enum.ToObject(actionFi.FieldType, 0));

                unlockDataFi?.SetValue(rd, emptyUnlock);
                unlockDataListFi?.SetValue(rd, Array.CreateInstance(udType, 0));
                return;
            }

            unlockDataFi?.SetValue(rd, unlocks[0]);

            Array extras = Array.CreateInstance(udType, Math.Max(0, unlocks.Count - 1));
            for (int i = 1; i < unlocks.Count; i++)
                extras.SetValue(unlocks[i], i - 1);
            unlockDataListFi?.SetValue(rd, extras);
        }

        static string BuildResearchUnlockKey(object unlock)
        {
            if (unlock == null) return string.Empty;

            Type type = unlock.GetType();
            string action = type.GetField("actionUnlock", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;
            string parameter1 = type.GetField("parameter1", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;
            string parameter2 = type.GetField("parameter2", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;
            string bonus = type.GetField("bonus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;
            string bonusParameter = type.GetField("bonusParameter", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;
            string unlockUi = type.GetField("unlockUIElement", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;
            string unlockEndGame = type.GetField("unlockEndGame", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(unlock)?.ToString() ?? string.Empty;

            return string.Join("|", action, parameter1, parameter2, bonus, bonusParameter, unlockUi, unlockEndGame);
        }

        // ── Price field handler (shared by spacecraft + launch vehicles) ─────────

        static void ApplyPriceFields(object target, Dictionary<string, JToken> fields, string prefix, string id, AllScriptableObjectManager allSO)
        {
            bool hasBuildCost      = fields.ContainsKey("buildCost");
            bool hasBuildResources = fields.ContainsKey("buildResources");
            if (!hasBuildCost && !hasBuildResources) return;

            var priceFi = FindField(target.GetType(), "priceBase");
            if (priceFi == null) { Warn(prefix, id, "priceBase field not found"); return; }

            var existing = priceFi.GetValue(target) as ResourcePrice;
            double buildCost = hasBuildCost
                ? fields["buildCost"].Value<double>()
                : existing?.BuildCost ?? 0.0;

            List<ResourcePriceOne> resources;
            if (hasBuildResources && fields["buildResources"].Type == JTokenType.Object)
            {
                resources = new List<ResourcePriceOne>();
                foreach (var kv in (JObject)fields["buildResources"])
                {
                    var res = allSO.AllResourceDefinitions.GetByID(kv.Key);
                    if (res == null) { Warn(prefix, id, $"unknown resource '{kv.Key}' in buildResources"); continue; }
                    resources.Add(new ResourcePriceOne(res, kv.Value.Value<double>()));
                }
            }
            else
            {
                resources = existing?.ListResources ?? new List<ResourcePriceOne>();
            }

            priceFi.SetValue(target, new ResourcePrice(resources, buildCost));
        }

        // ── Generic field setter ──────────────────────────────────────────────────

        static void ApplyFields(object target, Dictionary<string, JToken> fields, string prefix, string id)
        {
            foreach (var kv in fields)
            {
                try
                {
                    int dot = kv.Key.IndexOf('.');
                    if (dot >= 0)
                    {
                        string parentName = kv.Key.Substring(0, dot);
                        string childName  = kv.Key.Substring(dot + 1);

                        var parentFi = FindField(target.GetType(), parentName);
                        if (parentFi == null) { Warn(prefix, id, $"field '{parentName}' not found on {target.GetType().Name}"); continue; }

                        var parentObj = parentFi.GetValue(target);
                        if (parentObj == null) { Warn(prefix, id, $"'{parentName}' is null"); continue; }

                        var childFi = FindField(parentObj.GetType(), childName);
                        if (childFi == null) { Warn(prefix, id, $"field '{childName}' not found on {parentObj.GetType().Name}"); continue; }

                        SetPrimitive(childFi, parentObj, kv.Value, prefix, id);
                    }
                    else
                    {
                        var fi = FindField(target.GetType(), kv.Key);
                        if (fi == null) { Warn(prefix, id, $"field '{kv.Key}' not found on {target.GetType().Name}"); continue; }
                        SetPrimitive(fi, target, kv.Value, prefix, id);
                    }

                    Plugin.Log.LogDebug($"{prefix} {id}.{kv.Key} = {kv.Value}");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogError($"{prefix} Error setting '{kv.Key}' on '{id}': {ex.Message}");
                }
            }
        }

        internal static FieldInfo FindField(Type type, string name)
        {
            while (type != null && type != typeof(object))
            {
                var fi = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (fi != null) return fi;
                type = type.BaseType;
            }
            return null;
        }

        static void SetPrimitive(FieldInfo fi, object target, JToken value, string prefix, string id)
        {
            Type ft = fi.FieldType;
            if      (ft == typeof(float))   fi.SetValue(target, value.ToObject<float>());
            else if (ft == typeof(double))  fi.SetValue(target, value.ToObject<double>());
            else if (ft == typeof(int))     fi.SetValue(target, value.ToObject<int>());
            else if (ft == typeof(long))    fi.SetValue(target, value.ToObject<long>());
            else if (ft == typeof(bool))    fi.SetValue(target, value.ToObject<bool>());
            else if (ft == typeof(float?))  fi.SetValue(target, value.Type == JTokenType.Null ? (float?)null  : value.ToObject<float>());
            else if (ft == typeof(double?)) fi.SetValue(target, value.Type == JTokenType.Null ? (double?)null : value.ToObject<double>());
            else if (ft.IsEnum)
            {
                string strVal = value.Value<string>();
                try   { fi.SetValue(target, Enum.Parse(ft, strVal, ignoreCase: true)); }
                catch { Warn(prefix, id, $"unknown value '{strVal}' for enum '{ft.Name}' on field '{fi.Name}'"); }
            }
            else Warn(prefix, id, $"unsupported field type '{ft.Name}' for '{fi.Name}'");
        }

        static void Warn(string prefix, string id, string msg) =>
            Plugin.Log.LogWarning($"{prefix} {id}: {msg}");
    }
}
