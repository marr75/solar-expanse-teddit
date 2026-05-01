using System.IO;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace MyMod
{
    [BepInPlugin("com.yourname.mymod", "My Mod", "1.0.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static string PluginDir;

        // Set to false once you have dump.json.
        internal const bool DumpOnLoad = true;

        void Awake()
        {
            Log = Logger;
            PluginDir = Path.GetDirectoryName(Info.Location);
            Log.LogInfo("MyMod loaded.");
            new Harmony("com.yourname.mymod").PatchAll();
        }
    }
}
