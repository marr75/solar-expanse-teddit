using System.IO;
using System.Reflection;
using HarmonyLib;
using Manager;

namespace MyMod
{
    /// <summary>
    /// Hooks into ObjectInfoManager.Update() — a scene MonoBehaviour whose Update() DOES fire.
    /// (BepInEx plugin MonoBehaviour.Update() is never called in this game; scene objects are fine.)
    /// Waits for solarSystemLoadEventWas == true, meaning solarSystemLoadEvent.Invoke() has
    /// completed and all ObjectInfo.StartStuff() handlers have run — deposits are ready.
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
            Plugin.Log.LogInfo($"[MyMod] solarSystemLoadEvent done — {__instance.allObjectInfos?.Count} bodies.");

            // ── Data dump (disable once you have the data) ────────────────────
            if (Plugin.DumpOnLoad)
            {
                try { DataDumper.Run(Path.Combine(Plugin.PluginDir, "dump")); }
                catch (System.Exception ex) { Plugin.Log.LogError($"[DataDumper] {ex}"); }
            }

            // ── Facility patches ──────────────────────────────────────────────
            var facilityPatches = PatchConfig.Load(Path.Combine(Plugin.PluginDir, "facilities.json"));
            try { ScriptableObjectPatcher.RunFacilities(facilityPatches); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[FacilityPatcher] {ex}"); }

            // ── Spacecraft patches ────────────────────────────────────────────
            var spacecraftPatches = PatchConfig.Load(Path.Combine(Plugin.PluginDir, "spacecraft.json"));
            try { ScriptableObjectPatcher.RunSpacecraft(spacecraftPatches); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[SpacecraftPatcher] {ex}"); }

            // ── Launch vehicle patches ────────────────────────────────────────
            var launchVehiclePatches = PatchConfig.Load(Path.Combine(Plugin.PluginDir, "launch_vehicles.json"));
            try { ScriptableObjectPatcher.RunLaunchVehicles(launchVehiclePatches); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[LaunchVehiclePatcher] {ex}"); }

            // ── Research patches ──────────────────────────────────────────────
            var researchPatches = PatchConfig.Load(Path.Combine(Plugin.PluginDir, "research.json"));
            try { ScriptableObjectPatcher.RunResearch(researchPatches); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[ResearchPatcher] {ex}"); }

            // ── Deposit injection ─────────────────────────────────────────────
            var depositConfig = DepositConfig.Load(Path.Combine(Plugin.PluginDir, "deposits.json"));
            try { DepositInjector.Run(depositConfig, __instance); }
            catch (System.Exception ex) { Plugin.Log.LogError($"[DepositInjector] {ex}"); }
        }
    }
}
