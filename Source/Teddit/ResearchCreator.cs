using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Data.ScriptableObject;
using Language;
using Manager;
using Newtonsoft.Json.Linq;
using ScriptableObjectScripts;
using UnityEngine;

namespace Teddit
{
    internal static class ResearchCreator
    {
        /// <summary>
        /// Populated by the ResearchTreeCreateUIPostfix Harmony patch the first time
        /// ResearchTree.CreateUI() runs.  Used by RebuildResearchTree() to re-invoke it
        /// after new entries are injected.
        /// </summary>
        internal static object _researchTreeInstance;

        static readonly string[] CurrentLangCandidates =
            { "_currentLaungage", "_currentLanguage", "currentLaungage", "currentLanguage" };
        static readonly string[] DefaultLangCandidates =
            { "_defaultLanguage", "_defaultLaungage", "defaultLanguage", "defaultLaungage" };

        /// <summary>
        /// Creates a new ResearchDefinition from a field dict and injects it into AllResearchDefinition.
        /// Called by ScriptableObjectPatcher.RunResearch when an ID is not found in the game's list.
        /// </summary>
        internal static void CreateAndInjectResearch(string id, Dictionary<string, JToken> def, AllScriptableObjectManager allSO, string modDir)
        {
            var rd = ScriptableObject.CreateInstance<ResearchDefinition>();
            FacilityCreator.SetField(rd, "id", id);

            // researchType / researchSubType — required (UI crashes if null)
            string rtId  = FacilityCreator.GetVal<string>(def, "researchType",    null);
            string rstId = FacilityCreator.GetVal<string>(def, "researchSubType", null);
            object rt  = FindResearchTypeSO(allSO, rtId,  findSubType: false);
            object rst = FindResearchTypeSO(allSO, rstId, findSubType: true);
            if (rt  == null) Plugin.Log.LogWarning($"[ResearchCreator] {id}: unknown researchType '{rtId}'");
            if (rst == null) Plugin.Log.LogWarning($"[ResearchCreator] {id}: unknown researchSubType '{rstId}'");
            FacilityCreator.SetField(rd, "researchType",    rt);
            FacilityCreator.SetField(rd, "researchSubType", rst);

            // requirementsResearch must be a non-null array (iterated without null-guard in game code)
            FacilityCreator.SetField(rd, "requirementsResearch", new ResearchDefinition[0]);

            // Simple fields — apply before complex fields so complex can override
            JToken tok;
            if (def.TryGetValue("workHourToComplete", out tok)) FacilityCreator.SetField(rd, "workHourToComplete", tok.Value<float>());
            else FacilityCreator.SetField(rd, "workHourToComplete", 1200000f);

            FacilityCreator.SetField(rd, "stage",       FacilityCreator.GetVal<int>(def,  "stage",       1));
            FacilityCreator.SetField(rd, "subStage",    FacilityCreator.GetVal<int>(def,  "subStage",    0));
            FacilityCreator.SetField(rd, "showInTree",  FacilityCreator.GetVal<bool>(def, "showInTree",  false));
            FacilityCreator.SetField(rd, "isLocked",    FacilityCreator.GetVal<bool>(def, "isLocked",    false));
            FacilityCreator.SetField(rd, "isLockedForUI", FacilityCreator.GetVal<bool>(def, "isLockedForUI", false));

            // Icon — optional; falls back to null (game renders a placeholder)
            string iconRelPath = FacilityCreator.GetVal<string>(def, "icon", null);
            if (!string.IsNullOrEmpty(iconRelPath) && !string.IsNullOrEmpty(modDir))
            {
                string iconFull = Path.IsPathRooted(iconRelPath)
                    ? iconRelPath
                    : Path.Combine(modDir, iconRelPath);
                var sprite = FacilityCreator.LoadSprite(iconFull, id);
                if (sprite != null) rd.Sprite = sprite;
            }

            // Translations
            string name = FacilityCreator.GetVal<string>(def, "name", null);
            string desc = FacilityCreator.GetVal<string>(def, "description", null);
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(desc))
                InjectResearchTranslations(id, name, desc);

            // Complex fields: requirementsResearch, newViewResearchTreeParent, unlocks
            ScriptableObjectPatcher.ApplyResearchComplexFields(rd, def, id, allSO);

            // Inject into AllResearchDefinition
            var allResearch = allSO.AllResearchDefinition;
            Type baseType   = allResearch.GetType().BaseType;
            var listFI   = FacilityCreator.FindField(baseType, "list");
            var listNEFI = FacilityCreator.FindField(baseType, "listNotEmpty");
            if (listFI == null || listNEFI == null)
                throw new Exception("[ResearchCreator] Could not find AllResearchDefinition list fields via reflection.");

