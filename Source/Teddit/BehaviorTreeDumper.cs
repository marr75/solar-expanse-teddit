using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AI;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using HarmonyLib;

namespace Teddit
{
    internal static class BehaviorTreeDumper
    {
        static readonly HashSet<int> _dumpedInstanceIds = new HashSet<int>();
        static readonly PropertyInfo _friendlyNameProp =
            typeof(Task).GetProperty("FriendlyName", BindingFlags.Public | BindingFlags.Instance);

        internal static void DumpAll()
        {
            var bm = BehaviorManager.instance;
            Plugin.Log.LogInfo($"[BehaviorTreeDumper] BehaviorManager.instance==null? {bm == null}");
            if (bm == null)
                return;

            var trees = bm.BehaviorTrees;
            Plugin.Log.LogInfo($"[BehaviorTreeDumper] BehaviorTrees.Count={trees.Count}");
            if (trees.Count == 0)
            {
                Plugin.Log.LogWarning("[BehaviorTreeDumper] BehaviorTrees.Count==0 — trigger fired too early; no trees registered yet.");
                return;
            }

            string dumpDir = Path.Combine(Plugin.PluginDir, "dump");
            Directory.CreateDirectory(dumpDir);
            string outPath = Path.Combine(dumpDir, "behavior_trees.yaml");

            var sb = new StringBuilder();
            int written = 0;

            foreach (var tree in trees)
            {
                if (tree == null || tree.behavior == null)
                    continue;

                int instanceId;
                try { instanceId = tree.behavior.GetInstanceID(); }
                catch { continue; }

                if (!_dumpedInstanceIds.Add(instanceId))
                    continue;

                string treeName = "(unknown)";
                try { treeName = tree.behavior.GetBehaviorSource()?.behaviorName ?? "(null)"; }
                catch (Exception ex) { Plugin.Log.LogWarning($"[BehaviorTreeDumper] behaviorName read failed: {ex.Message}"); }

                string goName = "(unknown)";
                try { goName = tree.behavior.gameObject?.name ?? "(null)"; }
                catch { }

                var taskList = tree.taskList;
                int taskCount = taskList?.Count ?? 0;
                Plugin.Log.LogInfo($"[BehaviorTreeDumper] tree name={treeName} gameObject={goName} tasks={taskCount}");

                sb.AppendLine($"# {treeName} on {goName}");
                sb.AppendLine($"{YamlKey(treeName + "_" + goName)}:");
                sb.AppendLine($"  behavior_name: {YamlScalar(treeName)}");
                sb.AppendLine($"  game_object: {YamlScalar(goName)}");
                sb.AppendLine($"  task_count: {taskCount}");

                if (taskList != null && taskCount > 0)
                {
                    var parentIndex = tree.parentIndex;
                    var childrenIndex = tree.childrenIndex;

                    sb.AppendLine("  task_list:");
                    for (int i = 0; i < taskList.Count; i++)
                    {
                        var task = taskList[i];
                        if (task == null)
                        {
                            sb.AppendLine($"    - index: {i}");
                            sb.AppendLine($"      type: null");
                            continue;
                        }

                        string typeFull = task.GetType().FullName ?? task.GetType().Name;
                        string friendlyName = task.GetType().Name;
                        try
                        {
                            if (_friendlyNameProp != null)
                                friendlyName = _friendlyNameProp.GetValue(task) as string ?? friendlyName;
                        }
                        catch { }

                        int refId = task.ReferenceID;
                        int parent = (parentIndex != null && i < parentIndex.Count) ? parentIndex[i] : -1;

                        sb.AppendLine($"    - index: {i}");
                        sb.AppendLine($"      reference_id: {refId}");
                        sb.AppendLine($"      type: {YamlScalar(typeFull)}");
                        sb.AppendLine($"      friendly_name: {YamlScalar(friendlyName)}");
                        sb.AppendLine($"      parent_index: {parent}");

                        if (childrenIndex != null && i < childrenIndex.Count && childrenIndex[i] != null && childrenIndex[i].Count > 0)
                        {
                            sb.AppendLine("      children:");
                            foreach (int ci in childrenIndex[i])
                                sb.AppendLine($"        - {ci}");
                        }
                    }
                }

                sb.AppendLine();
                written++;
            }

            if (written > 0)
            {
                File.AppendAllText(outPath, sb.ToString());
                Plugin.Log.LogInfo($"[BehaviorTreeDumper] Appended {written} tree(s) → {outPath}");
            }
        }

        static string YamlKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unknown";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : '_');
            return sb.ToString();
        }

        static string YamlScalar(string s)
        {
            if (s == null) return "null";
            if (s.Length == 0) return "''";
            bool needsQuote = s.IndexOfAny(new[] { ':', '#', '\'', '"', '\n', '\r', '[', ']', '{', '}', ',', '&', '*', '?' }) >= 0
                || s[0] == ' ' || s[s.Length - 1] == ' ';
            if (!needsQuote) return s;
            return "'" + s.Replace("'", "''") + "'";
        }
    }

    [HarmonyPatch(typeof(CompanyBehaviour), nameof(CompanyBehaviour.EnableAI))]
    static class PatchCompanyBehaviourEnableAI
    {
        static void Postfix()
        {
            try { BehaviorTreeDumper.DumpAll(); }
            catch (Exception ex) { Plugin.Log.LogError($"[BehaviorTreeDumper] DumpAll exception: {ex}"); }
        }
    }
}
