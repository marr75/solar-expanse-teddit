using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CustomUpdate;
using Data.ScriptableObject;
using Game.VisualizationScripts;
using HarmonyLib;
using Manager;

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
    static class PatchResearchTreeShow
    {
        static MethodBase TargetMethod()
        {
            var t = AccessTools.TypeByName("Game.UI.Windows.Windows.ResearchTree.ResearchTree");
            if (t == null)
            {
                Plugin.Log.LogWarning("[Patch] ResearchTree type not found — Show patch skipped.");
                return null;
            }
            return AccessTools.Method(t, "Show");
        }

        static void Postfix(object __instance)
        {
            // New-research injection is temporarily disabled — do nothing here.
            // ResearchCreator._researchTreeInstance = __instance;
            // ResearchCreator.SpawnPendingEntries(__instance);
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
            foreach (var dir in dirs)
                ApplyModDir(dir, __instance);
        }

        static void ApplyModDir(string dir, ObjectInfoManager oi)
        {
            string label = Path.GetFileName(dir);

            var facilityPatches = PatchConfig.Load(Path.Combine(dir, "facilities.yaml"));
            try { ScriptableObjectPatcher.RunFacilities(facilityPatches, dir); }
            catch (Exception ex) { Plugin.Log.LogError($"[FacilityPatcher:{label}] {ex}"); }

            var spacecraftPatches = PatchConfig.Load(Path.Combine(dir, "spacecraft.yaml"));
            try { ScriptableObjectPatcher.RunSpacecraft(spacecraftPatches, dir); }
            catch (Exception ex) { Plugin.Log.LogError($"[SpacecraftPatcher:{label}] {ex}"); }

            var launchVehiclePatches = PatchConfig.Load(Path.Combine(dir, "launch_vehicles.yaml"));
            try { ScriptableObjectPatcher.RunLaunchVehicles(launchVehiclePatches, dir); }
            catch (Exception ex) { Plugin.Log.LogError($"[LaunchVehiclePatcher:{label}] {ex}"); }

            var researchPatches = PatchConfig.Load(Path.Combine(dir, "research.yaml"));
            try { ScriptableObjectPatcher.RunResearch(researchPatches, dir); }
            catch (Exception ex) { Plugin.Log.LogError($"[ResearchPatcher:{label}] {ex}"); }

            var depositConfig = DepositConfig.Load(Path.Combine(dir, "deposits.yaml"));
            try { DepositInjector.Run(depositConfig, oi); }
            catch (Exception ex) { Plugin.Log.LogError($"[DepositInjector:{label}] {ex}"); }
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
}