            ((List<ResearchDefinition>)listFI.GetValue(allResearch)).Add(rd);
            ((List<ResearchDefinition>)listNEFI.GetValue(allResearch)).Add(rd);

            try { allSO.AllMyIDScriptableObjects.Add(rd); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[ResearchCreator] AllMyIDScriptableObjects.Add failed: {ex.Message}"); }

            Plugin.Log.LogInfo($"[ResearchCreator] + {id} (type:{rtId}, stage:{FacilityCreator.GetVal<int>(def, "stage", 1)})");
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Finds a ResearchType or ResearchSubType by ID by scanning existing research definitions.
        /// This is guaranteed to work since existing entries already hold live SO references.
        /// </summary>
        internal static object FindResearchTypeSO(AllScriptableObjectManager allSO, string id, bool findSubType)
        {
            if (string.IsNullOrEmpty(id)) return null;
            foreach (var existing in allSO.AllResearchDefinition.List)
            {
                if (existing == null) continue;
                if (!findSubType)
                {
                    var rt = existing.ResearchType;
                    if (rt != null && rt.ID == id) return rt;
                }
                else
                {
                    var rst = existing.ResearchSubType;
                    if (rst != null && rst.ID == id) return rst;
                }
            }
            Plugin.Log.LogWarning($"[ResearchCreator] FindResearchTypeSO: '{id}' (subType={findSubType}) not found");
            return null;
        }

        internal static void InjectResearchTranslations(string id, string name, string description)
        {
            // rd.Title  = LEManager.Get(id + "_Title", null).ToUpper()  → key uses capital T
            // rd.Description = LEManager.Get(id + "_Description", null) → capital D
            // Lookup reads from Laungage.translations only (translations2 is a pre-load overlay,
            // merged into translations at file load — injecting there after load has no effect).
            try
            {
                var le     = MonoBehaviourSingleton<LEManager>.Instance;
                var leType = typeof(LEManager);
                object currentLang = FacilityCreator.GetFieldValueByNames(le, leType, CurrentLangCandidates);
                object defaultLang  = FacilityCreator.GetFieldValueByNames(le, leType, DefaultLangCandidates);
                foreach (var lang in new[] { currentLang, defaultLang })
                {
                    if (lang == null) continue;
                    var dictFi = lang.GetType().GetField("translations", BindingFlags.Public | BindingFlags.Instance);
                    if (!(dictFi?.GetValue(lang) is Dictionary<string, string> dict)) continue;

                    if (!string.IsNullOrEmpty(name))        dict[id + "_Title"]       = name;
                    if (!string.IsNullOrEmpty(description)) dict[id + "_Description"] = description;
                }

                // Fire OnTranslationChanged so any live UI refreshes its text
                var invokeMethod = leType.GetMethod("InvokeTranslationChanged",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                invokeMethod?.Invoke(le, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ResearchCreator] Translation injection failed for {id}: {ex.Message}");
            }
        }

        /// <summary>
        /// Called from the Harmony Show() postfix.  Finds entries in ListNotEmpty that were
        /// never added to listSpawnRD (i.e. mod-created entries injected after the initial tree
        /// build), destroys the branch that should contain them so SetData() can rebuild it
        /// cleanly, then re-spawns that branch with the new entry included.
        /// </summary>
        internal static void SpawnPendingEntries(object treeInstance)
        {
            const string prefix = "[ResearchCreator]";
            try
            {
                var treeType = treeInstance.GetType();

                var listSpawnFi = ScriptableObjectPatcher.FindField(treeType, "listSpawnRD");
                var dictFi      = ScriptableObjectPatcher.FindField(treeType, "dictionaryResearchParent");
                var listSpawnRD = listSpawnFi?.GetValue(treeInstance) as List<ResearchDefinition>;
                var dictObj     = dictFi?.GetValue(treeInstance) as System.Collections.IDictionary;
                if (listSpawnRD == null || dictObj == null)
                {
                    Plugin.Log.LogWarning($"{prefix} SpawnPendingEntries: listSpawnRD or dictionaryResearchParent not found.");
                    return;
                }

                // Find entries in the game's list that haven't been spawned yet
                var allSO = SerializedMonoBehaviourSingleton<AllScriptableObjectManager>.Instance;
                var unspawned = new List<ResearchDefinition>();
                foreach (var rd in allSO.AllResearchDefinition.ListNotEmpty)
                    if (!listSpawnRD.Contains(rd)) unspawned.Add(rd);
                if (unspawned.Count == 0) return;

                // Resolve types and members we need
                Type typeUIType  = typeof(ResearchDefinition).Assembly
                    .GetType("Game.UI.Windows.Windows.ResearchTree.ResearchTreeTypeUI");
                Type stageUIType = typeof(ResearchDefinition).Assembly
                    .GetType("Game.UI.Windows.Windows.ResearchTree.ResearchTreeTypeStageUI");
                if (typeUIType == null || stageUIType == null)
                {
                    Plugin.Log.LogWarning($"{prefix} ResearchTreeTypeUI/StageUI type not found.");
                    return;
                }

                var listStageUIFi = typeUIType.GetField("listResearchTreeTypeStageUI",
                    BindingFlags.Public | BindingFlags.Instance);
                var stageNumFi = ScriptableObjectPatcher.FindField(stageUIType, "stage");
                var stageElemFi = stageUIType.GetField("researchTreeTypeStageUIElement",
                    BindingFlags.Public | BindingFlags.Instance);
                var spawnMI = stageUIType.GetMethod("Spawn",
                    BindingFlags.Public | BindingFlags.Instance);

                if (listStageUIFi == null || stageNumFi == null || stageElemFi == null || spawnMI == null)
                {
                    Plugin.Log.LogWarning($"{prefix} Missing field/method on stage UI types.");
                    return;
                }

                // Collect unique root entries that need their branch rebuilt
                var rootsToRebuild = new HashSet<ResearchDefinition>();
                foreach (var rd in unspawned)
                {
                    var root = rd.ShowInTree ? rd : ResearchDefinition.GetRoot(rd);
                    if (root != null) rootsToRebuild.Add(root);
                    else Plugin.Log.LogWarning($"{prefix} No root found for '{rd.ID}' — cannot inject.");
                }

                int rebuilt = 0;
                foreach (var root in rootsToRebuild)
                {
                    if (root.ResearchType == null) continue;
                    if (!dictObj.Contains(root.ResearchType))
                    {
                        Plugin.Log.LogWarning($"{prefix} ResearchType '{root.ResearchType.ID}' not in dictionaryResearchParent.");
                        continue;
                    }

                    var typeUI     = dictObj[root.ResearchType];
                    var stageUIList = listStageUIFi.GetValue(typeUI) as System.Collections.IList;
                    if (stageUIList == null) continue;

                    // Find the stage UI row matching the root's stage
                    object targetStageUI = null;
                    foreach (var sUI in stageUIList)
                        if ((int)stageNumFi.GetValue(sUI) == root.Stage) { targetStageUI = sUI; break; }

                    if (targetStageUI == null)
                    {
                        Plugin.Log.LogWarning($"{prefix} No stage row found for root '{root.ID}' (stage {root.Stage}).");
                        continue;
                    }

                    // Remove root and all its descendants from listSpawnRD so SetData()
                    // re-spawns them (and discovers our new child along the way)
                    var toRemove = new List<ResearchDefinition>();
                    foreach (var existing in listSpawnRD)
                    {
                        if (existing == root) { toRemove.Add(existing); continue; }
                        var existRoot = ResearchDefinition.GetRoot(existing);
                        if (existRoot == root) toRemove.Add(existing);
                    }
                    foreach (var r in toRemove) listSpawnRD.Remove(r);

                    // Destroy the existing branch element so Spawn() creates a fresh one
                    var existingElem = stageElemFi.GetValue(targetStageUI) as MonoBehaviour;
                    if (existingElem != null)
                    {
                        UnityEngine.Object.Destroy(existingElem.gameObject);
                        stageElemFi.SetValue(targetStageUI, null);
                    }

                    // Spawn fresh — SetData() will scan ListNotEmpty and pick up our new entry
                    spawnMI.Invoke(targetStageUI, new object[] { root });
                    rebuilt++;
                    Plugin.Log.LogInfo($"{prefix} Rebuilt branch for root '{root.ID}'.");
                }

                if (rebuilt > 0)
                {
                    treeType.GetMethod("SetCorrectHighAfterSpawn",
                        BindingFlags.Public | BindingFlags.Instance)
                        ?.Invoke(treeInstance, null);
                    treeType.GetMethod("HorizontalLayoutRebuild",
                        BindingFlags.Public | BindingFlags.Instance)
                        ?.Invoke(treeInstance, null);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ResearchCreator] SpawnPendingEntries failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Called after RunResearch injects new entries.  If the tree is already visible
        /// the Show() postfix handles injection; this is a fallback for the rare case where
        /// the instance is already available at patcher time.
        /// </summary>
        internal static void RebuildResearchTree()
        {
            if (_researchTreeInstance == null)
            {
                Plugin.Log.LogInfo("[ResearchCreator] ResearchTree not yet shown — new entries will be injected when the tree is first opened.");
                return;
            }
            SpawnPendingEntries(_researchTreeInstance);
        }
    }
}
