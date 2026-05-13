using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using CustomUpdate;
using Data.ScriptableObject;
using Extensions;
using Game;
using Game.Info;
using Game.UI;
using Game.UI.Windows.Elements.ChoseFacilityElements;
using Game.UI.Windows.Windows;
using Game.VisualizationScripts;
using HarmonyLib;
using Manager;
using ScriptableObjectScripts;
using TMPro;
using UnityEngine;

namespace Teddit
{
    /// <summary>
    /// Patches ResearchTree.Show() so we can inject mod-created research nodes into the
    /// already-built tree the first time the player opens the window.
    ///
    /// CreateUI() is never called at runtime (the tree is built via scene-level events
    /// before our patcher runs), so patching Show() is the only reliable hook we have.
    /// SpawnPendingEntries rebuilds just the affected branch(es) so the new entries appear.
    /// </summary>
    [HarmonyPatch]
    static class PatchResearchTreeCreateUI
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Game.UI.Windows.Windows.ResearchTree.ResearchTree");
            if (t == null)
            {
                Plugin.Log.LogWarning("[Patch] ResearchTree type not found — Show patch skipped.");
                return null;
            }
            return AccessTools.Method(t, "CreateUI");
        }

        static void Prefix(object __instance)
        {
            ResearchCreator._researchTreeInstance = __instance;
            // Research node injection disabled.
            // New-research injection is temporarily disabled — do nothing here.
            // ResearchCreator._researchTreeInstance = __instance;
            // ResearchCreator.SpawnPendingEntries(__instance);
        }

        static void Postfix(object __instance)
        {
            ResearchCreator._researchTreeInstance = __instance;
            // Research node injection disabled.
        }
    }

    [HarmonyPatch]
    static class PatchResearchTreeShowAppend
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Game.UI.Windows.Windows.ResearchTree.ResearchTree");
            if (t == null)
                return null;
            return AccessTools.Method(t, "Show");
        }

        static void Postfix(object __instance)
        {
            ResearchCreator._researchTreeInstance = __instance;
            // Research node injection disabled.
        }
    }

    /// <summary>
    /// Hooks into ObjectInfoManager.Update() — a scene MonoBehaviour whose Update() DOES fire.
    /// (BepInEx plugin MonoBehaviour.Update() is never called in this game; scene objects are fine.)
    /// Waits for solarSystemLoadEventWas == true, meaning solarSystemLoadEvent.Invoke() has
    /// completed and all ObjectInfo.StartStuff() handlers have run — deposits are ready.
    ///
    /// Multi-mod loading order:
    ///   1. Root plugin directory (backwards-compatible location for config files)
    ///   2. Each subdirectory inside plugins/Teddit/mods/, sorted alphabetically
    ///
    /// Each mod directory may contain any subset of:
    ///   facilities.yaml, spacecraft.yaml, launch_vehicles.yaml, research.yaml, deposits.yaml
    /// Missing files are silently skipped. Patches from later mods override earlier ones
    /// for the same object-ID + field combinations.
    /// </summary>
    [HarmonyPatch(typeof(ObjectInfoManager), "Update")]
    static class PatchObjectInfoManagerUpdate
    {
        static bool _ran = false;

        static readonly FieldInfo _eventWasField = typeof(ObjectInfoManager)
            .GetField("solarSystemLoadEventWas",
                      BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(ObjectInfoManager __instance)
        {
            if (_ran) return;
            if (_eventWasField == null)
            {
                Plugin.Log.LogError("[Patch] solarSystemLoadEventWas field not found — check decompile.");
                _ran = true;
                return;
            }

            bool eventFired = (bool)_eventWasField.GetValue(__instance);
            if (!eventFired) return;

            _ran = true;
            Plugin.Log.LogInfo($"[Teddit] solarSystemLoadEvent done — {__instance.allObjectInfos?.Count} bodies.");

            // ── Data dump (disable once you have the data) ────────────────────
            if (Plugin.DumpOnLoad)
            {
                try { DataDumper.Run(Path.Combine(Plugin.PluginDir, "dump")); }
                catch (Exception ex) { Plugin.Log.LogError($"[DataDumper] {ex}"); }
            }

            var rootSettings = RootPatchSettings.Load(Plugin.PluginDir);

            // ── Collect mod directories in load order ─────────────────────────
            var dirs = new List<string> { Plugin.PluginDir };

            string modsFolder = Path.Combine(Plugin.PluginDir, "mods");
            if (Directory.Exists(modsFolder))
            {
                var subDirs = Directory.GetDirectories(modsFolder);
                Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
                dirs.AddRange(subDirs);
            }


            // ── Apply each mod directory in order ─────────────────────────────
            LifeSupportPatcher.ResetConfig();

            foreach (var dir in dirs)
                ApplyModDir(dir, __instance, rootSettings);
        }

        static void ApplyModDir(string dir, ObjectInfoManager oi, RootPatchSettings rootSettings)
        {
            string label = Path.GetFileName(dir);

            if (rootSettings.ResourcesEnabled)
            {
                var resourcePatches = PatchConfig.Load(Path.Combine(dir, "resources.yaml"));
                try { ScriptableObjectPatcher.RunResources(resourcePatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[ResourcePatcher:{label}] {ex}"); }
            }

            if (rootSettings.CompaniesEnabled)
            {
                var companyPatches = PatchConfig.Load(Path.Combine(dir, "companies.yaml"));
                try { ScriptableObjectPatcher.RunCompanies(companyPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[CompanyPatcher:{label}] {ex}"); }
            }

            if (rootSettings.FacilitiesEnabled)
            {
                var facilityPatches = PatchConfig.Load(Path.Combine(dir, "facilities.yaml"));
                try { ScriptableObjectPatcher.RunFacilities(facilityPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[FacilityPatcher:{label}] {ex}"); }
            }

            if (rootSettings.SpacecraftEnabled)
            {
                var spacecraftPatches = PatchConfig.Load(Path.Combine(dir, "spacecraft.yaml"));
                try { ScriptableObjectPatcher.RunSpacecraft(spacecraftPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[SpacecraftPatcher:{label}] {ex}"); }
            }

            if (rootSettings.LaunchVehiclesEnabled)
            {
                var launchVehiclePatches = PatchConfig.Load(Path.Combine(dir, "launch_vehicles.yaml"));
                try { ScriptableObjectPatcher.RunLaunchVehicles(launchVehiclePatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[LaunchVehiclePatcher:{label}] {ex}"); }
            }

            if (rootSettings.ResearchEnabled)
            {
                var researchPatches = PatchConfig.Load(Path.Combine(dir, "research.yaml"));
                try { ScriptableObjectPatcher.RunResearch(researchPatches, dir); }
                catch (Exception ex) { Plugin.Log.LogError($"[ResearchPatcher:{label}] {ex}"); }
            }

            if (rootSettings.DepositsEnabled)
            {
                var depositConfig = DepositConfig.Load(Path.Combine(dir, "deposits.yaml"));
                try { DepositInjector.Run(depositConfig, oi); }
                catch (Exception ex) { Plugin.Log.LogError($"[DepositInjector:{label}] {ex}"); }
            }

            if (rootSettings.BodiesEnabled)
            {
                var bodyPatches = PatchConfig.Load(Path.Combine(dir, "bodies.yaml"));
                try { BodyPatcher.Run(bodyPatches, oi); }
                catch (Exception ex) { Plugin.Log.LogError($"[BodyPatcher:{label}] {ex}"); }
            }

            if (rootSettings.LifeSupportEnabled)
            {
                bool honorEnabled = string.Equals(dir, Plugin.PluginDir, StringComparison.OrdinalIgnoreCase);
                var lifeSupportConfig = LifeSupportConfig.Load(Path.Combine(dir, "life_support.yaml"), honorEnabled);
                try { LifeSupportPatcher.MergeConfig(lifeSupportConfig, label); }
                catch (Exception ex) { Plugin.Log.LogError($"[LifeSupportPatcher:{label}] {ex}"); }
            }

        }
    }

    [HarmonyPatch(typeof(Spacecraft), "SetCurrentlyOnThisObject")]
    static class PatchSpacecraftSetCurrentlyOnThisObjectNoOrbitBodies
    {
        static void Postfix(Spacecraft __instance)
        {
            if (__instance == null || __instance.spacecraftType == null)
                return;

            var current = __instance.CurrentlyOnThisObject;
            if (current == null || !string.Equals(current.objectTypes.ToString(), "Orbit", StringComparison.OrdinalIgnoreCase))
                return;

            var parent = current.parentObjectInfo;
            if (parent == null || !BodyPatcher.BodyHasRemovedOrbit(parent))
                return;

            try
            {
                Plugin.Log.LogInfo($"[BodyPatcher] Redirecting '{__instance.GetSpacecraftName()}' from hidden orbit '{current.ObjectName}' to '{parent.ObjectName}'.");
                __instance.SetCurrentlyOnThisObject(parent);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to redirect spacecraft '{__instance.GetSpacecraftName()}' off hidden orbit '{current.ObjectName}': {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "NeedVehicleToLaunch")]
    static class PatchObjectInfoNeedVehicleToLaunchRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo __instance, ref bool __result)
        {
            if (__instance == null || !BodyPatcher.BodyHasRemovedOrbit(__instance))
                return true;

            __result = false;
            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "GetLayerLabel")]
    static class PatchObjectInfoGetLayerLabelRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo __instance, ref LabelsManager.ELayerLabel __result)
        {
            if (__instance == null || !BodyPatcher.BodyHasRemovedOrbit(__instance))
                return true;

            if (__instance.parentObjectInfo != null)
            {
                __result = LabelsManager.ELayerLabel.Moon;
                return false;
            }

            __result = LabelsManager.ELayerLabel.Planet;
            return false;
        }
    }

    [HarmonyPatch(typeof(ObjectInfo), "CheckEarthMoonCase")]
    static class PatchObjectInfoCheckEarthMoonCaseRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo objectInfoStart, ObjectInfo objectInfoTarget, ref bool __result)
        {
            bool startRemoved = BodyPatcher.BodyHasRemovedOrbit(objectInfoStart);
            bool targetRemoved = BodyPatcher.BodyHasRemovedOrbit(objectInfoTarget);
            if (!startRemoved && !targetRemoved)
                return true;

            Plugin.Log.LogInfo(
                $"[BodyPatcher] CheckEarthMoonCase override: start={objectInfoStart?.ObjectName ?? "NULL"} removed={startRemoved} type={objectInfoStart?.objectTypes.ToString() ?? "NULL"} " +
                $"target={objectInfoTarget?.ObjectName ?? "NULL"} removed={targetRemoved} type={objectInfoTarget?.objectTypes.ToString() ?? "NULL"} => false");
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    static class PatchGameManagerCheckCanPlanMissionLoggingRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo TargetProperty = PMMissionParameterType?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(GameManager), "CheckCanPlanMission");
        }

        static void Prefix(object pmp)
        {
            if (pmp == null) return;
            var start = GetPlannerObjectInfo(StartProperty, pmp);
            var target = GetPlannerObjectInfo(TargetProperty, pmp);
            bool startRemoved = BodyPatcher.BodyHasRemovedOrbit(start);
            bool targetRemoved = BodyPatcher.BodyHasRemovedOrbit(target);
            if (!startRemoved && !targetRemoved)
                return;

            Plugin.Log.LogInfo(
                $"[BodyPatcher] Planner start: start={start?.ObjectName ?? "NULL"} removed={startRemoved} type={start?.objectTypes.ToString() ?? "NULL"} " +
                $"target={target?.ObjectName ?? "NULL"} removed={targetRemoved} type={target?.objectTypes.ToString() ?? "NULL"}");
        }

        static ObjectInfo GetPlannerObjectInfo(PropertyInfo property, object pmp)
        {
            try
            {
                return property?.GetValue(pmp) as ObjectInfo;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch]
    static class PatchPMMissionParameterChangeRealToCalculationTargetRemovedOrbitBodies
    {
        static readonly Type PMMissionParameterType = AccessTools.TypeByName("PMMissionParameter");
        static readonly PropertyInfo StartProperty = PMMissionParameterType?.GetProperty("Start", BindingFlags.Public | BindingFlags.Instance);
        static readonly PropertyInfo TargetProperty = PMMissionParameterType?.GetProperty("Target", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo StartCalculationField = PMMissionParameterType?.GetField("objectInfoStartCalculation", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo TargetCalculationField = PMMissionParameterType?.GetField("objectInfoTargetCalculation", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo CenterBodyField = PMMissionParameterType?.GetField("centerBodyLambertPorkchop", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo OrbitCaseField = PMMissionParameterType?.GetField("orbitCase", BindingFlags.NonPublic | BindingFlags.Instance);

        static MethodBase TargetMethod()
        {
            return AccessTools.Method(PMMissionParameterType, "ChangeRealToCalculationTarget");
        }

        static void Postfix(object __instance)
        {
            if (__instance == null || PMMissionParameterType == null)
                return;

            var start = GetPlannerObjectInfoSafe(StartProperty, __instance);
            var target = GetPlannerObjectInfoSafe(TargetProperty, __instance);
            bool startRemoved = BodyPatcher.BodyHasRemovedOrbit(start);
            bool targetRemoved = BodyPatcher.BodyHasRemovedOrbit(target);
            if (!startRemoved && !targetRemoved)
                return;

            var normalizedStart = NormalizeRemovedOrbitCalculationBody(start);
            var normalizedTarget = NormalizeRemovedOrbitCalculationBody(target);
            if (normalizedStart == null || normalizedTarget == null || normalizedStart.NBody == null || normalizedTarget.NBody == null)
            {
                Plugin.Log.LogWarning(
                    $"[BodyPatcher] PMMissionParameter normalize skipped: start={start?.ObjectName ?? "NULL"} normalizedStart={normalizedStart?.ObjectName ?? "NULL"} " +
                    $"target={target?.ObjectName ?? "NULL"} normalizedTarget={normalizedTarget?.ObjectName ?? "NULL"}");
                return;
            }

            var startOrbit = normalizedStart.NBody.gameObject != null ? normalizedStart.NBody.gameObject.GetComponent<OrbitUniversal>() : null;
            var targetOrbit = normalizedTarget.NBody.gameObject != null ? normalizedTarget.NBody.gameObject.GetComponent<OrbitUniversal>() : null;
            if (startOrbit == null || targetOrbit == null)
            {
                Plugin.Log.LogWarning(
                    $"[BodyPatcher] PMMissionParameter normalize missing orbit: startCalc={normalizedStart.ObjectName} startOrbit={(startOrbit != null)} " +
                    $"targetCalc={normalizedTarget.ObjectName} targetOrbit={(targetOrbit != null)}");
                return;
            }

            bool orbitCase = ReferenceEquals(normalizedStart, normalizedTarget);
            NBody centerBody = targetOrbit.centerNbody ?? startOrbit.centerNbody;

            StartCalculationField?.SetValue(__instance, normalizedStart);
            TargetCalculationField?.SetValue(__instance, normalizedTarget);
            CenterBodyField?.SetValue(__instance, centerBody);
            OrbitCaseField?.SetValue(__instance, orbitCase);

            Plugin.Log.LogInfo(
                $"[BodyPatcher] PMMissionParameter normalize: start={start?.ObjectName ?? "NULL"} removed={startRemoved} -> calcStart={normalizedStart.ObjectName} type={normalizedStart.objectTypes} center={startOrbit.centerNbody?.name ?? "NULL"}; " +
                $"target={target?.ObjectName ?? "NULL"} removed={targetRemoved} -> calcTarget={normalizedTarget.ObjectName} type={normalizedTarget.objectTypes} center={targetOrbit.centerNbody?.name ?? "NULL"}; orbitCase={orbitCase}");
        }

        static ObjectInfo NormalizeRemovedOrbitCalculationBody(ObjectInfo body)
        {
            if (body == null)
                return null;

            if (!BodyPatcher.BodyHasRemovedOrbit(body) || body.parentObjectInfo == null)
                return body;

            return body.parentObjectInfo;
        }

        static ObjectInfo GetPlannerObjectInfoSafe(PropertyInfo property, object pmp)
        {
            try
            {
                return property?.GetValue(pmp) as ObjectInfo;
            }
            catch
            {
                return null;
            }
        }
    }

    [HarmonyPatch(typeof(ObjectInfoGroups), "SetAsteroidCommetsIfNotHaveGroup")]
    static class PatchObjectInfoGroupsSetAsteroidCommetsIfNotHaveGroupRemovedOrbitBodies
    {
        static void Postfix(ObjectInfoGroups __instance)
        {
            if (__instance == null || __instance.objectInGroup == null || __instance.objectInGroup.Count == 0)
                return;

            for (int i = __instance.objectInGroup.Count - 1; i >= 0; i--)
            {
                var objectInfo = __instance.objectInGroup[i];
                if (!BodyPatcher.BodyHasRemovedOrbit(objectInfo))
                    continue;

                __instance.objectInGroup.RemoveAt(i);
                if (objectInfo != null && objectInfo.parentObjectInfoGropup == __instance)
                    objectInfo.parentObjectInfoGropup = null;

                Plugin.Log.LogInfo($"[BodyPatcher] Removed '{objectInfo?.ObjectName ?? "NULL"}' from asteroid object group for moon-like presentation.");
            }
        }
    }

    [HarmonyPatch(typeof(ChoseFacilityWindow), "FilterByObjectInfo")]
    static class PatchChoseFacilityWindowFilterByObjectInfoRemovedOrbitBodies
    {
        static bool Prefix(ObjectInfo objectInfo, ref object __state)
        {
            __state = null;
            if (objectInfo == null || !BodyPatcher.BodyHasRemovedOrbit(objectInfo))
                return true;

            var field = AccessTools.Field(typeof(ObjectInfo), "objectTypes");
            if (field == null)
                return true;

            try
            {
                __state = field.GetValue(objectInfo);
                var asteroidEnum = Enum.Parse(field.FieldType, "Asteroid", ignoreCase: true);
                field.SetValue(objectInfo, asteroidEnum);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to present '{objectInfo.ObjectName}' as asteroid-like in facility picker: {ex.Message}");
                __state = null;
            }

            return true;
        }

        static void Postfix(ObjectInfo objectInfo, object __state)
        {
            if (objectInfo == null || __state == null)
                return;

            var field = AccessTools.Field(typeof(ObjectInfo), "objectTypes");
            if (field == null)
                return;

            try
            {
                field.SetValue(objectInfo, __state);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[BodyPatcher] Failed to restore objectTypes after facility picker for '{objectInfo.ObjectName}': {ex.Message}");
            }
        }
    }

    /// <summary>
    /// New modded spacecraft borrow a baked donor prefab. The donor prefab's Spacecraft
    /// component often still has its original serialized spacecraftType reference, and the
    /// base game only rewrites that reference for non-base completed hull designs.
    ///
    /// For ordinary modded SpacecraftType entries, that leaves the built ship identifying
    /// as the donor in the object-info rocket list after construction completes.
    ///
    /// This postfix force-rebinds the spawned runtime object back to the requested
    /// SpacecraftType so built ships keep their modded identity.
    /// </summary>
    [HarmonyPatch(typeof(global::ShipManager), "ConstructShipOnPlanet",
        new Type[] { typeof(Game.Info.ObjectInfo), typeof(SpacecraftType), typeof(Data.SpacecraftConstructData), typeof(Game.Company) })]
    static class PatchShipManagerConstructShipOnPlanet
    {
        static readonly FieldInfo _ooiFacilityField = typeof(ObjectOnOrbit)
            .GetField("facilityBaseDescriptor", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _ooiSpacecraftField = typeof(ObjectOnOrbit)
            .GetField("spacecraftType", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(SpacecraftType spacecraftType, ref object __result)
        {
            if (spacecraftType == null || __result == null)
                return;

            var spacecraft = __result as Spacecraft;
            if (spacecraft == null)
                return;

            if (spacecraft.spacecraftType != spacecraftType)
            {
                Plugin.Log.LogInfo($"[VehicleFix] Rebinding spawned spacecraft from '{spacecraft.spacecraftType?.ID ?? "null"}' to '{spacecraftType.ID}'.");
                spacecraft.spacecraftType = spacecraftType;
            }

            try
            {
                var orbitMarkers = spacecraft.GetComponentsInChildren<ObjectOnOrbit>(true);
                foreach (var marker in orbitMarkers)
                {
                    _ooiFacilityField?.SetValue(marker, null);
                    _ooiSpacecraftField?.SetValue(marker, spacecraftType);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VehicleFix] Failed to retarget ObjectOnOrbit markers for '{spacecraftType.ID}': {ex.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(ResourceDefinition), "get_IconString")]
    static class PatchResourceDefinitionGetIconString
    {
        static void Postfix(ResourceDefinition __instance, ref string __result)
        {
            if (ResourceCreator.UsesNamedSprite(__instance))
                __result = ResourceCreator.GetIconString(__instance);
        }
    }

    [HarmonyPatch(typeof(ResourceDefinition), "get_IconWithLinkString")]
    static class PatchResourceDefinitionGetIconWithLinkString
    {
        static void Postfix(ResourceDefinition __instance, ref string __result)
        {
            if (ResourceCreator.UsesNamedSprite(__instance))
                __result = ResourceCreator.GetIconWithLinkString(__instance);
        }
    }

    [HarmonyPatch(typeof(MyIDScriptableObject), "GetText")]
    static class PatchMyIDScriptableObjectGetText
    {
        static void Postfix(MyIDScriptableObject __instance, bool longText, bool firstIcon, string _highLightColor, bool colorIcon, string _highLightColorSprite, bool addSpace, ref string __result)
        {
            var resource = __instance as ResourceDefinition;
            if (resource == null || !ResourceCreator.UsesNamedSprite(resource))
                return;

            __result = ResourceCreator.FormatResourceLink(resource, longText, firstIcon, _highLightColor, colorIcon, _highLightColorSprite, addSpace);
        }
    }

    [HarmonyPatch]
    static class PatchMyExtensionsTupleToString
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Extensions.MyExtensions");
            return AccessTools.Method(t, "ToString", new[] { typeof(ValueTuple<ResourceDefinition, double>), typeof(string), typeof(double) });
        }

        static void Postfix(ValueTuple<ResourceDefinition, double> rb, string format, double multiplier, ref string __result)
        {
            if (!ResourceCreator.UsesNamedSprite(rb.Item1))
                return;
            __result = ResourceCreator.FormatResourceAmount(rb.Item1, rb.Item2, multiplier);
        }
    }

    [HarmonyPatch(typeof(global::DropDownEnum), "SetOptionsAwake")]
    static class PatchDropDownEnumSetOptionsAwake
    {
        static readonly FieldInfo _dropDownTypeFi = typeof(global::DropDownEnum).GetField("dropDownType", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _shortTextFi = typeof(global::DropDownEnum).GetField("shortText", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _emptyTextAllFi = typeof(global::DropDownEnum).GetField("emptyTextAll", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly PropertyInfo _allResourcesPi = typeof(global::DropDownEnum).GetProperty("AllResourceDefinitions", BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(global::DropDownEnum __instance)
        {
            if (_dropDownTypeFi == null || _allResourcesPi == null || __instance.dropDown == null)
                return;
            if (!string.Equals(_dropDownTypeFi.GetValue(__instance)?.ToString(), "resorce", StringComparison.OrdinalIgnoreCase))
                return;

            var resources = _allResourcesPi.GetValue(__instance, null) as List<ResourceDefinition>;
            if (resources == null || resources.Count != __instance.dropDown.options.Count)
                return;

            bool shortText = _shortTextFi != null && (bool)_shortTextFi.GetValue(__instance);
            bool emptyTextAll = _emptyTextAllFi != null && (bool)_emptyTextAllFi.GetValue(__instance);
            for (int i = 0; i < resources.Count; i++)
            {
                var rd = resources[i];
                if (rd == null || __instance.dropDown.options[i] == null)
                    continue;
                if (rd.ID == "id_resource_empty")
                {
                    __instance.dropDown.options[i].text = emptyTextAll ? Language.LEManager.Get("DropDownEnum.ALL") : Language.LEManager.Get("id_resource_empty");
                    continue;
                }
                __instance.dropDown.options[i].text = shortText
                    ? ResourceCreator.GetIconString(rd)
                    : ResourceCreator.GetIconString(rd) + " " + Language.LEManager.Get(rd.ID);
            }
            __instance.dropDown.RefreshShownValue();
        }
    }

    [HarmonyPatch(typeof(Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow), "SetData")]
    static class PatchOfferUIRowSetData
    {
        static readonly FieldInfo _rdTextFi = typeof(Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow).GetField("rdText", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(Game.UI.Windows.Elements.MarketOfferElements.OfferUIRow __instance)
        {
            var offer = __instance.Offer;
            if (offer?.Rd == null) return;
            var rdText = _rdTextFi?.GetValue(__instance) as TextMeshProUGUI;
            if (rdText != null)
                rdText.text = ResourceCreator.GetIconString(offer.Rd);
        }
    }

    [HarmonyPatch(typeof(GroundFacilityDescriptor), "get_SpecialCapabilities")]
    static class PatchGroundFacilityDescriptorGetSpecialCapabilities
    {
        static void Postfix(GroundFacilityDescriptor __instance, ref string __result)
        {
            if (__instance == null)
                return;

            if (string.IsNullOrEmpty(__result))
            {
                string customCapabilities = Language.LEManager.Get(__instance.ID + FacilityBaseDescriptor.CapabilitiesString, "");
                if (!string.IsNullOrEmpty(customCapabilities))
                    __result = customCapabilities;
            }

            if (string.IsNullOrEmpty(__result) || __instance.energyProductionData?.input == null || __instance.energyProductionData.input.Length == 0)
                return;

            foreach (var item in __instance.energyProductionData.input)
            {
                if (item?.resource == null || !ResourceCreator.UsesNamedSprite(item.resource))
                    continue;
                string oldIcon = $"<sprite index={item.resource.IdSpritAttlastextMeshPro}>";
                string newIcon = ResourceCreator.GetIconString(item.resource);
                __result = __result.Replace(oldIcon, newIcon);
            }
        }
    }

    [HarmonyPatch(typeof(UIRowFacility), "GetTooltipString")]
    static class PatchChoseFacilityRowTooltip
    {
        static readonly FieldInfo _rowDataField = typeof(UIRowFacility)
            .GetField("curentRowFacilityData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _isDisabledField = typeof(UIRowFacility)
            .GetField("isDisabled", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        static void Postfix(UIRowFacility __instance, ref string __result)
        {
            var rowData = _rowDataField?.GetValue(__instance) as RowFacilityData;
            var descriptor = rowData?.FacilityDescriptor;
            if (descriptor == null)
                return;

            ObjectInfo currentObject = null;
            try
            {
                currentObject = SerializedMonoBehaviourSingleton<UIManager>.Instance
                    .GetWindow<ChoseFacilityWindow>()?.ObjectInfoCurrent;
            }
            catch
            {
                // Leave null; FacilityBaseDescriptor.Tooltip tolerates it.
            }

            string baseTooltip = descriptor.Tooltip(MonoBehaviourSingleton<GameManager>.Instance.Player, currentObject);
            if (string.IsNullOrWhiteSpace(__result))
            {
                __result = baseTooltip;
                return;
            }

            bool isDisabled = _isDisabledField != null && (bool)_isDisabledField.GetValue(__instance);
            if (!isDisabled)
                return;

            if (!string.Equals(__result, baseTooltip, StringComparison.Ordinal))
                __result = baseTooltip + "\n\n" + __result;
        }

        static Exception Finalizer(UIRowFacility __instance, Exception __exception, ref string __result)
        {
            if (__exception == null)
                return null;

            var rowData = _rowDataField?.GetValue(__instance) as RowFacilityData;
            var descriptor = rowData?.FacilityDescriptor;
            string id = descriptor?.ID ?? "<unknown>";
            Plugin.Log.LogError($"[TooltipFix] Facility tooltip crashed for '{id}': {__exception}");

            if (descriptor == null)
            {
                __result = Language.LEManager.Get("ToolTip_empty");
                return null;
            }

            string name = Language.LEManager.Get(id, id).ToUpper();
            string description = Language.LEManager.Get(id + "_Description", string.Empty) ?? string.Empty;
            string capabilities = Language.LEManager.Get(id + FacilityBaseDescriptor.CapabilitiesString, string.Empty) ?? string.Empty;

            __result = descriptor.TooltipStart
                + "<size=14><font=\"Oxanium-Medium SDF\"><b><color=white>" + name + "</color></b></font></size>\n\n"
                + description;

            if (!string.IsNullOrWhiteSpace(capabilities))
                __result += "\n\n" + capabilities;

            return null;
        }
    }

    /// <summary>
    /// Injects a small version badge into the bottom-right corner of the main menu.
    /// Patches MenuSceneUI.Start() which runs after the menu canvas is fully built.
    /// </summary>
    [HarmonyPatch(typeof(MenuSceneUI), "Start")]
    static class PatchMenuSceneUIStart
    {
        static void Postfix(MenuSceneUI __instance)
        {
            try
            {
                // Find the root canvas — the menu scene has one screen-space overlay canvas.
                Canvas canvas = null;
                foreach (var c in UnityEngine.Object.FindObjectsOfType<Canvas>())
                {
                    if (c.isRootCanvas) { canvas = c; break; }
                }
                if (canvas == null)
                {
                    Plugin.Log.LogWarning("[VersionBadge] No root Canvas found in menu scene.");
                    return;
                }

                var go = new GameObject("TedditVersionBadge");
                go.transform.SetParent(canvas.transform, false);
                go.transform.SetAsLastSibling(); // render on top

                var text = go.AddComponent<TextMeshProUGUI>();
                text.text = $"Teddit v{Plugin.Version}";
                text.fontSize = 18f;
                text.color = new Color(1f, 1f, 1f, 0.85f);
                text.alignment = TextAlignmentOptions.BottomRight;
                text.raycastTarget = false;

                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(1f, 0f);
                rect.anchorMax = new Vector2(1f, 0f);
                rect.pivot     = new Vector2(1f, 0f);
                rect.sizeDelta = new Vector2(180f, 28f);
                rect.anchoredPosition = new Vector2(-12f, 12f);

                UnityEngine.Object.Destroy(go, 5f);
                Plugin.Log.LogInfo($"[VersionBadge] Injected version badge (Teddit v{Plugin.Version}).");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[VersionBadge] Failed to create version badge: {ex.Message}");
            }
        }
    }
}
