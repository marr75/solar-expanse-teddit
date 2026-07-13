using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using AI;
using AI.Actions;
using BehaviorDesigner.Runtime;
using BehaviorDesigner.Runtime.Tasks;
using HarmonyLib;

namespace Teddit
{
    internal static class BehaviorTreeDumper
    {
        static readonly HashSet<int> _dumpedInstanceIds = new HashSet<int>();
        static readonly HashSet<int> _expandedExternalIds = new HashSet<int>();
        static bool _sessionFileInitialized;

        static readonly PropertyInfo _friendlyNameProp =
            typeof(Task).GetProperty("FriendlyName", BindingFlags.Public | BindingFlags.Instance);
        static readonly FieldInfo _lazyExternalField =
            typeof(LazyExternalBehavior).GetField("externalBehaviorTree", BindingFlags.NonPublic | BindingFlags.Instance);
        static readonly FieldInfo _doContractField =
            typeof(DoContract).GetField("doContractBehavior", BindingFlags.NonPublic | BindingFlags.Instance);

        internal static void DumpAll()
        {
            var bm = BehaviorManager.instance;
            Plugin.Log.LogDebug($"[BehaviorTreeDumper] BehaviorManager.instance==null? {bm == null}");
            if (bm == null)
                return;

            var trees = bm.BehaviorTrees;
            Plugin.Log.LogDebug($"[BehaviorTreeDumper] BehaviorTrees.Count={trees.Count}");
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
                Plugin.Log.LogDebug($"[BehaviorTreeDumper] tree name={treeName} gameObject={goName} tasks={taskCount}");

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
                        int refId = task.ReferenceID;
                        int parent = (parentIndex != null && i < parentIndex.Count) ? parentIndex[i] : -1;

                        sb.AppendLine($"    - index: {i}");
                        sb.AppendLine($"      reference_id: {refId}");
                        sb.AppendLine($"      type: {YamlScalar(typeFull)}");
                        sb.AppendLine($"      friendly_name: {YamlScalar(FriendlyOf(task))}");
                        sb.AppendLine($"      parent_index: {parent}");

                        if (childrenIndex != null && i < childrenIndex.Count && childrenIndex[i] != null && childrenIndex[i].Count > 0)
                        {
                            sb.AppendLine("      children:");
                            foreach (int ci in childrenIndex[i])
                                sb.AppendLine($"        - {ci}");
                        }

                        var external = GetExternalRef(task);
                        if (external != null || IsExternalDispatcher(task))
                            ExpandExternal(external, sb, 6);
                    }
                }

                sb.AppendLine();
                written++;
            }

            if (written > 0)
            {
                if (!_sessionFileInitialized)
                {
                    File.WriteAllText(outPath, $"# Solar Expanse behavior-tree dump — session {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n\n" + sb.ToString());
                    _sessionFileInitialized = true;
                    Plugin.Log.LogInfo($"[BehaviorTreeDumper] Wrote {written} tree(s) (truncated) → {outPath}");
                }
                else
                {
                    File.AppendAllText(outPath, sb.ToString());
                    Plugin.Log.LogInfo($"[BehaviorTreeDumper] Appended {written} tree(s) → {outPath}");
                }
            }
        }

        static bool IsExternalDispatcher(Task task) =>
            task is LazyExternalBehavior || task is DoContract;

        static ExternalBehavior GetExternalRef(Task task)
        {
            object value = null;
            if (task is LazyExternalBehavior lazy)
                value = _lazyExternalField?.GetValue(lazy);
            else if (task is DoContract dc)
                value = _doContractField?.GetValue(dc);

            var external = value as ExternalBehavior;
            return external == null ? null : external; // UnityEngine.Object null-check collapses destroyed refs
        }

        static void ExpandExternal(ExternalBehavior external, StringBuilder sb, int indent)
        {
            string pad = new string(' ', indent);
            if (external == null)
            {
                sb.AppendLine($"{pad}external_resolved: false");
                Plugin.Log.LogWarning("[BehaviorTreeDumper] external behavior reference was null/unresolvable at dump time.");
                return;
            }

            string extName = "(unknown)";
            try { extName = external.name; }
            catch { }
            sb.AppendLine($"{pad}external_behavior: {YamlScalar(extName)}");

            int extId;
            try { extId = external.GetInstanceID(); }
            catch { extId = extName.GetHashCode(); }

            if (!_expandedExternalIds.Add(extId))
            {
                sb.AppendLine($"{pad}external_resolved: true");
                sb.AppendLine($"{pad}external_note: already_expanded");
                Plugin.Log.LogDebug($"[BehaviorTreeDumper] external '{extName}' already expanded this session — referencing prior dump.");
                return;
            }

            BehaviorSource source = null;
            try { source = external.GetBehaviorSource(); }
            catch (Exception ex) { Plugin.Log.LogWarning($"[BehaviorTreeDumper] GetBehaviorSource('{extName}') failed: {ex.Message}"); }

            if (source == null)
            {
                sb.AppendLine($"{pad}external_resolved: false");
                Plugin.Log.LogWarning($"[BehaviorTreeDumper] external '{extName}' has no BehaviorSource.");
                return;
            }

            try
            {
                source.Owner = external;
                source.CheckForSerialization(false);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"[BehaviorTreeDumper] CheckForSerialization('{extName}') failed: {ex.Message}"); }

            sb.AppendLine($"{pad}external_resolved: true");

            Task root = source.RootTask;
            if (root == null)
            {
                sb.AppendLine($"{pad}external_task_count: 0");
                Plugin.Log.LogWarning($"[BehaviorTreeDumper] external '{extName}' RootTask null after deserialization.");
                return;
            }

            int count = 0;
            var body = new StringBuilder();
            EmitTaskTree(root, body, indent + 2, ref count);

            sb.AppendLine($"{pad}external_task_count: {count}");
            sb.AppendLine($"{pad}external_tree:");
            sb.Append(body);
            Plugin.Log.LogDebug($"[BehaviorTreeDumper] expanded external '{extName}' resolved=true tasks={count}");
        }

        static void EmitTaskTree(Task task, StringBuilder sb, int indent, ref int count)
        {
            string pad = new string(' ', indent);
            if (task == null)
            {
                sb.AppendLine($"{pad}- type: null");
                return;
            }

            count++;
            string typeFull = task.GetType().FullName ?? task.GetType().Name;
            sb.AppendLine($"{pad}- type: {YamlScalar(typeFull)}");
            sb.AppendLine($"{pad}  friendly_name: {YamlScalar(FriendlyOf(task))}");
            sb.AppendLine($"{pad}  reference_id: {task.ReferenceID}");

            var external = GetExternalRef(task);
            if (external != null || IsExternalDispatcher(task))
                ExpandExternal(external, sb, indent + 2);

            if (task is ParentTask parent && parent.Children != null && parent.Children.Count > 0)
            {
                sb.AppendLine($"{pad}  children:");
                foreach (var child in parent.Children)
                    EmitTaskTree(child, sb, indent + 4, ref count);
            }
        }

        static string FriendlyOf(Task task)
        {
            string friendly = task.GetType().Name;
            try
            {
                if (_friendlyNameProp != null)
                    friendly = _friendlyNameProp.GetValue(task) as string ?? friendly;
            }
            catch { }
            return friendly;
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
